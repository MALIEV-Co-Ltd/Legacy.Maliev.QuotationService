using Docker.DotNet;
using DotNet.Testcontainers.Containers;

namespace Legacy.Maliev.QuotationService.Tests.Infrastructure;

internal sealed class DisposableContainerSingle : IAsyncDisposable
{
    private readonly DockerClient docker;
    private readonly DisposableContainerPair.OwnedContainer owned;
    private int clientReleased;
    private void ReleaseClient()
    {
        if (Interlocked.Exchange(ref clientReleased, 1) == 0) docker.Dispose();
    }
    private DisposableContainerSingle(DockerClient docker, DisposableContainerPair.OwnedContainer owned)
    { this.docker = docker; this.owned = owned; }
    internal IContainer Container => owned.Container;

    internal static async Task<DisposableContainerSingle> StartAsync(string owner,
        Func<ContainerAttempt, IContainer> factory, CancellationToken token = default)
    {
        var endpoint = new Uri(await DisposableContainerStartup.LocalDockerEndpointAsync(token));
        var docker = new DockerClientBuilder().WithEndpoint(endpoint).Build();
        var run = Guid.NewGuid().ToString("N");
        var attempt = 0;
        try
        {
            var resource = await DisposableContainerStartup.StartAsync(() =>
            {
                var identity = new ContainerAttempt(endpoint, owner, run, "pg", ++attempt);
                return new DisposableContainerPair.OwnedContainer(factory(identity), docker, identity);
            }, Task.Delay, token);
            return new(docker, (DisposableContainerPair.OwnedContainer)resource);
        }
        catch (Exception failure)
        {
            if (ContainerOperationCustody.CustodyFor(failure) is { } retained)
                ContainerOperationCustody.Retain(retained.Operation, "container-single-client-release", [docker, retained], docker.Dispose);
            else docker.Dispose();
            throw;
        }
    }

    public async ValueTask DisposeAsync()
    {
        var cleanup = owned.DisposeAndVerifyAsync();
        try { await cleanup.WaitAsync(TimeSpan.FromSeconds(15)); }
        catch (Exception failure)
        {
            ContainerOperationCustody.AttachCustody(failure, ContainerOperationCustody.Retain(cleanup,
                "container-single-disposal-client-release", [docker, owned], ReleaseClient));
            throw;
        }
        finally { if (cleanup.IsCompleted) ReleaseClient(); }
    }
}
