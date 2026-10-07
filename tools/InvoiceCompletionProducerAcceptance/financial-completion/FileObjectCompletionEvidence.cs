using System.Data;
using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using Npgsql;

namespace InvoiceCompletionProducerAcceptance;

/// <summary>Validated actual File metadata, without an assertion of quarantine-source absence.</summary>
public sealed class FileObjectCompletionEvidence
{
    internal FileObjectCompletionEvidence(FileUploadRow upload, FileMoveRow move, string accountingPdfSha256, string persistenceSha256)
    {
        Upload = upload;
        Move = move;
        AccountingPdfSha256 = accountingPdfSha256;
        PersistenceSha256 = persistenceSha256;
    }

    public FileUploadRow Upload { get; }
    public FileMoveRow Move { get; }
    public string AccountingPdfSha256 { get; }
    public string PersistenceSha256 { get; }
    // Neither journal state nor successful destination download is an actual storage-API absence observation.
    public bool QuarantineAbsenceObserved => false;
}

public sealed record FileUploadRow(int ID, string? Bucket, string? Name, string? ContentType, long? Size);
public sealed record FileMoveRow(Guid OperationId, bool ScanClean, string SourceBucket, string SourceObjectName,
    long SourceGeneration, string DestinationBucket, string DestinationObjectName, long? DestinationGeneration, string State);
public sealed record FileMetadataSnapshot(long UploadTotal, long UploadExpected, long JournalTotal, long JournalExpected,
    FileUploadRow[] Uploads, FileMoveRow[] Journals, JsonElement PersistenceSnapshot);

/// <summary>Reads the independently admitted isolated File database; never seeds, migrates or repairs it.</summary>
public static class FileObjectCompletionReadback
{
    internal const long MaximumPdfBytes = 10 * 1024 * 1024;

