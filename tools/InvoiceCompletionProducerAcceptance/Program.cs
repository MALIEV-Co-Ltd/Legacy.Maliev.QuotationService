using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Legacy.Maliev.QuotationService.Application.Models;
using Npgsql;
using InvoiceCompletionProducerAcceptance;

// Hosted-only real-producer driver. It never seeds/migrates/starts or substitutes a producer.
using var lifetime = new CancellationTokenSource(TimeSpan.FromMinutes(3));
var gitResources = new List<GitResource>();
var checks = new List<string>();
FixtureProfile? profile = null;
try
{
    if (!OperatingSystem.IsLinux() || Environment.GetEnvironmentVariable("GITHUB_ACTIONS") != "true")
        throw new InvalidOperationException("Hosted Ubuntu fixture required.");
    var profilePath = Path.GetFullPath(args.Single());
    var ownedProfiles = Path.GetFullPath("TestResults/C821ProducerProfiles") + Path.DirectorySeparatorChar;
    if (!profilePath.StartsWith(ownedProfiles, StringComparison.Ordinal) || new FileInfo(profilePath).Length > 65536)
        throw new InvalidOperationException("Bounded owned profile required.");
    profile = JsonSerializer.Deserialize<FixtureProfile>(await File.ReadAllTextAsync(profilePath, lifetime.Token))
        ?? throw new InvalidDataException();
    var fixture = profile ?? throw new InvalidDataException();
    if (!fixture.RunId.StartsWith("c821-", StringComparison.Ordinal)
        || !Guid.TryParseExact(fixture.RunId[5..], "D", out var run) || run == Guid.Empty
        || fixture.RunId != "c821-" + run.ToString("D")
        || fixture.ExpiresUtc <= DateTimeOffset.UtcNow || fixture.ExpiresUtc > DateTimeOffset.UtcNow.AddMinutes(30)
        || fixture.QuotationId <= 0 || fixture.OrderIds.Length == 0 || fixture.OrderIds.Length > 16
        || fixture.OrderIds.Any(id => id <= 0) || fixture.OrderIds.Distinct().Count() != fixture.OrderIds.Length
        || fixture.ExpectedInvoiceItems <= 0 || fixture.ExpectedInvoiceItems > 64
        || !fixture.InvoiceIntent.TryGetProperty("SendEmail", out var sendEmail) || sendEmail.ValueKind != JsonValueKind.False)
        throw new InvalidDataException("Invalid disposable scenario.");
    foreach (var origin in new[] { fixture.AuthOrigin, fixture.AccountingOrigin, fixture.QuotationOrigin, fixture.OrderOrigin, fixture.IamOrigin })
        if (origin.Scheme != "https" || !origin.IsLoopback || origin.AbsolutePath != "/"
            || origin.Query.Length != 0 || origin.Fragment.Length != 0 || origin.UserInfo.Length != 0)
            throw new InvalidOperationException("HTTPS loopback fixture origin required.");
    if (fixture.Sources.Length != 5 || !fixture.Sources.Select(source => source.Owner).ToHashSet(StringComparer.Ordinal)
            .SetEquals(["Auth", "Accounting", "Quotation", "Order", "IAM"])) throw new InvalidDataException();
    foreach (var source in fixture.Sources)
    {
        if (!Regex.IsMatch(source.Commit, "^[a-f0-9]{40}$") || !Regex.IsMatch(source.Tree, "^[a-f0-9]{40}$"))
            throw new InvalidDataException();
        if (await Git(source.Repository, "rev-parse", "HEAD") != source.Commit
            || await Git(source.Repository, "rev-parse", "HEAD^{tree}") != source.Tree
            || await Git(source.Repository, "status", "--porcelain", "--untracked-files=normal") != "")
            throw new InvalidOperationException("Exact clean owner input required.");
    }
    var databases = new Dictionary<string, string>();
    foreach (var name in ProducerDatabaseBindings.Roles())
    {
        var connection = new NpgsqlConnectionStringBuilder(Secret("C821_DB_" + name));
        if (connection.Host is not ("127.0.0.1" or "localhost" or "::1")
            || connection.Database is null || !connection.Database.StartsWith("c821_", StringComparison.Ordinal)
            || !connection.Database.Contains(run.ToString("N"), StringComparison.Ordinal)) throw new InvalidOperationException();
        connection.Pooling = false;
        connection.Timeout = 10;
        connection.CommandTimeout = 15;
        databases.Add(name, connection.ConnectionString);
        var version = await Scalar<int>(connection.ConnectionString, "SELECT current_setting('server_version_num')::int");
        if (version is < 180000 or >= 190000) throw new InvalidOperationException("PostgreSQL18 required.");
    }
    ProducerDatabaseBindings.ValidateSeparateDatabases(databases);
    foreach (var source in fixture.Sources)
    {
        ProducerDatabaseBindings.Validate(source.Owner, source.DatabaseBindings);
        using var process = Process.GetProcessById(source.ProcessId);
        if (process.HasExited || Math.Abs((process.StartTime.ToUniversalTime() - source.ActualStartUtc.UtcDateTime).TotalMilliseconds) > 1)
            throw new InvalidOperationException("Owner process identity changed.");
        var commandBytes = await File.ReadAllBytesAsync($"/proc/{source.ProcessId}/cmdline", lifetime.Token);
        var environmentBytes = await File.ReadAllBytesAsync($"/proc/{source.ProcessId}/environ", lifetime.Token);
        if (commandBytes.Length > 65536 || environmentBytes.Length > 262144) throw new InvalidDataException();
        var command = Encoding.UTF8.GetString(commandBytes).Split('\0', StringSplitOptions.RemoveEmptyEntries);
        var environment = Encoding.UTF8.GetString(environmentBytes).Split('\0', StringSplitOptions.RemoveEmptyEntries)
            .Select(value => value.Split('=', 2)).ToDictionary(value => value[0], value => value.Length == 2 ? value[1] : "", StringComparer.Ordinal);
        ProducerDatabaseBindings.ValidateLaunchArguments(command, source.ExecutableDll, process.MainModule?.FileName ?? throw new InvalidDataException());
        if (environment.GetValueOrDefault("ASPNETCORE_ENVIRONMENT") != "Production"
            || environment.TryGetValue("DOTNET_ENVIRONMENT", out var dotnetEnvironment) && dotnetEnvironment != "Production"
            || environment.GetValueOrDefault("C821_FIXTURE_RUN_ID") != fixture.RunId
            || !DateTimeOffset.TryParse(environment.GetValueOrDefault("C821_FIXTURE_EXPIRES_UTC"), CultureInfo.InvariantCulture,
                DateTimeStyles.RoundtripKind, out var expiry) || expiry != fixture.ExpiresUtc
            || Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(source.ExecutableDll, lifetime.Token))) != source.DllSha256)
            throw new InvalidOperationException("Normal finite owned producer process required.");
        var origins = new Dictionary<string, Uri> { ["Auth"] = fixture.AuthOrigin, ["Accounting"] = fixture.AccountingOrigin,
            ["Quotation"] = fixture.QuotationOrigin, ["Order"] = fixture.OrderOrigin, ["IAM"] = fixture.IamOrigin };
        foreach (var binding in source.ServiceBindings)
            if (!binding.Key.StartsWith("Services__", StringComparison.Ordinal) || !origins.TryGetValue(binding.Value, out var expectedOrigin)
                || environment.GetValueOrDefault(binding.Key) != expectedOrigin.AbsoluteUri)
                throw new InvalidOperationException("Actual producer service origin isolation mismatch.");
        if (source.Owner == "Quotation"
            && (environment.GetValueOrDefault("Services__Order__BaseUrl") != fixture.OrderOrigin.AbsoluteUri
                || environment.GetValueOrDefault("Services__Accounting__BaseUrl") != fixture.AccountingOrigin.AbsoluteUri
                || environment.GetValueOrDefault("QuotationInvoiceCompletion__Enabled") != "true"
                || environment.GetValueOrDefault("MALIEV_OBSERVABILITY_STANDBY") != "true"
                || environment.GetValueOrDefault("Services__Auth__BaseUrl") != fixture.AuthOrigin.AbsoluteUri
                || environment.GetValueOrDefault("Services__IAMService__BaseUrl") != fixture.IamOrigin.AbsoluteUri
                || environment.GetValueOrDefault("Features__ResourceScopedAuthEnabled") != "true"
                || environment.GetValueOrDefault("Features__AllowExactServiceClaimsForLiveCheck") != "false"
                || environment.GetValueOrDefault("Features__FailOpenOnIAMError") != "false"))
            throw new InvalidOperationException("Quotation writer dependency isolation/standby required.");
        ProducerDatabaseBindings.ValidateEnvironment(source.Owner, source.DatabaseBindings, environment, databases);
    }
    lifetime.CancelAfter(TimeSpan.FromSeconds(Math.Min(180, (fixture.ExpiresUtc - DateTimeOffset.UtcNow).TotalSeconds)));
    checks.Add("Exact owner inputs, actual finite Production processes and separate disposable PG18 backends admitted");
    using var auth = Client(fixture.AuthOrigin);
    using var accounting = Client(fixture.AccountingOrigin);
    using var quotation = Client(fixture.QuotationOrigin);
    var employee = await Token(auth, "/auth/v1/login", new { UserName = Secret("C821_EMPLOYEE_USERNAME"), Password = Secret("C821_EMPLOYEE_PASSWORD"), IdentityKind = 1 });
    var employeeSubject = JwtPayload(employee).GetProperty("sub").GetString() ?? throw new InvalidDataException();
    var intranet = await Token(auth, "/auth/v1/service/login", new { ClientId = "legacy-intranet", ClientSecret = Secret("C821_INTR_SERVICE_SECRET") });
    var executor = await Token(auth, "/auth/v1/service/login", new { ClientId = "legacy-accounting", ClientSecret = Secret("C821_ACC_SERVICE_SECRET") });
    if (await Scalar<int>(databases["AUTH_SESSIONS"], "SELECT COUNT(*)::int FROM refresh_sessions WHERE \"IdentityId\"=@employee AND \"RevokedAt\" IS NULL", ("employee", employeeSubject)) < 1)
        throw new InvalidDataException("Actual current employee session missing.");
    var operation = Guid.NewGuid();
    var delegation = await Token(auth, "/auth/v1/exchange/invoice-create",
        new { EmployeeAccessToken = employee, fixture.QuotationId, OperationId = operation.ToString("D") }, intranet);
    using var prepare = new HttpRequestMessage(HttpMethod.Post, $"/invoices/from-quotation/{fixture.QuotationId}/prepare")
    {
        Content = JsonContent.Create(fixture.InvoiceIntent),
    };
    prepare.Headers.Authorization = new("Bearer", intranet);
    prepare.Headers.Add("Idempotency-Key", operation.ToString("D"));
    prepare.Headers.Add("X-Maliev-Employee-Delegation", "Bearer " + delegation);
    var financial = await Document(accounting, prepare);
    var invoice = financial.GetProperty("InvoiceId").GetInt32();
    var originalVersion = financial.GetProperty("OriginalQuotationVersion").GetString() ?? throw new InvalidDataException();
    if (financial.GetProperty("OperationId").GetString() != operation.ToString("D")
        || financial.GetProperty("QuotationId").GetInt32() != fixture.QuotationId || invoice <= 0
        || financial.GetProperty("EmployeeSubject").GetString() != employeeSubject) throw new InvalidDataException();
    if (await Scalar<int>(databases["INVOICE"], "SELECT COUNT(*)::int FROM \"Invoice\" WHERE \"ID\"=@invoice", ("invoice", invoice)) != 1
        || await Scalar<int>(databases["INVOICE"], "SELECT COUNT(*)::int FROM \"OrderItem\" WHERE \"InvoiceID\"=@invoice", ("invoice", invoice)) != fixture.ExpectedInvoiceItems
        || await Scalar<int>(databases["INVOICE"], "SELECT COUNT(*)::int FROM \"InvoiceCreationAdmission\" WHERE \"OperationID\"=@operation AND \"QuotationID\"=@quote AND \"OriginIssuer\"=@issuer AND \"EmployeeSubject\"=@employee AND \"FinancialOwnershipJson\" IS NOT NULL",
            ("operation", operation), ("quote", fixture.QuotationId), ("issuer", financial.GetProperty("OriginIssuer").GetString()!), ("employee", employeeSubject)) != 1)
        throw new InvalidDataException("Actual committed financial rows/admission missing.");
    checks.Add("Actual Auth login/session and original delegation; Accounting atomic prepared invoice/items/admission read back");
    var originalOrders = new Dictionary<int, JsonElement>();
    foreach (var id in fixture.OrderIds)
        originalOrders.Add(id, await OrderRow(id));
    var capability = await BoundCapability();
    var claims = JwtPayload(capability);
    if (claims.EnumerateObject().Count() != 16 || claims.EnumerateObject().Select(value => value.Name).Distinct().Count() != 16
        || claims.GetProperty("invoice_id").GetString() != invoice.ToString(CultureInfo.InvariantCulture)
        || claims.GetProperty("quotation_version").GetString() != originalVersion
        || claims.GetProperty("financial_binding").GetString() != financial.GetProperty("FinancialBinding").GetString()
        || claims.GetProperty("operation_id").GetString() != operation.ToString("D")) throw new InvalidDataException();
    using (var decision = Decision(capability))
    using (var discardedAcknowledgement = await quotation.SendAsync(decision, HttpCompletionOption.ResponseHeadersRead, lifetime.Token))
    {
        // Deliberately discard the response body. Only fresh owning readback can acknowledge completion.
    }
    var completed = await ReadReceipt(capability);
    if (completed.State != "Completed" || completed.CompletedOrders != fixture.OrderIds.Length
        || completed.TotalOrders != fixture.OrderIds.Length || completed.InvoiceId != invoice
        || completed.OperationId != operation.ToString("D") || completed.QuotationId != fixture.QuotationId
        || completed.OriginalQuotationVersion != originalVersion || completed.FinancialBinding != financial.GetProperty("FinancialBinding").GetString())
        throw new InvalidDataException("Owning completion not established.");
    if (await Scalar<int>(databases["QUOTATION"], "SELECT COUNT(*)::int FROM \"QuotationInvoiceCompletionOperation\" WHERE \"OperationId\"=@operation AND \"State\"='Completed' AND \"InvoiceId\"=@invoice", ("operation", operation), ("invoice", invoice)) != 1)
        throw new InvalidDataException();
    foreach (var id in fixture.OrderIds)
    {
        var after = await OrderRow(id);
        if (after.GetProperty("AllowCancellation").GetBoolean()) throw new InvalidDataException();
        var initial = originalOrders[id];
        var priorPromise = initial.GetProperty("PromisedDate");
        if (priorPromise.ValueKind != JsonValueKind.Null && after.GetProperty("PromisedDate").GetString() != priorPromise.GetString())
            throw new InvalidDataException("Existing promise was overwritten.");
        if (priorPromise.ValueKind == JsonValueKind.Null && initial.GetProperty("LeadTime").ValueKind != JsonValueKind.Null
            && after.GetProperty("PromisedDate").ValueKind == JsonValueKind.Null) throw new InvalidDataException("Missing promise did not converge.");
        if (await Scalar<int>(databases["ORDER_STATUS"], "SELECT COUNT(*)::int FROM \"OrderStatusHistory\" h JOIN \"OrderStatus\" s ON s.\"ID\"=h.\"OrderStatusID\" WHERE h.\"OrderID\"=@id AND lower(s.\"Name\")='accepted'", ("id", id)) != 1)
            throw new InvalidDataException("Actual accepted OrderStatus history missing/duplicated.");
    }
    checks.Add("Actual invoice-bound Auth issuer; independently verified Quotation owning Completed receipt; both Order and Status DB effects");
    var snapshot = await PersistentFingerprint();
    var fresh = await BoundCapability();
    if (JwtPayload(fresh).GetProperty("jti").GetString() == claims.GetProperty("jti").GetString()) throw new InvalidDataException();
    using (var replay = Decision(fresh))
    using (var replayResponse = await quotation.SendAsync(replay, lifetime.Token))
        if (replayResponse.StatusCode != HttpStatusCode.OK) throw new InvalidDataException();
    if (await ReadReceipt(fresh) != completed || await PersistentFingerprint() != snapshot) throw new InvalidDataException("Replay changed retained/persisted effects.");
    var unbound = await Token(auth, "/auth/v1/exchange/quotation-invoice-completion",
        new { EmployeeAccessToken = employee, fixture.QuotationId, OperationId = operation.ToString("D") }, intranet);
    using (var rejected = ReceiptRequest(unbound))
    using (var rejection = await quotation.SendAsync(rejected, lifetime.Token))
        if (rejection.StatusCode != HttpStatusCode.Forbidden) throw new InvalidDataException("Old unbound capability was adopted.");
    if (await PersistentFingerprint() != snapshot) throw new InvalidDataException();
    checks.Add("Genuine fresh remint has new nonce; completed replay retains receipt/SQL effects; genuine old unbound proof rejected");
    if (gitResources.Any(resource => !resource.Exited)) throw new InvalidOperationException("Owned preflight resource cleanup incomplete.");
    await WriteReceipt(true, null);

    async Task<string> BoundCapability() => await Token(auth, "/auth/v1/exchange/quotation-invoice-completion",
        new { EmployeeAccessToken = employee, fixture.QuotationId, OperationId = operation.ToString("D"), InvoiceId = invoice }, intranet);
    HttpRequestMessage ReceiptRequest(string proof) => BoundRequest(HttpMethod.Get,
        $"/quotations/{fixture.QuotationId}/invoice-completion/operations/{operation:D}?invoiceId={invoice}", proof);
    HttpRequestMessage Decision(string proof)
    {
        var request = BoundRequest(HttpMethod.Put, $"/quotations/{fixture.QuotationId}/decision", proof);
        request.Content = JsonContent.Create(new { Accepted = true, EmployeeInitiated = true, InvoiceId = invoice });
        return request;
    }
    HttpRequestMessage BoundRequest(HttpMethod method, string route, string proof)
    {
        var request = new HttpRequestMessage(method, route);
        request.Headers.Authorization = new("Bearer", executor);
        request.Headers.Add("X-Maliev-Quotation-Invoice-Capability", "Bearer " + proof);
        request.Headers.Add("Idempotency-Key", operation.ToString("D"));
        request.Headers.Add("X-Expected-Modified-Date", originalVersion);
        return request;
    }
    async Task<QuotationInvoiceCompletionReceipt> ReadReceipt(string proof)
    {
        using var request = ReceiptRequest(proof);
        using var response = await quotation.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, lifetime.Token);
        if (response.StatusCode != HttpStatusCode.OK || response.Headers.CacheControl?.NoStore != true) throw new InvalidDataException();
        var receipt = await Body(response);
        if (receipt.ValueKind != JsonValueKind.Object || receipt.EnumerateObject().Count() != 16
            || receipt.EnumerateObject().Select(value => value.Name).Distinct().Count() != 16) throw new InvalidDataException();
        return receipt.Deserialize<QuotationInvoiceCompletionReceipt>() ?? throw new InvalidDataException();
    }
    async Task<JsonElement> OrderRow(int id)
    {
        using var json = JsonDocument.Parse(await Scalar<string>(databases["ORDER"],
            "SELECT row_to_json(o)::text FROM \"Order\" o WHERE \"ID\"=@id", ("id", id)));
        return json.RootElement.Clone();
    }
    async Task<string> PersistentFingerprint()
    {
        var parts = new List<string>
        {
            await Scalar<string>(databases["INVOICE"], "SELECT row_to_json(i)::text FROM \"Invoice\" i WHERE \"ID\"=@invoice", ("invoice", invoice)),
            await Scalar<string>(databases["INVOICE"], "SELECT COALESCE(json_agg(i ORDER BY \"ID\"),'[]'::json)::text FROM \"OrderItem\" i WHERE \"InvoiceID\"=@invoice", ("invoice", invoice)),
            await Scalar<string>(databases["QUOTATION"], "SELECT row_to_json(q)::text FROM \"Quotation\" q WHERE \"ID\"=@quote", ("quote", fixture.QuotationId)),
        };
        foreach (var id in fixture.OrderIds.OrderBy(value => value))
        {
            parts.Add((await OrderRow(id)).GetRawText());
            parts.Add(await Scalar<string>(databases["ORDER_STATUS"], "SELECT COALESCE(json_agg(h ORDER BY \"ID\"),'[]'::json)::text FROM \"OrderStatusHistory\" h WHERE \"OrderID\"=@id", ("id", id)));
        }
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(parts))));
    }
}
catch (Exception exception)
{
    await WriteReceipt(false, exception.GetType().Name);
    Environment.ExitCode = 1;
}

