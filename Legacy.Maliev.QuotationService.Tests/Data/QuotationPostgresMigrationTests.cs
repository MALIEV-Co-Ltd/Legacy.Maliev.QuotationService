using System.Data.Common;
using System.Security.Cryptography;
using System.Text;
using Legacy.Maliev.QuotationService.Application.Interfaces;
using Legacy.Maliev.QuotationService.Application.Models;
using Legacy.Maliev.QuotationService.Data;
using Legacy.Maliev.QuotationService.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Moq;
using Testcontainers.PostgreSql;

namespace Legacy.Maliev.QuotationService.Tests.Data;

public sealed class QuotationPostgresMigrationTests : IAsyncLifetime
{
    private readonly PostgreSqlContainer quotationPostgres = new PostgreSqlBuilder("postgres:18-alpine").Build();
    private readonly PostgreSqlContainer requestPostgres = new PostgreSqlBuilder("postgres:18-alpine").Build();

    public Task InitializeAsync() => Task.WhenAll(quotationPostgres.StartAsync(), requestPostgres.StartAsync());
    public async Task DisposeAsync() { await quotationPostgres.DisposeAsync(); await requestPostgres.DisposeAsync(); }

    [Fact]
    public async Task InitialMigrations_PreserveFinancialRequestFileAndDocumentBehavior()
    {
        await using var quotationContext = QuotationContext(); await using var requestContext = RequestContext();
        await Task.WhenAll(quotationContext.Database.MigrateAsync(), requestContext.Database.MigrateAsync());
        var repository = Repository(quotationContext, requestContext);
        var quotation = await repository.CreateQuotationAsync(QuotationRequest(), CancellationToken.None);
        var line = await repository.CreateOrderItemAsync(new(quotation.Id, null, "Legacy line", 2, 50m), CancellationToken.None);
        var file = await repository.CreateQuotationFileAsync(quotation.Id, "legacy-quotes", "quotes/1.pdf", CancellationToken.None);
        var order = await repository.CreateOrderLinkAsync(quotation.Id, 77, CancellationToken.None);
        var request = await repository.CreateRequestAsync(new("Ada", "Lovelace", "ada@example.test", "02", "TH", "MALIEV", "TAX", "quote", "internal", null), CancellationToken.None);
        var requestFile = await repository.CreateRequestFileAsync(request.Id, "legacy-requests", "requests/1.stl", CancellationToken.None);
        var loaded = await repository.GetQuotationAsync(quotation.Id, CancellationToken.None);
        var snapshot = await repository.GetDocumentSnapshotAsync(quotation.Id, CancellationToken.None);

        Assert.Equal(104m, loaded?.QuotedAmount); Assert.Equal(100m, line.Subtotal); Assert.Equal("quotes/1.pdf", file?.ObjectName); Assert.Equal(77, order?.OrderId);
        Assert.Equal("Ada", request.FirstName); Assert.Equal("requests/1.stl", requestFile?.ObjectName);
        Assert.Single(snapshot!.OrderItems); Assert.Single(snapshot.Files);
        Assert.Equal(4, await quotationContext.Database.SqlQueryRaw<int>("SELECT COUNT(*)::int AS \"Value\" FROM information_schema.tables WHERE table_schema='public' AND table_name IN ('Quotation','OrderItem','QuotationFile','QuotationHasOrder')").SingleAsync());
        Assert.Equal(3, await requestContext.Database.SqlQueryRaw<int>("SELECT COUNT(*)::int AS \"Value\" FROM information_schema.tables WHERE table_schema='public' AND table_name IN ('Request','RequestFile','RequestCreateIdempotency')").SingleAsync());
        Assert.Equal(
            ["Fingerprint", "KeyHash", "RequestID"],
            await requestContext.Database.SqlQueryRaw<string>(
                    "SELECT column_name AS \"Value\" FROM information_schema.columns WHERE table_schema='public' AND table_name='RequestCreateIdempotency' ORDER BY column_name")
                .ToListAsync());
    }

