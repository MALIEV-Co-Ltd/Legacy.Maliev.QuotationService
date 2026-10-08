using System.Diagnostics;
using System.Diagnostics.Tracing;
using System.IO.Pipes;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using Microsoft.Diagnostics.NETCore.Client;
using Microsoft.Diagnostics.Tracing;

namespace FinancialHttpCounters;

public sealed class EventCollector : IAsyncDisposable
{
    public const string Projection =
        "HttpHandlerDiagnosticListener/System.Net.Http.HttpRequestOut:-\n" +
        "HttpHandlerDiagnosticListener/System.Net.Http.HttpRequestOut.Start:-Id=*Activity.Id;Method=Request.Method.Method;Scheme=Request.RequestUri.Scheme;Host=Request.RequestUri.IdnHost;Port=Request.RequestUri.Port;Path=Request.RequestUri.AbsolutePath\n" +
        "HttpHandlerDiagnosticListener/System.Net.Http.HttpRequestOut.Stop:-Id=*Activity.Id;Status=Response.StatusCode;Task=RequestTaskStatus\n" +
        "Microsoft.AspNetCore/Microsoft.AspNetCore.Hosting.BeginRequest:-Id=httpContext.TraceIdentifier;Method=httpContext.Request.Method;Path=httpContext.Request.Path\n" +
        "Microsoft.AspNetCore/Microsoft.AspNetCore.Hosting.EndRequest:-Id=httpContext.TraceIdentifier;Status=httpContext.Response.StatusCode";

    public ProcessPin Pin { get; }
    public Process Held { get; }
    public CounterState State { get; }
    public object Gate { get; } = new();
    public bool Stopped { get; private set; }
    public bool Drained { get; private set; }
    public int Lost { get; private set; } = -1;
    private EventPipeSession? session;
    private Task<EventPipeSession>? startTask;
    private Task? stopTask;
    private readonly SemaphoreSlim operations = new(1, 1);
    private SafeHandle? heldHandle;
    private SafeHandle? transportHandle;
    private Stream? transport;
    private bool startAttempted, cleanupAttempted, cleanupFailed, resourcesClosed;
    internal bool CleanupQuiescent => resourcesClosed && (startTask is null || startTask.IsCompleted)
        && (stopTask is null || stopTask.IsCompleted) && (reader is null || reader.IsCompleted);

    private Task? reader;
    private BudgetStream? stream;
    private CancellationTokenSource lifetime;
    private bool stopAttempted;
    private bool stopUncertain;
    private readonly long traceMaximum;
    internal bool CleanupOriginallyFailed => cleanupFailed || stopUncertain;
    internal bool TraceBudgetFaultObserved => stream?.BudgetExceeded == true && reader?.IsFaulted == true;
    private int readerFault;
    public ReaderFaultCategory ReaderFault => (ReaderFaultCategory)Volatile.Read(ref readerFault);

    public EventCollector(ProcessPin pin, Uri document, Uri file, CancellationToken token)
        : this(pin, document, file, token, 64 * 1024 * 1024) { }

    internal EventCollector(ProcessPin pin, Uri document, Uri file, CancellationToken token, CollectorControlFault fault)
        : this(pin, document, file, token, ControlMaximum(fault)) { }

    private static long ControlMaximum(CollectorControlFault fault) => fault switch
    {
        CollectorControlFault.TraceBudgetOneByte => 1,
        _ => throw new InvalidDataException("Known collector control required")
    };

    private EventCollector(ProcessPin pin, Uri document, Uri file, CancellationToken token, long traceMaximum)
    {
        this.traceMaximum = traceMaximum;
        Pin = pin;
        State = new(pin.Owner, document, file);
        lifetime = CancellationTokenSource.CreateLinkedTokenSource(token);
        try { Held = Process.GetProcessById(pin.Pid); }
        catch { lifetime.Dispose(); throw; }
        // Native handle acquisition happens only after the caller retains this owner.
    }

