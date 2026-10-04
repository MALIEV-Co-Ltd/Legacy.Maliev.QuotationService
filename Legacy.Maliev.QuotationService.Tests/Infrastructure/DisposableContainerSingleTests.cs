using System.Net;
using Docker.DotNet;
using Npgsql;
using Testcontainers.PostgreSql;

namespace Legacy.Maliev.QuotationService.Tests.Infrastructure;

public sealed class DisposableContainerSingleTests
{
    [Fact]
    public async Task Single_owned_postgres_opens_current_backend_and_verifies_exact_removal()
    {
        ContainerAttempt? identity = null;
        var owned = await DisposableContainerSingle.StartAsync("quotation100-single-proof", attempt =>
        {
            identity = attempt;
            return new PostgreSqlBuilder("postgres:18-alpine").WithDockerEndpoint(attempt.Endpoint)
                .WithName(attempt.Name).WithLabel(attempt.Labels).Build();
        });
        try
        {
            Assert.NotNull(identity);
            await using var connection = new NpgsqlConnection(DisposablePostgresConnectionPolicy.Isolate(
                ((PostgreSqlContainer)owned.Container).GetConnectionString()));
            await connection.OpenAsync();
            await using var command = new NpgsqlCommand("SELECT 1", connection);
            Assert.Equal(1, await command.ExecuteScalarAsync());
        }
        finally { await owned.DisposeAsync(); }
        using var docker = new DockerClientBuilder().WithEndpoint(identity!.Endpoint).Build();
        var absent = await Assert.ThrowsAsync<DockerContainerNotFoundException>(() =>
            docker.Containers.InspectContainerAsync(identity.Name));
        Assert.Equal(HttpStatusCode.NotFound, absent.StatusCode);
    }
}
