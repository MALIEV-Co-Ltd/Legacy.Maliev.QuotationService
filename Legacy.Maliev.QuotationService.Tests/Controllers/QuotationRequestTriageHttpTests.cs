using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Runtime.ExceptionServices;
using Legacy.Maliev.QuotationService.Application.Interfaces;
using Legacy.Maliev.QuotationService.Application.Models;
using System.Data.Common;
using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Containers;
using Legacy.Maliev.QuotationService.Data;
using Legacy.Maliev.QuotationService.Domain;
using Maliev.Aspire.ServiceDefaults.IAM;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.AspNetCore.Mvc.ModelBinding;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.IdentityModel.Tokens;
using Npgsql;
using StackExchange.Redis;
using Testcontainers.PostgreSql;

namespace Legacy.Maliev.QuotationService.Tests.Controllers;

/// <summary>Normal JWT/Production HTTP lifecycle; real databases, Redis and IAM client with controlled remote IAM HTTP only.</summary>
public sealed class QuotationRequestTriageHttpTests(RequestTriageFixture fixture) : IClassFixture<RequestTriageFixture>
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Ordinary_body_updates_use_source_reader_without_writing_server_owned_fields(bool file)
    {
        var row = await fixture.SeedAsync(Guid.NewGuid().ToString("N"), audited: true);
        await using var app = fixture.App();
        using var client = fixture.Client(app, requestFiles: file);
        var created = new DateTime(2001, 1, 1);
        var fileId = 0;
        if (file)
        {
            await using var seed = fixture.Context();
            var attachment = new QuotationRequestFile
            {
                RequestId = row.Id, Bucket = "old", ObjectName = "old",
                CreatedDate = created, ModifiedDate = created
            };
            seed.Files.Add(attachment);
            await seed.SaveChangesAsync();
            fileId = attachment.Id;
        }
        using var request = new HttpRequestMessage(HttpMethod.Put,
            file ? $"/quotationrequests/files/{fileId}" : $"/quotationrequests/{row.Id}");
        request.Content = new StringContent(file
            ? "{RequestId:'" + row.Id + "',RequestId:null,Bucket:42,Bucket:null,ObjectName:false,"
                + "Id:2147483647,CreatedDate:'2000-01-01T00:00:00Z'}"
            : "{FirstName:42,FirstName:null,Done:'true',Done:null,JourneyId:'" + Guid.NewGuid().ToString("D")
                + "',TransactionId:'client-forged',QualificationState:'not_qualified',QualificationVersion:'99',"
                + "CreatedDate:'2000-01-01T00:00:00Z'}", Encoding.UTF8, "application/json");
        if (!file)
            request.Headers.Add("X-Expected-Modified-Date",
                DateTime.SpecifyKind(row.ModifiedDate!.Value, DateTimeKind.Utc).ToString("O"));

        using var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        await using var db = fixture.Context();
        var retained = await db.Requests.AsNoTracking().SingleAsync(value => value.Id == row.Id);
        Assert.Equal(row.JourneyId, retained.JourneyId);
        Assert.Equal(row.TransactionId, retained.TransactionId);
        Assert.Equal(row.CreatedDate, retained.CreatedDate);
        Assert.Equal(row.QualificationState, retained.QualificationState);
        Assert.Equal(row.QualificationVersion, retained.QualificationVersion);
        if (file)
        {
            var attachment = await db.Files.AsNoTracking().SingleAsync(value => value.Id == fileId);
            Assert.Equal(row.Id, attachment.RequestId);
            Assert.Equal("42", attachment.Bucket);
            Assert.Equal("false", attachment.ObjectName);
            Assert.Equal(created, attachment.CreatedDate);
            Assert.Equal(row.ModifiedDate, retained.ModifiedDate);
        }
        else
        {
            Assert.Equal("42", retained.FirstName);
            Assert.Equal(true, retained.Done);
            Assert.Null(retained.LastName);
            Assert.True(retained.ModifiedDate > row.ModifiedDate);
        }
    }

    [Fact]
    public async Task Empty_database_keeps_source_notfound_for_default_filtered_and_distant_pages()
    {
        await using var existing = fixture.Context();
        var connection = new NpgsqlConnectionStringBuilder(existing.Database.GetDbConnection().ConnectionString)
        {
            Database = "empty_page_" + Guid.NewGuid().ToString("N")
        }.ConnectionString;
        var options = new DbContextOptionsBuilder<QuotationRequestDbContext>().UseNpgsql(connection).Options;
        await using var database = new QuotationRequestDbContext(options);
        Assert.False(await database.Database.CanConnectAsync());
        Exception? primaryFailure = null;
        try
        {
            await database.Database.MigrateAsync();
            await using (var original = fixture.App())
            await using (var app = original.WithWebHostBuilder(builder => builder.ConfigureTestServices(services =>
            {
                services.RemoveAll<DbContextOptions<QuotationRequestDbContext>>();
                services.AddSingleton(options);
            })))
            {
                using var client = fixture.Client(app);
                using (var response = await client.GetAsync("/quotationrequests"))
                    Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
                using (var response = await client.GetAsync("/quotationrequests?search=absent"))
                    Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
                using (var response = await client.GetAsync("/quotationrequests?index=2147483647&size=250"))
                    Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
            }
        }
        catch (Exception error)
        {
            primaryFailure = error;
            throw;
        }
        finally
        {
            try
            {
                await database.Database.EnsureDeletedAsync();
                Assert.False(await database.Database.CanConnectAsync());
            }
            catch (Exception cleanup) when (primaryFailure is not null)
            {
                primaryFailure.Data["EmptyPageDatabaseCleanupFailure"] = cleanup;
            }
        }
    }

    [Theory]
    [InlineData("%")]
    [InlineData("_")]
    [InlineData("\\")]
    public async Task Text_search_treats_source_contains_metacharacters_literally(string character)
    {
        var marker = Guid.NewGuid().ToString("N");
        var literal = await fixture.SeedAsync(marker + character);
        var distractor = await fixture.SeedAsync(marker + "X");
        await using var app = fixture.App();
        using var client = fixture.Client(app);
        using var response = await client.GetAsync("/quotationrequests?search=" + Uri.EscapeDataString(marker + character));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var json = await JsonAsync(response);
        var ids = Ids(json);
        Assert.Contains(literal.Id, ids);
        Assert.DoesNotContain(distractor.Id, ids);
        Assert.Equal(1, json.RootElement.GetProperty("TotalRecords").GetInt32());
    }

    [Theory]
    [InlineData("FirstName")]
    [InlineData("LastName")]
    [InlineData("Message")]
    [InlineData("TelephoneNumber")]
    [InlineData("CompanyName")]
    [InlineData("Email")]
    [InlineData("InternalComment")]
    [InlineData("Country")]
    public async Task Padded_text_search_preserves_each_source_field_literal(string field)
    {
        var marker = Guid.NewGuid().ToString("N");
        var literal = await fixture.SeedAsync(Guid.NewGuid().ToString("N"), field: field, value: " " + marker + " ");
        var unpadded = await fixture.SeedAsync(Guid.NewGuid().ToString("N"), field: field, value: marker);
        var absent = await fixture.SeedAsync(Guid.NewGuid().ToString("N"), field: field, value: null);
        await using var app = fixture.App();
        using var client = fixture.Client(app);
        using var response = await client.GetAsync("/quotationrequests?search=" + Uri.EscapeDataString(" " + marker.ToUpperInvariant() + " "));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var json = await JsonAsync(response);
        Assert.Equal(new[] { literal.Id }, Ids(json));
        Assert.DoesNotContain(unpadded.Id, Ids(json));
        Assert.DoesNotContain(absent.Id, Ids(json));
        Assert.Equal(1, json.RootElement.GetProperty("TotalRecords").GetInt32());
    }

    [Theory]
    [InlineData("\t", "latin")]
    [InlineData(" ", "ทดสอบ")]
    public async Task Padded_text_search_preserves_tabs_and_Thai(string padding, string text)
    {
        var marker = text + Guid.NewGuid().ToString("N");
        var literal = await fixture.SeedAsync(padding + marker + padding);
        var unpadded = await fixture.SeedAsync(marker);
        await using var app = fixture.App();
        using var client = fixture.Client(app);
        using var response = await client.GetAsync("/quotationrequests?search=" + Uri.EscapeDataString(padding + marker + padding));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var json = await JsonAsync(response);
        Assert.Equal(new[] { literal.Id }, Ids(json));
        Assert.DoesNotContain(unpadded.Id, Ids(json));
        Assert.Equal(1, json.RootElement.GetProperty("TotalRecords").GetInt32());
    }

    [Fact]
    public async Task Padded_text_without_literal_match_retains_source_notfound()
    {
        var marker = Guid.NewGuid().ToString("N");
        await fixture.SeedAsync(marker);
        await using var app = fixture.App();
        using var client = fixture.Client(app);
        using var response = await client.GetAsync("/quotationrequests?search=" + Uri.EscapeDataString(" " + marker + " "));
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Theory]
    [InlineData(" ")]
    [InlineData("\t")]
    public async Task Padded_numeric_search_keeps_source_identifier_selection(string padding)
    {
        var row = await fixture.SeedAsync(Guid.NewGuid().ToString("N"));
        var distractor = await fixture.SeedAsync(padding + row.Id + padding);
        await using var app = fixture.App();
        using var client = fixture.Client(app);
        using var response = await client.GetAsync("/quotationrequests?search=" + Uri.EscapeDataString(padding + row.Id + padding));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var json = await JsonAsync(response);
        Assert.Equal(new[] { row.Id }, Ids(json));
        Assert.DoesNotContain(distractor.Id, Ids(json));
        Assert.Equal(1, json.RootElement.GetProperty("TotalRecords").GetInt32());
        var item = json.RootElement.GetProperty("Items")[0];
        Assert.Equal(row.JourneyId, item.GetProperty("JourneyId").GetGuid());
        Assert.Equal(row.TransactionId, item.GetProperty("TransactionId").GetString());
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("\t")]
    [InlineData(" \t ")]
    public async Task Blank_http_query_retains_actual_model_binding_contract(string? search)
    {
        var binding = new LiteralSearchBindingCapture();
        await WithLiteralSearchDatabaseAsync(async (db, app) =>
        {
            var rows = await SeedLiteralSearchRowsAsync(db, "alpha beta", "alphabeta", null);
            Assert.True(app.Services.GetRequiredService<IModelMetadataProvider>().GetMetadataForType(typeof(string)).ConvertEmptyStringToNull);
            using var client = fixture.Client(app);
            var uri = search is null ? "/quotationrequests" : "/quotationrequests?search=" + Uri.EscapeDataString(search);
            using var response = await client.GetAsync(uri);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.True(binding.Observed);
            Assert.Equal(search is not null, binding.ArgumentPresent);
            Assert.Null(binding.Search);
            using var json = await JsonAsync(response);
            Assert.Equal(rows.Select(x => x.Id).OrderBy(id => id).ToArray(), Ids(json));
            Assert.Equal(rows.Length, json.RootElement.GetProperty("TotalRecords").GetInt32());
        }, binding);
    }

    [Theory]
    [InlineData(" ")]
    [InlineData("\t")]
    [InlineData(" \t ")]
    public async Task Direct_repository_whitespace_keeps_source_literal_filter(string search)
    {
        await WithLiteralSearchDatabaseAsync(async (db, app) =>
        {
            var rows = await SeedLiteralSearchRowsAsync(db, "alpha" + search + "beta", "alphabeta", null);
            await using var scope = app.Services.CreateAsyncScope();
            var service = scope.ServiceProvider.GetRequiredService<IQuotationService>();
            var page = await service.GetRequestsAsync(null, search, 1, null, CancellationToken.None);
            Assert.NotNull(page);
            Assert.Equal(new[] { rows[0].Id }, page.Items.Select(x => x.Id).ToArray());
            Assert.Equal(1, page.TotalRecords);
        });
    }

    [Theory]
    [InlineData(false, true, HttpStatusCode.Unauthorized)]
    [InlineData(true, false, HttpStatusCode.Forbidden)]
    public async Task Padded_search_retains_auth_and_live_permission_guards(bool authenticated, bool allowed, HttpStatusCode status)
    {
        var marker = Guid.NewGuid().ToString("N");
        await fixture.SeedAsync(" " + marker + " ");
        await using var before = fixture.Context();
        var requests = JsonSerializer.Serialize(await before.Requests.AsNoTracking().OrderBy(row => row.Id).ToArrayAsync());
        var audits = JsonSerializer.Serialize(await before.RequestQualificationAudit.AsNoTracking().OrderBy(row => row.Id).ToArrayAsync());
        await using var app = fixture.App(allowed: allowed);
        using var client = fixture.Client(app, authenticated: authenticated);
        using var response = await client.GetAsync("/quotationrequests?search=" + Uri.EscapeDataString(" " + marker + " "));
        Assert.Equal(status, response.StatusCode);
        Assert.Equal(requests, JsonSerializer.Serialize(await before.Requests.AsNoTracking().OrderBy(row => row.Id).ToArrayAsync()));
        Assert.Equal(audits, JsonSerializer.Serialize(await before.RequestQualificationAudit.AsNoTracking().OrderBy(row => row.Id).ToArrayAsync()));
    }

    private async Task WithLiteralSearchDatabaseAsync(
        Func<QuotationRequestDbContext, WebApplicationFactory<Program>, Task> action,
        LiteralSearchBindingCapture? binding = null)
    {
        var owner = fixture.Context();
        QuotationRequestDbContext? isolated = null;
        WebApplicationFactory<Program>? original = null;
        WebApplicationFactory<Program>? app = null;
        Exception? primary = null;
        var cleanupFailures = new List<Exception>();
        try
        {
            var connection = new NpgsqlConnectionStringBuilder(owner.Database.GetDbConnection().ConnectionString)
            {
                Database = "literal_search_" + Guid.NewGuid().ToString("N")
            }.ConnectionString;
            var options = new DbContextOptionsBuilder<QuotationRequestDbContext>().UseNpgsql(connection).Options;
            isolated = new(options);
            await isolated.Database.EnsureCreatedAsync();
            original = fixture.App();
            app = original.WithWebHostBuilder(builder => builder.ConfigureTestServices(services =>
            {
                services.RemoveAll<DbContextOptions<QuotationRequestDbContext>>();
                services.AddSingleton(options);
                if (binding is not null) services.Configure<MvcOptions>(options => options.Filters.Add(binding));
            }));
            await action(isolated, app);
        }
        catch (Exception error)
        {
            primary = error;
            throw;
        }
        finally
        {
            if (app is not null)
                try { await app.DisposeAsync(); } catch (Exception error) { cleanupFailures.Add(error); }
            if (original is not null)
                try { await original.DisposeAsync(); } catch (Exception error) { cleanupFailures.Add(error); }
            if (isolated is not null)
            {
                try { await isolated.Database.EnsureDeletedAsync(); } catch (Exception error) { cleanupFailures.Add(error); }
                try { await isolated.DisposeAsync(); } catch (Exception error) { cleanupFailures.Add(error); }
            }
            try { await owner.DisposeAsync(); } catch (Exception error) { cleanupFailures.Add(error); }
            if (cleanupFailures.Count != 0)
            {
                var cleanup = new AggregateException("Literal-search disposable database cleanup failed.", cleanupFailures);
                if (primary is null) throw cleanup;
                try { primary.Data["LiteralSearchDatabaseCleanupFailure"] = cleanup; }
                catch { /* Secondary metadata access must never replace the original test failure. */ }
            }
        }
    }

    private static async Task<QuotationRequest[]> SeedLiteralSearchRowsAsync(QuotationRequestDbContext db, params string?[] messages)
    {
        var timestamp = DateTime.SpecifyKind(DateTime.UtcNow, DateTimeKind.Unspecified);
        var rows = messages.Select(message => new QuotationRequest
        {
            Message = message,
            JourneyId = Guid.NewGuid(),
            CreatedDate = timestamp,
            ModifiedDate = timestamp,
            Done = false
        }).ToArray();
        db.Requests.AddRange(rows);
        await db.SaveChangesAsync();
        foreach (var row in rows) row.TransactionId = $"request-{row.Id}";
        await db.SaveChangesAsync();
        return rows;
    }

    private sealed class LiteralSearchBindingCapture : IActionFilter
    {
        public bool Observed { get; private set; }
        public bool ArgumentPresent { get; private set; }
        public string? Search { get; private set; }
        public void OnActionExecuting(ActionExecutingContext context)
        {
            if (context.ActionDescriptor.Parameters.Any(parameter => parameter.Name == "search"))
            {
                Observed = true;
                ArgumentPresent = context.ActionArguments.TryGetValue("search", out var value);
                Search = value as string;
            }
        }
        public void OnActionExecuted(ActionExecutedContext context) { }
    }

    [Theory]
    [InlineData("RequestId_Ascending", false)]
    [InlineData("RequestId_Descending", true)]
    [InlineData("RequestCreatedDate_Ascending", false)]
    [InlineData("RequestCreatedDate_Descending", true)]
    [InlineData("RequestModifiedDate_Ascending", false)]
    [InlineData("RequestModifiedDate_Descending", true)]
    public async Task List_retains_six_source_sorts_and_attribution(string sort, bool descending)
    {
        var marker = Guid.NewGuid().ToString("N");
        var first = await fixture.SeedAsync(marker, DateTime.UtcNow.AddDays(-2));
        var second = await fixture.SeedAsync(marker, DateTime.UtcNow.AddDays(-1));
        await using var app = fixture.App();
        using var client = fixture.Client(app);
        using var response = await client.GetAsync($"/quotationrequests?search={marker}&sort={sort}&size=1&index=1");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var json = await JsonAsync(response);
        Assert.Equal([descending ? second.Id : first.Id], Ids(json));
        Assert.Equal(2, json.RootElement.GetProperty("TotalRecords").GetInt32());
        Assert.Equal(2, json.RootElement.GetProperty("TotalPages").GetInt32());
        Assert.True(json.RootElement.GetProperty("HasNextPage").GetBoolean());
        var row = json.RootElement.GetProperty("Items")[0];
        Assert.Equal(descending ? second.JourneyId : first.JourneyId, row.GetProperty("JourneyId").GetGuid());
        Assert.Equal($"request-{row.GetProperty("Id").GetInt32()}", row.GetProperty("TransactionId").GetString());
    }

    [Theory]
    [InlineData("FirstName")]
    [InlineData("LastName")]
    [InlineData("Message")]
    [InlineData("TelephoneNumber")]
    [InlineData("CompanyName")]
    [InlineData("Email")]
    [InlineData("InternalComment")]
    [InlineData("Country")]
    public async Task Text_search_covers_source_field_case_insensitively(string field)
    {
        var marker = Guid.NewGuid().ToString("N");
        var row = await fixture.SeedAsync("unrelated", field: field, value: marker.ToUpperInvariant());
        await using var app = fixture.App();
        using var client = fixture.Client(app);
        using var response = await client.GetAsync("/quotationrequests?search=" + marker);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var json = await JsonAsync(response);
        Assert.Equal([row.Id], Ids(json));
    }

    [Fact]
    public async Task Numeric_search_and_page_bounds_preserve_current_consumer_contract()
    {
        var row = await fixture.SeedAsync(Guid.NewGuid().ToString("N"));
        await using var app = fixture.App();
        using var client = fixture.Client(app);
        using var response = await client.GetAsync($"/quotationrequests?search={row.Id}&index=0&size=1");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var json = await JsonAsync(response);
        Assert.Equal([row.Id], Ids(json));
        Assert.Equal(1, json.RootElement.GetProperty("PageIndex").GetInt32());
        Assert.Equal(1, json.RootElement.GetProperty("TotalPages").GetInt32());
        using var zero = await client.GetAsync($"/quotationrequests?search={row.Id}&index=0&size=0");
        Assert.Equal(HttpStatusCode.NotFound, zero.StatusCode);
        using var outOfRange = await client.GetAsync($"/quotationrequests?search={row.Id}&index=2&size=1");
        Assert.Equal(HttpStatusCode.NotFound, outOfRange.StatusCode);
        using var missing = await client.GetAsync("/quotationrequests?search=" + Guid.NewGuid().ToString("N"));
        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
        using var missingDetail = await client.GetAsync("/quotationrequests/2147483647");
        Assert.Equal(HttpStatusCode.NotFound, missingDetail.StatusCode);
    }


    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(51)]
    [InlineData(261)]
    public async Task Nonpositive_size_preserves_count_first_source_status_and_private_failure(int count)
    {
        var marker = Guid.NewGuid().ToString("N");
        for (var i = 0; i < count; i++) await fixture.SeedAsync(marker);
        await fixture.SeedAsync(Guid.NewGuid().ToString("N"));
        await using var snapshot = fixture.Context();
        var before = JsonSerializer.Serialize(await snapshot.Requests.AsNoTracking()
            .Where(row => row.Message == marker).OrderBy(row => row.Id).ToArrayAsync());
        var probe = new QuotationCollectionCommandProbe(marker, "Request");
        await using var app = fixture.App(interceptors: [probe]);
        using var client = fixture.Client(app);
        await using var deniedApp = fixture.App(allowed: false, interceptors: [probe]);
        using var denied = fixture.Client(deniedApp);
        foreach (var descending in new[] { false, true })
        {
            var sort = descending ? "RequestId_Descending" : "RequestId_Ascending";
            foreach (var size in new[] { int.MinValue, -1, 0 })
            {
                foreach (var index in new int?[] { null, int.MinValue, -1, 0, 1, 2, 3, int.MaxValue })
                {
                    probe.Commands.Clear();
                    var query = $"/quotationrequests?search={marker}&sort={sort}&size={size}"
                        + (index is null ? "" : $"&index={index}");
                    using var response = await client.GetAsync(query);
                    Assert.Equal("count", Assert.Single(probe.Commands));
                    if (count == 0 || size == 0)
                    {
                        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
                        continue;
                    }
                    Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
                    Assert.Equal("application/json", response.Content.Headers.ContentType?.MediaType);
                    var body = await response.Content.ReadAsStringAsync();
                    using var json = JsonDocument.Parse(body);
                    Assert.Equal(["details", "error", "statusCode", "traceId"],
                        json.RootElement.EnumerateObject().Select(property => property.Name).OrderBy(name => name).ToArray());
                    Assert.Equal("An internal server error occurred", json.RootElement.GetProperty("error").GetString());
                    Assert.Equal(500, json.RootElement.GetProperty("statusCode").GetInt32());
                    Assert.Equal(JsonValueKind.Null, json.RootElement.GetProperty("details").ValueKind);
                    Assert.False(string.IsNullOrWhiteSpace(json.RootElement.GetProperty("traceId").GetString()));
                    Assert.DoesNotContain(marker, body, StringComparison.Ordinal);
                    Assert.DoesNotContain("QuotationCollectionReadException", body, StringComparison.Ordinal);
                    Assert.DoesNotContain("Npgsql", body, StringComparison.Ordinal);
                }
                probe.Commands.Clear();
                using var anonymous = fixture.Client(app, authenticated: false);
                using var unauthenticated = await anonymous.GetAsync($"/quotationrequests?search={marker}&size={size}");
                Assert.Equal(HttpStatusCode.Unauthorized, unauthenticated.StatusCode);
                Assert.Empty(probe.Commands);
                using var forbidden = await denied.GetAsync($"/quotationrequests?search={marker}&size={size}");
                Assert.Equal(HttpStatusCode.Forbidden, forbidden.StatusCode);
                Assert.Empty(probe.Commands);
            }
        }
        var after = JsonSerializer.Serialize(await snapshot.Requests.AsNoTracking()
            .Where(row => row.Message == marker).OrderBy(row => row.Id).ToArrayAsync());
        Assert.Equal(before, after);
    }

    [Theory]
    [InlineData(int.MinValue)]
    [InlineData(-1)]
    [InlineData(0)]
    public async Task Nonpositive_size_preserves_count_failure_before_classification(int size)
    {
        var marker = Guid.NewGuid().ToString("N");
        await fixture.SeedAsync(marker);
        var probe = new QuotationCollectionCommandProbe(marker, "Request") { FailCount = true };
        await using var app = fixture.App(interceptors: [probe]);
        using var client = fixture.Client(app);
        using var response = await client.GetAsync($"/quotationrequests?search={marker}&size={size}");
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("count", Assert.Single(probe.Commands));
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal("The request cannot be processed.", json.RootElement.GetProperty("error").GetString());
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(0)]
    public async Task Nonpositive_size_preserves_caller_cancellation_at_count(int size)
    {
        var marker = Guid.NewGuid().ToString("N");
        await fixture.SeedAsync(marker);
        using var cancellation = new CancellationTokenSource();
        var probe = new QuotationCollectionCommandProbe(marker, "Request") { CancelCount = cancellation };
        await using var app = fixture.App(interceptors: [probe]);
        using var client = fixture.Client(app);
        cancellation.CancelAfter(TimeSpan.FromSeconds(15));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            client.GetAsync($"/quotationrequests?search={marker}&size={size}", cancellation.Token));
        Assert.Equal("count", Assert.Single(probe.Commands));
    }

    [Theory]
    [InlineData(100)]
    [InlineData(101)]
    [InlineData(250)]
    [InlineData(251)]
    [InlineData(int.MaxValue)]
    public async Task Positive_explicit_size_is_not_capped_after_filtering(int size)
    {
        const int count = 261;
        var marker = Guid.NewGuid().ToString("N");
        var rows = new List<int>();
        for (var i = 0; i < count; i++) rows.Add((await fixture.SeedAsync(marker)).Id);
        await fixture.SeedAsync("unrelated");
        await using var app = fixture.App();
        using var client = fixture.Client(app);
        foreach (var descending in new[] { false, true })
        {
            var expected = descending ? rows.AsEnumerable().Reverse().ToArray() : rows.ToArray();
            var sort = descending ? "RequestId_Descending" : "RequestId_Ascending";
            foreach (var index in new int?[] { null, -1, 0, 1, 2, 3, int.MaxValue })
            {
                var normalizedIndex = Math.Max(index ?? 1, 1);
                var offset = ((long)normalizedIndex - 1) * size;
                var query = $"/quotationrequests?search={marker}&sort={sort}&size={size}" + (index is null ? "" : $"&index={index}");
                using var response = await client.GetAsync(query);
                if (offset >= count)
                {
                    Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
                    continue;
                }
                Assert.Equal(HttpStatusCode.OK, response.StatusCode);
                using var json = await JsonAsync(response);
                Assert.Equal(expected.Skip((int)offset).Take(size), Ids(json));
                Assert.Equal(count, json.RootElement.GetProperty("TotalRecords").GetInt32());
                Assert.Equal((int)Math.Ceiling(count / (double)size), json.RootElement.GetProperty("TotalPages").GetInt32());
                Assert.Equal(normalizedIndex, json.RootElement.GetProperty("PageIndex").GetInt32());
                Assert.Equal(normalizedIndex > 1, json.RootElement.GetProperty("HasPreviousPage").GetBoolean());
                Assert.Equal(normalizedIndex < (int)Math.Ceiling(count / (double)size), json.RootElement.GetProperty("HasNextPage").GetBoolean());
                Assert.False(json.RootElement.TryGetProperty("PageSize", out _));
                foreach (var row in json.RootElement.GetProperty("Items").EnumerateArray())
                {
                    Assert.NotEqual(Guid.Empty, row.GetProperty("JourneyId").GetGuid());
                    Assert.Equal($"request-{row.GetProperty("Id").GetInt32()}", row.GetProperty("TransactionId").GetString());
                }
            }
        }
        using var missing = await client.GetAsync($"/quotationrequests?search={marker}-missing&size={size}");
        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
        using var anonymous = fixture.Client(app, authenticated: false);
        using var unauthenticated = await anonymous.GetAsync($"/quotationrequests?search={marker}&size={size}");
        Assert.Equal(HttpStatusCode.Unauthorized, unauthenticated.StatusCode);
        await using var deniedApp = fixture.App(allowed: false);
        using var denied = fixture.Client(deniedApp);
        using var forbidden = await denied.GetAsync($"/quotationrequests?search={marker}&size={size}");
        Assert.Equal(HttpStatusCode.Forbidden, forbidden.StatusCode);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(51)]
    [InlineData(261)]
    public async Task Omitted_size_uses_filtered_count_without_default_or_cap(int count)
    {
        var marker = Guid.NewGuid().ToString("N");
        var rows = new List<int>();
        for (var i = 0; i < count; i++) rows.Add((await fixture.SeedAsync(marker)).Id);
        await fixture.SeedAsync("unrelated");
        await using var app = fixture.App();
        using var client = fixture.Client(app);
        foreach (var descending in new[] { false, true })
        {
            var expected = descending ? rows.AsEnumerable().Reverse().ToArray() : rows.ToArray();
            var sort = descending ? "RequestId_Descending" : "RequestId_Ascending";
            foreach (var index in new int?[] { null, -1, 0, 1, 2, int.MaxValue })
            {
                var query = $"/quotationrequests?search={marker}&sort={sort}" + (index is null ? "" : $"&index={index}");
                using var response = await client.GetAsync(query);
                if (count == 0 || index > 1)
                {
                    Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
                    continue;
                }
                Assert.Equal(HttpStatusCode.OK, response.StatusCode);
                using var json = await JsonAsync(response);
                Assert.Equal(expected, Ids(json));
                Assert.Equal(count, json.RootElement.GetProperty("TotalRecords").GetInt32());
                Assert.Equal(1, json.RootElement.GetProperty("TotalPages").GetInt32());
                Assert.Equal(1, json.RootElement.GetProperty("PageIndex").GetInt32());
                Assert.False(json.RootElement.TryGetProperty("PageSize", out _));
            }
            using var explicitSize = await client.GetAsync($"/quotationrequests?search={marker}&sort={sort}&size=50&index=1");
            Assert.Equal(count == 0 ? HttpStatusCode.NotFound : HttpStatusCode.OK, explicitSize.StatusCode);
            if (count == 0) continue;
            using var explicitJson = await JsonAsync(explicitSize);
            Assert.Equal(expected.Take(50), Ids(explicitJson));
            Assert.Equal((int)Math.Ceiling(count / 50d), explicitJson.RootElement.GetProperty("TotalPages").GetInt32());
        }
        using var missing = await client.GetAsync($"/quotationrequests?search={marker}-missing");
        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
    }

    [Theory]
    [InlineData(int.MaxValue, 250)]
    [InlineData(1073741825, 4)]
    [InlineData(int.MaxValue, 2)]
    public async Task Distant_page_does_not_overflow_or_replay_first_page_rows(int pageIndex, int size)
    {
        var marker = Guid.NewGuid().ToString("N");
        var first = await fixture.SeedAsync(marker);
        var second = await fixture.SeedAsync(marker);
        await using var app = fixture.App();
        using var client = fixture.Client(app);
        using var response = await client.GetAsync($"/quotationrequests?search={marker}&index={pageIndex}&size={size}");
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        using var missing = await client.GetAsync($"/quotationrequests?search={marker}-missing&index={pageIndex}&size={size}");
        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
        using var firstPage = await client.GetAsync($"/quotationrequests?search={marker}&index=1&size=250");
        Assert.Equal(HttpStatusCode.OK, firstPage.StatusCode);
        using var full = await JsonAsync(firstPage);
        Assert.Equal([first.Id, second.Id], Ids(full));
        Assert.Equal(2, full.RootElement.GetProperty("TotalRecords").GetInt32());
        Assert.Equal(first.JourneyId, full.RootElement.GetProperty("Items")[0].GetProperty("JourneyId").GetGuid());
        Assert.Equal(first.TransactionId, full.RootElement.GetProperty("Items")[0].GetProperty("TransactionId").GetString());
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    [InlineData(null)]
    public async Task Ordinary_update_and_done_preserve_attribution_and_immutable_history(bool? done)
    {
        var row = await fixture.SeedAsync(Guid.NewGuid().ToString("N"), audited: true);
        await using var app = fixture.App();
        using var client = fixture.Client(app);
        using var cached = await client.GetAsync($"/quotationrequests/{row.Id}");
        Assert.Equal(HttpStatusCode.OK, cached.StatusCode);
        using var update = Update(row, done, expected: row.ModifiedDate);
        using var response = await client.SendAsync(update);
        await using var db = fixture.Context();
        var persisted = await db.Requests.AsNoTracking().SingleAsync(x => x.Id == row.Id);
        var audit = Assert.Single(await db.RequestQualificationAudit.Where(x => x.RequestId == row.Id).ToListAsync());
        Assert.Equal(row.JourneyId, persisted.JourneyId);
        Assert.Equal(row.TransactionId, persisted.TransactionId);
        Assert.Equal(row.CreatedDate, persisted.CreatedDate);
        Assert.Equal("qualified", persisted.QualificationState);
        Assert.Equal(1, persisted.QualificationVersion);
        Assert.Equal(row.QualificationStateChangedUtc, persisted.QualificationStateChangedUtc);
        Assert.Equal("employee-historical-fixture", audit.ChangedBy);
        Assert.Equal(row.JourneyId, audit.JourneyId);
        Assert.Equal(row.TransactionId, audit.TransactionId);
        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        Assert.Equal(done, persisted.Done);
        Assert.Equal("Reviewed", persisted.Message);
        using var detail = await client.GetAsync($"/quotationrequests/{row.Id}");
        using var json = await JsonAsync(detail);
        Assert.Equal("Reviewed", json.RootElement.GetProperty("Message").GetString());
        Assert.Equal(row.JourneyId, json.RootElement.GetProperty("JourneyId").GetGuid());
        Assert.Equal(row.TransactionId, json.RootElement.GetProperty("TransactionId").GetString());
        if (done.HasValue) Assert.Equal(done.Value, json.RootElement.GetProperty("Done").GetBoolean());
        else Assert.False(json.RootElement.TryGetProperty("Done", out _));
    }

    [Fact]
    public async Task Expected_version_conflict_and_malformed_header_do_not_mutate()
    {
        var row = await fixture.SeedAsync(Guid.NewGuid().ToString("N"), audited: true);
        await using var app = fixture.App();
        using var client = fixture.Client(app);
        using var stale = Update(row, true, row.ModifiedDate!.Value.AddDays(-1));
        using var staleResponse = await client.SendAsync(stale);
        Assert.Equal(HttpStatusCode.Conflict, staleResponse.StatusCode);
        using var malformed = Update(row, true, null);
        malformed.Headers.Add("X-Expected-Modified-Date", "not-a-date");
        using var malformedResponse = await client.SendAsync(malformed);
        Assert.Equal(HttpStatusCode.BadRequest, malformedResponse.StatusCode);
        await using var db = fixture.Context();
        var persisted = await db.Requests.AsNoTracking().SingleAsync(x => x.Id == row.Id);
        Assert.Equal(row.Message, persisted.Message);
        Assert.Equal(row.ModifiedDate, persisted.ModifiedDate);
        Assert.Equal(1, await db.RequestQualificationAudit.CountAsync(x => x.RequestId == row.Id));
    }

    [Theory]
    [InlineData(0L)]
    [InlineData(9L)]
    [InlineData(-864000000000L)]
    public async Task Ordinary_update_advances_microsecond_version_and_rejects_replayed_token_when_clock_stalls_or_regresses(long clockOffsetTicks)
    {
        var row = await fixture.SeedAsync(Guid.NewGuid().ToString("N"), audited: true);
        var originalVersion = Assert.IsType<DateTime>(row.ModifiedDate);
        var clock = new RequestMutationClock(new DateTimeOffset(DateTime.SpecifyKind(originalVersion.AddTicks(clockOffsetTicks), DateTimeKind.Utc)));
        await using var originalApp = fixture.App();
        await using var app = originalApp.WithWebHostBuilder(builder => builder.ConfigureTestServices(services =>
        {
            services.RemoveAll<TimeProvider>();
            services.AddSingleton<TimeProvider>(clock);
        }));
        using var client = fixture.Client(app);
        using var first = Update(row, true, originalVersion);
        using var firstResponse = await client.SendAsync(first);
        Assert.Equal(HttpStatusCode.NoContent, firstResponse.StatusCode);
        using var firstDetail = await client.GetAsync($"/quotationrequests/{row.Id}");
        Assert.Equal(HttpStatusCode.OK, firstDetail.StatusCode);
        var firstState = await firstDetail.Content.ReadFromJsonAsync<QuotationRequestResponse>(new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
        Assert.NotNull(firstState);
        Assert.Equal(originalVersion.AddTicks(10), firstState.ModifiedDate);
        using var stale = Update(row, false, originalVersion);
        using var staleResponse = await client.SendAsync(stale);
        Assert.Equal(HttpStatusCode.Conflict, staleResponse.StatusCode);
        await using (var db = fixture.Context())
        {
            var retained = await db.Requests.AsNoTracking().SingleAsync(x => x.Id == row.Id);
            Assert.True(retained.Done);
            Assert.Equal(firstState.ModifiedDate, retained.ModifiedDate);
        }
        using var fresh = Update(row, false, firstState.ModifiedDate);
        using var freshResponse = await client.SendAsync(fresh);
        Assert.Equal(HttpStatusCode.NoContent, freshResponse.StatusCode);
        using var replay = Update(row, true, firstState.ModifiedDate);
        using var replayResponse = await client.SendAsync(replay);
        Assert.Equal(HttpStatusCode.Conflict, replayResponse.StatusCode);
        using var finalDetail = await client.GetAsync($"/quotationrequests/{row.Id}");
        Assert.Equal(HttpStatusCode.OK, finalDetail.StatusCode);
        var finalState = await finalDetail.Content.ReadFromJsonAsync<QuotationRequestResponse>(new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
        Assert.NotNull(finalState);
        Assert.Equal(originalVersion.AddTicks(20), finalState.ModifiedDate);
        await using var verify = fixture.Context();
        var persisted = await verify.Requests.AsNoTracking().SingleAsync(x => x.Id == row.Id);
        Assert.False(persisted.Done);
        Assert.Equal("Reviewed", persisted.Message);
        Assert.Equal(finalState.ModifiedDate, persisted.ModifiedDate);
        Assert.Equal(row.JourneyId, persisted.JourneyId);
        Assert.Equal(row.TransactionId, persisted.TransactionId);
        Assert.Equal(row.CreatedDate, persisted.CreatedDate);
        Assert.Equal(row.QualificationState, persisted.QualificationState);
        Assert.Equal(row.QualificationVersion, persisted.QualificationVersion);
        Assert.Equal(row.QualificationStateChangedUtc, persisted.QualificationStateChangedUtc);
        Assert.Equal(1, await verify.RequestQualificationAudit.CountAsync(x => x.RequestId == row.Id));
    }

    private sealed class RequestMutationClock(DateTimeOffset value) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => value;
    }

    [Fact]
    public async Task Concurrent_same_expected_version_has_one_writer_and_one_conflict()
    {
        var row = await fixture.SeedAsync(Guid.NewGuid().ToString("N"));
        await using var app = fixture.App();
        using var client = fixture.Client(app);
        using var first = Update(row, true, row.ModifiedDate);
        using var second = Update(row, false, row.ModifiedDate);
        var responses = await Task.WhenAll(client.SendAsync(first), client.SendAsync(second));
        try
        {
            Assert.Equal([HttpStatusCode.NoContent, HttpStatusCode.Conflict], responses.Select(x => x.StatusCode).Order().ToArray());
            await using var db = fixture.Context();
            Assert.Equal(1, await db.Requests.CountAsync(x => x.Id == row.Id));
            Assert.Equal(row.JourneyId, (await db.Requests.SingleAsync(x => x.Id == row.Id)).JourneyId);
        }
        finally { foreach (var response in responses) response.Dispose(); }
    }

    [Theory]
    [InlineData("GET", false, 401)]
    [InlineData("GET", true, 403)]
    [InlineData("PUT", true, 403)]
    [InlineData("DELETE", true, 403)]
    public async Task Normal_auth_or_live_permission_denial_preserves_graph(string method, bool authenticated, int status)
    {
        var row = await fixture.SeedAsync(Guid.NewGuid().ToString("N"), audited: true);
        await using var app = fixture.App(allowed: false);
        using var client = fixture.Client(app, authenticated);
        using var request = method == "PUT" ? Update(row, true, row.ModifiedDate) : new HttpRequestMessage(new HttpMethod(method), $"/quotationrequests/{row.Id}");
        using var response = await client.SendAsync(request);
        Assert.Equal(status, (int)response.StatusCode);
        await using var db = fixture.Context();
        var persisted = await db.Requests.SingleAsync(x => x.Id == row.Id);
        Assert.Equal(row.Message, persisted.Message);
        Assert.Equal(row.ModifiedDate, persisted.ModifiedDate);
        Assert.Equal(1, await db.RequestQualificationAudit.CountAsync(x => x.RequestId == row.Id));
    }

    [Fact]
    public async Task Delete_plain_root_retains_unrelated_metadata_and_durable_create_receipt()
    {
        var row = await fixture.SeedAsync(Guid.NewGuid().ToString("N"));
        await using (var db = fixture.Context())
        {
            db.Files.Add(new() { RequestId = row.Id, Bucket = "synthetic", ObjectName = "fixture-only" });
            db.RequestCreateIdempotency.Add(new() { KeyHash = new string('a', 32) + Guid.NewGuid().ToString("N"), Fingerprint = new string('b', 64), RequestId = row.Id });
            await db.SaveChangesAsync();
        }
        await using var app = fixture.App();
        using var client = fixture.Client(app);
        using var before = await client.GetAsync($"/quotationrequests/{row.Id}");
        Assert.Equal(HttpStatusCode.OK, before.StatusCode);
        using var response = await client.DeleteAsync($"/quotationrequests/{row.Id}");
        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        using var replay = await client.DeleteAsync($"/quotationrequests/{row.Id}");
        Assert.Equal(HttpStatusCode.NotFound, replay.StatusCode);
        using var detail = await client.GetAsync($"/quotationrequests/{row.Id}");
        Assert.Equal(HttpStatusCode.NotFound, detail.StatusCode);
        await using var verify = fixture.Context();
        Assert.False(await verify.Requests.AnyAsync(x => x.Id == row.Id));
        Assert.Equal(1, await verify.Files.CountAsync(x => x.RequestId == row.Id));
        Assert.Equal(1, await verify.RequestCreateIdempotency.CountAsync(x => x.RequestId == row.Id));
    }

    [Fact]
    public async Task Delete_audited_root_observes_existing_fk_rejection_without_erasing_history()
    {
        var row = await fixture.SeedAsync(Guid.NewGuid().ToString("N"), audited: true);
        await using var app = fixture.App();
        using var client = fixture.Client(app);
        using var response = await client.DeleteAsync($"/quotationrequests/{row.Id}");
        await using var db = fixture.Context();
        Assert.True(await db.Requests.AnyAsync(x => x.Id == row.Id));
        Assert.Equal(1, await db.RequestQualificationAudit.CountAsync(x => x.RequestId == row.Id));
        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
    }

    [Fact]
    public async Task Readback_preserves_current_state_but_suppresses_incomplete_attribution_and_pii()
    {
        var complete = await fixture.SeedAsync(Guid.NewGuid().ToString("N"), audited: true);
        var incomplete = await fixture.SeedAsync(Guid.NewGuid().ToString("N"));
        await using (var db = fixture.Context())
        {
            (await db.Requests.SingleAsync(x => x.Id == incomplete.Id)).JourneyId = null;
            await db.SaveChangesAsync();
        }
        await using var app = fixture.App();
        using var client = fixture.Client(app);
        using var response = await client.GetAsync($"/quotationrequests/qualification-outcomes/readback?fromUtc={Uri.EscapeDataString(DateTime.UtcNow.AddDays(-4).ToString("O"))}&toUtc={Uri.EscapeDataString(DateTime.UtcNow.AddMinutes(-1).ToString("O"))}");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.True(response.Headers.CacheControl?.NoStore);
        using var json = await JsonAsync(response);
        var rows = json.RootElement.GetProperty("requests").EnumerateArray().ToArray();
        var attributed = Assert.Single(rows, x => x.GetProperty("requestId").GetInt32() == complete.Id);
        Assert.Equal("qualified", attributed.GetProperty("state").GetString());
        Assert.Equal(complete.JourneyId, attributed.GetProperty("journeyId").GetGuid());
        Assert.Equal(complete.TransactionId, attributed.GetProperty("transactionId").GetString());
        Assert.Equal(["createdUtc", "journeyId", "requestId", "state", "transactionId"], attributed.EnumerateObject().Select(x => x.Name).Order().ToArray());
        var unattributed = Assert.Single(rows, x => x.GetProperty("requestId").GetInt32() == incomplete.Id);
        Assert.False(unattributed.TryGetProperty("journeyId", out _));
        Assert.False(unattributed.TryGetProperty("transactionId", out _));
        Assert.Equal(["createdUtc", "requestId", "state"], unattributed.EnumerateObject().Select(x => x.Name).Order().ToArray());
    }

    [Theory]
    [InlineData("nonemployee", 403)]
    [InlineData("live-denied", 403)]
    [InlineData("reversed", 400)]
    [InlineData("future", 400)]
    [InlineData("too-wide", 400)]
    [InlineData("missing-utc", 400)]
    public async Task Readback_denies_wrong_admission_or_invalid_window(string scenario, int expected)
    {
        await using var app = fixture.App(allowed: scenario != "live-denied");
        using var client = fixture.Client(app, employee: scenario != "nonemployee");
        var from = DateTime.UtcNow.AddDays(-2);
        var to = DateTime.UtcNow.AddMinutes(-1);
        if (scenario == "reversed") from = to.AddDays(1);
        if (scenario == "future") to = DateTime.UtcNow.AddDays(1);
        if (scenario == "too-wide") from = to.AddDays(-32);
        var fromText = scenario == "missing-utc" ? DateTime.SpecifyKind(from, DateTimeKind.Unspecified).ToString("O") : from.ToString("O");
        using var response = await client.GetAsync($"/quotationrequests/qualification-outcomes/readback?fromUtc={Uri.EscapeDataString(fromText)}&toUtc={Uri.EscapeDataString(to.ToString("O"))}");
        Assert.Equal(expected, (int)response.StatusCode);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Command_failure_before_explicit_commit_retries_only_after_rollback_in_fresh_context(bool delete)
    {
        var row = await fixture.SeedAsync(Guid.NewGuid().ToString("N"));
        var fault = new RequestTriageAckFault(delete);
        var rollback = new RequestCreateRollbackObserver();
        await using var app = fixture.App(interceptors: [fault, rollback]);
        using var client = fixture.Client(app);
        using var request = delete ? new HttpRequestMessage(HttpMethod.Delete, $"/quotationrequests/{row.Id}") : Update(row, true, row.ModifiedDate);
        using var response = await client.SendAsync(request);
        // Observe durable graph and reached command counts BEFORE response assertions.
        await using var db = fixture.Context();
        var persisted = await db.Requests.AsNoTracking().SingleOrDefaultAsync(x => x.Id == row.Id);
        var receipts = await db.RequestCreateIdempotency.CountAsync(x => x.RequestId == row.Id);
        Assert.True(fault.Fired, "Synthetic fault did not reach the actual executed mutation.");
        Assert.False(fault.Autocommit);
        if (delete) Assert.Null(persisted);
        else Assert.Equal("Reviewed", persisted!.Message);
        Assert.Equal(0, receipts);
        Assert.Equal(1, rollback.Rollbacks);
        Assert.Equal(2, fault.Executions);
        Assert.Equal(2, fault.Contexts.Count);
        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Redis_eviction_denial_cannot_override_postgres_authoritative_detail(bool delete)
    {
        var row = await fixture.SeedAsync(Guid.NewGuid().ToString("N"));
        await using var app = fixture.App();
        using var client = fixture.Client(app);
        using var cached = await client.GetAsync($"/quotationrequests/{row.Id}");
        Assert.Equal(HttpStatusCode.OK, cached.StatusCode);
        var old = await cached.Content.ReadFromJsonAsync<QuotationRequestResponse>(new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
        Assert.NotNull(old);
        await using (var scope = app.Services.CreateAsyncScope())
        {
            var cache = scope.ServiceProvider.GetRequiredService<IQuotationCache>();
            await cache.SetAsync($"request:{row.Id}", old, TimeSpan.FromMinutes(2), CancellationToken.None);
            Assert.Equal(old, await cache.GetAsync<QuotationRequestResponse>($"request:{row.Id}", CancellationToken.None));
        }
        using var redis = await ConnectionMultiplexer.ConnectAsync(fixture.RedisConnection);
        var database = redis.GetDatabase();
        await database.ExecuteAsync("ACL", "SETUSER", "default", "-del", "-unlink");
        try
        {
            // Prove real Redis denial, not a substituted cache implementation.
            await Assert.ThrowsAsync<RedisServerException>(() => database.KeyDeleteAsync("synthetic-denial-probe"));
            using var request = delete ? new HttpRequestMessage(HttpMethod.Delete, $"/quotationrequests/{row.Id}") : Update(row, true, row.ModifiedDate);
            using var response = await client.SendAsync(request);
            Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
            await using var db = fixture.Context();
            var persisted = await db.Requests.AsNoTracking().SingleOrDefaultAsync(x => x.Id == row.Id);
            if (delete) Assert.Null(persisted);
            else Assert.Equal("Reviewed", persisted!.Message);
            using var detail = await client.GetAsync($"/quotationrequests/{row.Id}");
            Assert.Equal(delete ? HttpStatusCode.NotFound : HttpStatusCode.OK, detail.StatusCode);
            if (!delete)
            {
                using var json = await JsonAsync(detail);
                Assert.Equal("Reviewed", json.RootElement.GetProperty("Message").GetString());
            }
        }
        finally { await database.ExecuteAsync("ACL", "SETUSER", "default", "+del", "+unlink"); }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Late_cache_writer_cannot_poison_updated_or_deleted_detail(bool delete)
    {
        var row = await fixture.SeedAsync(Guid.NewGuid().ToString("N"));
        await using var app = fixture.App();
        using var client = fixture.Client(app);
        using var before = await client.GetAsync($"/quotationrequests/{row.Id}");
        var old = await before.Content.ReadFromJsonAsync<QuotationRequestResponse>();
        Assert.NotNull(old);
        using var request = delete ? new HttpRequestMessage(HttpMethod.Delete, $"/quotationrequests/{row.Id}") : Update(row, true, row.ModifiedDate);
        using var response = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        using (var scope = app.Services.CreateScope())
        {
            var cache = scope.ServiceProvider.GetRequiredService<IQuotationCache>();
            await cache.SetAsync($"request:{row.Id}", old, TimeSpan.FromMinutes(2), CancellationToken.None);
            Assert.Equal(old, await cache.GetAsync<QuotationRequestResponse>($"request:{row.Id}", CancellationToken.None));
        }
        using var detail = await client.GetAsync($"/quotationrequests/{row.Id}");
        Assert.Equal(delete ? HttpStatusCode.NotFound : HttpStatusCode.OK, detail.StatusCode);
        if (!delete)
        {
            using var json = await JsonAsync(detail);
            Assert.Equal("Reviewed", json.RootElement.GetProperty("Message").GetString());
        }
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task Lost_commit_ack_requires_single_commit_and_generic_unavailable(bool delete, bool arbitrary)
    {
        var row = await fixture.SeedAsync(Guid.NewGuid().ToString("N"));
        var transient = new RequestCreateCommitFault();
        var nonprovider = new RequestCreateArbitraryCommitFault();
        await using var app = fixture.App(interceptors: [arbitrary ? nonprovider : transient]);
        using var client = fixture.Client(app);
        using var request = delete ? new HttpRequestMessage(HttpMethod.Delete, $"/quotationrequests/{row.Id}") : Update(row, true, row.ModifiedDate);
        using var response = await client.SendAsync(request);
        await using var db = fixture.Context();
        var persisted = await db.Requests.AsNoTracking().SingleOrDefaultAsync(x => x.Id == row.Id);
        Assert.True(arbitrary ? nonprovider.Fired : transient.Fired);
        Assert.Equal(1, arbitrary ? nonprovider.Commits : transient.CommitAcknowledgements);
        if (delete) Assert.Null(persisted);
        else Assert.Equal("Reviewed", persisted!.Message);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        using var json = await JsonAsync(response);
        Assert.Equal("request_mutation_unavailable", json.RootElement.GetProperty("code").GetString());
        Assert.DoesNotContain("Synthetic", await response.Content.ReadAsStringAsync(), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Postcommit_disposal_failure_cannot_replay_receiptless_mutation(bool delete)
    {
        var row = await fixture.SeedAsync(Guid.NewGuid().ToString("N"));
        var fault = new RequestCreateDisposalFault();
        await using var app = fixture.App(interceptors: [fault.Commit, fault.Disposal]);
        using var client = fixture.Client(app);
        using var request = delete ? new HttpRequestMessage(HttpMethod.Delete, $"/quotationrequests/{row.Id}") : Update(row, true, row.ModifiedDate);
        using var response = await client.SendAsync(request);
        Assert.True(fault.Fired);
        Assert.Equal(1, fault.Commits);
        await using var db = fixture.Context();
        var persisted = await db.Requests.AsNoTracking().SingleOrDefaultAsync(x => x.Id == row.Id);
        if (delete) Assert.Null(persisted);
        else Assert.Equal("Reviewed", persisted!.Message);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task Rollback_or_cleanup_failure_preserves_both_internal_causes_without_replay(bool delete, bool teardown)
    {
        var row = await fixture.SeedAsync(Guid.NewGuid().ToString("N"));
        var command = new RequestTriageAckFault(delete);
        var rollback = new RequestCreateRollbackObserver(!teardown);
        var cleanup = new RequestCreateRollbackTeardownFault(rollback);
        var retained = false;
        var busyReader = false;
        void Observe(object? sender, FirstChanceExceptionEventArgs args)
        {
            if (command.Fired && args.Exception.GetType().FullName == "Npgsql.NpgsqlOperationInProgressException") busyReader = true;
            var second = teardown ? cleanup.Failure : rollback.Failure;
            if (command.Failure is not null && second is not null && Contains(args.Exception, command.Failure) && Contains(args.Exception, second)) retained = true;
        }
        await using var app = fixture.App(interceptors: teardown ? [command, rollback, cleanup] : [command, rollback]);
        using var client = fixture.Client(app);
        using var request = delete ? new HttpRequestMessage(HttpMethod.Delete, $"/quotationrequests/{row.Id}") : Update(row, true, row.ModifiedDate);
        AppDomain.CurrentDomain.FirstChanceException += Observe;
        try
        {
            using var response = await client.SendAsync(request);
            Assert.True(command.Fired);
            Assert.True(teardown ? cleanup.Fired : rollback.Fired, $"Rollback/cleanup fault not reached; busyReader={busyReader}.");
            Assert.True(retained, "Exact synthetic original and rollback/cleanup causes were not retained internally.");
            Assert.Equal(1, command.Executions);
            Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
            await using var db = fixture.Context();
            var persisted = await db.Requests.AsNoTracking().SingleAsync(x => x.Id == row.Id);
            Assert.Equal(row.Message, persisted.Message);
            Assert.Equal(row.ModifiedDate, persisted.ModifiedDate);
            Assert.Equal(0, await db.RequestCreateIdempotency.CountAsync(x => x.RequestId == row.Id));
            Assert.DoesNotContain("Synthetic", await response.Content.ReadAsStringAsync(), StringComparison.Ordinal);
        }
        finally { AppDomain.CurrentDomain.FirstChanceException -= Observe; }
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task Caller_cancellation_propagates_with_correct_pre_or_postcommit_graph(bool delete, bool afterCommit)
    {
        var row = await fixture.SeedAsync(Guid.NewGuid().ToString("N"));
        using var cancel = new CancellationTokenSource();
        var command = new RequestTriageAckFault(delete, cancel);
        var commit = new RequestCreateCommitFault(cancel);
        await using var app = fixture.App(interceptors: [afterCommit ? commit : command]);
        using var client = fixture.Client(app);
        using var request = delete ? new HttpRequestMessage(HttpMethod.Delete, $"/quotationrequests/{row.Id}") : Update(row, true, row.ModifiedDate);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => client.SendAsync(request, cancel.Token));
        Assert.True(afterCommit ? commit.Fired : command.Fired);
        if (afterCommit) Assert.Equal(1, commit.CommitAcknowledgements);
        else Assert.Equal(1, command.Executions);
        await using var db = fixture.Context();
        var persisted = await db.Requests.AsNoTracking().SingleOrDefaultAsync(x => x.Id == row.Id);
        if (afterCommit && delete) Assert.Null(persisted);
        else
        {
            Assert.NotNull(persisted);
            Assert.Equal(afterCommit ? "Reviewed" : row.Message, persisted.Message);
            if (!afterCommit) Assert.Equal(row.ModifiedDate, persisted.ModifiedDate);
        }
    }

    private static bool Contains(Exception exception, Exception expected, int depth = 0)
    {
        if (ReferenceEquals(exception, expected)) return true;
        if (depth == 8) return false;
        if (exception is AggregateException aggregate) return aggregate.InnerExceptions.Any(x => Contains(x, expected, depth + 1));
        return exception.InnerException is not null && Contains(exception.InnerException, expected, depth + 1);
    }

    [Fact]
    public async Task Direct_precommit_business_failure_remains400_without_any_write_or_retry()
    {
        var row = await fixture.SeedAsync(Guid.NewGuid().ToString("N"));
        var fault = new RequestTriageBusinessFault();
        var rollback = new RequestCreateRollbackObserver();
        await using var app = fixture.App(interceptors: [fault, rollback]);
        using var client = fixture.Client(app);
        using var request = Update(row, true, row.ModifiedDate);
        using var response = await client.SendAsync(request);
        Assert.Equal(1, fault.Saves);
        Assert.Equal(1, rollback.Rollbacks);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        await using var db = fixture.Context();
        var persisted = await db.Requests.AsNoTracking().SingleAsync(x => x.Id == row.Id);
        Assert.Equal(row.Message, persisted.Message);
        Assert.Equal(row.ModifiedDate, persisted.ModifiedDate);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Missing_root_outcome_cleanup_failure_is_unavailable_not_a_business400(bool delete)
    {
        var rollback = new RequestCreateRollbackObserver();
        var cleanup = new RequestCreateRollbackTeardownFault(rollback);
        await using var app = fixture.App(interceptors: [rollback, cleanup]);
        using var client = fixture.Client(app);
        var missing = new QuotationRequest { Id = int.MaxValue };
        using var request = delete ? new HttpRequestMessage(HttpMethod.Delete, "/quotationrequests/2147483647") : Update(missing, true, null);
        using var response = await client.SendAsync(request);
        Assert.Equal(1, rollback.Rollbacks);
        Assert.True(cleanup.Fired);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        await using var db = fixture.Context();
        Assert.False(await db.Requests.AnyAsync(x => x.Id == int.MaxValue));
    }

    [Theory]
    [InlineData(false, false, 500)]
    [InlineData(false, true, 500)]
    [InlineData(true, false, 500)]
    [InlineData(true, true, 400)]
    public async Task Unretryable_precommit_failure_preserves_existing_category_and_rolls_back(bool delete, bool business, int status)
    {
        var row = await fixture.SeedAsync(Guid.NewGuid().ToString("N"));
        Exception cause = business ? new InvalidOperationException("Synthetic precommit business failure.") : new PostgresException("Synthetic precommit rejection.", "ERROR", "ERROR", "P0001");
        var fault = new RequestTriageAckFault(delete, failure: cause);
        var rollback = new RequestCreateRollbackObserver();
        await using var app = fixture.App(interceptors: [fault, rollback]);
        using var client = fixture.Client(app);
        using var request = delete ? new HttpRequestMessage(HttpMethod.Delete, $"/quotationrequests/{row.Id}") : Update(row, true, row.ModifiedDate);
        var wrapped = false;
        void Observe(object? sender, FirstChanceExceptionEventArgs args)
        {
            if (args.Exception is DbUpdateException && Contains(args.Exception, cause)) wrapped = true;
        }
        AppDomain.CurrentDomain.FirstChanceException += Observe;
        try
        {
            using var response = await client.SendAsync(request);
            Assert.True(fault.Fired);
            Assert.Equal(1, fault.Executions);
            Assert.Equal(1, rollback.Rollbacks);
            await using var db = fixture.Context();
            var persisted = await db.Requests.AsNoTracking().SingleAsync(x => x.Id == row.Id);
            Assert.Equal(row.Message, persisted.Message);
            Assert.Equal(row.ModifiedDate, persisted.ModifiedDate);
            Assert.True(status == (int)response.StatusCode, $"Expected={status}; actual={(int)response.StatusCode}; exactSyntheticCauseWrappedByDbUpdateException={wrapped}.");
        }
        finally { AppDomain.CurrentDomain.FirstChanceException -= Observe; }
    }

    private static HttpRequestMessage Update(QuotationRequest row, bool? done, DateTime? expected)
    {
        var request = new HttpRequestMessage(HttpMethod.Put, $"/quotationrequests/{row.Id}")
        {
            Content = JsonContent.Create(new
            {
                FirstName = "Reviewed",
                LastName = "Fixture",
                Email = "updated@example.invalid",
                TelephoneNumber = "0000",
                Country = "Thailand",
                CompanyName = "Synthetic",
                TaxIdentification = "fixture",
                Message = "Reviewed",
                InternalComment = "review-only",
                Done = done,
                JourneyId = Guid.NewGuid(),
                TransactionId = "client-forged",
                CreatedDate = DateTime.UtcNow,
                QualificationState = "not_qualified",
                QualificationVersion = 99
            })
        };
        if (expected.HasValue) request.Headers.Add("X-Expected-Modified-Date", DateTime.SpecifyKind(expected.Value, DateTimeKind.Utc).ToString("O"));
        return request;
    }

    private static async Task<JsonDocument> JsonAsync(HttpResponseMessage response)
    {
        await using var stream = await response.Content.ReadAsStreamAsync();
        return await JsonDocument.ParseAsync(stream);
    }
    private static int[] Ids(JsonDocument json) => json.RootElement.GetProperty("Items").EnumerateArray().Select(x => x.GetProperty("Id").GetInt32()).ToArray();
}

public sealed class RequestTriageBusinessFault : SaveChangesInterceptor
{
    public int Saves { get; private set; }
    public override ValueTask<InterceptionResult<int>> SavingChangesAsync(DbContextEventData eventData,
        InterceptionResult<int> result, CancellationToken cancellationToken = default)
    {
        Saves++;
        throw new InvalidOperationException("Synthetic direct precommit business failure.");
    }
}

public sealed class RequestTriageFixture : IAsyncLifetime
{
    private PostgreSqlContainer postgres = null!;
    private IContainer redis = null!;
    private Infrastructure.DisposableContainerPair? containers;
    private readonly RSA key = RSA.Create(2048);
    private string RequestConnection => new NpgsqlConnectionStringBuilder(Infrastructure.DisposablePostgresConnectionPolicy.Isolate(postgres.GetConnectionString())) { Database = "request_triage" }.ConnectionString;
    public string RedisConnection => $"{redis.Hostname}:{redis.GetMappedPublicPort(6379)}";

    public async Task InitializeAsync()
    {
        containers = await Infrastructure.DisposableContainerPair.StartAsync("quotation97-triage",
            attempt => new PostgreSqlBuilder("postgres:18-alpine").WithDockerEndpoint(attempt.Endpoint)
                .WithName(attempt.Name).WithLabel(attempt.Labels).Build(),
            attempt => new ContainerBuilder("redis:7-alpine").WithDockerEndpoint(attempt.Endpoint)
                .WithName(attempt.Name).WithLabel(attempt.Labels).WithPortBinding(6379, true)
                .WithWaitStrategy(Wait.ForUnixContainer().UntilInternalTcpPortIsAvailable(6379)).Build());
        postgres = (PostgreSqlContainer)containers.First;
        redis = containers.Second;
        await using (var connection = new NpgsqlConnection(Infrastructure.DisposablePostgresConnectionPolicy.Isolate(postgres.GetConnectionString())))
        {
            await connection.OpenAsync();
            await using var command = new NpgsqlCommand("CREATE DATABASE request_triage", connection);
            await command.ExecuteNonQueryAsync();
        }
        await using var quotation = new QuotationDbContext(new DbContextOptionsBuilder<QuotationDbContext>().UseNpgsql(Infrastructure.DisposablePostgresConnectionPolicy.Isolate(postgres.GetConnectionString())).Options);
        await quotation.Database.MigrateAsync();
        await using var requests = Context();
        await requests.Database.MigrateAsync();
    }

    public QuotationRequestDbContext Context() => new(new DbContextOptionsBuilder<QuotationRequestDbContext>().UseNpgsql(RequestConnection).Options);

    public async Task<QuotationRequest> SeedAsync(string marker, DateTime? date = null, bool audited = false, string? field = null, string? value = null)
    {
        // Existing historical graph, fixture-only; no invented authorization/session grants.
        await using var db = Context();
        var timestamp = DateTime.SpecifyKind(date ?? DateTime.UtcNow.AddDays(-1), DateTimeKind.Unspecified);
        timestamp = timestamp.AddTicks(-(timestamp.Ticks % 10));
        var row = new QuotationRequest { FirstName = "Synthetic", Message = marker, JourneyId = Guid.NewGuid(), CreatedDate = timestamp, ModifiedDate = timestamp, Done = false };
        if (field is not null) typeof(QuotationRequest).GetProperty(field)!.SetValue(row, value);
        db.Requests.Add(row);
        await db.SaveChangesAsync();
        row.TransactionId = $"request-{row.Id}";
        if (audited)
        {
            row.QualificationState = "qualified"; row.QualificationVersion = 1; row.QualificationStateChangedUtc = timestamp;
            db.RequestQualificationAudit.Add(new()
            {
                RequestId = row.Id,
                JourneyId = row.JourneyId,
                TransactionId = row.TransactionId,
                IdempotencyKey = Guid.NewGuid().ToString("N"),
                PreviousState = "unreviewed",
                NewState = "qualified",
                ChangedBy = "employee-historical-fixture",
                ChangedUtc = timestamp,
                Version = 1
            });
        }
        await db.SaveChangesAsync();
        return row;
    }

    public HttpClient Client(WebApplicationFactory<Program> app, bool authenticated = true, bool employee = true, bool requestFiles = false)
    {
        var client = app.CreateClient();
        if (authenticated)
        {
            var claims = new List<Claim> { new("sub", "employee-triage-fixture"), new("identity_kind", employee ? "employee" : "customer") };
            if (employee) claims.Add(new("role", "Employee"));
            if (requestFiles) claims.Add(new("permissions", "legacy.quotation-files.write"));
            foreach (var permission in new[] { "read", "update", "delete" }) claims.Add(new("permissions", "legacy.quotation-requests." + permission));
            var jwt = new JwtSecurityToken("https://triage-auth.example", "triage-services", claims, DateTime.UtcNow.AddMinutes(-1), DateTime.UtcNow.AddMinutes(10), new SigningCredentials(new RsaSecurityKey(key), SecurityAlgorithms.RsaSha256));
            client.DefaultRequestHeaders.Authorization = new("Bearer", new JwtSecurityTokenHandler().WriteToken(jwt));
        }
        return client;
    }

    public WebApplicationFactory<Program> App(bool allowed = true, params IInterceptor[] interceptors) => new Factory(new Dictionary<string, string?>
    {
        ["ConnectionStrings:QuotationDbContext"] = Infrastructure.DisposablePostgresConnectionPolicy.Isolate(postgres.GetConnectionString()),
        ["ConnectionStrings:QuotationRequestDbContext"] = RequestConnection,
        ["ConnectionStrings:redis"] = RedisConnection,
        ["Jwt:PublicKey"] = Convert.ToBase64String(Encoding.UTF8.GetBytes(key.ExportSubjectPublicKeyInfoPem())),
        ["Jwt:Issuer"] = "https://triage-auth.example",
        ["Jwt:Audience"] = "triage-services",
        ["IAM:LivePermissionChecks:Credential"] = "synthetic-triage-live-check",
        ["OTEL_EXPORTER_OTLP_ENDPOINT"] = "",
        ["Observability:RuntimeMetricsEnabled"] = "false"
    }, allowed, interceptors);

    public async Task DisposeAsync()
    {
        if (containers is not null) await containers.DisposeAsync();
        key.Dispose();
    }

    private sealed class Factory(Dictionary<string, string?> settings, bool allowed, IInterceptor[] interceptors) : WebApplicationFactory<Program>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Production");
            QuotationTestWorkloadExchange.Prepare(builder);
            foreach (var setting in settings) builder.UseSetting(setting.Key, setting.Value);
            builder.ConfigureLogging(logging => logging.ClearProviders());
            builder.ConfigureTestServices(services =>
            {
                if (interceptors.Length > 0) services.AddDbContext<QuotationRequestDbContext>(options => options.AddInterceptors(interceptors));
                services.AddScoped<IIamServiceClient, IamServiceClient>();
                services.AddHttpClient("IAMService", client => client.BaseAddress = new Uri("https://triage-iam.example"))
                    .ConfigurePrimaryHttpMessageHandler(() => new Transport(allowed));
            });
        }
    }

    private sealed class Transport(bool allowed) : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            QuotationTestWorkloadExchange.AssertQuotationSubject(request);
            Assert.Equal(HttpMethod.Post, request.Method);
            Assert.Equal("/iam/v1/auth/check-permission", request.RequestUri!.AbsolutePath);
            Assert.Equal("synthetic-triage-live-check", Assert.Single(request.Headers.GetValues("X-Maliev-IAM-Live-Check-Key")));
            using var json = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(cancellationToken));
            Assert.Equal("employee-triage-fixture", json.RootElement.GetProperty("principalId").GetString());
            Assert.Contains(json.RootElement.GetProperty("permissionId").GetString(), new[] { "legacy.quotation-requests.read", "legacy.quotation-requests.update", "legacy.quotation-requests.delete", "legacy.quotation-files.write" });
            Assert.True(json.RootElement.GetProperty("bypassCache").GetBoolean());
            return new(HttpStatusCode.OK) { Content = JsonContent.Create(new { allowed }) };
        }
    }
}

/// <summary>Throws once after normal Npgsql command execution; reports whether the command was autocommitted.</summary>
public sealed class RequestTriageAckFault(bool delete, CancellationTokenSource? cancel = null, Exception? failure = null) : DbCommandInterceptor
{
    public int Executions { get; private set; }
    public bool Fired { get; private set; }
    public bool Autocommit { get; private set; }
    public HashSet<Guid> Contexts { get; } = [];
    public Exception? Failure { get; private set; }

    private void Observe(DbCommand command)
    {
        var expected = delete ? "DELETE FROM \"Request\"" : "UPDATE \"Request\"";
        if (!command.CommandText.StartsWith(expected, StringComparison.Ordinal)) return;
        Executions++;
        if (Fired) return;
        Fired = true;
        Autocommit = command.Transaction is null;
        if (cancel is not null) { cancel.Cancel(); throw new OperationCanceledException(cancel.Token); }
        Failure = failure ?? new NpgsqlException("Synthetic triage command acknowledgement loss.", new IOException("Synthetic interruption."));
        throw Failure;
    }

    public override ValueTask<int> NonQueryExecutedAsync(DbCommand command, CommandExecutedEventData eventData,
        int result, CancellationToken cancellationToken = default)
    {
        if (command.CommandText.StartsWith(delete ? "DELETE FROM \"Request\"" : "UPDATE \"Request\"", StringComparison.Ordinal))
            Contexts.Add(eventData.Context!.ContextId.InstanceId);
        Observe(command);
        return ValueTask.FromResult(result);
    }

    public override async ValueTask<DbDataReader> ReaderExecutedAsync(DbCommand command, CommandExecutedEventData eventData,
        DbDataReader result, CancellationToken cancellationToken = default)
    {
        if (command.CommandText.StartsWith(delete ? "DELETE FROM \"Request\"" : "UPDATE \"Request\"", StringComparison.Ordinal))
        {
            Contexts.Add(eventData.Context!.ContextId.InstanceId);
            // Throwing before EF receives the reader transfers cleanup responsibility to this fixture.
            if (!Fired) await result.DisposeAsync();
        }
        Observe(command);
        return result;
    }
}