    public static async Task<FileObjectCompletionEvidence> ReadAsync(string fileConnection,
        AccountingCompletionEvidence accounting, CancellationToken cancellationToken)
    {
        if (!OperatingSystem.IsLinux() || Environment.GetEnvironmentVariable("GITHUB_ACTIONS") != "true")
            throw new InvalidOperationException("Hosted owning process/database admission required.");
        var database = new NpgsqlConnectionStringBuilder(fileConnection);
        if (database.Host is not ("127.0.0.1" or "::1") || database.Port is < 1024 or > 65535
            || string.IsNullOrEmpty(database.Database) || !database.Database.StartsWith("c821_", StringComparison.Ordinal))
            throw new InvalidDataException("Exact isolated File database required.");
        database.Pooling = false;
        database.Timeout = 5;
        database.CommandTimeout = 10;
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(10));
        await using var connection = new NpgsqlConnection(database.ConnectionString);
        await connection.OpenAsync(deadline.Token);
        await using var transaction = await connection.BeginTransactionAsync(IsolationLevel.RepeatableRead, deadline.Token);
        await using (var readOnly = new NpgsqlCommand("SET TRANSACTION READ ONLY", connection, transaction))
            await readOnly.ExecuteNonQueryAsync(deadline.Token);
        await using var command = new NpgsqlCommand("""
            SELECT json_build_object(
              'UploadTotal', (SELECT COUNT(*) FROM "Upload"),
              'UploadExpected', (SELECT COUNT(*) FROM "Upload" WHERE "Bucket"=@bucket AND "Name"=@object),
              'JournalTotal', (SELECT COUNT(*) FROM "StorageMoveJournal"),
              'JournalExpected', (SELECT COUNT(*) FROM "StorageMoveJournal"
                WHERE "DestinationBucket"=@bucket AND "DestinationObjectName"=@object),
              'Uploads', (SELECT COALESCE(json_agg(u),'[]'::json) FROM
                (SELECT "ID", "Bucket", "Name", "ContentType", "Size" FROM "Upload" ORDER BY "ID" LIMIT 2) u),
              'Journals', (SELECT COALESCE(json_agg(j),'[]'::json) FROM
                (SELECT "OperationId", "ScanClean", "SourceBucket", "SourceObjectName", "SourceGeneration",
                  "DestinationBucket", "DestinationObjectName", "DestinationGeneration", "State"
                 FROM "StorageMoveJournal" ORDER BY "OperationId" LIMIT 2) j),
              'PersistenceSnapshot', json_build_object(
                'Uploads', (SELECT COALESCE(json_agg(u ORDER BY "ID"),'[]'::json) FROM "Upload" u),
                'Journals', (SELECT COALESCE(json_agg(j ORDER BY "OperationId"),'[]'::json) FROM "StorageMoveJournal" j),
                'QuarantineIntents', (SELECT COALESCE(json_agg(q ORDER BY "OperationId"),'[]'::json) FROM "QuarantineUploadIntent" q))
            )::text
            """, connection, transaction);
        command.Parameters.AddWithValue("bucket", accounting.Bucket);
        command.Parameters.AddWithValue("object", accounting.ObjectName);
        var json = await command.ExecuteScalarAsync(deadline.Token) as string ?? throw new InvalidDataException("File snapshot missing.");
        if (json.Length > 16384) throw new InvalidDataException("File snapshot exceeded bound.");
        using var document = JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = 5 });
        var evidence = ValidateSnapshot(ParseSnapshot(document.RootElement), accounting);
        await transaction.CommitAsync(deadline.Token);
        return evidence;
    }

    internal static FileMetadataSnapshot ParseSnapshot(JsonElement value)
    {
        RequireUniqueObjects(value);
        return value.Deserialize<FileMetadataSnapshot>(new JsonSerializerOptions
        {
            RespectRequiredConstructorParameters = true,
            RespectNullableAnnotations = true,
            UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        }) ?? throw new InvalidDataException("File snapshot missing.");
    }

    internal static FileObjectCompletionEvidence ValidateSnapshot(FileMetadataSnapshot snapshot, AccountingCompletionEvidence accounting)
    {
        RequireSingleRows(snapshot.UploadTotal, snapshot.UploadExpected, snapshot.JournalTotal, snapshot.JournalExpected);
        if (snapshot.Uploads is not [FileUploadRow upload] || snapshot.Journals is not [FileMoveRow move])
            throw new InvalidDataException("Actual single File rows required.");
        if (accounting.PdfSha256.Length != 64 || accounting.PdfSha256.Any(character => character is not (>= '0' and <= '9' or >= 'A' and <= 'F'))
            || upload.ID <= 0 || upload.Bucket != accounting.Bucket || upload.Name != accounting.ObjectName
            || upload.ContentType != "application/pdf" || upload.Size is not > 0 or > MaximumPdfBytes
            || move.OperationId == Guid.Empty || !move.ScanClean || move.State != "MetadataCommitted"
            || move.SourceGeneration <= 0 || move.DestinationGeneration is not > 0
            || string.IsNullOrWhiteSpace(move.SourceBucket) || string.IsNullOrWhiteSpace(move.SourceObjectName)
            || move.SourceBucket.Length > 255 || move.SourceObjectName.Length > 1024
            || move.DestinationBucket != accounting.Bucket || move.DestinationObjectName != accounting.ObjectName
            || (move.SourceBucket == move.DestinationBucket && move.SourceObjectName == move.DestinationObjectName))
            throw new InvalidDataException("File clean committed coordinates/generations differ from Accounting.");
        var persisted = snapshot.PersistenceSnapshot;
        if (persisted.ValueKind != JsonValueKind.Object) throw new InvalidDataException("Full File persistence snapshot required.");
        var names = persisted.EnumerateObject().Select(property => property.Name).ToArray();
        string[] expected = ["Uploads", "Journals", "QuarantineIntents"];
        if (names.Length != expected.Length || names.Distinct(StringComparer.Ordinal).Count() != names.Length
            || expected.Any(name => !persisted.TryGetProperty(name, out var rows)
                || rows.ValueKind != JsonValueKind.Array || rows.GetArrayLength() != 1))
            throw new InvalidDataException("Full exact single-object File persistence snapshot required.");
        RequireUniqueObjects(persisted);
        string persistenceSha256 = Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(persisted.GetRawText())));
        return new FileObjectCompletionEvidence(upload, move, accounting.PdfSha256, persistenceSha256);
    }

    internal static void RequireSingleRows(long uploadTotal, long uploadExpected, long journalTotal, long journalExpected)
    {
        if (uploadTotal != 1 || uploadExpected != 1 || journalTotal != 1 || journalExpected != 1)
            throw new InvalidDataException("Exactly one total and expected Upload and journal required in the isolated File database.");
    }

    public static void RequireStableReplay(FileObjectCompletionEvidence before, FileObjectCompletionEvidence after)
    {
        if (before.Upload != after.Upload || before.Move != after.Move || before.AccountingPdfSha256 != after.AccountingPdfSha256
            || before.PersistenceSha256 != after.PersistenceSha256)
            throw new InvalidDataException("Completed replay changed File rows or document digest.");
    }

    private static void RequireUniqueObjects(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in value.EnumerateObject())
            {
                if (!names.Add(property.Name)) throw new InvalidDataException("Duplicate File snapshot field.");
                RequireUniqueObjects(property.Value);
            }
        }
        else if (value.ValueKind == JsonValueKind.Array)
            foreach (var item in value.EnumerateArray()) RequireUniqueObjects(item);
    }
}

