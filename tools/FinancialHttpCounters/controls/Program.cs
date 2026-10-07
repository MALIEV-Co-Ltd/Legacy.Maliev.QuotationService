using FinancialHttpCounters;
using System.Text.Json;

int controls = 0;
void Check(bool value) { if (!value) throw new InvalidDataException("Controlled expectation failed"); controls++; }
void Reject(Action operation)
{ try { operation(); throw new Exception("Expected fail-closed admission"); } catch (InvalidDataException) { controls++; } }
DateTime t = new(2026, 10, 7, 12, 0, 0, DateTimeKind.Utc);
Uri document = new("https://document.synthetic.test:45443/");
Uri file = new("https://file.synthetic.test:45444/");
CounterState State(string owner)
{
    var state = new CounterState(owner, document, file);
    state.Heartbeat(); state.Listener("Microsoft.AspNetCore"); state.Listener("HttpHandlerDiagnosticListener");
    state.Record("Microsoft.AspNetCore", "Microsoft.AspNetCore.Hosting.BeginRequest", new() { ["Id"] = "private-health-id", ["Method"] = "GET", ["Path"] = "/health" }, t);
    state.Record("Microsoft.AspNetCore", "Microsoft.AspNetCore.Hosting.EndRequest", new() { ["Id"] = "private-health-id", ["Status"] = "200" }, t.AddSeconds(1));
    return state;
}
Dictionary<string, string> Start(string id, string method, string path) => new()
{ ["Id"] = id, ["Method"] = method, ["Scheme"] = "https", ["Host"] = file.IdnHost, ["Port"] = "45444", ["Path"] = path };
Dictionary<string, string> Stop(string id) => new() { ["Id"] = id, ["Status"] = "Created", ["Task"] = "RanToCompletion" };
void Out(CounterState state, string eventName, Dictionary<string, string> fields, double seconds)
    => state.Record("HttpHandlerDiagnosticListener", "System.Net.Http.HttpRequestOut." + eventName, fields, t.AddSeconds(seconds));

var accounting = State("Accounting");
Out(accounting, "Start", Start("private-post-id", "POST", "/Uploads"), 4);
Out(accounting, "Stop", Stop("private-post-id"), 5);
Out(accounting, "Start", Start("private-delete-id", "DELETE", "/Uploads"), 6);
Out(accounting, "Stop", Stop("private-delete-id"), 7);
var window = accounting.FinalizeWindow(t.AddSeconds(2), t.AddSeconds(10), TimeSpan.FromMilliseconds(250));
Check(window["AccountingFileUploadPost"] == new CounterState.Counts(1, 1, 1));
Check(window["AccountingOtherUploadBoundaryMethod"].Started == 1);
var replay = accounting.FinalizeWindow(t.AddSeconds(10), t.AddSeconds(20), TimeSpan.FromMilliseconds(250));
Check(replay["AccountingFileUploadPost"].Started == 0);
string publicJson = JsonSerializer.Serialize(window);
Check(!publicJson.Contains("synthetic.test") && !publicJson.Contains("private-post") && !publicJson.Contains("/Uploads"));

var provider = State("Notification");
var brevo = Start("private-provider-id", "POST", "/v3/smtp/email"); brevo["Host"] = "api.brevo.com"; brevo["Port"] = "443";
Out(provider, "Start", brevo, 4); Out(provider, "Stop", Stop("private-provider-id"), 5);
Check(provider.FinalizeWindow(t.AddSeconds(2), t.AddSeconds(10), TimeSpan.FromMilliseconds(250))["NotificationProviderHttpAttempt"].Started == 1);

