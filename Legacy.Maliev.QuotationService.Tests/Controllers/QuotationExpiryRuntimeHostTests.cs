using System.Collections.Concurrent;
using System.Data.Common;
using System.Net;
using Legacy.Maliev.QuotationService.Api.Workers;
using Legacy.Maliev.QuotationService.Data;
using Legacy.Maliev.QuotationService.Domain;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Time.Testing;

namespace Legacy.Maliev.QuotationService.Tests.Controllers;

/// <summary>Actual Production worker/DI and PostgreSQL, with controlled time and command faults; no provider effects.</summary>
public sealed class QuotationExpiryRuntimeHostTests(DraftAggregateFixture fixture) : IClassFixture<DraftAggregateFixture>
{
    private static readonly DateTimeOffset Start = new(2030, 1, 1, 0, 0, 0, TimeSpan.Zero);
    private static DateTime Utc(DateTimeOffset value) => DateTime.SpecifyKind(value.UtcDateTime, DateTimeKind.Unspecified);

    [Fact]
    public async Task Hosted_expiry_preserves_decided_rows_and_processes_strict_boundary_on_four_hour_tick()
    {
        var now = Utc(Start);
        var rows = await Seed((now.AddHours(-1), null), (now, null), (now.AddHours(1), null),
            (now.AddDays(-1), true), (now.AddDays(-1), false));
        var clock = new FakeTimeProvider(Start);
        await using var original = fixture.App();
        await using var app = original.WithWebHostBuilder(builder => builder.ConfigureTestServices(services => services.AddSingleton<TimeProvider>(clock)));
        using var client = app.CreateClient();
        Assert.Single(app.Services.GetServices<IHostedService>().OfType<ExpiredQuotationWorker>());
        await Until(async () => (await Read(rows[0].Id)).Accepted == false);
        var expired = await Read(rows[0].Id);
        Assert.Equal(now, expired.ModifiedDate);
        Assert.Equal(rows[0].CreatedDate, expired.CreatedDate);
        Assert.Null((await Read(rows[1].Id)).Accepted);
        Assert.Null((await Read(rows[2].Id)).Accepted);
        await DecidedUnchanged(rows[3]);
        await DecidedUnchanged(rows[4]);
        clock.Advance(TimeSpan.FromHours(4));
        await Until(async () => (await Read(rows[2].Id)).Accepted == false);
        Assert.False((await Read(rows[1].Id)).Accepted);
        Assert.Equal(Utc(Start.AddHours(4)), (await Read(rows[2].Id)).ModifiedDate);
        await DecidedUnchanged(rows[3]);
        await DecidedUnchanged(rows[4]);
        await using var db = fixture.Context();
        Assert.False(await db.AcceptedOutcomes.AnyAsync());
        Assert.False(await db.GoogleAnalyticsOutbox.AnyAsync());
        using var liveness = await client.GetAsync("/quotation/liveness");
        Assert.Equal(HttpStatusCode.OK, liveness.StatusCode);
    }

    [Fact]
    public async Task Worker_command_failure_is_observable_atomic_and_recovers_on_the_next_real_tick()
    {
        var rows = await Seed((Utc(Start).AddDays(-1), null), (Utc(Start).AddDays(-2), null));
        var clock = new FakeTimeProvider(Start);
        var fault = new ExpiryCommandFault();
        var logs = new ExpiryLogs();
        await using var original = fixture.App();
        await using var app = original.WithWebHostBuilder(builder =>
        {
            builder.ConfigureLogging(logging => logging.AddProvider(logs));
            builder.ConfigureTestServices(services =>
            {
                services.AddSingleton<TimeProvider>(clock);
                services.AddDbContext<QuotationDbContext>(options => options.AddInterceptors(fault));
            });
        });
        using var client = app.CreateClient();
        await Until(() => Task.FromResult(!logs.Errors.IsEmpty));
        Assert.Equal(nameof(InvalidOperationException), Assert.Single(logs.Errors));
        foreach (var row in rows)
        {
            var stored = await Read(row.Id);
            Assert.Null(stored.Accepted);
            Assert.Equal(row.ModifiedDate, stored.ModifiedDate);
        }
        using (var liveness = await client.GetAsync("/quotation/liveness")) Assert.Equal(HttpStatusCode.OK, liveness.StatusCode);
        clock.Advance(TimeSpan.FromHours(4));
        await Until(async () => (await Read(rows[1].Id)).Accepted == false);
        Assert.False((await Read(rows[0].Id)).Accepted);
        Assert.Equal(2, fault.Commands);
        Assert.Single(logs.Errors);
    }

