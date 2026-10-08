using System.Collections;
using System.Globalization;

namespace FinancialHttpCounters;

// Only known projections enter the state machine. No URL, headers, query,
// token, activity/request ID or exception string leaves this in-memory state.
public sealed class CounterState(string owner, Uri document, Uri file)
{
    private readonly Dictionary<string, Pending> pending = new(StringComparer.Ordinal);
    private readonly List<Completed> completed = [];
    public int Heartbeats { get; private set; }
    public int HealthCanaries { get; private set; }
    public bool OutgoingListenerSeen { get; private set; }
    public bool IncomingListenerSeen { get; private set; }
    public int PendingCount => pending.Count;
    public int EventCount { get; private set; }
    public DateTime LastEventUtc { get; private set; }
    private bool accountingOutgoingObserved, accountingCandidateObserved, accountingOriginRejected;
    private int windowFailure;
    public WindowFailureCategory WindowFailure => (WindowFailureCategory)Volatile.Read(ref windowFailure);

    public void Heartbeat() => Heartbeats++;
    public void Listener(string name)
    {
        if (name == "HttpHandlerDiagnosticListener") OutgoingListenerSeen = true;
        if (name == "Microsoft.AspNetCore") IncomingListenerSeen = true;
    }

    public static Dictionary<string, string> DecodeArguments(object raw)
    {
        Require(raw is Array { Length: <= 8 }, "Diagnostic projection array schema changed");
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (object? item in (Array)raw)
        {
            Require(item is IDictionary<string, object>, "Diagnostic projection structure schema changed");
            var entry = (IDictionary<string, object>)item!;
            Require(entry.Count == 2 && entry.TryGetValue("Key", out var key) && key is string
                && entry.TryGetValue("Value", out var value) && (value is string || value is null), "Diagnostic projection pair schema changed");
            var name = (string)entry["Key"];
            var text = entry["Value"] as string ?? "";
            Require(name.Length <= 32 && text.Length <= 4096 && result.TryAdd(name, text), "Duplicate/oversized diagnostic projection");
        }
        return result;
    }