Reject(() => Out(State("Accounting"), "Stop", Stop("unseen"), 4));
var duplicate = State("Accounting"); Out(duplicate, "Start", Start("dup", "POST", "/Uploads"), 4);
Reject(() => Out(duplicate, "Start", Start("dup", "POST", "/Uploads"), 5));
Reject(() => duplicate.FinalizeWindow(t.AddSeconds(2), t.AddSeconds(10), TimeSpan.FromMilliseconds(250)));
var crossing = State("Accounting"); Out(crossing, "Start", Start("cross", "POST", "/Uploads"), 9); Out(crossing, "Stop", Stop("cross"), 11);
Reject(() => crossing.FinalizeWindow(t.AddSeconds(2), t.AddSeconds(10), TimeSpan.FromMilliseconds(250)));
var guarded = State("Accounting"); Out(guarded, "Start", Start("guard", "POST", "/Uploads"), 4); Out(guarded, "Stop", Stop("guard"), 9.9);
Reject(() => guarded.FinalizeWindow(t.AddSeconds(2), t.AddSeconds(10), TimeSpan.FromMilliseconds(250)));
Reject(() => new CounterState("Notification", document, file).FinalizeWindow(t.AddSeconds(2), t.AddSeconds(10), TimeSpan.FromMilliseconds(250)));
var query = Start("private-query", "POST", "/Uploads?private-query-value");
Reject(() => Out(State("Accounting"), "Start", query, 4));
Reject(() => CounterState.DecodeArguments(new object[] { new Dictionary<string, object> { ["Key"] = "Id", ["Value"] = "x", ["Unexpected"] = "secret" } }));
var decoded = CounterState.DecodeArguments(new object[] { new Dictionary<string, object> { ["Key"] = "Id", ["Value"] = "private-id" } });
Check(decoded["Id"] == "private-id");
using var bytes = new BudgetStream(new MemoryStream([1, 2, 3]), 2, CancellationToken.None);
Reject(() => bytes.ReadExactly(new byte[3]));
// Decoder mutations exercise rejected wire structures rather than supplied counters.
Reject(() => CounterState.DecodeArguments("private-unstructured-value"));
Reject(() => CounterState.DecodeArguments(new object[9]));
Reject(() => CounterState.DecodeArguments(new object[] { null! }));
Reject(() => CounterState.DecodeArguments(new object[] { new object() }));
Reject(() => CounterState.DecodeArguments(new object[] { new Dictionary<string, object> { ["Key"] = "Id" } }));
Reject(() => CounterState.DecodeArguments(new object[] { new Dictionary<string, object> { ["Key"] = 1, ["Value"] = "private" } }));
Reject(() => CounterState.DecodeArguments(new object[] { new Dictionary<string, object> { ["Key"] = "Id", ["Value"] = 1 } }));
Reject(() => CounterState.DecodeArguments(new object[] { new Dictionary<string, object> { ["Key"] = new string('k', 33), ["Value"] = "private" } }));
Reject(() => CounterState.DecodeArguments(new object[] { new Dictionary<string, object> { ["Key"] = "Id", ["Value"] = new string('v', 4097) } }));
Reject(() => CounterState.DecodeArguments(new object[]
{
    new Dictionary<string, object> { ["Key"] = "Id", ["Value"] = "private-a" },
    new Dictionary<string, object> { ["Key"] = "Id", ["Value"] = "private-b" }
}));
var nullProjection = CounterState.DecodeArguments(new object[] { new Dictionary<string, object> { ["Key"] = "Status", ["Value"] = null! } });
Check(nullProjection["Status"] == "");
// The two permitted self-describing bridge schemas have allocated runtime IDs.
Check(EventCollector.RequireBridgeSchema("NewDiagnosticListener", ["SourceName"]));
Check(!EventCollector.RequireBridgeSchema("Event", ["SourceName", "EventName", "Arguments"]));
Reject(() => EventCollector.RequireBridgeSchema("EventSourceMessage", ["message"]));
Reject(() => EventCollector.RequireBridgeSchema("Message", ["Message"]));
Reject(() => EventCollector.RequireBridgeSchema("Version", ["Major", "Minor", "Patch"]));
Reject(() => EventCollector.RequireBridgeSchema("EventJson", ["SourceName", "EventName", "ArgmentsJson"]));
Reject(() => EventCollector.RequireBridgeSchema("Activity1Start", ["SourceName", "EventName", "Arguments"]));
Reject(() => EventCollector.RequireBridgeSchema("", ["SourceName", "EventName", "Arguments"]));
Reject(() => EventCollector.RequireBridgeSchema("event", ["SourceName", "EventName", "Arguments"]));
Reject(() => EventCollector.RequireBridgeSchema("Event", ["EventName", "SourceName", "Arguments"]));
Reject(() => EventCollector.RequireBridgeSchema("Event", ["SourceName", "EventName"]));
Reject(() => EventCollector.RequireBridgeSchema("Event", ["SourceName", "EventName", "Arguments", "PrivateExtra"]));
Reject(() => EventCollector.RequireBridgeSchema("Event", ["SourceName", "SourceName", "Arguments"]));
Reject(() => EventCollector.RequireBridgeSchema("NewDiagnosticListener", ["SourceName", "PrivateExtra"]));
// Actual failed window predicates produce only their fixed category; success clears prior failure.
void WindowReject(CounterState state, WindowFailureCategory expected)
{
    bool rejected = false;
    try { state.FinalizeWindow(t.AddSeconds(2), t.AddSeconds(10), TimeSpan.FromMilliseconds(250)); }
    catch (InvalidDataException) { rejected = true; }
    Check(rejected && state.WindowFailure == expected);
}
CounterState PrerequisiteState(bool heartbeat, bool health, bool incoming, bool outgoing)
{
    var state = new CounterState("Accounting", document, file);
    if (heartbeat) state.Heartbeat();
    if (incoming) state.Listener("Microsoft.AspNetCore");
    if (outgoing) state.Listener("HttpHandlerDiagnosticListener");
    if (health)
    {
        state.Record("Microsoft.AspNetCore", "Microsoft.AspNetCore.Hosting.BeginRequest", new() { ["Id"] = "synthetic-health", ["Method"] = "GET", ["Path"] = "/health" }, t);
        state.Record("Microsoft.AspNetCore", "Microsoft.AspNetCore.Hosting.EndRequest", new() { ["Id"] = "synthetic-health", ["Status"] = "200" }, t.AddSeconds(1));
    }
    return state;
}
WindowReject(duplicate, WindowFailureCategory.PendingRequests);
WindowReject(PrerequisiteState(false, true, true, true), WindowFailureCategory.MissingHeartbeat);
WindowReject(PrerequisiteState(true, false, true, true), WindowFailureCategory.MissingHealthCanary);
WindowReject(PrerequisiteState(true, true, false, true), WindowFailureCategory.MissingIncomingListener);
WindowReject(PrerequisiteState(true, true, true, false), WindowFailureCategory.OutgoingListener);
WindowReject(crossing, WindowFailureCategory.BoundarySpan);
WindowReject(guarded, WindowFailureCategory.GuardInterval);
_ = guarded.FinalizeWindow(t.AddSeconds(2), t.AddSeconds(20), TimeSpan.FromMilliseconds(250));
Check(guarded.WindowFailure == WindowFailureCategory.None);
Console.WriteLine(JsonSerializer.Serialize(new { ControlledCasesPassed = controls, HostedEventPipeWitness = false, GenuineEightHostFinancialAccepted = false }));
