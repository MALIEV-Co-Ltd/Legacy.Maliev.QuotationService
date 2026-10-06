using System.IO.Pipes;
using System.Net;
using InvoiceCompletionProducerAcceptance.Companion;
using Microsoft.AspNetCore.Server.Kestrel.Core;

if (args is not [var profilePath] || !OperatingSystem.IsLinux()
    || Environment.GetEnvironmentVariable("GITHUB_ACTIONS") != "true")
    throw new InvalidDataException("Dedicated normal hosted storage-front caller required.");
using var startup = new CancellationTokenSource(TimeSpan.FromSeconds(30));
var profile = await FrontHostAdmission.ReadAsync(profilePath, startup.Token);
if (typeof(HostedStorageFrontAssemblyMarker).Assembly.Location != profile.ExecutableDll)
    throw new InvalidDataException("Actual front assembly differs from the admitted executable.");
var remaining = profile.ExpiresUtc - DateTimeOffset.UtcNow;
if (remaining <= TimeSpan.Zero) throw new InvalidDataException("Front owner lease already expired.");
using var lease = new CancellationTokenSource(remaining);
using var admission = CancellationTokenSource.CreateLinkedTokenSource(startup.Token, lease.Token);
await FrontHostAdmission.ObserveOwnerAsync(profile, admission.Token);
var backend = new ObservedStorageBackend(profile.Backend);
await backend.ObserveAsync(admission.Token);
using var front = new HostedStorageFront(profile.FrontOrigin, backend, profile.RunId, profile.ExpiresUtc);
var builder = WebApplication.CreateSlimBuilder(new WebApplicationOptions { Args = [], ContentRootPath = profile.Repository });
builder.Configuration.Sources.Clear();
builder.Logging.ClearProviders();
builder.WebHost.ConfigureKestrel(options =>
{
    options.AddServerHeader = false;
    options.Limits.MaxRequestBodySize = 32 * 1024 * 1024;
    options.Limits.MaxRequestHeadersTotalSize = 32768;
    options.Limits.MaxConcurrentConnections = 16;
    options.Limits.MaxConcurrentUpgradedConnections = 0;
    options.Limits.RequestHeadersTimeout = TimeSpan.FromSeconds(2);
    options.Limits.KeepAliveTimeout = TimeSpan.FromSeconds(5);
    options.Limits.MinRequestBodyDataRate = new MinDataRate(1024, TimeSpan.FromSeconds(2));
    options.Limits.MinResponseDataRate = new MinDataRate(1024, TimeSpan.FromSeconds(2));
    options.Listen(IPAddress.Parse(profile.FrontOrigin.Host.Trim('[', ']')), profile.FrontOrigin.Port,
        listen => listen.Protocols = HttpProtocols.Http1);
});
await using var app = builder.Build();
app.Run(front.HandleAsync);
try
{
    await app.StartAsync(admission.Token);
    // Listener is observable before File starts; all data requests stay 503 until owner bootstrap installs.
    Console.WriteLine("Owned storage-front listener started; public bootstrap pending.");
    using var pipe = new AnonymousPipeClientStream(PipeDirection.In, profile.BootstrapPipeHandle);
    using var bootstrap = CancellationTokenSource.CreateLinkedTokenSource(startup.Token, lease.Token);
    await front.InstallPublicBootstrapAsync(pipe, bootstrap.Token);
    await FrontHostAdmission.ObserveOwnerAsync(profile, bootstrap.Token);
    Console.WriteLine("Owned public-only bootstrap installed.");
    await app.WaitForShutdownAsync(lease.Token);
}
catch (OperationCanceledException) when (lease.IsCancellationRequested)
{
    // The finite owner lease is a normal termination path; finally still stops the exact listener.
}
finally
{
    front.Dispose();
    using var shutdown = new CancellationTokenSource(TimeSpan.FromSeconds(5));
    await app.StopAsync(shutdown.Token);
}

internal sealed class HostedStorageFrontAssemblyMarker { }