public sealed record FileSignedBytesEvidence(long Generation, long Bytes, string Sha256);

/// <summary>Independent generation-specific bytes only through the owned hosted loopback front.</summary>
public static class FileSignedByteReadback
{
    public static async Task<FileSignedBytesEvidence> ReadAsync(Uri signedUri, Uri independentlyObservedOrigin,
        DateTimeOffset leaseExpiresUtc, FileObjectCompletionEvidence file,
        Func<CancellationToken, Task> observeOwningOrigin, CancellationToken cancellationToken)
    {
        if (!OperatingSystem.IsLinux() || Environment.GetEnvironmentVariable("GITHUB_ACTIONS") != "true")
            throw new InvalidOperationException("Owned hosted acceptance required.");
        ValidateSignedUri(signedUri, independentlyObservedOrigin, file);
        var remaining = leaseExpiresUtc - DateTimeOffset.UtcNow;
        if (leaseExpiresUtc.Offset != TimeSpan.Zero || remaining <= TimeSpan.Zero || remaining > TimeSpan.FromMinutes(30))
            throw new InvalidDataException("Current finite origin-owner lease required.");
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(remaining < TimeSpan.FromSeconds(10) ? remaining : TimeSpan.FromSeconds(10));
        await observeOwningOrigin(deadline.Token).WaitAsync(deadline.Token);
        using var client = new HttpClient(new SocketsHttpHandler
        {
            AllowAutoRedirect = false,
            UseProxy = false,
            UseCookies = false,
            AutomaticDecompression = DecompressionMethods.None,
            MaxResponseHeadersLength = 32,
            ConnectTimeout = TimeSpan.FromSeconds(5),
        })
        { Timeout = Timeout.InfiniteTimeSpan };
        using var request = new HttpRequestMessage(HttpMethod.Get, signedUri);
        using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, deadline.Token);
        if (response.StatusCode != HttpStatusCode.OK || response.Headers.Location is not null
            || response.Content.Headers.ContentEncoding.Count != 0
            || response.Content.Headers.ContentLength is > FileObjectCompletionReadback.MaximumPdfBytes
            || response.Content.Headers.ContentLength is long length && length != file.Upload.Size)
            throw new InvalidDataException("Signed generation read did not return the exact bounded destination.");
        await observeOwningOrigin(deadline.Token).WaitAsync(deadline.Token);
        await using var content = await response.Content.ReadAsStreamAsync(deadline.Token);
        var result = await VerifyBytesAsync(content, file, leaseExpiresUtc, deadline.Token);
        await observeOwningOrigin(deadline.Token).WaitAsync(deadline.Token);
        if (DateTimeOffset.UtcNow >= leaseExpiresUtc) throw new InvalidDataException("Origin-owner lease expired.");
        return result;
    }

    internal static void ValidateSignedUri(Uri signedUri, Uri origin, FileObjectCompletionEvidence file)
    {
        if (!origin.IsAbsoluteUri || !signedUri.IsAbsoluteUri) throw new InvalidDataException("Absolute owning URI required.");
        bool isolated = origin.Scheme is "http" or "https" && IPAddress.TryParse(origin.Host.Trim('[', ']'), out var ip)
            && IPAddress.IsLoopback(ip) && origin.Port is >= 1024 and <= 65535;
        if (!isolated || origin.UserInfo != "" || origin.AbsolutePath != "/" || origin.Query != "" || origin.Fragment != ""
            || !signedUri.IsAbsoluteUri || signedUri.OriginalString.Length > 16384 || signedUri.UserInfo != "" || signedUri.Fragment != ""
            || signedUri.GetLeftPart(UriPartial.Authority) != origin.GetLeftPart(UriPartial.Authority)
            || signedUri.GetComponents(UriComponents.Path, UriFormat.UriEscaped) !=
                Uri.EscapeDataString(file.Upload.Bucket!) + "/" + string.Join('/', file.Upload.Name!.Split('/').Select(Uri.EscapeDataString)))
            throw new InvalidDataException("Signed read escaped exact owning origin/object.");
        var query = signedUri.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries).Select(part => part.Split('=', 2)).ToArray();
        if (query.Any(pair => pair.Length != 2 || pair[0].Length == 0 || pair[1].Length == 0)
            || query.Select(pair => Uri.UnescapeDataString(pair[0])).Distinct(StringComparer.Ordinal).Count() != query.Length)
            throw new InvalidDataException("Exact unique signed query fields required.");
        var generation = query.Where(pair => pair[0] == "generation").ToArray();
        if (generation.Length != 1 || generation[0][1] != file.Move.DestinationGeneration!.Value.ToString(CultureInfo.InvariantCulture)
            || query.Count(pair => pair[0] == "X-Goog-Signature") != 1)
            throw new InvalidDataException("Actual exact destination generation signed read required.");
        // Cryptographic signature and expiry are enforced by the real storage/front owner, not a synthetic callback.
    }

    internal static async Task<FileSignedBytesEvidence> VerifyBytesAsync(Stream content, FileObjectCompletionEvidence file,
        DateTimeOffset leaseExpiresUtc, CancellationToken cancellationToken)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        byte[] buffer = new byte[65536];
        long count = 0;
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (DateTimeOffset.UtcNow >= leaseExpiresUtc) throw new InvalidDataException("Owner lease expired during signed bytes.");
            int read = await content.ReadAsync(buffer, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            if (DateTimeOffset.UtcNow >= leaseExpiresUtc) throw new InvalidDataException("Owner lease expired during signed bytes.");
            if (read == 0) break;
            count += read;
            if (count > FileObjectCompletionReadback.MaximumPdfBytes || count > file.Upload.Size)
                throw new InvalidDataException("Signed bytes exceeded metadata or 10 MiB limit.");
            hash.AppendData(buffer, 0, read);
        }
        string digest = Convert.ToHexString(hash.GetHashAndReset());
        if (count != file.Upload.Size || digest != file.AccountingPdfSha256)
            throw new InvalidDataException("Signed generation bytes differ from Accounting PDF digest/size.");
        return new FileSignedBytesEvidence(file.Move.DestinationGeneration!.Value, count, digest);
    }
}
