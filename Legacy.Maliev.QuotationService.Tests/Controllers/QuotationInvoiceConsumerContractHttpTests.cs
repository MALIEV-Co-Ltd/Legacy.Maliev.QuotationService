using System.Collections.Concurrent;
using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Docker.DotNet.Models;
using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Containers;
using Legacy.Maliev.AccountingService.Application.Models;
using Legacy.Maliev.AccountingService.Data;
using Legacy.Maliev.QuotationService.Data;
using Legacy.Maliev.QuotationService.Domain;
using Maliev.Aspire.ServiceDefaults.IAM;
using Maliev.Aspire.ServiceDefaults.LegacyAuth;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Http;
using Microsoft.Extensions.Logging;
using Microsoft.IdentityModel.Tokens;
using Npgsql;
using Testcontainers.PostgreSql;

namespace Legacy.Maliev.QuotationService.Tests.Controllers;

/// <summary>Unmodified committed Accounting client against normal Production Quotation HTTP.</summary>
public sealed class QuotationInvoiceConsumerContractHttpTests(InvoiceConsumerFixture fixture) : IClassFixture<InvoiceConsumerFixture>
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Producer_ordinary_accepted_update_preserves_partial_saga_original_version(bool attachInvoice)
    {
        var seeded = await fixture.SeedAsync(true); var orders = new InvoiceOrderTransport { Fail = true };
        await using var app = fixture.App(orders); using var client = fixture.Client(app, out _);
        using var partial = await Decide(client, seeded.Id, 0);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, partial.StatusCode); orders.Fail = false;
        using var updated = await Update(client, seeded, attachInvoice ? 901 : null, "producer ordinary edit");
        Assert.Equal(HttpStatusCode.NoContent, updated.StatusCode);
        using var replay = await Decide(client, seeded.Id, 0);
        Assert.Equal(HttpStatusCode.OK, replay.StatusCode);
        await using var db = fixture.Context(); var row = await db.Quotations.AsNoTracking().SingleAsync(x => x.Id == seeded.Id);
        Assert.Equal(attachInvoice ? 901 : (int?)null, row.InvoiceId);
        Assert.Equal("producer ordinary edit", row.Comment); Assert.NotEqual(seeded.ModifiedDate, row.ModifiedDate);
        Assert.Equal(seeded.AcceptedUtc, row.AcceptedUtc); Assert.Equal(seeded.AcceptanceOrigin, row.AcceptanceOrigin);
        Assert.Equal(seeded.SourceJourneyId, row.SourceJourneyId); Assert.Equal(17, row.SourceRequestId);
        Assert.Single(await db.AcceptedOutcomes.Where(x => x.QuotationId == seeded.Id).ToListAsync());
        var expected = $"quotation-{seeded.Id}-accepted-{DateTime.SpecifyKind(InvoiceConsumerFixture.Modified, DateTimeKind.Utc).Ticks:x}-order-701";
        Assert.All(orders.Requests, x => Assert.Equal(expected, x.Key));
        Assert.Equal(InvoiceConsumerFixture.Modified, row.DecisionOrderVersion);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Producer_missing_modified_date_binds_created_date_or_epoch_not_acceptance_time(bool missingCreated)
    {
        var seeded = await fixture.SeedAsync(true);
        await using (var setup = fixture.Context())
        {
            var row = await setup.Quotations.SingleAsync(x => x.Id == seeded.Id); row.ModifiedDate = null;
            if (missingCreated) row.CreatedDate = null;
            await setup.SaveChangesAsync(); seeded.ModifiedDate = null; if (missingCreated) seeded.CreatedDate = null;
        }
        var orders = new InvoiceOrderTransport { Fail = true }; await using var app = fixture.App(orders);
        using var client = fixture.Client(app, out _);
        using var partial = await Decide(client, seeded.Id, 0); Assert.Equal(HttpStatusCode.ServiceUnavailable, partial.StatusCode);
        orders.Fail = false;
        using var update = await Update(client, seeded, null, "producer fallback"); Assert.Equal(HttpStatusCode.NoContent, update.StatusCode);
        using var replay = await Decide(client, seeded.Id, 0); Assert.Equal(HttpStatusCode.OK, replay.StatusCode);
        var expectedVersion = missingCreated ? DateTime.SpecifyKind(DateTime.UnixEpoch, DateTimeKind.Unspecified) : InvoiceConsumerFixture.Modified.AddHours(-2);
        var expectedKey = $"quotation-{seeded.Id}-accepted-{DateTime.SpecifyKind(expectedVersion, DateTimeKind.Utc).Ticks:x}-order-701";
        Assert.All(orders.Requests, x => Assert.Equal(expectedKey, x.Key));
        await using var db = fixture.Context(); var persisted = await db.Quotations.AsNoTracking().SingleAsync(x => x.Id == seeded.Id);
        Assert.Equal(expectedVersion, persisted.DecisionOrderVersion); Assert.Equal(seeded.AcceptedUtc, persisted.AcceptedUtc);
    }

    [Fact]
    public async Task Producer_existing_binding_is_never_rebound_by_ordinary_update()
    {
        var seeded = await fixture.SeedAsync(true); var bound = InvoiceConsumerFixture.Modified.AddHours(-3);
        await using (var setup = fixture.Context()) { var row = await setup.Quotations.SingleAsync(x => x.Id == seeded.Id); row.DecisionOrderVersion = bound; await setup.SaveChangesAsync(); }
        await using var app = fixture.App(); using var client = fixture.Client(app, out _);
        using var update = await Update(client, seeded, 901, "preserve existing binding"); Assert.Equal(HttpStatusCode.NoContent, update.StatusCode);
        await using var db = fixture.Context(); Assert.Equal(bound, (await db.Quotations.AsNoTracking().SingleAsync(x => x.Id == seeded.Id)).DecisionOrderVersion);
    }

    [Theory]
    [InlineData(null)]
    [InlineData(false)]
    public async Task Producer_unaccepted_ordinary_update_does_not_create_decision_binding(bool? accepted)
    {
        var seeded = await fixture.SeedAsync(accepted); await using var app = fixture.App(); using var client = fixture.Client(app, out _);
        using var update = await Update(client, seeded, null, "unaccepted edit"); Assert.Equal(HttpStatusCode.NoContent, update.StatusCode);
        await using var db = fixture.Context(); var row = await db.Quotations.AsNoTracking().SingleAsync(x => x.Id == seeded.Id);
        Assert.Null(row.DecisionOrderVersion); Assert.Equal(accepted, row.Accepted); Assert.Empty(await db.AcceptedOutcomes.Where(x => x.QuotationId == seeded.Id).ToListAsync());
    }

    [Fact]
    public async Task Producer_stale_expected_version_never_persists_binding_or_ordinary_fields()
    {
        var seeded = await fixture.SeedAsync(true); await using var app = fixture.App(); using var client = fixture.Client(app, out _);
        using var update = await Update(client, seeded, 901, "stale edit", InvoiceConsumerFixture.Modified.AddSeconds(-1));
        Assert.Equal(HttpStatusCode.Conflict, update.StatusCode); await AssertUnchanged(seeded);
        await using var db = fixture.Context(); Assert.Null((await db.Quotations.AsNoTracking().SingleAsync(x => x.Id == seeded.Id)).DecisionOrderVersion);
    }

    [Fact]
    public async Task Producer_rejected_save_rolls_back_binding_with_all_mapped_fields()
    {
        var seeded = await fixture.SeedAsync(true);
        await using var db = fixture.Context();
        await db.Database.ExecuteSqlRawAsync("ALTER TABLE \"Quotation\" ADD CONSTRAINT \"producer88_reject_edit\" CHECK (\"Comment\" <> 'producer-bind-fault')");
        try
        {
            await using var app = fixture.App(); using var client = fixture.Client(app, out _);
            using var update = await Update(client, seeded, 901, "producer-bind-fault"); Assert.Equal(HttpStatusCode.InternalServerError, update.StatusCode);
            await AssertUnchanged(seeded); await using var fresh = fixture.Context();
            var row = await fresh.Quotations.AsNoTracking().SingleAsync(x => x.Id == seeded.Id); Assert.Null(row.DecisionOrderVersion); Assert.Equal(seeded.Comment, row.Comment);
        }
        finally { await db.Database.ExecuteSqlRawAsync("ALTER TABLE \"Quotation\" DROP CONSTRAINT \"producer88_reject_edit\""); }
    }

    [Fact]
    public async Task Producer_real_decline_then_accept_resets_prior_binding_and_keeps_first_outcome()
    {
        var seeded = await fixture.SeedAsync(true); var oldBound = InvoiceConsumerFixture.Modified.AddHours(-3);
        long outcomeId;
        await using (var setup = fixture.Context())
        {
            var row = await setup.Quotations.SingleAsync(x => x.Id == seeded.Id); row.DecisionOrderVersion = oldBound; await setup.SaveChangesAsync();
            outcomeId = (await setup.AcceptedOutcomes.SingleAsync(x => x.QuotationId == seeded.Id)).Id;
        }
        var orders = new InvoiceOrderTransport(); await using var app = fixture.App(orders); using var client = fixture.Client(app, out _);
        using var original = await Decide(client, seeded.Id, 0); Assert.Equal(HttpStatusCode.OK, original.StatusCode);
        using var decline = await client.PutAsJsonAsync($"/quotations/{seeded.Id}/decision", new { Accepted = false, EmployeeInitiated = true }); Assert.Equal(HttpStatusCode.OK, decline.StatusCode);
        await using (var check = fixture.Context()) Assert.Null((await check.Quotations.AsNoTracking().SingleAsync(x => x.Id == seeded.Id)).DecisionOrderVersion);
        using var accept = await client.PutAsJsonAsync($"/quotations/{seeded.Id}/decision", new { Accepted = true, EmployeeInitiated = true }); Assert.Equal(HttpStatusCode.OK, accept.StatusCode);
        await using var db = fixture.Context(); var final = await db.Quotations.AsNoTracking().SingleAsync(x => x.Id == seeded.Id);
        Assert.Null(final.DecisionOrderVersion); Assert.True(final.Accepted); Assert.Equal(seeded.AcceptedUtc, final.AcceptedUtc);
        var outcome = Assert.Single(await db.AcceptedOutcomes.Where(x => x.QuotationId == seeded.Id).ToListAsync()); Assert.Equal(outcomeId, outcome.Id); Assert.Equal(seeded.AcceptedUtc, outcome.AcceptedUtc);
        var requests = orders.Requests.ToArray(); Assert.Equal(3, requests.Length); Assert.Equal(3, requests.Select(x => x.Key).Distinct().Count());
        Assert.EndsWith("/declined", requests[1].Path, StringComparison.Ordinal);
    }


    private static async Task<HttpResponseMessage> Update(HttpClient client, Quotation seeded, int? invoice, string comment, DateTime? expected = null)
    {
        using var request = new HttpRequestMessage(HttpMethod.Put, $"/quotations/{seeded.Id}") { Content = JsonContent.Create(new { seeded.CustomerId, seeded.EmployeeId, InvoiceId = invoice, seeded.Period, seeded.ExpirationDate, seeded.Subtotal, seeded.Vat, seeded.Total, seeded.WithholdingTax, seeded.CurrencyId, Comment = comment, seeded.Fob, seeded.ShippedVia, seeded.Terms, seeded.Accepted }) };
        if ((expected ?? seeded.ModifiedDate) is DateTime version) request.Headers.Add("X-Expected-Modified-Date", new DateTimeOffset(DateTime.SpecifyKind(version, DateTimeKind.Utc)).ToString("O"));
        return await client.SendAsync(request);
    }

    [Fact]
    public async Task Consumer_historical_partial_saga_late_invoice_must_keep_original_order_keys()
    {
        var seeded = await fixture.SeedAsync(accepted: true);
        long originalOutcomeId;
        await using (var before = fixture.Context()) originalOutcomeId = (await before.AcceptedOutcomes.SingleAsync(x => x.QuotationId == seeded.Id)).Id;
        var orders = new InvoiceOrderTransport { Fail = true };
        await using var app = fixture.App(orders);
        using var client = fixture.Client(app, out var trace);
        using var partial = await Decide(client, seeded.Id, 0);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, partial.StatusCode);
        Assert.NotEmpty(orders.Requests);
        orders.Fail = false;
        var consumer = new InvoiceQuotationCompletionClient(client);
        var operation = Guid.NewGuid();
        await consumer.CompleteAsync(seeded.Id, 901, operation, default);
        await consumer.CompleteAsync(seeded.Id, 901, operation, default);
        var update = Assert.Single(trace.Responses, x => x.Path == $"/quotations/{seeded.Id}" && x.Method == "PUT");
        Assert.Equal(new DateTimeOffset(DateTime.SpecifyKind(InvoiceConsumerFixture.Modified, DateTimeKind.Utc)).ToString("O"), update.ExpectedVersion);
        using (var wire = JsonDocument.Parse(update.Body!))
        {
            Assert.Equal(new[] { "accepted", "comment", "currencyId", "customerId", "employeeId", "expirationDate", "fob", "invoiceId", "period", "shippedVia", "subtotal", "terms", "total", "vat", "withholdingTax" }, wire.RootElement.EnumerateObject().Select(x => x.Name).OrderBy(x => x, StringComparer.Ordinal).ToArray());
            Assert.Equal(901, wire.RootElement.GetProperty("invoiceId").GetInt32());
            Assert.True(wire.RootElement.GetProperty("accepted").GetBoolean());
            Assert.Equal(100m, wire.RootElement.GetProperty("subtotal").GetDecimal());
            Assert.Equal(7m, wire.RootElement.GetProperty("vat").GetDecimal());
            Assert.Equal(107m, wire.RootElement.GetProperty("total").GetDecimal());
        }
        var decisions = trace.Responses.Where(x => x.Path.EndsWith("/decision", StringComparison.Ordinal) && x.Key is not null).ToArray();
        Assert.Equal(2, decisions.Length);
        Assert.All(decisions, request =>
        {
            Assert.Equal(operation.ToString("D"), request.Key); Assert.Null(request.ExpectedVersion);
            using var wire = JsonDocument.Parse(request.Body!);
            var property = Assert.Single(wire.RootElement.EnumerateObject());
            Assert.Equal("accepted", property.Name); Assert.True(property.Value.GetBoolean());
        });
        await using var db = fixture.Context();
        var row = await db.Quotations.AsNoTracking().SingleAsync(x => x.Id == seeded.Id);
        Assert.Equal(901, row.InvoiceId); Assert.True(row.Accepted);
        Assert.Equal(100m, row.Subtotal); Assert.Equal(7m, row.Vat); Assert.Equal(107m, row.Total); Assert.Equal(3m, row.WithholdingTax);
        Assert.Equal(seeded.AcceptedUtc, row.AcceptedUtc);
        Assert.Equal("employee", row.AcceptanceOrigin);
        Assert.Equal(seeded.SourceJourneyId, row.SourceJourneyId); Assert.Equal(17, row.SourceRequestId);
        var outcome = Assert.Single(await db.AcceptedOutcomes.Where(x => x.QuotationId == seeded.Id).ToListAsync());
        Assert.Equal(originalOutcomeId, outcome.Id);
        Assert.Equal(seeded.AcceptedUtc, outcome.AcceptedUtc); Assert.Equal("employee", outcome.AcceptanceOrigin);
        Assert.Equal($"quotation-{seeded.Id}:accepted:v1", outcome.EventKey);
        Assert.Equal(seeded.SourceJourneyId, outcome.SourceJourneyId); Assert.Equal(17, outcome.SourceRequestId);
        Assert.Contains(trace.Responses, x => x.Path == $"/quotations/{seeded.Id}" && x.Method == "PUT" && x.Status == HttpStatusCode.NoContent);
        var originalVersion = DateTime.SpecifyKind(InvoiceConsumerFixture.Modified, DateTimeKind.Utc);
        var expected = $"quotation-{seeded.Id}-accepted-{originalVersion.Ticks:x}-order-701";
        // Literal legacy version differs from AcceptedUtc; no use of production key builder.
        Assert.All(orders.Requests, x => Assert.Equal(expected, x.Key));
        Assert.Equal(InvoiceConsumerFixture.Modified, row.DecisionOrderVersion);
    }

    [Fact]
    public async Task Consumer_same_invoice_replay_preserves_first_acceptance_and_no_full_update()
    {
        var seeded = await fixture.SeedAsync(true, 901);
        var orders = new InvoiceOrderTransport();
        await using var app = fixture.App(orders);
        using var client = fixture.Client(app, out var trace);
        var consumer = new InvoiceQuotationCompletionClient(client); var key = Guid.NewGuid();
        await consumer.CompleteAsync(seeded.Id, 901, key, default);
        await consumer.CompleteAsync(seeded.Id, 901, key, default);
        Assert.All(trace.Responses.Where(x => x.Method == "PUT"), x => Assert.Equal(key.ToString("D"), x.Key));
        Assert.DoesNotContain(trace.Responses, x => x.Method == "PUT" && x.Path == $"/quotations/{seeded.Id}");
        Assert.Equal(2, orders.Requests.Count); Assert.Single(orders.Requests.Select(x => x.Key).Distinct());
        await AssertUnchanged(seeded);
    }

    [Fact]
    public async Task Consumer_different_invoice_conflicts_before_any_mutation()
    {
        var seeded = await fixture.SeedAsync(true, 901);
        await using var app = fixture.App(); using var client = fixture.Client(app, out var trace);
        await Assert.ThrowsAsync<InvoiceCreationConflictException>(() => new InvoiceQuotationCompletionClient(client).CompleteAsync(seeded.Id, 902, Guid.NewGuid(), default));
        Assert.Single(trace.Responses); await AssertUnchanged(seeded);
    }

    [Fact]
    public async Task Consumer_same_invoice_partial_order_failure_retry_preserves_keys_and_outcome()
    {
        var seeded = await fixture.SeedAsync(true, 901); var orders = new InvoiceOrderTransport { Fail = true };
        await using var app = fixture.App(orders); using var client = fixture.Client(app, out _);
        var consumer = new InvoiceQuotationCompletionClient(client); var key = Guid.NewGuid();
        await Assert.ThrowsAsync<InvoiceCreationDependencyException>(() => consumer.CompleteAsync(seeded.Id, 901, key, default));
        Assert.NotEmpty(orders.Requests); orders.Fail = false;
        await consumer.CompleteAsync(seeded.Id, 901, key, default);
        Assert.Single(orders.Requests.Select(x => x.Key).Distinct()); await AssertUnchanged(seeded);
    }

    [Fact]
    public async Task Direct_approved_employee_late_attachment_preserves_historical_order_keys()
    {
        var seeded = await fixture.SeedAsync(true); var orders = new InvoiceOrderTransport { Fail = true };
        await using var app = fixture.App(orders); using var client = fixture.Client(app, out _);
        using var partial = await Decide(client, seeded.Id, 0); Assert.Equal(HttpStatusCode.ServiceUnavailable, partial.StatusCode);
        orders.Fail = false;
        using var attach = await Decide(client, seeded.Id, 901); using var replay = await Decide(client, seeded.Id, 901);
        Assert.Equal(HttpStatusCode.OK, attach.StatusCode); Assert.Equal(HttpStatusCode.OK, replay.StatusCode);
        Assert.Single(orders.Requests.Select(x => x.Key).Distinct());
        await using var db = fixture.Context(); var row = await db.Quotations.AsNoTracking().SingleAsync(x => x.Id == seeded.Id);
        Assert.Equal(InvoiceConsumerFixture.Modified, row.DecisionOrderVersion); Assert.Equal(901, row.InvoiceId);
        Assert.Equal(seeded.AcceptedUtc, row.AcceptedUtc); Assert.Single(await db.AcceptedOutcomes.Where(x => x.QuotationId == seeded.Id).ToListAsync());
    }

    [Theory]
    [InlineData("workload", true, HttpStatusCode.Forbidden)]
    [InlineData("employee", false, HttpStatusCode.Forbidden)]
    [InlineData("unsigned", true, HttpStatusCode.Unauthorized)]
    public async Task Direct_employee_intent_boundary_denials_leave_all_state_unchanged(string profile, bool iamAllowed, HttpStatusCode status)
    {
        var seeded = await fixture.SeedAsync(); await using var app = fixture.App(allowed: iamAllowed);
        using var client = fixture.Client(app, out _, profile);
        using var response = await Decide(client, seeded.Id, 901);
        Assert.Equal(status, response.StatusCode); await AssertUnchanged(seeded);
    }

    private static Task<HttpResponseMessage> Decide(HttpClient client, int id, int invoice) => client.PutAsJsonAsync($"/quotations/{id}/decision", new { Accepted = true, EmployeeInitiated = true, InvoiceId = invoice });
    private async Task AssertUnchanged(Quotation seeded)
    {
        await using var db = fixture.Context(); var row = await db.Quotations.AsNoTracking().SingleAsync(x => x.Id == seeded.Id);
        Assert.Equal(seeded.InvoiceId, row.InvoiceId); Assert.Equal(seeded.Accepted, row.Accepted);
        Assert.Equal(seeded.ModifiedDate, row.ModifiedDate); Assert.Equal(seeded.AcceptedUtc, row.AcceptedUtc);
        Assert.Equal(seeded.AcceptanceOrigin, row.AcceptanceOrigin); Assert.Equal(seeded.SourceJourneyId, row.SourceJourneyId);
        Assert.Equal(seeded.SourceRequestId, row.SourceRequestId);
        Assert.Equal(seeded.Accepted == true ? 1 : 0, await db.AcceptedOutcomes.CountAsync(x => x.QuotationId == seeded.Id));
    }
}

