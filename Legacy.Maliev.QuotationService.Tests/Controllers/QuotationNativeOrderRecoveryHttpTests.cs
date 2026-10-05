using System.Collections.Concurrent;
using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Net.Http.Json;
using Legacy.Maliev.QuotationService.Api.Clients;
using Legacy.Maliev.QuotationService.Application.Interfaces;
using Legacy.Maliev.QuotationService.Application.Models;
using Legacy.Maliev.QuotationService.Domain;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Legacy.Maliev.QuotationService.Tests.Controllers;

/// <summary>Native loopback HTTP behind the production typed client and real persisted decision workflow.</summary>
public sealed class QuotationNativeOrderRecoveryHttpTests(QuotationNormalIamFixture fixture) : IClassFixture<QuotationNormalIamFixture>
{
    [Theory]
    [InlineData(201, true, OrderDecisionResult.Completed)]
    [InlineData(409, false, OrderDecisionResult.Conflict)]
    [InlineData(404, true, OrderDecisionResult.NotFound)]
    public async Task Production_typed_client_sends_named_route_key_and_workload_identity_over_native_HTTP(
        int status, bool accepted, OrderDecisionResult expected)
    {
        await using var server = await OrderLoopback.StartAsync(status);
        var boundary = new QuotationNormalIamBoundary();
        await using var app = App(boundary, server);
        var client = app.Services.GetRequiredService<IOrderDecisionClient>();
        Assert.IsType<OrderDecisionClient>(client);
        Assert.Equal(expected, await client.TransitionAsync(9101, accepted, "native-order-110", CancellationToken.None)
            .WaitAsync(TimeSpan.FromSeconds(15)));
        var sent = Assert.Single(server.Calls);
        Assert.Equal(9101, sent.OrderId);
        Assert.Equal(accepted ? "accepted" : "declined", sent.Decision);
        Assert.Equal("native-order-110", sent.Key);
        Assert.True(sent.QuotationWorkload);
        Assert.Equal(1, boundary.LoginCalls);
        Assert.Equal(0, boundary.IamCalls);
    }

    [Fact]
    public async Task Native_HttpClient_deadline_returns_unavailable_without_canceling_the_caller()
    {
        await using var server = await OrderLoopback.StartAsync(201, hold: true);
        var boundary = new QuotationNormalIamBoundary();
        await using var app = App(boundary, server, TimeSpan.FromSeconds(1));
        using var caller = new CancellationTokenSource();
        var client = app.Services.GetRequiredService<IOrderDecisionClient>();
        Assert.Equal(OrderDecisionResult.Unavailable, await client.TransitionAsync(9102, true, "native-deadline-110", caller.Token)
            .WaitAsync(TimeSpan.FromSeconds(15)));
        Assert.False(caller.IsCancellationRequested);
        Assert.True(server.Entered.Task.IsCompletedSuccessfully);
        Assert.True(Assert.Single(server.Calls).QuotationWorkload);
    }