async Task WriteReceipt(bool passed, string? failureKind)
{
    var run = profile?.RunId;
    if (run is null || !Regex.IsMatch(run, "^c821-[a-f0-9-]{36}$")) run = "c821-invalid-profile";
    var directory = Path.Combine("TestResults", "C821ProducerJoin", run, Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(directory);
    await File.WriteAllTextAsync(Path.Combine(directory, "receipt.json"), JsonSerializer.Serialize(new
    {
        Passed = passed, FailureKind = failureKind, Checks = checks, SourceInputs = profile?.Sources.Select(source => new { source.Owner, source.Commit, source.Tree }),
        ObservedUtc = DateTimeOffset.UtcNow, GitResources = gitResources,
        Limits = "Real boundary driver; supplied owner fixture cleanup remains with its launcher. Accounting full resume/document/notification and Intranet UI are separate. SQL stability alone does not prove zero downstream HTTP requests.",
    }, new JsonSerializerOptions { WriteIndented = true }));
    Console.WriteLine(passed ? "Actual producer boundary acceptance passed; opaque receipt retained." : "Actual producer boundary acceptance failed; opaque receipt retained.");
}

async Task<string> Git(string repository, params string[] arguments)
{
    using var process = new Process { StartInfo = new ProcessStartInfo("git") { WorkingDirectory = repository, UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true } };
    foreach (var argument in arguments) process.StartInfo.ArgumentList.Add(argument);
    process.Start();
    var started = process.StartTime.ToUniversalTime();
    var identity = process.MainModule?.FileName ?? "git";
    var position = gitResources.Count;
    gitResources.Add(new(process.Id, started, identity, false));
    using var deadline = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
    deadline.CancelAfter(TimeSpan.FromSeconds(10));
    var output = process.StandardOutput.ReadToEndAsync(deadline.Token);
    var errors = process.StandardError.ReadToEndAsync(deadline.Token);
    try
    {
        await process.WaitForExitAsync(deadline.Token);
        await errors;
        if (process.ExitCode != 0) throw new InvalidOperationException("Source preflight failed.");
        return (await output).Trim();
    }
    finally
    {
        try
        {
            if (!process.HasExited && process.StartTime.ToUniversalTime() == started)
            {
                try { process.CloseMainWindow(); } catch (InvalidOperationException) { } catch (PlatformNotSupportedException) { }
                if (!process.HasExited) process.Kill(entireProcessTree: false);
            }
            using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            await process.WaitForExitAsync(cleanup.Token);
            try { await Task.WhenAll(output, errors).WaitAsync(cleanup.Token); }
            catch (Exception) { _ = output.Exception; _ = errors.Exception; }
        }
        catch (Exception) { /* Exact owned resource remains recorded; successful acceptance is forbidden. */ }
        gitResources[position] = new(process.Id, started, identity, process.HasExited);
    }
}

async Task<T> Scalar<T>(string connection, string sql, params (string Name, object Value)[] values)
{
    await using var database = new NpgsqlConnection(connection);
    await database.OpenAsync(lifetime.Token);
    await using var command = new NpgsqlCommand(sql, database) { CommandTimeout = 15 };
    foreach (var value in values) command.Parameters.AddWithValue(value.Name, value.Value);
    var result = await command.ExecuteScalarAsync(lifetime.Token) ?? throw new InvalidDataException();
    if (result is string text && text.Length > 262144) throw new InvalidDataException();
    return (T)result;
}

async Task<string> Token(HttpClient client, string route, object body, string? bearer = null)
{
    using var request = new HttpRequestMessage(HttpMethod.Post, route) { Content = JsonContent.Create(body) };
    if (bearer is not null) request.Headers.Authorization = new("Bearer", bearer);
    var document = await Document(client, request);
    var properties = document.EnumerateObject().Where(value => value.Name.Equals("AccessToken", StringComparison.OrdinalIgnoreCase)).ToArray();
    return properties.Length == 1 && properties[0].Value.ValueKind == JsonValueKind.String
        ? properties[0].Value.GetString() ?? throw new InvalidDataException() : throw new InvalidDataException();
}

async Task<JsonElement> Document(HttpClient client, HttpRequestMessage request)
{
    using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, lifetime.Token);
    if (!response.IsSuccessStatusCode) throw new InvalidDataException("Actual producer rejected scenario.");
    return await Body(response);
}

