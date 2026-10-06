using System.Security.Cryptography;
using System.Text.Json;

namespace InvoiceCompletionProducerAcceptance.Companion;

/// <summary>One-time owner-pipe public material; no registration HTTP endpoint, JWT or private signer material.</summary>
public sealed record PublicSigningBootstrap(string Algorithm, string PublicKey, ObservedFileHost File);

/// <summary>Consumes exactly one bounded public bootstrap frame from the owner-held pipe.</summary>
public static class PublicSigningBootstrapReader
{
    public static async Task<PublicSigningBootstrap> ReadAsync(Stream ownerPipe, string runId,
        DateTimeOffset expiresUtc, CancellationToken cancellationToken)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var remaining = expiresUtc - DateTimeOffset.UtcNow;
        if (remaining <= TimeSpan.Zero || remaining > TimeSpan.FromMinutes(30))
            throw new InvalidDataException("Current finite owner lease required.");
        deadline.CancelAfter(remaining < TimeSpan.FromSeconds(30) ? remaining : TimeSpan.FromSeconds(30));
        using var output = new MemoryStream();
        byte[] chunk = new byte[4096];
        int count;
        while ((count = await ownerPipe.ReadAsync(chunk, deadline.Token)) > 0)
        {
            if (output.Length + count > 16384) throw new InvalidDataException("Owner bootstrap frame exceeded bound.");
            await output.WriteAsync(chunk.AsMemory(0, count), deadline.Token);
        }
        // EOF seals the one-time message; a second message or trailing JSON is rejected.
        using var document = ParseSingleFrame(output.ToArray());
        RequireProperties(document.RootElement, ["Algorithm", "PublicKey", "File"]);
        RequireProperties(document.RootElement.GetProperty("File"),
            ["Pid", "StartedUtc", "ExecutableDll", "ExecutableSha256", "RunId", "ExpiresUtc"]);
        var value = document.Deserialize<PublicSigningBootstrap>(new JsonSerializerOptions
        {
            RespectRequiredConstructorParameters = true,
            RespectNullableAnnotations = true,
        }) ?? throw new InvalidDataException("Bootstrap missing.");
        if (value.Algorithm != "GOOG4-RSA-SHA256" || value.File.RunId != runId || value.File.ExpiresUtc != expiresUtc
            || value.File.Pid <= 0 || value.File.StartedUtc.Offset != TimeSpan.Zero
            || value.File.StartedUtc > DateTimeOffset.UtcNow || value.File.StartedUtc >= expiresUtc
            || DateTimeOffset.UtcNow >= expiresUtc || !Path.IsPathFullyQualified(value.File.ExecutableDll)
            || value.File.ExecutableSha256.Length != 64 || value.File.ExecutableSha256.Any(character => !Uri.IsHexDigit(character)))
            throw new InvalidDataException("Bootstrap source/run identity differs.");
        byte[] publicBytes = Convert.FromBase64String(value.PublicKey);
        if (publicBytes.Length is < 256 or > 4096 || Convert.ToBase64String(publicBytes) != value.PublicKey)
            throw new InvalidDataException("Canonical bounded public key required.");
        using var key = RSA.Create();
        key.ImportSubjectPublicKeyInfo(publicBytes, out int used);
        if (used != publicBytes.Length || key.KeySize != 2048) throw new InvalidDataException("Actual File public RSA shape required.");
        return value;
    }

    private static JsonDocument ParseSingleFrame(byte[] frame)
    {
        try { return JsonDocument.Parse(frame, new JsonDocumentOptions { MaxDepth = 4 }); }
        catch (JsonException)
        {
            // Keep the public failure contract stable and do not expose owner-pipe contents in diagnostics.
            throw new JsonException("Exactly one bounded public bootstrap JSON object required.");
        }
    }

    private static void RequireProperties(JsonElement value, string[] fields)
    {
        if (value.ValueKind != JsonValueKind.Object) throw new InvalidDataException("Bootstrap object required.");
        var actual = value.EnumerateObject().Select(property => property.Name).ToArray();
        if (actual.Length != fields.Length || actual.Distinct(StringComparer.Ordinal).Count() != fields.Length
            || !actual.ToHashSet(StringComparer.Ordinal).SetEquals(fields))
            throw new InvalidDataException("Exact unique public bootstrap fields required.");
    }
}
