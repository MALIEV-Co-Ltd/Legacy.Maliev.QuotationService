using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Npgsql;

namespace Legacy.Maliev.QuotationService.Tests.Diagnostics;

/// <summary>Actual executable startup failures before any database or remote client can run.</summary>
public sealed class QuotationStartupProcessTests
{
    [Theory]
    [InlineData("missing-issuer", "InvalidOperationException")]
    [InlineData("invalid-base64", "FormatException")]
    [InlineData("invalid-pem", "ArgumentException")]
    public async Task Production_bootstrap_failure_emits_one_safe_critical_event_and_exit_one(
        string scenario, string expectedExceptionType)
    {
        var root = FindRepository();
        var apiDirectory = Path.Combine(root, "Legacy.Maliev.QuotationService.Api", "bin", "Release", "net10.0");
        var api = Path.Combine(apiDirectory, "Legacy.Maliev.QuotationService.Api.dll");
        Assert.True(File.Exists(api), "The Release API must be built before its executable startup proof.");
        Assert.True(File.Exists(Path.ChangeExtension(api, ".runtimeconfig.json")));
        var marker = "synthetic-startup-" + Guid.NewGuid().ToString("N");
        var connection = new NpgsqlConnectionStringBuilder
        {
            Host = "127.0.0.1", Port = 1, Database = "startup", Username = "startup",
            Password = Guid.NewGuid().ToString("N"),
        }.ConnectionString;
        var start = new ProcessStartInfo(Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") ?? "dotnet")
        {
            WorkingDirectory = apiDirectory,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        start.ArgumentList.Add(api);
        // Inherit only runtime necessities; never inherit provider or service credentials.
        start.Environment.Clear();
        foreach (var name in new[] { "PATH", "DOTNET_ROOT", "DOTNET_ROOT_X64", "HOME", "TMPDIR", "TEMP", "TMP", "SystemRoot", "WINDIR", "LANG", "LC_ALL" })
        {
            var value = Environment.GetEnvironmentVariable(name);
            if (value is not null) start.Environment[name] = value;
        }
        foreach (var setting in new Dictionary<string, string>
        {
            ["ASPNETCORE_ENVIRONMENT"] = "Production",
            ["DOTNET_ENVIRONMENT"] = "Production",
            ["ConnectionStrings__QuotationDbContext"] = connection,
            ["ConnectionStrings__QuotationRequestDbContext"] = connection,
            ["ConnectionStrings__redis"] = "127.0.0.1:1",
            ["Cache__RedisEnabled"] = "false",
            ["Jwt__Issuer"] = scenario == "missing-issuer" ? "" : "https://startup.example",
            ["Jwt__Audience"] = "startup-services",
            ["Jwt__SecurityKey"] = "",
            ["Jwt__PublicKey"] = scenario == "invalid-base64" ? marker : Convert.ToBase64String(Encoding.UTF8.GetBytes(marker)),
            ["OTEL_EXPORTER_OTLP_ENDPOINT"] = "",
            ["Observability__RuntimeMetricsEnabled"] = "false",
        }) start.Environment[setting.Key] = setting.Value;

        using var process = Process.Start(start)!;
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        string stdout;
        string stderr;
        try
        {
            var output = ReadBoundedAsync(process.StandardOutput, deadline.Token);
            var error = ReadBoundedAsync(process.StandardError, deadline.Token);
            await Task.WhenAll(output, error, process.WaitForExitAsync(deadline.Token));
            stdout = await output;
            stderr = await error;
        }
        finally
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                await process.WaitForExitAsync(cleanup.Token);
            }
        }
        if (process.ExitCode != 1)
        {
            // An unrelated setup/configuration failure is not the intended behavioral RED.
            var type = Regex.Match(stderr, @"Unhandled exception\. (?<type>[\w.]+)").Groups["type"].Value;
            var frames = Regex.Matches(stderr, @"^\s+at (?<method>[^\(\r\n]+)", RegexOptions.Multiline)
                .Select(match => match.Groups["method"].Value).Take(5);
            var boundary = $"Actual exception type={type}; methods={string.Join(", ", frames)}";
            Assert.True(stderr.Contains(expectedExceptionType, StringComparison.Ordinal), boundary);
            Assert.True(stderr.Contains("AddJwtAuthentication", StringComparison.Ordinal), boundary);
        }
        Assert.Equal(1, process.ExitCode);
        Assert.DoesNotContain(marker, stdout + stderr, StringComparison.Ordinal);
        Assert.DoesNotContain("Unhandled exception", stdout + stderr, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("   at ", stdout + stderr, StringComparison.Ordinal);
        var records = new List<JsonElement>();
        foreach (var line in stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            try
            {
                using var document = JsonDocument.Parse(line);
                if (document.RootElement.TryGetProperty("EventId", out var id) && id.GetInt32() == 5102)
                    records.Add(document.RootElement.Clone());
            }
            catch (JsonException) { }
        }
        var failure = Assert.Single(records);
        Assert.Equal("CRITICAL", failure.GetProperty("severity").GetString());
        var state = failure.GetProperty("State");
        Assert.Equal("StartupFailure", state.GetProperty("EventName").GetString());
        Assert.Equal("HostInitialization", state.GetProperty("Operation").GetString());
        Assert.Equal(expectedExceptionType, state.GetProperty("ExceptionType").GetString());
        Assert.Equal(JsonValueKind.Null, failure.GetProperty("Exception").ValueKind);
    }

    private static async Task<string> ReadBoundedAsync(StreamReader reader, CancellationToken cancellationToken)
    {
        var text = new StringBuilder();
        var buffer = new char[4096];
        int count;
        while ((count = await reader.ReadAsync(buffer.AsMemory(), cancellationToken)) > 0)
        {
            if (text.Length + count > 64 * 1024) throw new InvalidOperationException("Owned startup process output exceeded its bound.");
            text.Append(buffer, 0, count);
        }
        return text.ToString();
    }

    private static string FindRepository()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
            if (File.Exists(Path.Combine(directory.FullName, "Legacy.Maliev.QuotationService.slnx"))) return directory.FullName;
        throw new InvalidOperationException("Quotation repository was not found.");
    }
}
