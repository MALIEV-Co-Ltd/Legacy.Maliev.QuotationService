using System.Globalization;
using System.Text;
using System.Text.Json;

namespace Legacy.Maliev.QuotationService.MigrationRunner;

public static class SchemaBaselineReceiptCanonicalizer
{
    private const string DomainSeparator = "Legacy.Maliev.QuotationService.SchemaBaselineReceipt.v1";
    private static readonly HashSet<string> RequiredProperties = new(StringComparer.Ordinal)
    {
        "SchemaVersion", "Workload", "SourceSnapshotId", "CopyPlanId", "SchemaHash",
        "AttestationKeyId", "Host", "Port", "Database", "ExpiresUtc",
    };

    public static bool TryParseAndCreatePayload(
        string json,
        out SchemaBaselineReceiptPayload payload,
        out byte[] canonicalPayload)
    {
        payload = null!;
        canonicalPayload = [];
        try
        {
            using var document = JsonDocument.Parse(json, new JsonDocumentOptions
            {
                AllowTrailingCommas = false,
                CommentHandling = JsonCommentHandling.Disallow,
            });
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                return false;
            }

            var properties = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
            foreach (var property in document.RootElement.EnumerateObject())
            {
                if (!RequiredProperties.Contains(property.Name) || !properties.TryAdd(property.Name, property.Value))
                {
                    return false;
                }
            }

            if (properties.Count != RequiredProperties.Count ||
                !TryString(properties, "SchemaVersion", out var schemaVersion) ||
                !TryString(properties, "Workload", out var workload) ||
                !TryString(properties, "SourceSnapshotId", out var sourceSnapshotId) ||
                !TryString(properties, "CopyPlanId", out var copyPlanId) ||
                !TryString(properties, "SchemaHash", out var schemaHash) ||
                !TryString(properties, "AttestationKeyId", out var attestationKeyId) ||
                !TryString(properties, "Host", out var host) ||
                !properties["Port"].TryGetInt32(out var port) || port is < 1 or > 65535 ||
                !TryString(properties, "Database", out var database) ||
                properties["ExpiresUtc"].ValueKind != JsonValueKind.String ||
                !properties["ExpiresUtc"].TryGetDateTimeOffset(out var expiresUtc))
            {
                return false;
            }

            payload = new(schemaVersion, workload, sourceSnapshotId, copyPlanId, schemaHash,
                attestationKeyId, host, port, database, expiresUtc);
            canonicalPayload = CreatePayload(payload);
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    public static byte[] CreatePayload(SchemaBaselineReceiptPayload payload)
    {
        ArgumentNullException.ThrowIfNull(payload);
        using var stream = new MemoryStream();
        using (var writer = new BinaryWriter(stream, new UTF8Encoding(false), leaveOpen: true))
        {
            WriteString(writer, DomainSeparator);
            WriteString(writer, payload.SchemaVersion);
            WriteString(writer, payload.Workload);
            WriteString(writer, payload.SourceSnapshotId);
            WriteString(writer, payload.CopyPlanId);
            WriteString(writer, payload.SchemaHash);
            WriteString(writer, payload.AttestationKeyId);
            WriteString(writer, payload.Host);
            writer.Write(payload.Port);
            WriteString(writer, payload.Database);
            WriteString(writer, payload.ExpiresUtc.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture));
        }

        return stream.ToArray();
    }

    private static bool TryString(
        IReadOnlyDictionary<string, JsonElement> properties,
        string name,
        out string value)
    {
        value = string.Empty;
        if (properties[name].ValueKind != JsonValueKind.String)
        {
            return false;
        }

        value = properties[name].GetString() ?? string.Empty;
        return !string.IsNullOrWhiteSpace(value);
    }

    private static void WriteString(BinaryWriter writer, string value)
    {
        var bytes = Encoding.UTF8.GetBytes(value);
        writer.Write(bytes.Length);
        writer.Write(bytes);
    }
}
