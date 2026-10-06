using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
using Legacy.Maliev.QuotationService.Api.Controllers;
using Legacy.Maliev.QuotationService.Application.Models;

namespace QualificationOutcomeWireSource;

internal static class Program
{
    private static async Task Main(string[] args)
    {
        if (args is not [var suppliedRepository] || !OperatingSystem.IsLinux()
            || Environment.GetEnvironmentVariable("GITHUB_ACTIONS") != "true")
            throw new InvalidDataException("Dedicated normal hosted source-wire step required.");
        var repository = Path.GetFullPath(suppliedRepository);
        if (repository != Path.GetFullPath(Environment.GetEnvironmentVariable("GITHUB_WORKSPACE") ?? ""))
            throw new InvalidDataException("Hosted workspace differs.");
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var token = deadline.Token;
        var head = await GitAsync(repository, "HEAD", token);
        var tree = await GitAsync(repository, "HEAD^{tree}", token);
        if (!Regex.IsMatch(head, "^[0-9a-f]{40}$") || !Regex.IsMatch(tree, "^[0-9a-f]{40}$")
            || head != Environment.GetEnvironmentVariable("GITHUB_SHA"))
            throw new InvalidDataException("Actual hosted checkout identity differs.");
        var runId = Environment.GetEnvironmentVariable("GITHUB_RUN_ID") ?? "";
        var attempt = Environment.GetEnvironmentVariable("GITHUB_RUN_ATTEMPT") ?? "";
        if (!Regex.IsMatch(runId, "^[1-9][0-9]{0,19}$") || !Regex.IsMatch(attempt, "^[1-9][0-9]{0,8}$"))
            throw new InvalidDataException("Hosted run/attempt missing.");
        var output = Path.Combine(repository, "TestResults", "QualificationWire");
        Directory.CreateDirectory(output);
        var cases = new List<object>();
        foreach (var name in new[] { "empty", "mixed" })
        {
            var emission = await QualificationOutcomeWire.RenderAsync(name, token);
            if (emission.StatusCode != 200 || !emission.CamelCase
                || emission.IgnoreCondition != System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)
                throw new InvalidDataException("Actual controller serialization contract differs.");
            var file = name + ".json";
            await CreateAsync(Path.Combine(output, file), emission.Bytes, token);
            cases.Add(new
            {
                caseName = name,
                path = file,
                sha256 = Hash(emission.Bytes),
                length = emission.Bytes.Length,
                emission.StatusCode,
                emission.ContentType,
                emission.CamelCase,
                ignoreCondition = emission.IgnoreCondition.ToString(),
                emission.ActualDtoType,
                emission.ActualMvcExecutorType,
            });
        }
        var sourcePaths = new[]
        {
            "Legacy.Maliev.QuotationService.Application/Models/QuotationModels.cs",
            "Legacy.Maliev.QuotationService.Api/Controllers/QuotationRequestsController.cs",
            "Legacy.Maliev.QuotationService.Api/Program.cs",
            "tools/QualificationOutcomeWireSource/QualificationOutcomeWire.cs",
            "tools/QualificationOutcomeWireSource/Program.cs",
            "tools/QualificationOutcomeWireSource/ChildStartObservation.cs",
            "Legacy.Maliev.QuotationService.Tests/Controllers/QualificationOutcomeWireSourceTests.cs",
        };
        var sources = new List<object>();
        foreach (var path in sourcePaths)
            sources.Add(new { path, sha256 = Hash(await File.ReadAllBytesAsync(Path.Combine(repository, path), token)) });
        var assemblies = new List<object>();
        foreach (var type in new[] { typeof(QualificationOutcomeReadback), typeof(QuotationRequestsController), typeof(QualificationOutcomeWire) })
        {
            var assembly = type.Assembly;
            assemblies.Add(new { name = assembly.GetName().Name, sha256 = Hash(await File.ReadAllBytesAsync(assembly.Location, token)) });
        }
        var trx = "qualification-wire.trx";
        var receipt = new
        {
            schemaVersion = 1,
            producer = "Legacy.Maliev.QuotationService",
            head,
            tree,
            runId,
            runAttempt = int.Parse(attempt, System.Globalization.CultureInfo.InvariantCulture),
            boundary = "actual DTO + actual controller JsonResult + MVC executor; synthetic service; no routing/authentication/database proof",
            cases,
            sources,
            assemblies,
            nativeResults = new { path = trx, sha256 = Hash(await File.ReadAllBytesAsync(Path.Combine(output, trx), token)) },
        };
        await CreateAsync(Path.Combine(output, "qualification-wire-receipt.json"),
            JsonSerializer.SerializeToUtf8Bytes(receipt, new JsonSerializerOptions(JsonSerializerDefaults.Web) { WriteIndented = true }), token);
        Console.WriteLine("Actual qualification DTO/action/MVC wire evidence retained for two synthetic cases.");
    }

    private static string Hash(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));

    private static async Task CreateAsync(string path, byte[] bytes, CancellationToken token)
    {
        await using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, true);
        await stream.WriteAsync(bytes, token);
    }

    private static async Task<string> GitAsync(string repository, string revision, CancellationToken token)
    {
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo("git")
            {
                WorkingDirectory = repository,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            },
        };
        foreach (var argument in new[] { "rev-parse", "--verify", revision }) process.StartInfo.ArgumentList.Add(argument);
        if (!process.Start()) throw new InvalidDataException("Source identity process did not start.");
        DateTime? started = null;
        try
        {
            started = ChildStartObservation.Capture(() => process.StartTime.ToUniversalTime(), () => process.HasExited);
            var output = process.StandardOutput.ReadToEndAsync(token);
            var error = process.StandardError.ReadToEndAsync(token);
            await process.WaitForExitAsync(token);
            await error;
            if (process.ExitCode != 0) throw new InvalidDataException("Source identity could not be observed.");
            return (await output).Trim();
        }
        finally
        {
            process.Refresh();
            if (!process.HasExited)
            {
                if (started is null || process.StartTime.ToUniversalTime() != started.Value)
                    throw new InvalidDataException("Source identity helper ownership changed.");
                process.Kill();
                using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                await process.WaitForExitAsync(cleanup.Token);
                if (!process.HasExited) throw new InvalidDataException("Source identity helper cleanup failed.");
            }
        }
    }
}