async Task<JsonElement> Body(HttpResponseMessage response)
{
    await using var stream = await response.Content.ReadAsStreamAsync(lifetime.Token);
    using var bounded = new MemoryStream();
    var buffer = new byte[4096];
    while (true)
    {
        var read = await stream.ReadAsync(buffer, lifetime.Token);
        if (read == 0) break;
        if (bounded.Length + read > 65536) throw new InvalidDataException();
        bounded.Write(buffer, 0, read);
    }
    using var json = JsonDocument.Parse(bounded.ToArray());
    return json.RootElement.Clone();
}

static JsonElement JwtPayload(string compact)
{
    if (compact.Length > 16384 || compact.Any(char.IsWhiteSpace)) throw new InvalidDataException();
    var parts = compact.Split('.');
    if (parts.Length != 3) throw new InvalidDataException();
    var value = parts[1].Replace('-', '+').Replace('_', '/');
    value = value.PadRight((value.Length + 3) / 4 * 4, '=');
    using var json = JsonDocument.Parse(Convert.FromBase64String(value));
    return json.RootElement.Clone();
}

static HttpClient Client(Uri origin) => new(new SocketsHttpHandler { AllowAutoRedirect = false, UseCookies = false }) { BaseAddress = origin, Timeout = TimeSpan.FromSeconds(45) };
static string Secret(string name) => Environment.GetEnvironmentVariable(name) is { Length: > 0 } value ? value : throw new InvalidOperationException("Required private fixture input absent.");

sealed record FixtureProfile(string RunId, DateTimeOffset ExpiresUtc, Uri AuthOrigin, Uri AccountingOrigin,
    Uri QuotationOrigin, Uri OrderOrigin, Uri IamOrigin, int QuotationId, int[] OrderIds, int ExpectedInvoiceItems,
    JsonElement InvoiceIntent, SourceInput[] Sources);
sealed record SourceInput(string Owner, string Repository, string Commit, string Tree, int ProcessId,
    DateTimeOffset ActualStartUtc, string ExecutableDll, string DllSha256, Dictionary<string, string> DatabaseBindings, Dictionary<string, string> ServiceBindings);
sealed record GitResource(int ProcessId, DateTime ActualStartUtc, string Executable, bool Exited);