    [Fact]
    public async Task Caller_cancellation_after_native_send_propagates_instead_of_becoming_unavailable()
    {
        await using var server = await OrderLoopback.StartAsync(201, hold: true);
        await using var app = App(new(), server);
        using var caller = new CancellationTokenSource();
        var client = app.Services.GetRequiredService<IOrderDecisionClient>();
        var send = client.TransitionAsync(9103, false, "native-caller-cancel-110", caller.Token);
        await server.Entered.Task.WaitAsync(TimeSpan.FromSeconds(15));
        caller.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await send.WaitAsync(TimeSpan.FromSeconds(15)));
        Assert.True(caller.IsCancellationRequested);
        Assert.True(Assert.Single(server.Calls).QuotationWorkload);
    }

    [Theory]
    [InlineData(503, HttpStatusCode.ServiceUnavailable, QuotationDecisionStatus.DependencyUnavailable)]
    [InlineData(409, HttpStatusCode.Conflict, QuotationDecisionStatus.DependencyConflict)]
    public async Task Partial_native_order_failure_preserves_one_acceptance_and_replays_ordered_stable_keys(
        int failedStatus, HttpStatusCode expectedHttp, QuotationDecisionStatus expectedDecision)
    {
        await using var server = await OrderLoopback.StartAsync(failedStatus, failingOrder: 9202);
        var boundary = new QuotationNormalIamBoundary();
        await using var app = App(boundary, server, TimeSpan.FromSeconds(2));
        using var client = fixture.Client(app);
        var row = await fixture.SeedAsync();
        await using var db = fixture.Context();
        foreach (var order in new[] { 9201, 9202 })
            db.OrderLinks.Add(new QuotationOrderLink { QuotationId = row.Id, OrderId = order, CreatedDate = row.CreatedDate, ModifiedDate = row.ModifiedDate });
        await db.SaveChangesAsync();
        using var failed = await client.PutAsJsonAsync($"/quotations/{row.Id}/decision", new QuotationDecisionRequest(true))
            .WaitAsync(TimeSpan.FromSeconds(20));
        Assert.Equal(expectedHttp, failed.StatusCode);
        var partial = (await failed.Content.ReadFromJsonAsync<QuotationDecisionResponse>())!;
        Assert.Equal(expectedDecision, partial.Status);
        Assert.Equal(1, partial.CompletedOrders);
        Assert.Equal(2, partial.TotalOrders);
        var stored = await db.Quotations.AsNoTracking().SingleAsync(value => value.Id == row.Id);
        Assert.True(stored.Accepted);
        Assert.Equal(row.InvoiceId, stored.InvoiceId);
        Assert.Equal(row.Total, stored.Total);
        Assert.Equal(row.CreatedDate, stored.CreatedDate);
        Assert.Single(await db.AcceptedOutcomes.AsNoTracking().Where(value => value.QuotationId == row.Id).ToArrayAsync());
        Assert.Empty(await db.GoogleAnalyticsOutbox.AsNoTracking().Where(value => value.QuotationId == row.Id).ToArrayAsync());
        var firstCalls = server.Calls.ToArray();
        Assert.Equal(9201, firstCalls[0].OrderId);
        Assert.Contains(firstCalls, call => call.OrderId == 9202);
        Assert.All(firstCalls, call => Assert.True(call.QuotationWorkload));
        var keys = firstCalls.GroupBy(call => call.OrderId).ToDictionary(group => group.Key, group => Assert.Single(group.Select(call => call.Key).Distinct()));
        Assert.All(keys.Values, key => Assert.Contains($"quotation-{row.Id}-accepted-", key));
        server.Recover();
        using var recovered = await client.PutAsJsonAsync($"/quotations/{row.Id}/decision", new QuotationDecisionRequest(true))
            .WaitAsync(TimeSpan.FromSeconds(20));
        Assert.Equal(HttpStatusCode.OK, recovered.StatusCode);
        var completed = (await recovered.Content.ReadFromJsonAsync<QuotationDecisionResponse>())!;
        Assert.Equal(QuotationDecisionStatus.Completed, completed.Status);
        Assert.Equal(2, completed.CompletedOrders);
        Assert.Equal(2, completed.TotalOrders);
        Assert.Equal(partial.ModifiedDate, completed.ModifiedDate);
        var replay = server.Calls.ToArray().Skip(firstCalls.Length).ToArray();
        Assert.Equal(new[] { 9201, 9202 }, replay.Select(call => call.OrderId));
        Assert.All(replay, call => { Assert.Equal(keys[call.OrderId], call.Key); Assert.True(call.QuotationWorkload); });
        Assert.Equal(stored.ModifiedDate, (await db.Quotations.AsNoTracking().SingleAsync(value => value.Id == row.Id)).ModifiedDate);
        Assert.Single(await db.AcceptedOutcomes.AsNoTracking().Where(value => value.QuotationId == row.Id).ToArrayAsync());
        Assert.True(boundary.IamCalls >= 2);
        Assert.Equal(1, boundary.LoginCalls);
    }

    private WebApplicationFactory<Program> App(QuotationNormalIamBoundary boundary, OrderLoopback server, TimeSpan? timeout = null) =>
        fixture.App(boundary).WithWebHostBuilder(builder => builder.ConfigureTestServices(services =>
            services.AddHttpClient<IOrderDecisionClient, OrderDecisionClient>(http =>
            {
                http.BaseAddress = server.Address;
                http.Timeout = timeout ?? TimeSpan.FromSeconds(10);
            })));

    private sealed record SentOrder(int OrderId, string Decision, string Key, bool QuotationWorkload);

    private sealed class OrderLoopback(WebApplication app, int status, bool hold, int? failingOrder) : IAsyncDisposable
    {
        private volatile bool recovered;
        public Uri Address { get; private set; } = null!;
        public ConcurrentQueue<SentOrder> Calls { get; } = new();
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public void Recover() => recovered = true;

        public static async Task<OrderLoopback> StartAsync(int status, bool hold = false, int? failingOrder = null)
        {
            var builder = WebApplication.CreateSlimBuilder();
            builder.Logging.ClearProviders();
            builder.WebHost.UseUrls("http://127.0.0.1:0");
            var app = builder.Build();
            var server = new OrderLoopback(app, status, hold, failingOrder);
            app.MapPost("/orderstatuses/histories/{orderId:int}/{decision}", server.Send);
            await app.StartAsync();
            server.Address = new Uri(Assert.Single(app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses));
            Assert.True(IPAddress.IsLoopback(IPAddress.Parse(server.Address.Host)));
            return server;
        }

        private async Task Send(HttpContext context)
        {
            var order = int.Parse(context.Request.RouteValues["orderId"]!.ToString()!, System.Globalization.CultureInfo.InvariantCulture);
            var authorization = context.Request.Headers.Authorization.ToString();
            var workload = authorization.StartsWith("Bearer ", StringComparison.Ordinal)
                && new JwtSecurityTokenHandler().ReadJwtToken(authorization[7..]).Subject == "service:legacy-quotation";
            Calls.Enqueue(new(order, context.Request.RouteValues["decision"]!.ToString()!, context.Request.Headers["Idempotency-Key"].ToString(), workload));
            Entered.TrySetResult();
            if (hold)
            {
                try { await Task.Delay(Timeout.InfiniteTimeSpan, context.RequestAborted); }
                catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested) { }
                return;
            }
            context.Response.StatusCode = recovered || failingOrder.HasValue && order != failingOrder ? 201 : status;
        }

        public async ValueTask DisposeAsync()
        {
            await app.StopAsync().WaitAsync(TimeSpan.FromSeconds(10));
            await app.DisposeAsync();
        }
    }
}
