using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace InvoiceCompletionProducerAcceptance.Companion;

/// <summary>Source proposal for the endpoint's GET authorization gate; does not emulate storage.</summary>
public sealed class HostedV4SignedReadVerifier : IDisposable
{
    private readonly RSA publicKey = RSA.Create();
    private readonly string authority;
    private readonly string signatureHost;
    private readonly DateTimeOffset leaseExpires;
    private bool disposed;

    /// <summary>Imports only the actual observed File public key and pins the admitted origin and lease.</summary>
    public HostedV4SignedReadVerifier(string base64Spki, string expectedAuthority, DateTimeOffset expiresUtc)
    {
        try
        {
            byte[] bytes = Convert.FromBase64String(base64Spki);
            if (bytes.Length is < 256 or > 4096 || Convert.ToBase64String(bytes) != base64Spki)
                throw new ArgumentException("Canonical bounded public SPKI required.");
            publicKey.ImportSubjectPublicKeyInfo(bytes, out int consumed);
            if (consumed != bytes.Length || publicKey.KeySize != 2048)
                throw new ArgumentException("Exact File public RSA key required.");
            if (!Regex.IsMatch(expectedAuthority, @"\A(?:127\.0\.0\.1|\[::1\]):[1-9][0-9]{0,4}\z", RegexOptions.CultureInvariant)
                || !Uri.TryCreate("http://" + expectedAuthority, UriKind.Absolute, out Uri? origin)
                || origin.Port is < 1 or > 65535 || expiresUtc.Offset != TimeSpan.Zero)
                throw new ArgumentException("Exact admitted endpoint authority and UTC expiry required.");
            authority = expectedAuthority;
            // Google UrlSigner.Options.WithPort changes URL authority, not its canonical signed Host.
            // The actual request authority is still independently pinned, including the port.
            signatureHost = origin.Host;
            leaseExpires = expiresUtc;
        }
        catch { publicKey.Dispose(); throw; }
    }

