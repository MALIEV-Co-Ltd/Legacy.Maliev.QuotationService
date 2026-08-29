using System.Text.Json;

namespace Legacy.Maliev.QuotationService.MigrationRunner;

public static class SignedSchemaBaselineReceiptParser
{
    public static bool TryParse(string json, out SignedSchemaBaselineReceipt receipt)
    {
        receipt = null!;
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

            string? payload = null;
            string? signature = null;
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in document.RootElement.EnumerateObject())
            {
                if (!seen.Add(property.Name) || property.Value.ValueKind != JsonValueKind.String)
                {
                    return false;
                }

                switch (property.Name)
                {
                    case "Payload":
                        payload = property.Value.GetString();
                        break;
                    case "Signature":
                        signature = property.Value.GetString();
                        break;
                    default:
                        return false;
                }
            }

            if (seen.Count != 2 || string.IsNullOrWhiteSpace(payload) || string.IsNullOrWhiteSpace(signature))
            {
                return false;
            }

            receipt = new(payload, signature);
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }
}