    public void Record(string source, string name, Dictionary<string, string> args, DateTime utc)
    {
        Require(utc.Kind == DateTimeKind.Utc && ++EventCount <= 100000, "Finite known diagnostic stream required");
        LastEventUtc = utc;
        bool outgoing = source == "HttpHandlerDiagnosticListener";
        bool incoming = source == "Microsoft.AspNetCore";
        Require(outgoing || incoming, "Unexpected diagnostic source");
        bool start = name == (outgoing ? "System.Net.Http.HttpRequestOut.Start" : "Microsoft.AspNetCore.Hosting.BeginRequest");
        bool stop = name == (outgoing ? "System.Net.Http.HttpRequestOut.Stop" : "Microsoft.AspNetCore.Hosting.EndRequest");
        Require(start || stop, "Unexpected diagnostic event");
        string id = Required(args, "Id");
        Require(id.Length <= 256 && id.Length > 0, "Actual request identity required");
        string key = (outgoing ? "out:" : "in:") + id;
        if (start)
        {
            var expected = outgoing ? new[] { "Id", "Method", "Scheme", "Host", "Port", "Path" } : new[] { "Id", "Method", "Path" };
            Require(args.Keys.Order().SequenceEqual(expected.Order()), "Start projection schema differs");
            string method = Required(args, "Method"), path = Required(args, "Path");
            Require(method.Length is > 0 and <= 16 && path.StartsWith('/') && !path.Contains('?') && !path.Contains('#'), "Method/path projection unavailable");
            string label = Classify(outgoing, method, path, args);
            Require(pending.Count < 1024 && pending.TryAdd(key, new(utc, label, !outgoing && method == "GET" && path == "/health")), "Unbalanced/duplicate request start");
        }
        else
        {
            var expected = outgoing ? new[] { "Id", "Status", "Task" } : new[] { "Id", "Status" };
            Require(args.Keys.Order().SequenceEqual(expected.Order()), "Stop projection changed");
            Require(pending.Remove(key, out var found), "Missing request start");
            var begun = found ?? throw new InvalidDataException("Missing request start");
            int status;
            string value = args["Status"];
            if (outgoing && Enum.TryParse<System.Net.HttpStatusCode>(value, out var code)) status = (int)code;
            else if (!int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out status)) status = -1;
            Require(utc >= begun.StartUtc, "Request clock regressed");
            bool success = status is >= 200 and < 300 && (!outgoing || args["Task"] == "RanToCompletion");
            if (begun.Canary && success) HealthCanaries++;
            completed.Add(new(begun.StartUtc, utc, begun.Label, success));
        }
    }

    private string Classify(bool outgoing, string method, string path, Dictionary<string, string> args)
    {
        if (!outgoing)
        {
            if (owner == "Document" && method == "POST" && path.Equals("/pdfs/invoice", StringComparison.OrdinalIgnoreCase)) return "DocumentInvoiceRenderBoundary";
            if (owner == "Document" && method == "POST" && path.Equals("/pdfs/receipt", StringComparison.OrdinalIgnoreCase)) return "DocumentReceiptRenderBoundary";
            if (owner == "File" && method == "POST" && path.Equals("/uploads", StringComparison.OrdinalIgnoreCase)) return "FileUploadBoundary";
            return "Other";
        }
        Require(int.TryParse(args["Port"], out int port) && port is > 0 and <= 65535, "Actual HTTP origin port required");
        string scheme = args["Scheme"], host = args["Host"];
        bool Origin(Uri origin) => scheme == origin.Scheme && host.Equals(origin.IdnHost, StringComparison.OrdinalIgnoreCase) && port == origin.Port;
        if (owner == "Accounting")
        {
            // These private booleans observe the existing classifier's exact branches.
            accountingOutgoingObserved = true;
            bool documentCandidate = method == "POST" && (path.Equals("/pdfs/invoice", StringComparison.OrdinalIgnoreCase)
                || path.Equals("/pdfs/receipt", StringComparison.OrdinalIgnoreCase));
            bool uploadCandidate = path.Equals("/uploads", StringComparison.OrdinalIgnoreCase);
            accountingCandidateObserved |= documentCandidate || uploadCandidate;
            accountingOriginRejected |= (documentCandidate && !Origin(document)) || (uploadCandidate && !Origin(file));
        }
        if (owner == "Accounting" && Origin(document))
        {
            if (method == "POST" && path.Equals("/pdfs/invoice", StringComparison.OrdinalIgnoreCase)) return "AccountingInvoiceRenderPost";
            if (method == "POST" && path.Equals("/pdfs/receipt", StringComparison.OrdinalIgnoreCase)) return "AccountingReceiptRenderPost";
        }
        if (owner == "Accounting" && Origin(file) && path.Equals("/uploads", StringComparison.OrdinalIgnoreCase))
            return method == "POST" ? "AccountingFileUploadPost" : "AccountingOtherUploadBoundaryMethod";
        // Counts ANY HttpClient request to the fixed selected-source Brevo origin.
        if (owner == "Notification" && scheme == "https" && host.Equals("api.brevo.com", StringComparison.OrdinalIgnoreCase) && port == 443)
            return "NotificationProviderHttpAttempt";
        return "Other";
    }

    public Dictionary<string, Counts> FinalizeWindow(DateTime from, DateTime until, TimeSpan guard)
    {
        Volatile.Write(ref windowFailure, (int)WindowFailureCategory.None);
        RequirePrerequisites(pending.Count == 0 && Heartbeats > 0 && HealthCanaries > 0 && IncomingListenerSeen, "Actual schema canary/quiescence required");
        if (owner is "Accounting" or "Notification") RequireWindow(OutgoingListenerSeen, "Actual outgoing DiagnosticListener required", WindowFailureCategory.OutgoingListener);
        var effects = completed.Where(x => x.Label != "Other").ToArray();
        RequireWindow(!effects.Any(x => (x.StartUtc <= from && x.StopUtc > from) || (x.StartUtc <= until && x.StopUtc > until)), "Side effect spans phase boundary", WindowFailureCategory.BoundarySpan);
        RequireWindow(!effects.Any(x => (x.StartUtc - from).Duration() < guard || (x.StopUtc - from).Duration() < guard
            || (x.StartUtc - until).Duration() < guard || (x.StopUtc - until).Duration() < guard), "Side effect inside phase guard interval", WindowFailureCategory.GuardInterval);
        string[] labels = owner switch
        {
            "Accounting" => ["AccountingInvoiceRenderPost", "AccountingReceiptRenderPost", "AccountingFileUploadPost", "AccountingOtherUploadBoundaryMethod"],
            "Document" => ["DocumentInvoiceRenderBoundary", "DocumentReceiptRenderBoundary"],
            "File" => ["FileUploadBoundary"],
            "Notification" => ["NotificationProviderHttpAttempt"],
            _ => throw new InvalidDataException("Unknown selected owner")
        };
        var result = new Dictionary<string, Counts>();
        foreach (string label in labels)
        {
            var selected = effects.Where(x => x.Label == label && x.StartUtc > from && x.StopUtc <= until).ToArray();
            result.Add(label, new(selected.Length, selected.Length, selected.Count(x => x.Success)));
        }
        return result;
    }

    internal AccountingEffectObservation ObserveAccountingEffects(DateTime from, DateTime until)
    {
        // Called only after the actual reader has drained. No payload or mutable state escapes.
        var effects = completed.Where(x => x.Label is "AccountingInvoiceRenderPost" or "AccountingReceiptRenderPost"
            or "AccountingFileUploadPost" or "AccountingOtherUploadBoundaryMethod").ToArray();
        return new(accountingOutgoingObserved, accountingCandidateObserved, accountingOriginRejected,
            effects.Length > 0, effects.Any(x => !(x.StartUtc > from && x.StopUtc <= until)));
    }

    internal sealed record AccountingEffectObservation(bool OutgoingObserved, bool OperationalCandidateObserved,
        bool OperationalOriginRejected, bool ClassifiedEffectObserved, bool ClassifiedEffectOutsideWindow);

    private void RequirePrerequisites(bool condition, string message)
    {
        try { Require(condition, message); }
        catch (InvalidDataException)
        {
            // Finalization follows reader quiescence. Classify only actual existing state flags.
            WindowFailureCategory category = pending.Count != 0 ? WindowFailureCategory.PendingRequests
                : Heartbeats <= 0 ? WindowFailureCategory.MissingHeartbeat
                : HealthCanaries <= 0 ? WindowFailureCategory.MissingHealthCanary
                : !IncomingListenerSeen ? WindowFailureCategory.MissingIncomingListener
                : WindowFailureCategory.None;
            Volatile.Write(ref windowFailure, (int)category);
            throw;
        }
    }

    private void RequireWindow(bool condition, string message, WindowFailureCategory category)
    {
        try { Require(condition, message); }
        catch (InvalidDataException)
        {
            Volatile.Write(ref windowFailure, (int)category);
            throw;
        }
    }

    private static string Required(Dictionary<string, string> args, string key)
    { Require(args.TryGetValue(key, out string? value), "Projection key missing"); return value!; }
    public static void Require([System.Diagnostics.CodeAnalysis.DoesNotReturnIf(false)] bool condition, string message)
    { if (!condition) throw new InvalidDataException(message); }
    private sealed record Pending(DateTime StartUtc, string Label, bool Canary);
    private sealed record Completed(DateTime StartUtc, DateTime StopUtc, string Label, bool Success);
    public sealed record Counts(int Started, int Completed, int Successful);
}

// Fixed failed-predicate categories contain no observed values or exception text.
public enum WindowFailureCategory
{
    None,
    PendingRequests,
    MissingHeartbeat,
    MissingHealthCanary,
    MissingIncomingListener,
    OutgoingListener,
    BoundarySpan,
    GuardInterval
}