    [Fact]
    public async Task ExpiryStatsAndModifiedDateConcurrency_AreAtomicAndDeterministic()
    {
        await using var quotationContext = QuotationContext(); await using var requestContext = RequestContext();
        await Task.WhenAll(quotationContext.Database.MigrateAsync(), requestContext.Database.MigrateAsync()); var repository = Repository(quotationContext, requestContext);
        var quotation = await repository.CreateQuotationAsync(QuotationRequest(expiration: new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc)), CancellationToken.None);
        var stale = quotation.ModifiedDate!.Value;
        Assert.Equal(1, await repository.DeclineExpiredAsync(new DateTime(2026, 7, 15, 0, 0, 0, DateTimeKind.Utc), CancellationToken.None));
        quotationContext.ChangeTracker.Clear();
        Assert.Equal(UpdateResult.Conflict, await repository.UpdateQuotationAsync(quotation.Id, QuotationRequest(), new DateTimeOffset(stale), CancellationToken.None));
        var stats = await repository.GetStatsAsync(CancellationToken.None); Assert.Equal(0, stats.Accepted); Assert.Equal(1, stats.Declined); Assert.Equal(0, stats.Open);
        Assert.Equal(3m, await repository.GetWithholdingTaxAsync(quotation.Id, CancellationToken.None));
    }

    [Fact]
    public async Task Stats_UseOnePostgresCommandAndPreserveNullableStatusBuckets()
    {
        await using var setupQuotationContext = QuotationContext();
        await using var requestContext = RequestContext();
        await Task.WhenAll(
            setupQuotationContext.Database.MigrateAsync(),
            requestContext.Database.MigrateAsync());
        var setupRepository = Repository(setupQuotationContext, requestContext);
        var baseline = await setupRepository.GetStatsAsync(CancellationToken.None);
        await setupRepository.CreateQuotationAsync(QuotationRequest(accepted: true), CancellationToken.None);
        await setupRepository.CreateQuotationAsync(QuotationRequest(accepted: false), CancellationToken.None);
        await setupRepository.CreateQuotationAsync(QuotationRequest(accepted: null), CancellationToken.None);

        var commandCounter = new CommandCounter();
        await using var measuredQuotationContext = QuotationContext(commandCounter);
        var measuredRepository = Repository(measuredQuotationContext, requestContext);

        var stats = await measuredRepository.GetStatsAsync(CancellationToken.None);

        Assert.Equal(1, commandCounter.ReaderCommands);
        Assert.Equal(baseline.Accepted + 1, stats.Accepted);
        Assert.Equal(baseline.Declined + 1, stats.Declined);
        Assert.Equal(baseline.Open + 1, stats.Open);
    }

    [Fact]
    public async Task CustomerQuotationDetails_EnforceOwnershipAndComposeReadOnlyMetadata()
    {
        await using var quotationContext = QuotationContext();
        await using var requestContext = RequestContext();
        await Task.WhenAll(
            quotationContext.Database.MigrateAsync(),
            requestContext.Database.MigrateAsync());
        var repository = Repository(quotationContext, requestContext);
        var quotation = await repository.CreateQuotationAsync(
            QuotationRequest(customerId: 42),
            CancellationToken.None);
        await repository.CreateOrderItemAsync(
            new(quotation.Id, 77, "Owned line", 2, 50m),
            CancellationToken.None);
        await repository.CreateOrderLinkAsync(quotation.Id, 77, CancellationToken.None);
        await repository.CreateQuotationFileAsync(
            quotation.Id,
            "legacy-quotes",
            "quotes/owned.pdf",
            CancellationToken.None);

        Assert.Null(await repository.GetCustomerQuotationAsync(41, quotation.Id, CancellationToken.None));
        var details = await repository.GetCustomerQuotationAsync(42, quotation.Id, CancellationToken.None);

        Assert.NotNull(details);
        Assert.Equal(42, details.Quotation.CustomerId);
        Assert.Equal("Owned line", Assert.Single(details.OrderItems).Description);
        Assert.Equal(77, Assert.Single(details.Orders).OrderId);
        Assert.Equal("quotes/owned.pdf", Assert.Single(details.Files).ObjectName);
    }

    [Fact]
    public async Task RequestFileCreate_RestartOrCacheLoss_ReconcilesExistingNaturalTuple()
    {
        await using var setupQuotationContext = QuotationContext();
        await using var setupRequestContext = RequestContext();
        await Task.WhenAll(
            setupQuotationContext.Database.MigrateAsync(),
            setupRequestContext.Database.MigrateAsync());
        var setupRepository = Repository(setupQuotationContext, setupRequestContext);
        var request = await setupRepository.CreateRequestAsync(
            new("Replay", "Test", "replay@example.test", null, "TH", null, null, "request-file replay", null, null),
            CancellationToken.None);
        var first = await setupRepository.CreateRequestFileAsync(
            request.Id,
            "legacy-requests",
            "instant-quotation/replay/model.stl",
            CancellationToken.None);

        await using var restartedQuotationContext = QuotationContext();
        await using var restartedRequestContext = RequestContext();
        var restartedRepository = Repository(restartedQuotationContext, restartedRequestContext);
        var replay = await restartedRepository.CreateRequestFileAsync(
            request.Id,
            " legacy-requests ",
            " instant-quotation/replay/model.stl ",
            CancellationToken.None);

        Assert.NotNull(first);
        Assert.Equal(first.Id, replay?.Id);
        Assert.Equal(
            1,
            await restartedRequestContext.Files.CountAsync(
                file => file.RequestId == request.Id
                    && file.Bucket == "legacy-requests"
                    && file.ObjectName == "instant-quotation/replay/model.stl"));
    }

    [Fact]
    public async Task RequestFileCreate_ConcurrentIdenticalAttempts_InsertOnce()
    {
        await using var setupQuotationContext = QuotationContext();
        await using var setupRequestContext = RequestContext();
        await Task.WhenAll(
            setupQuotationContext.Database.MigrateAsync(),
            setupRequestContext.Database.MigrateAsync());
        var setupRepository = Repository(setupQuotationContext, setupRequestContext);
        var request = await setupRepository.CreateRequestAsync(
            new("Concurrent", "Test", "concurrent@example.test", null, "TH", null, null, "request-file concurrency", null, null),
            CancellationToken.None);

        await using var quotationContext1 = QuotationContext();
        await using var requestContext1 = RequestContext();
        await using var quotationContext2 = QuotationContext();
        await using var requestContext2 = RequestContext();
        var repository1 = Repository(quotationContext1, requestContext1);
        var repository2 = Repository(quotationContext2, requestContext2);

        var attempts = await Task.WhenAll(
            repository1.CreateRequestFileAsync(
                request.Id,
                "legacy-requests",
                "instant-quotation/concurrent/model.stl",
                CancellationToken.None),
            repository2.CreateRequestFileAsync(
                request.Id,
                "legacy-requests",
                "instant-quotation/concurrent/model.stl",
                CancellationToken.None));

        Assert.All(attempts, Assert.NotNull);
        Assert.Equal(attempts[0]!.Id, attempts[1]!.Id);
        await using var verificationContext = RequestContext();
        Assert.Equal(
            1,
            await verificationContext.Files.CountAsync(
                file => file.RequestId == request.Id
                    && file.Bucket == "legacy-requests"
                    && file.ObjectName == "instant-quotation/concurrent/model.stl"));
    }

    [Fact]
    public async Task RequestFileCreate_PreexistingDuplicateTuple_ReturnsOldestWithoutAddingAnother()
    {
        await using var setupQuotationContext = QuotationContext();
        await using var setupRequestContext = RequestContext();
        await Task.WhenAll(
            setupQuotationContext.Database.MigrateAsync(),
            setupRequestContext.Database.MigrateAsync());
        var setupRepository = Repository(setupQuotationContext, setupRequestContext);
        var request = await setupRepository.CreateRequestAsync(
            new("Historical", "Duplicate", "historical@example.test", null, "TH", null, null, "legacy duplicate", null, null),
            CancellationToken.None);
        var now = DateTime.UtcNow;
        setupRequestContext.Files.AddRange(
            new QuotationRequestFile
            {
                RequestId = request.Id,
                Bucket = "legacy-requests",
                ObjectName = "instant-quotation/historical/model.stl",
                CreatedDate = now,
                ModifiedDate = now,
            },
            new QuotationRequestFile
            {
                RequestId = request.Id,
                Bucket = "legacy-requests",
                ObjectName = "instant-quotation/historical/model.stl",
                CreatedDate = now.AddSeconds(1),
                ModifiedDate = now.AddSeconds(1),
            });
        await setupRequestContext.SaveChangesAsync();
        var oldestId = await setupRequestContext.Files
            .Where(file => file.RequestId == request.Id)
            .MinAsync(file => file.Id);

        await using var restartedQuotationContext = QuotationContext();
        await using var restartedRequestContext = RequestContext();
        var replay = await Repository(restartedQuotationContext, restartedRequestContext)
            .CreateRequestFileAsync(
                request.Id,
                "legacy-requests",
                "instant-quotation/historical/model.stl",
                CancellationToken.None);

        Assert.Equal(oldestId, replay?.Id);
        Assert.Equal(
            2,
            await restartedRequestContext.Files.CountAsync(file => file.RequestId == request.Id));
    }

    [Fact]
    public async Task RequestCreate_LostResponseAndRestart_ReplaysOriginalDurableResult()
    {
        await using var setupQuotationContext = QuotationContext();
        await using var setupRequestContext = RequestContext();
        await Task.WhenAll(
            setupQuotationContext.Database.MigrateAsync(),
            setupRequestContext.Database.MigrateAsync());
        IQuotationService firstService = Repository(setupQuotationContext, setupRequestContext);
        var request = Request("lost-response@example.test", "Lost response");
        var keyHash = Hash("legacy-web-quotation-lost-response");
        var fingerprint = Hash("lost-response-payload");

        var first = await firstService.CreateRequestIdempotentlyAsync(
            request,
            keyHash,
            fingerprint,
            CancellationToken.None);

        await using var restartedQuotationContext = QuotationContext();
        await using var restartedRequestContext = RequestContext();
        IQuotationService restartedService = Repository(restartedQuotationContext, restartedRequestContext);
        var replay = await restartedService.CreateRequestIdempotentlyAsync(
            request,
            keyHash,
            fingerprint,
            CancellationToken.None);

        Assert.Equal(IdempotencyBindingResult.Acquired, first.Binding);
        Assert.Equal(IdempotencyBindingResult.Matched, replay.Binding);
        Assert.NotNull(first.Response);
        Assert.Equal(first.Response, replay.Response);
        Assert.Equal(
            1,
            await restartedRequestContext.Requests.CountAsync(value => value.Email == "lost-response@example.test"));
    }

    [Fact]
    public async Task RequestCreate_ConcurrentIdenticalAttempts_ConvergeToOneRequest()
    {
        await using var setupQuotationContext = QuotationContext();
        await using var setupRequestContext = RequestContext();
        await Task.WhenAll(
            setupQuotationContext.Database.MigrateAsync(),
            setupRequestContext.Database.MigrateAsync());
        await using var quotationContext1 = QuotationContext();
        await using var requestContext1 = RequestContext();
        await using var quotationContext2 = QuotationContext();
        await using var requestContext2 = RequestContext();
        IQuotationService service1 = Repository(quotationContext1, requestContext1);
        IQuotationService service2 = Repository(quotationContext2, requestContext2);
        var request = Request("concurrent-request@example.test", "Concurrent request");
        var keyHash = Hash("legacy-web-quotation-concurrent-request");
        var fingerprint = Hash("concurrent-request-payload");

        var outcomes = await Task.WhenAll(
            service1.CreateRequestIdempotentlyAsync(request, keyHash, fingerprint, CancellationToken.None),
            service2.CreateRequestIdempotentlyAsync(request, keyHash, fingerprint, CancellationToken.None));

        Assert.Contains(outcomes, value => value.Binding == IdempotencyBindingResult.Acquired);
        Assert.Contains(outcomes, value => value.Binding == IdempotencyBindingResult.Matched);
        Assert.All(outcomes, value => Assert.NotNull(value.Response));
        Assert.Equal(outcomes[0].Response!.Id, outcomes[1].Response!.Id);
        await using var verificationContext = RequestContext();
        Assert.Equal(
            1,
            await verificationContext.Requests.CountAsync(value => value.Email == "concurrent-request@example.test"));
    }

    [Fact]
    public async Task RequestCreate_SameKeyAndChangedFingerprint_FailsClosedWithoutSecondRequest()
    {
        await using var quotationContext = QuotationContext();
        await using var requestContext = RequestContext();
        await Task.WhenAll(
            quotationContext.Database.MigrateAsync(),
            requestContext.Database.MigrateAsync());
        IQuotationService service = Repository(quotationContext, requestContext);
        var keyHash = Hash("legacy-web-quotation-conflict");
        var first = await service.CreateRequestIdempotentlyAsync(
            Request("conflict@example.test", "Original"),
            keyHash,
            Hash("original-payload"),
            CancellationToken.None);
        var conflict = await service.CreateRequestIdempotentlyAsync(
            Request("conflict@example.test", "Changed"),
            keyHash,
            Hash("changed-payload"),
            CancellationToken.None);

        Assert.Equal(IdempotencyBindingResult.Acquired, first.Binding);
        Assert.Equal(IdempotencyBindingResult.Conflict, conflict.Binding);
        Assert.Null(conflict.Response);
        Assert.Equal(
            1,
            await requestContext.Requests.CountAsync(value => value.Email == "conflict@example.test"));
    }

    [Fact]
    public async Task RequestCreate_RedisCacheUnavailable_DoesNotAffectDurableCreate()
    {
        await using var quotationContext = QuotationContext();
        await using var requestContext = RequestContext();
        await Task.WhenAll(
            quotationContext.Database.MigrateAsync(),
            requestContext.Database.MigrateAsync());
        var cache = new Mock<IQuotationCache>(MockBehavior.Strict);
        IQuotationService service = new QuotationRepository(
            quotationContext,
            requestContext,
            cache.Object,
            TimeProvider.System);

        var result = await service.CreateRequestIdempotentlyAsync(
            Request("redis-independent@example.test", "Redis independent"),
            Hash("legacy-web-quotation-redis-independent"),
            Hash("redis-independent-payload"),
            CancellationToken.None);

        Assert.Equal(IdempotencyBindingResult.Acquired, result.Binding);
        Assert.True(result.Response?.Id > 0);
        cache.VerifyNoOtherCalls();
    }

    private QuotationRepository Repository(QuotationDbContext quotations, QuotationRequestDbContext requests)
    {
        var cache = new Mock<IQuotationCache>();
        cache.Setup(x => x.GetAsync<QuotationResponse>(It.IsAny<string>(), It.IsAny<CancellationToken>())).ReturnsAsync((QuotationResponse?)null);
        cache.Setup(x => x.GetAsync<QuotationRequestResponse>(It.IsAny<string>(), It.IsAny<CancellationToken>())).ReturnsAsync((QuotationRequestResponse?)null);
        return new(quotations, requests, cache.Object, TimeProvider.System);
    }

    private static UpsertQuotationRequest QuotationRequest(DateTime? expiration = null, int? customerId = null, bool? accepted = null) => new(customerId, null, null, 30, expiration ?? new DateTime(2026, 12, 31, 0, 0, 0, DateTimeKind.Utc), 100m, 7m, 107m, 3m, 764, "legacy", "FOB", "Courier", "30 days", accepted);
    private static UpsertQuotationRequestRequest Request(string email, string message) => new(
        "Ada",
        "Lovelace",
        email,
        null,
        "TH",
        null,
        null,
        message,
        null,
        null);
    private static string Hash(string value) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
    private QuotationDbContext QuotationContext(params IInterceptor[] interceptors) => new(new DbContextOptionsBuilder<QuotationDbContext>().UseNpgsql(quotationPostgres.GetConnectionString()).AddInterceptors(interceptors).Options);
    private QuotationRequestDbContext RequestContext() => new(new DbContextOptionsBuilder<QuotationRequestDbContext>().UseNpgsql(requestPostgres.GetConnectionString()).Options);

    private sealed class CommandCounter : DbCommandInterceptor
    {
        private int readerCommands;

        public int ReaderCommands => Volatile.Read(ref readerCommands);

        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
            DbCommand command,
            CommandEventData eventData,
            InterceptionResult<DbDataReader> result,
            CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref readerCommands);
            return base.ReaderExecutingAsync(command, eventData, result, cancellationToken);
        }
    }
}
