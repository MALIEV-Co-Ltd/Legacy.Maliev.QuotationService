using System.Net.Security;
using System.Net.Http.Headers;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;

namespace Legacy.Maliev.QuotationService.MigrationRunner;

public sealed class InClusterCloudNativePgRuntimeObserver : ICloudNativePgRuntimeObserver, IDisposable
{
    private const string ApiServer = "https://kubernetes.default.svc";
    private const string TokenPath = "/var/run/secrets/kubernetes.io/serviceaccount/token";
    private const string CaPath = "/var/run/secrets/kubernetes.io/serviceaccount/ca.crt";
    private readonly HttpClient client;

    public InClusterCloudNativePgRuntimeObserver()
    {
        var roots = new X509Certificate2Collection();
        roots.ImportFromPemFile(CaPath);
        var handler = new SocketsHttpHandler();
        handler.SslOptions.RemoteCertificateValidationCallback = (_, certificate, _, errors) =>
        {
            if (certificate is null || (errors & SslPolicyErrors.RemoteCertificateNameMismatch) != 0) return false;
            using var chain = new X509Chain();
            chain.ChainPolicy.TrustMode = X509ChainTrustMode.CustomRootTrust;
            chain.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck;
            chain.ChainPolicy.CustomTrustStore.AddRange(roots);
            return chain.Build(new X509Certificate2(certificate));
        };
        client = new HttpClient(handler) { BaseAddress = new Uri(ApiServer) };
    }

    public async Task<CloudNativePgRuntimeObservation> ObserveAsync(
        string clusterNamespace,
        string clusterName,
        CancellationToken cancellationToken)
    {
        try
        {
            string token = (await File.ReadAllTextAsync(TokenPath, cancellationToken).ConfigureAwait(false)).Trim();
            if (token.Length == 0) throw new PostgreSqlSnapshotRejectedException("CloudNativePG observation token is empty.");
            using var request = new HttpRequestMessage(HttpMethod.Get,
                $"/apis/postgresql.cnpg.io/v1/namespaces/{Uri.EscapeDataString(clusterNamespace)}/clusters/{Uri.EscapeDataString(clusterName)}");
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            using HttpResponseMessage response = await client.SendAsync(request, cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode) throw new PostgreSqlSnapshotRejectedException("CloudNativePG target observation failed.");
            using JsonDocument document = await JsonDocument.ParseAsync(
                await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false), cancellationToken: cancellationToken).ConfigureAwait(false);
            JsonElement metadata = document.RootElement.GetProperty("metadata");
            JsonElement status = document.RootElement.GetProperty("status");
            string observedNamespace = metadata.GetProperty("namespace").GetString() ?? string.Empty;
            string observedName = metadata.GetProperty("name").GetString() ?? string.Empty;
            string uid = metadata.GetProperty("uid").GetString() ?? string.Empty;
            long generation = metadata.GetProperty("generation").GetInt64();
            long observedGeneration = status.GetProperty("observedGeneration").GetInt64();
            bool Condition(string type) => status.GetProperty("conditions").EnumerateArray().Any(condition =>
                condition.GetProperty("type").GetString() == type && condition.GetProperty("status").GetString() == "True");
            bool healthy = observedNamespace == clusterNamespace && observedName == clusterName && uid.Length > 0 &&
                generation > 0 && observedGeneration == generation && status.GetProperty("phase").GetString() == "Cluster in healthy state" &&
                Condition("Ready") && Condition("ConsistentSystemID") && Condition("ContinuousArchiving") && Condition("LastBackupSucceeded");
            return new(observedNamespace, observedName, uid, generation, observedGeneration, healthy);
        }
        catch (PostgreSqlSnapshotRejectedException) { throw; }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or HttpRequestException or JsonException or InvalidOperationException or KeyNotFoundException)
        {
            throw new PostgreSqlSnapshotRejectedException("CloudNativePG target observation failed closed.");
        }
    }

    public void Dispose() => client.Dispose();
}