    /// <summary>Checks the actual raw request target, method and Host before any backend read.</summary>
    public bool Authorize(string method, string actualHost, string rawTarget, DateTimeOffset nowUtc)
    {
        if (disposed || nowUtc.Offset != TimeSpan.Zero || nowUtc >= leaseExpires || method != "GET"
            || actualHost != authority || rawTarget.Length is < 1 or > 16384)
            return false;
        try
        {
            int separator = rawTarget.IndexOf('?');
            if (separator <= 0 || rawTarget.IndexOf('?', separator + 1) >= 0 || rawTarget.Contains('#')) return false;
            string path = rawTarget[..separator];
            // Dedicated invoice read route only; preserve canonical escaped bytes verbatim.
            if (!Regex.IsMatch(path, @"\A/maliev\.com/invoices/[A-Za-z0-9._~/%-]+\z", RegexOptions.CultureInvariant)
                || path.Contains("//") || path.Split('/').Any(segment => segment is "." or "..")
                || !ValidEscapes(path)) return false;
            string decodedPath = Uri.UnescapeDataString(path);
            if (!decodedPath.StartsWith("/maliev.com/invoices/", StringComparison.Ordinal)
                || decodedPath.Contains('%') || decodedPath.Contains('\\') || decodedPath.Contains("//")
                || decodedPath.Any(char.IsControl) || decodedPath.Split('/').Any(segment => segment is "." or "..")) return false;
            var parameters = new Dictionary<string, (string Raw, string Decoded)>(StringComparer.Ordinal);
            foreach (string pair in rawTarget[(separator + 1)..].Split('&'))
            {
                int equals = pair.IndexOf('=');
                if (equals <= 0 || pair.IndexOf('=', equals + 1) >= 0) return false;
                string name = pair[..equals], raw = pair[(equals + 1)..];
                if (!Regex.IsMatch(name, @"\A[A-Za-z0-9-]+\z", RegexOptions.CultureInvariant)
                    || !Regex.IsMatch(raw, @"\A[A-Za-z0-9._~%-]+\z", RegexOptions.CultureInvariant)
                    || !ValidEscapes(raw) || !parameters.TryAdd(name, (raw, Uri.UnescapeDataString(raw)))) return false;
            }
            string[] required = ["X-Goog-Algorithm", "X-Goog-Credential", "X-Goog-Date", "X-Goog-Expires", "X-Goog-SignedHeaders", "X-Goog-Signature"];
            if (!required.All(parameters.ContainsKey)
                || parameters.Keys.Any(name => !required.Contains(name, StringComparer.Ordinal)
                    && name is not ("generation" or "response-content-disposition"))) return false;
            string Get(string name) => parameters[name].Decoded;
            if (Get("X-Goog-Algorithm") != "GOOG4-RSA-SHA256" || Get("X-Goog-SignedHeaders") != "host") return false;
            string[] credential = Get("X-Goog-Credential").Split('/');
            if (credential.Length != 5 || credential[0] != "hosted-file-signing@example.invalid"
                || !Regex.IsMatch(credential[2], @"\A[a-z0-9-]{1,64}\z", RegexOptions.CultureInvariant)
                || credential[3] != "storage" || credential[4] != "goog4_request") return false;
            if (!DateTimeOffset.TryParseExact(Get("X-Goog-Date"), "yyyyMMdd'T'HHmmss'Z'", CultureInfo.InvariantCulture,
                    DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out DateTimeOffset signedAt)
                || credential[1] != signedAt.ToString("yyyyMMdd", CultureInfo.InvariantCulture)
                || !Regex.IsMatch(Get("X-Goog-Expires"), @"\A[1-9][0-9]{0,5}\z", RegexOptions.CultureInvariant)
                || !int.TryParse(Get("X-Goog-Expires"), NumberStyles.None, CultureInfo.InvariantCulture, out int seconds)
                || seconds > 604800 || nowUtc < signedAt || nowUtc >= signedAt.AddSeconds(seconds)) return false;
            if (parameters.TryGetValue("generation", out var generation)
                && (!Regex.IsMatch(generation.Decoded, @"\A[1-9][0-9]{0,18}\z", RegexOptions.CultureInvariant)
                    || !long.TryParse(generation.Decoded, NumberStyles.None, CultureInfo.InvariantCulture, out _))) return false;
            if (!Regex.IsMatch(Get("X-Goog-Signature"), @"\A[0-9a-f]{512}\z", RegexOptions.CultureInvariant)) return false;
            string query = string.Join("&", parameters.Where(pair => pair.Key != "X-Goog-Signature")
                .OrderBy(pair => pair.Key, StringComparer.Ordinal).Select(pair => pair.Key + "=" + pair.Value.Raw));
            string canonical = "GET\n" + path + "\n" + query + "\nhost:" + signatureHost + "\n\nhost\nUNSIGNED-PAYLOAD";
            string scope = string.Join("/", credential.Skip(1));
            string toSign = "GOOG4-RSA-SHA256\n" + Get("X-Goog-Date") + "\n" + scope + "\n"
                + Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(canonical)));
            return publicKey.VerifyData(Encoding.UTF8.GetBytes(toSign), Convert.FromHexString(Get("X-Goog-Signature")),
                HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        }
        catch (Exception error) when (error is ArgumentException or FormatException or CryptographicException)
        { return false; }
    }

    private static bool ValidEscapes(string value)
    {
        for (int index = 0; index < value.Length; index++)
        {
            if (value[index] != '%') continue;
            if (index + 2 >= value.Length || !IsHex(value[index + 1]) || !IsHex(value[index + 2])) return false;
            index += 2;
        }
        return true;
        static bool IsHex(char value) => value is >= '0' and <= '9' or >= 'A' and <= 'F';
    }

    /// <summary>Releases the owned public RSA handle.</summary>
    public void Dispose() { if (!disposed) { disposed = true; publicKey.Dispose(); } }
}
