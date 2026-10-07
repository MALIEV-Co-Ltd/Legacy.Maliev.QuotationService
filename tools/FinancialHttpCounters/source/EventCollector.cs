using System.Diagnostics;
using System.Diagnostics.Tracing;
using Microsoft.Diagnostics.NETCore.Client;
using Microsoft.Diagnostics.Tracing;

namespace FinancialHttpCounters;

public sealed class EventCollector : IAsyncDisposable
{
    public const string Projection =
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
    private Task? reader;
    private BudgetStream? stream;
    private CancellationTokenSource lifetime;
    private bool stopAttempted;
    private bool stopUncertain;
    private int readerFault;
    public ReaderFaultCategory ReaderFault => (ReaderFaultCategory)Volatile.Read(ref readerFault);

    public EventCollector(ProcessPin pin, Uri document, Uri file, CancellationToken token)
    {
        Pin = pin; Held = Process.GetProcessById(pin.Pid);
        // Obtain a retained OS process handle rather than observing an integer PID alone.
        _ = Held.SafeHandle;
        State = new(pin.Owner, document, file);
        lifetime = CancellationTokenSource.CreateLinkedTokenSource(token);
    }

    public async Task StartAsync(CancellationToken token)
    {
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
        session = await new DiagnosticsClient(Pin.Pid).StartEventPipeSessionAsync(config, token);
        stream = new BudgetStream(session.EventStream, 64 * 1024 * 1024, lifetime.Token);
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
        CounterState.Require(session is not null && reader is not null, "Started exact session required");
        CounterState.Require(!stopAttempted, "Exact session stop may be attempted once");
        stopAttempted = true;
        try { await session!.StopAsync(token); Stopped = true; }
        catch { stopUncertain = true; throw; }
        await reader!.WaitAsync(token);
        CounterState.Require(Drained && Lost == 0, "No-loss drained session required");
    }

    public async ValueTask DisposeAsync()
    {
        // Sessions are owned; target application processes are not killed here.
        bool stopFailed = false;
        try
        {
            if (session is not null && !Stopped)
            {
                // Client marks a session stopped before its IPC response. A
                // second StopAsync can be a no-op after a lost/failed response;
                // it must never turn uncertainty into a cleanup-success receipt.
                if (stopAttempted || stopUncertain) stopFailed = true;
                else
                {
                    stopAttempted = true;
                    using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                    try { await session.StopAsync(stop.Token); Stopped = true; }
                    catch { stopUncertain = true; throw; }
                }
            }
        }
        catch (Exception) { stopFailed = true; }
        finally
        {
            lifetime.Cancel(); session?.Dispose();
            try
            {
                if (reader is not null)
                {
                    try { await reader.WaitAsync(TimeSpan.FromSeconds(5)); }
                    catch (OperationCanceledException) { }
                    catch (InvalidDataException) { }
                }
            }
            finally { Held.Dispose(); lifetime.Dispose(); }
        }
        CounterState.Require(!stopFailed, "Exact EventPipe session stop unconfirmed");
    }
}

// No trace file: bytes flow straight into the parser, with bounded total bytes
// and cancellation-aware reads. Public receipts never include payload bytes.
public sealed class BudgetStream(Stream inner, long maximum, CancellationToken token) : Stream
{
    private long count;
    public bool SawEof { get; private set; }
    public override int Read(byte[] buffer, int offset, int length)
    {
        token.ThrowIfCancellationRequested();
        int read = inner.ReadAsync(buffer.AsMemory(offset, length), token).AsTask().GetAwaiter().GetResult();
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
