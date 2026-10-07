// Synthetic codec witness only. This never invokes Brevo, renders a real PDF,
// stores business data, supplies IAM, or claims genuine financial acceptance.
if (args.Length != 1 || !int.TryParse(args[0], out int port) || port is < 1024 or > 65535)
    throw new InvalidDataException("One disposable loopback port required");
var builder = WebApplication.CreateSlimBuilder();
builder.Logging.ClearProviders();
builder.WebHost.UseUrls($"http://127.0.0.1:{port}");
var app = builder.Build();
app.MapGet("/health", () => Results.Ok());
app.MapPost("/pdfs/invoice", () => Results.Ok());
app.MapPost("/Uploads", () => Results.Created("/synthetic", new { FixtureOnly = true }));
app.MapDelete("/Uploads", () => Results.NoContent());
app.MapGet("/trigger", async () =>
{
    using var http = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{port}"), Timeout = TimeSpan.FromSeconds(5) };
    using var render = await http.PostAsync("/pdfs/invoice", new StringContent("synthetic"));
    using var upload = await http.PostAsync("/Uploads?synthetic-query-must-not-enter-projection", new StringContent("synthetic"));
    using var remove = await http.DeleteAsync("/Uploads?synthetic-query-must-not-enter-projection");
    return render.IsSuccessStatusCode && upload.IsSuccessStatusCode && remove.IsSuccessStatusCode ? Results.NoContent() : Results.StatusCode(500);
});
app.MapGet("/replay", () => Results.NoContent());
await app.RunAsync();