    public async Task StartAsync(CancellationToken token)
    {
        await operations.WaitAsync(token);
        try
        {
            CounterState.Require(!cleanupAttempted && !startAttempted && startTask is null && session is null, "Exact collector start already attempted");
            heldHandle ??= Held.SafeHandle;
            var providers = new[]
            {
                // Explicit transforms suppress implicit payload serialization. No query,
                // headers, body, tokens, exception detail or stacks are requested.
                new EventPipeProvider("Microsoft-Diagnostics-DiagnosticSource", EventLevel.Informational, 0x802,
                    new Dictionary<string, string> { ["FilterAndPayloadSpecs"] = Projection }),
                new EventPipeProvider("System.Runtime", EventLevel.Informational, 0,
                    new Dictionary<string, string> { ["EventCounterIntervalSec"] = "1" })
            };
            var config = new EventPipeSessionConfiguration(providers, circularBufferSizeMB: 16,
                requestRundown: false, requestStackwalk: false);
            startAttempted = true;
            startTask = new DiagnosticsClient(Pin.Pid).StartEventPipeSessionAsync(config, token);
            session = await startTask;
            transport = session.EventStream;
            transportHandle = TransportHandle(transport);
            stream = new BudgetStream(transport, traceMaximum, lifetime.Token);
            reader = Task.Factory.StartNew(() =>
            {
                ReaderFaultCategory stage = ReaderFaultCategory.ParserSetup;
                try
                {
                    stage = ReaderFaultCategory.ParserSetup;
                    using var source = new EventPipeEventSource(stream);
                    source.Dynamic.All += entry =>
                    {
                        stage = ReaderFaultCategory.ProcessIdentity;
                        CounterState.Require(entry.ProcessID == Pin.Pid, "Actual EventPipe stream PID differs");
                        lock (Gate)
                        {
                            if (entry.ProviderName == "System.Runtime" && entry.EventName == "EventCounters")
                            { State.Heartbeat(); stage = ReaderFaultCategory.ParserRead; return; }
                            if (entry.ProviderName != "Microsoft-Diagnostics-DiagnosticSource") { stage = ReaderFaultCategory.ParserRead; return; }
                            // Self-describing EventPipe IDs are allocated by NameInfo,
                            // not the DiagnosticSource methods' Event attributes.
                            stage = entry.EventName == "NewDiagnosticListener"
                                ? ReaderFaultCategory.ListenerSchema : ReaderFaultCategory.BridgeSchema;
                            bool listener = RequireBridgeSchema(entry.EventName, entry.PayloadNames);
                            if (listener)
                            {
                                State.Listener((string)entry.PayloadValue(0)); stage = ReaderFaultCategory.ParserRead; return;
                            }
                            stage = ReaderFaultCategory.ProjectionArguments;
                            var arguments = CounterState.DecodeArguments(entry.PayloadValue(2));
                            stage = ReaderFaultCategory.CounterState;
                            State.Record((string)entry.PayloadValue(0), (string)entry.PayloadValue(1),
                                arguments, entry.TimeStamp.ToUniversalTime());
                            stage = ReaderFaultCategory.ParserRead;
                        }
                    };
                    stage = ReaderFaultCategory.ParserRead;
                    bool processed = source.Process();
                    stage = ReaderFaultCategory.ParserCompletion;
                    CounterState.Require(processed, "EventPipe parser did not finish");
                    Lost = source.EventsLost;
                    stage = ReaderFaultCategory.StreamCompletion;
                    // Check the owned stream itself has a real EOF after parser completion.
                    CounterState.Require(stream.Read(new byte[1], 0, 1) == 0 && stream.SawEof && Lost == 0,
                        "Complete EOF and zero sequence-reported event loss required");
                    Drained = true;
                }
                catch (Exception error)
                {
                    Volatile.Write(ref readerFault, (int)(error is OperationCanceledException
                        ? ReaderFaultCategory.Cancelled : stage));
                    throw;
                }
            }, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
        }
        catch { cleanupFailed = true; throw; }
        finally { operations.Release(); }
    }

    public static bool RequireBridgeSchema(string eventName, string[] payloadNames)
    {
        bool listener = eventName == "NewDiagnosticListener";
        CounterState.Require(listener || eventName == "Event", "Unexpected DiagnosticSource bridge event");
        string[] expected = listener ? ["SourceName"] : ["SourceName", "EventName", "Arguments"];
        CounterState.Require(payloadNames.SequenceEqual(expected), "DiagnosticSource bridge schema changed");
        return listener;
    }

    public void CheckReader()
    { if (reader?.IsFaulted == true) throw new InvalidDataException("Private EventPipe reader failed"); }

    public async Task StopAndDrainAsync(CancellationToken token)
    {
        await operations.WaitAsync(token);
        try
        {
            CounterState.Require(!cleanupAttempted && session is not null && reader is not null, "Started exact session required");
            CounterState.Require(!stopAttempted, "Exact session stop may be attempted once");
            stopAttempted = true;
            stopTask = session!.StopAsync(token);
            try { await stopTask; Stopped = true; }
            catch { stopUncertain = true; throw; }
            await reader!.WaitAsync(token);
            CounterState.Require(Drained && Lost == 0, "No-loss drained session required");
        }
        catch { cleanupFailed = true; throw; }
        finally { operations.Release(); }
    }

    private static SafeHandle TransportHandle(Stream owned) => owned switch
    {
        NetworkStream network => network.Socket.SafeHandle,
        PipeStream pipe => pipe.SafePipeHandle,
        FileStream file => file.SafeFileHandle,
        _ => throw new InvalidDataException("Owned EventPipe transport handle unavailable")
    };

    public async ValueTask DisposeAsync()
    {
        // Failed cleanup can be retried on this retained owner. Original uncertainty
        // stays sticky even when a later attempt physically settles its resources.
        using var finite = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        bool entered = false;
        try
        {
            await operations.WaitAsync(finite.Token); entered = true;
            cleanupAttempted = true;
            if (!resourcesClosed)
            {
                try { heldHandle ??= Held.SafeHandle; } catch { cleanupFailed = true; }
                if (startTask?.IsCompletedSuccessfully == true) session ??= startTask.Result;
                if (session is not null && (transport is null || transportHandle is null))
                {
                    try { transport ??= session.EventStream; transportHandle ??= TransportHandle(transport); }
                    catch { cleanupFailed = true; }
                }
                if (session is not null && !Stopped)
                {
                    if (!stopAttempted)
                    {
                        stopAttempted = true;
                        try { stopTask = session.StopAsync(finite.Token); }
                        catch { stopUncertain = true; cleanupFailed = true; }
                    }
                    if (stopTask is not null)
                    {
                        try { await stopTask.WaitAsync(finite.Token); Stopped = true; }
                        catch { stopUncertain = true; cleanupFailed = true; }
                    }
                    else { stopUncertain = true; cleanupFailed = true; }
                }
                try { lifetime.Cancel(); } catch { cleanupFailed = true; }
                if (reader is not null)
                {
                    try { await reader.WaitAsync(finite.Token); }
                    catch { cleanupFailed = true; }
                }
                // Completion wrappers never authorize disposal beneath an in-flight operation.
                bool settled = (startTask is null || startTask.IsCompleted)
                    && (stopTask is null || stopTask.IsCompleted) && (reader is null || reader.IsCompleted);
                if (!settled) { cleanupFailed = true; return; }
                foreach (var task in new Task?[] { startTask, stopTask, reader })
                    if (task?.IsFaulted == true) { _ = task.Exception; cleanupFailed = true; }
                // The pinned SDK cannot expose its pre-handoff socket after a failed
                // connection. Task completion alone is not physical IPC closure.
                bool ipcKnown = (!startAttempted || startTask?.IsCompletedSuccessfully == true)
                    && (!stopAttempted || stopTask?.IsCompletedSuccessfully == true);
                if (!ipcKnown || heldHandle is null || (transport is not null && transportHandle is null))
                { cleanupFailed = true; return; }
                try { session?.Dispose(); } catch { cleanupFailed = true; }
                try { transport?.Dispose(); } catch { cleanupFailed = true; }
                try { Held.Dispose(); } catch { cleanupFailed = true; }
                try { lifetime.Dispose(); } catch { cleanupFailed = true; }
                resourcesClosed = heldHandle.IsClosed && (transport is null || transportHandle?.IsClosed == true);
                if (!resourcesClosed) cleanupFailed = true;
            }
        }
        catch { cleanupFailed = true; throw; }
        finally
        {
            try { finite.Cancel(); } catch { cleanupFailed = true; }
            if (entered) operations.Release();
            CounterState.Require(resourcesClosed && !cleanupFailed && !stopUncertain,
                "Exact EventPipe ownership cleanup unconfirmed");
        }
    }

}

// No trace file: bytes flow straight into the parser, with bounded total bytes
// and cancellation-aware reads. Public receipts never include payload bytes.
public sealed class BudgetStream(Stream inner, long maximum, CancellationToken token) : Stream
{
    private long count;
    internal bool BudgetExceeded { get; private set; }
    public bool SawEof { get; private set; }
    public override int Read(byte[] buffer, int offset, int length)
    {
        token.ThrowIfCancellationRequested();
        int read = inner.ReadAsync(buffer.AsMemory(offset, length), token).AsTask().GetAwaiter().GetResult();
        BudgetExceeded |= count + read > maximum;
        CounterState.Require((count += read) <= maximum, "Finite private trace byte budget exceeded");
        SawEof |= read == 0;
        return read;
    }
    public override bool CanRead => true;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
    public override void Flush() => throw new NotSupportedException();
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
}

// Constant operation categories only: no exception types/text or trace values.
public enum ReaderFaultCategory
{
    None,
    ParserSetup,
    ProcessIdentity,
    ListenerSchema,
    BridgeSchema,
    ProjectionArguments,
    CounterState,
    ParserRead,
    ParserCompletion,
    StreamCompletion,
    Cancelled
}