    [Fact]
    public async Task Stopping_the_real_worker_cancels_its_timer_and_prevents_later_tick_writes()
    {
        var rows = await Seed((Utc(Start).AddDays(-1), null));
        var clock = new FakeTimeProvider(Start);
        await using var original = fixture.App();
        await using var app = original.WithWebHostBuilder(builder => builder.ConfigureTestServices(services => services.AddSingleton<TimeProvider>(clock)));
        using var client = app.CreateClient();
        await Until(async () => (await Read(rows[0].Id)).Accepted == false);
        var worker = Assert.Single(app.Services.GetServices<IHostedService>().OfType<ExpiredQuotationWorker>());
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await worker.StopAsync(deadline.Token);
        var execution = Assert.IsAssignableFrom<Task>(worker.ExecuteTask);
        Assert.True(execution.IsCompleted);
        Assert.False(execution.IsFaulted);
        var added = Assert.Single(await Seed((Utc(Start).AddDays(-1), null)));
        clock.Advance(TimeSpan.FromHours(4));
        var stored = await Read(added.Id);
        Assert.Null(stored.Accepted);
        Assert.Equal(added.ModifiedDate, stored.ModifiedDate);
    }

    private async Task<Quotation[]> Seed(params (DateTime Expiration, bool? Accepted)[] values)
    {
        var old = Utc(Start.AddDays(-10));
        var rows = values.Select(value => new Quotation
        {
            CustomerId = 101,
            EmployeeId = 41,
            CurrencyId = 1,
            Period = 30,
            ExpirationDate = value.Expiration,
            Accepted = value.Accepted,
            Subtotal = 100m,
            Vat = 7m,
            Total = 107m,
            WithholdingTax = 3m,
            CreatedDate = old,
            ModifiedDate = old,
            Comment = "Synthetic expiry fixture"
        }).ToArray();
        await using var db = fixture.Context();
        db.Quotations.AddRange(rows);
        await db.SaveChangesAsync();
        return rows;
    }
    private async Task<Quotation> Read(int id)
    {
        await using var db = fixture.Context();
        return await db.Quotations.AsNoTracking().SingleAsync(value => value.Id == id);
    }
    private async Task DecidedUnchanged(Quotation original)
    {
        var current = await Read(original.Id);
        Assert.Equal(original.Accepted, current.Accepted);
        Assert.Equal(original.ModifiedDate, current.ModifiedDate);
        Assert.Equal(original.CreatedDate, current.CreatedDate);
        Assert.Equal(original.Total, current.Total);
        Assert.Equal(original.InvoiceId, current.InvoiceId);
    }
    private static async Task Until(Func<Task<bool>> condition)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(12));
        while (!await condition()) await Task.Delay(20, deadline.Token);
    }
    private sealed class ExpiryCommandFault : DbCommandInterceptor
    {
        public int Commands { get; private set; }
        public override ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(DbCommand command, CommandEventData eventData,
            InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            if (command.CommandText.StartsWith("UPDATE \"Quotation\"", StringComparison.Ordinal) && ++Commands == 1)
                throw new InvalidOperationException("Synthetic expiry command failure.");
            return ValueTask.FromResult(result);
        }
    }
    private sealed class ExpiryLogs : ILoggerProvider
    {
        public ConcurrentQueue<string> Errors { get; } = new();
        public ILogger CreateLogger(string categoryName) => new Recorder(this, categoryName);
        public void Dispose() { }
        private sealed class Recorder(ExpiryLogs owner, string category) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
            public bool IsEnabled(LogLevel logLevel) => true;
            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
            {
                if (category == typeof(ExpiredQuotationWorker).FullName && logLevel >= LogLevel.Error)
                    owner.Errors.Enqueue(exception?.GetType().Name ?? "none");
            }
        }
    }
}