public sealed class InvoiceConsumerFixture : IAsyncLifetime
{
    public static readonly DateTime Modified = new(2026, 9, 29, 4, 4, 5, DateTimeKind.Unspecified);
    private PostgreSqlContainer postgres = null!;
    private IContainer redis = null!;
    private Infrastructure.DisposableContainerPair? containers;
    private readonly RSA key = RSA.Create(2048);
    public List<string> StorageDiagnostics { get; } = [];
    private readonly List<string> connectionPhases = [];
    private string Requests => new NpgsqlConnectionStringBuilder(postgres.GetConnectionString()) { Database = "invoice_consumer_requests" }.ConnectionString;
    public QuotationDbContext Context() => new(new DbContextOptionsBuilder<QuotationDbContext>().UseNpgsql(postgres.GetConnectionString()).Options);
    public async Task InitializeAsync()
    {
        containers = await Infrastructure.DisposableContainerPair.StartAsync("quotation99-invoice-consumer",
            attempt => new PostgreSqlBuilder("postgres:18-alpine").WithDockerEndpoint(attempt.Endpoint)
                .WithName(attempt.Name).WithLabel(attempt.Labels)
                .WithCreateParameterModifier(parameters => ConfigureOwnedStorage(parameters, "5432/tcp", "/var/lib/postgresql", 268435456)).Build(),
            attempt => new ContainerBuilder("redis:7-alpine").WithDockerEndpoint(attempt.Endpoint)
                .WithName(attempt.Name).WithLabel(attempt.Labels).WithPortBinding(6379, true)
                .WithWaitStrategy(Wait.ForUnixContainer().UntilInternalTcpPortIsAvailable(6379))
                .WithCreateParameterModifier(parameters => ConfigureOwnedStorage(parameters, "6379/tcp", "/data", 16777216)).Build());
        postgres = (PostgreSqlContainer)containers.First;
        redis = containers.Second;
        await CaptureStorageAsync("ready");
        try
        {
            await using (var connection = new NpgsqlConnection(postgres.GetConnectionString()))
            {
                await CaptureConnectionAsync("open-start", connection);
                await connection.OpenAsync();
                await CaptureConnectionAsync("open-complete", connection);
                await using var command = new NpgsqlCommand("CREATE DATABASE invoice_consumer_requests", connection);
                await CaptureConnectionAsync("create-start", connection);
                await command.ExecuteNonQueryAsync();
                await CaptureConnectionAsync("create-complete", connection);
            }
            await CaptureStorageAsync("second-database");
            await using var db = Context(); await db.Database.MigrateAsync();
            await using var requests = new QuotationRequestDbContext(new DbContextOptionsBuilder<QuotationRequestDbContext>().UseNpgsql(Requests).Options); await requests.Database.MigrateAsync();
            await CaptureStorageAsync("both-migrations");
        }
        catch (Exception error)
        {
            await CaptureStorageAsync("initialization-failed");
            await Infrastructure.OwnedPostgresDiagnostics.PreserveFailureAsync(() =>
            {
                error.Data["OwnedPostgresStorage"] = string.Join(Environment.NewLine, StorageDiagnostics);
                Console.WriteLine(string.Join(Environment.NewLine, StorageDiagnostics));
                return Task.FromResult(string.Empty);
            });
            throw;
        }
    }
    private async Task CaptureStorageAsync(string phase)
    {
        var snapshot = await Infrastructure.OwnedPostgresDiagnostics.ObserveAsync(postgres, "quotation99-invoice-consumer", phase);
        StorageDiagnostics.Add(await Infrastructure.OwnedPostgresDiagnostics.PreserveFailureAsync(() =>
        {
            var metadata = System.Text.Json.Nodes.JsonNode.Parse(snapshot)!.AsObject();
            var phases = new System.Text.Json.Nodes.JsonArray();
            foreach (var item in connectionPhases) phases.Add(System.Text.Json.Nodes.JsonNode.Parse(item));
            metadata["ConnectionPhases"] = phases;
            return Task.FromResult(metadata.ToJsonString());
        }));
    }
    private async Task CaptureConnectionAsync(string phase, NpgsqlConnection connection)
    {
        if (connectionPhases.Count < 4)
            connectionPhases.Add(await Infrastructure.OwnedPostgresDiagnostics.ObserveConnectionAsync(postgres, "quotation99-invoice-consumer", phase, connection));
    }
    private static void ConfigureOwnedStorage(CreateContainerParameters parameters, string port, string path, int bytes)
    {
        parameters.HostConfig ??= new HostConfig();
        parameters.HostConfig.PortBindings ??= new Dictionary<string, IList<PortBinding>>();
        parameters.HostConfig.PortBindings[port] = [new PortBinding { HostIP = "127.0.0.1", HostPort = "" }];
        parameters.HostConfig.Tmpfs = new Dictionary<string, string> { [path] = $"rw,noexec,nosuid,size={bytes}" };
    }
    public async Task<Quotation> SeedAsync(bool? accepted = null, int? invoice = null)
    {
        await using var db = Context(); var row = new Quotation
        {
            CustomerId = 42,
            EmployeeId = 7,
            InvoiceId = invoice,
            CurrencyId = 764,
            Period = 30,
            ExpirationDate = new DateTime(2035, 1, 1),
            Subtotal = 100m,
            Vat = 7m,
            Total = 107m,
            WithholdingTax = 3m,
            Comment = "synthetic invoice consumer",
            Accepted = accepted,
            CreatedDate = Modified.AddHours(-2),
            ModifiedDate = Modified,
            AcceptedUtc = accepted == true ? Modified.AddHours(-1) : null,
            AcceptanceOrigin = accepted == true ? "employee" : null,
            SourceRequestId = 17,
            SourceJourneyId = Guid.NewGuid()
        };
        db.Quotations.Add(row); await db.SaveChangesAsync();
        db.OrderLinks.Add(new QuotationOrderLink { QuotationId = row.Id, OrderId = 701, CreatedDate = Modified });
        if (accepted == true) db.AcceptedOutcomes.Add(new QuotationAcceptedOutcome { QuotationId = row.Id, EventKey = $"quotation-{row.Id}:accepted:v1", AcceptedUtc = row.AcceptedUtc!.Value, AcceptanceOrigin = "employee", SourceRequestId = 17, SourceJourneyId = row.SourceJourneyId });
        await db.SaveChangesAsync(); return row;
    }
    public WebApplicationFactory<Program> App(InvoiceOrderTransport? orders = null, bool allowed = true) => new Factory(this, orders ?? new(), allowed);
    public HttpClient Client(WebApplicationFactory<Program> app, out InvoiceConsumerTrace trace, string profile = "employee")
    {
        using var bootstrap = app.CreateClient(); trace = new InvoiceConsumerTrace { InnerHandler = app.Server.CreateHandler() };
        var client = new HttpClient(trace) { BaseAddress = new Uri("http://localhost") };
        if (profile != "unsigned") client.DefaultRequestHeaders.Authorization = new("Bearer", Token(profile));
        return client;
    }
    private string Token(string profile)
    {
        var subject = profile == "employee" ? "employee-invoice-fixture" : profile == "quotation" ? "service:legacy-quotation" : "service:legacy-accounting";
        var claims = new List<Claim> { new("sub", subject), new("identity_kind", profile == "employee" ? "employee" : "service"), new("permissions", "legacy.quotations.read"), new("permissions", "legacy.customer-quotations.read"), new("permissions", "legacy.quotations.update") };
        if (profile == "employee") claims.Add(new("role", "Employee"));
        return new JwtSecurityTokenHandler().WriteToken(new JwtSecurityToken("https://invoice-fixture-auth.example", "invoice-fixture-services", claims, DateTime.UtcNow.AddMinutes(-1), DateTime.UtcNow.AddMinutes(10), new SigningCredentials(new RsaSecurityKey(key), SecurityAlgorithms.RsaSha256)));
    }
    public async Task DisposeAsync()
    {
        try { if (containers is not null) await containers.DisposeAsync(); }
        finally { key.Dispose(); }
    }
    private sealed class Factory(InvoiceConsumerFixture fixture, InvoiceOrderTransport orders, bool allowed) : WebApplicationFactory<Program>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Production");
            foreach (var setting in new Dictionary<string, string?>
            {
                ["ConnectionStrings:QuotationDbContext"] = fixture.postgres.GetConnectionString(),
                ["ConnectionStrings:QuotationRequestDbContext"] = fixture.Requests,
                ["ConnectionStrings:redis"] = $"{fixture.redis.Hostname}:{fixture.redis.GetMappedPublicPort(6379)}",
                ["Jwt:PublicKey"] = Convert.ToBase64String(Encoding.UTF8.GetBytes(fixture.key.ExportSubjectPublicKeyInfoPem())),
                ["Jwt:Issuer"] = "https://invoice-fixture-auth.example",
                ["Jwt:Audience"] = "invoice-fixture-services",
                ["Services:Auth:BaseUrl"] = "https://invoice-fixture-auth.example",
                ["Services:Order:BaseUrl"] = "https://invoice-fixture-order.example",
                ["ServiceAuthentication:ClientId"] = "legacy-quotation",
                ["ServiceAuthentication:ClientSecret"] = "synthetic-invoice-fixture-only",
                ["IAM:LivePermissionChecks:Credential"] = "synthetic-invoice-live-check",
                ["OTEL_EXPORTER_OTLP_ENDPOINT"] = "",
                ["Observability:RuntimeMetricsEnabled"] = "false"
            }) builder.UseSetting(setting.Key, setting.Value);
            builder.ConfigureLogging(logging => logging.ClearProviders());
            builder.ConfigureTestServices(services =>
            {
                services.AddScoped<IIamServiceClient, IamServiceClient>();
                services.AddHttpClient("IAMService", client => client.BaseAddress = new Uri("https://invoice-fixture-iam.example")).ConfigurePrimaryHttpMessageHandler(() => new InvoiceIamTransport(allowed));
                services.Configure<HttpClientFactoryOptions>(LegacyServiceAccessTokenProvider.HttpClientName, options => options.HttpMessageHandlerBuilderActions.Add(http => http.PrimaryHandler = new InvoiceLoginTransport(fixture.Token("quotation"))));
                services.Configure<HttpClientFactoryOptions>(nameof(Legacy.Maliev.QuotationService.Application.Interfaces.IOrderDecisionClient), options => options.HttpMessageHandlerBuilderActions.Add(http => http.PrimaryHandler = orders));
            });
        }
    }
    private sealed class InvoiceIamTransport(bool allowed) : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Assert.Equal("/iam/v1/auth/check-permission", request.RequestUri!.AbsolutePath);
            Assert.Equal("synthetic-invoice-live-check", Assert.Single(request.Headers.GetValues("X-Maliev-IAM-Live-Check-Key")));
            using var json = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(cancellationToken));
            Assert.Contains(json.RootElement.GetProperty("principalId").GetString(), new[] { "employee-invoice-fixture", "service:legacy-accounting" });
            Assert.Contains(json.RootElement.GetProperty("permissionId").GetString(), new[] { "legacy.quotations.update", "legacy.quotations.read", "legacy.customer-quotations.read" });
            Assert.Equal("global", json.RootElement.GetProperty("resourcePath").GetString());
            if (json.RootElement.GetProperty("permissionId").GetString() == "legacy.quotations.update") Assert.True(json.RootElement.GetProperty("bypassCache").GetBoolean());
            return new(HttpStatusCode.OK) { Content = JsonContent.Create(new { allowed }) };
        }
    }
    private sealed class InvoiceLoginTransport(string token) : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Assert.Equal("/auth/v1/service/login", request.RequestUri!.AbsolutePath); Assert.Null(request.Headers.Authorization);
            using var json = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(cancellationToken));
            Assert.Equal("legacy-quotation", json.RootElement.GetProperty("clientId").GetString());
            Assert.Equal("synthetic-invoice-fixture-only", json.RootElement.GetProperty("clientSecret").GetString());
            return new(HttpStatusCode.OK) { Content = JsonContent.Create(new { accessToken = token, expiresIn = 300 }) };
        }
    }
}
public sealed class InvoiceConsumerTrace : DelegatingHandler
{
    public ConcurrentQueue<(string Method, string Path, HttpStatusCode Status, string? Key, string? ExpectedVersion, string? Body)> Responses { get; } = new();
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var body = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
        var key = request.Headers.TryGetValues("Idempotency-Key", out var keys) ? Assert.Single(keys) : null;
        var expected = request.Headers.TryGetValues("X-Expected-Modified-Date", out var versions) ? Assert.Single(versions) : null;
        var response = await base.SendAsync(request, cancellationToken);
        Responses.Enqueue((request.Method.Method, request.RequestUri!.AbsolutePath, response.StatusCode, key, expected, body)); return response;
    }
}
public sealed class InvoiceOrderTransport : HttpMessageHandler
{
    public bool Fail { get; set; }
    public ConcurrentQueue<(string Path, string Key)> Requests { get; } = new();
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        Assert.Equal("Bearer", request.Headers.Authorization?.Scheme);
        Assert.Contains(request.RequestUri!.AbsolutePath, new[] { "/orderstatuses/histories/701/accepted", "/orderstatuses/histories/701/declined" });
        Requests.Enqueue((request.RequestUri.AbsolutePath, Assert.Single(request.Headers.GetValues("Idempotency-Key"))));
        return Task.FromResult(new HttpResponseMessage(Fail ? HttpStatusCode.ServiceUnavailable : HttpStatusCode.Created));
    }
}
