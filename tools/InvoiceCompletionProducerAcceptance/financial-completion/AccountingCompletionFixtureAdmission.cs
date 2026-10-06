using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Text;
using InvoiceCompletionProducerAcceptance.Companion;

namespace InvoiceCompletionProducerAcceptance;

/// <summary>Read-only preflight for Accounting's exact outbound document/file/no-send fixture targets.</summary>
public static class AccountingCompletionFixtureAdmission
{
    /// <summary>Must run after core exact source, launch and all11 database isolation admission.</summary>
    public static async Task VerifyAsync(int processId, DateTimeOffset actualStartUtc, string runId,
        DateTimeOffset expiresUtc, Uri documentOrigin, Uri fileOrigin, Uri notificationOrigin,
        ObservedFileHost admittedFile, CancellationToken cancellationToken)
    {
        if (!OperatingSystem.IsLinux() || Environment.GetEnvironmentVariable("GITHUB_ACTIONS") != "true"
            || expiresUtc <= DateTimeOffset.UtcNow || expiresUtc > DateTimeOffset.UtcNow.AddMinutes(30))
            throw new InvalidOperationException("Current finite hosted fixture required.");
        ValidateTargetOrigins(documentOrigin, fileOrigin, notificationOrigin);
        if (admittedFile.RunId != runId || admittedFile.ExpiresUtc != expiresUtc)
            throw new InvalidDataException("File host must belong to the same exact admitted run lease.");
        using var process = Process.GetProcessById(processId);
        if (process.HasExited || Math.Abs((process.StartTime.ToUniversalTime() - actualStartUtc.UtcDateTime).TotalMilliseconds) > 1)
            throw new InvalidOperationException("Accounting process identity changed.");
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(5));
        var environment = Decode(await ReadBounded($"/proc/{processId}/environ", deadline.Token));
        if (environment.GetValueOrDefault("ASPNETCORE_ENVIRONMENT") != "Production"
            || environment.GetValueOrDefault("C821_FIXTURE_RUN_ID") != runId
            || !DateTimeOffset.TryParse(environment.GetValueOrDefault("C821_FIXTURE_EXPIRES_UTC"),
                CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var actualExpiry) || actualExpiry != expiresUtc)
            throw new InvalidOperationException("Accounting fixture lease differs.");
        ValidateOrigins(environment, documentOrigin, fileOrigin, notificationOrigin);
        await CompanionLauncherAdmission.VerifyProcessAsync("File", admittedFile.Pid, admittedFile.StartedUtc,
            admittedFile.ExecutableDll, admittedFile.ExecutableSha256.ToUpperInvariant(), runId, expiresUtc, deadline.Token);
        var fileEnvironment = Decode(await ReadBounded($"/proc/{admittedFile.Pid}/environ", deadline.Token));
        CompanionLauncherAdmission.RequireExact(fileEnvironment, "ASPNETCORE_URLS", fileOrigin.GetLeftPart(UriPartial.Authority));
        await CompanionLauncherAdmission.VerifyProcessAsync("File", admittedFile.Pid, admittedFile.StartedUtc,
            admittedFile.ExecutableDll, admittedFile.ExecutableSha256.ToUpperInvariant(), runId, expiresUtc, deadline.Token);
        if (process.HasExited || Math.Abs((process.StartTime.ToUniversalTime() - actualStartUtc.UtcDateTime).TotalMilliseconds) > 1
            || expiresUtc <= DateTimeOffset.UtcNow) throw new InvalidOperationException("Accounting fixture lease/process changed.");
    }

    /// <summary>Document/Notification retain HTTPS; only the independently admitted dedicated File host uses HTTP.</summary>
    public static void ValidateTargetOrigins(Uri documentOrigin, Uri fileOrigin, Uri notificationOrigin)
    {
        foreach (var (origin, scheme) in new[] { (documentOrigin, "https"), (fileOrigin, "http"), (notificationOrigin, "https") })
            if (!origin.IsAbsoluteUri || origin.Scheme != scheme
                || !IPAddress.TryParse(origin.Host.Trim('[', ']'), out var address) || !IPAddress.IsLoopback(address)
                || origin.Port is < 1024 or > 65535 || origin.AbsolutePath != "/"
                || origin.UserInfo.Length != 0 || origin.Query.Length != 0 || origin.Fragment.Length != 0)
                throw new InvalidDataException("Exact owned loopback fixture origin required.");
    }

    /// <summary>Accounting consumes direct Services keys, without a BaseUrl suffix.</summary>
    public static void ValidateOrigins(IReadOnlyDictionary<string, string> environment, Uri documentOrigin,
        Uri fileOrigin, Uri notificationOrigin)
    {
        var expected = new Dictionary<string, Uri>(StringComparer.Ordinal)
        {
            ["Services__Document"] = documentOrigin,
            ["Services__File"] = fileOrigin,
            ["Services__Notification"] = notificationOrigin,
        };
        foreach (var binding in expected)
        {
            var aliases = environment.Keys.Where(key => key.Replace(":", "__", StringComparison.Ordinal)
                .Equals(binding.Key, StringComparison.OrdinalIgnoreCase)).ToArray();
            if (aliases is not [var actualKey] || actualKey != binding.Key || environment[actualKey] != binding.Value.AbsoluteUri)
                throw new InvalidDataException("Consumed Accounting external origin differs or is ambiguous.");
        }
    }

    private static Dictionary<string, string> Decode(byte[] bytes) => Encoding.UTF8.GetString(bytes)
        .Split('\0', StringSplitOptions.RemoveEmptyEntries).Select(value => value.Split('=', 2))
        .ToDictionary(value => value[0], value => value.Length == 2 ? value[1] : "", StringComparer.Ordinal);

    private static async Task<byte[]> ReadBounded(string path, CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 4096, true);
        using var output = new MemoryStream();
        var buffer = new byte[4096];
        while (true)
        {
            var count = await stream.ReadAsync(buffer, cancellationToken);
            if (count == 0) return output.ToArray();
            if (output.Length + count > 262144) throw new InvalidDataException();
            output.Write(buffer, 0, count);
        }
    }
}
