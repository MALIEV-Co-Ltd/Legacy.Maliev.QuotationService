using System.Globalization;
using System.Net;

namespace InvoiceCompletionProducerAcceptance.Companion;

internal sealed record ResumableReply(string? Location, string? Range);

internal static class ResumableResponsePolicy
{
    private const string UploadPath = "/upload/storage/v1/b/maliev.com/o";

    internal static ResumableReply Read(HttpResponseMessage response, bool sdk, string method,
        string rawTarget, Uri backend, Uri origin, long maximumBytes)
    {
        bool upload = rawTarget.Split('?', 2)[0] == UploadPath;
        bool incomplete = (int)response.StatusCode == 308;
        if ((int)response.StatusCode is >= 300 and <= 399 && response.StatusCode != HttpStatusCode.NotModified
            && !(sdk && upload && method == "PUT" && incomplete))
            throw new InvalidDataException("Unreviewed backend redirect denied.");
        string? location = null;
        if (response.Headers.Location is Uri supplied)
        {
            if (!sdk || !upload || method != "POST" || response.StatusCode is not (HttpStatusCode.OK or HttpStatusCode.Created))
                throw new InvalidDataException("Location is only admitted for successful upload initiation.");
            if (!supplied.IsAbsoluteUri || supplied.OriginalString.Length > 16384
                || supplied.GetLeftPart(UriPartial.Authority) != backend.GetLeftPart(UriPartial.Authority)
                || supplied.UserInfo != "" || supplied.Fragment != "" || supplied.AbsolutePath != UploadPath)
                throw new InvalidDataException("Resumable session escaped exact backend origin/route.");
            var query = supplied.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries)
                .Select(part => part.Split('=', 2)).ToArray();
            if (query.Any(pair => pair.Length != 2 || pair[0].Length == 0 || pair[1].Length == 0)
                || query.Select(pair => Uri.UnescapeDataString(pair[0])).Distinct(StringComparer.Ordinal).Count() != query.Length
                || query.Count(pair => pair[0] == "upload_id") != 1)
                throw new InvalidDataException("One actual backend session identifier required.");
            location = origin.GetLeftPart(UriPartial.Authority) + supplied.GetComponents(UriComponents.PathAndQuery, UriFormat.UriEscaped);
        }
        else if (sdk && upload && method == "POST" && response.StatusCode is HttpStatusCode.OK or HttpStatusCode.Created)
            throw new InvalidDataException("Successful upload initiation omitted its actual session.");
        string? range = null;
        if (response.Headers.TryGetValues("Range", out var values))
        {
            var all = values.ToArray();
            if (!sdk || !upload || method != "PUT" || !incomplete || all.Length != 1
                || !all[0].StartsWith("bytes=0-", StringComparison.Ordinal)
                || !long.TryParse(all[0][8..], NumberStyles.None, CultureInfo.InvariantCulture, out long last)
                || maximumBytes <= 0 || last >= maximumBytes)
                throw new InvalidDataException("Unexpected SDK upload acknowledgement range.");
            range = all[0];
        }
        // A zero-byte status probe may report no committed Range. 308 is not followed as a redirect.
        return new ResumableReply(location, range);
    }
}
