using Maliev.Aspire.ServiceDefaults.Logging;
using Microsoft.Extensions.Logging.Console;

namespace Legacy.Maliev.QuotationService.Api.Diagnostics;

/// <summary>Reports fatal application initialization without the runtime's plaintext exception fallback.</summary>
public static class QuotationStartupBoundary
{
    /// <summary>Runs the host entry point and returns a safe failing exit code for initialization errors.</summary>
    /// <param name="initializeHost">The real application initialization and lifetime operation.</param>
    /// <returns>Zero after normal shutdown, or one after a reported initialization failure.</returns>
    public static async Task<int> RunAsync(Func<Task> initializeHost)
    {
        ArgumentNullException.ThrowIfNull(initializeHost);
        try
        {
            await initializeHost();
            return 0;
        }
        // Framework hosts need the original exception for discovery and startup validation.
        // Only this executable owns its process exit code and fatal console fallback.
        catch (Exception exception) when (System.Reflection.Assembly.GetEntryAssembly() == typeof(QuotationStartupBoundary).Assembly
            && exception is not HostAbortedException)
        {
            using var factory = LoggerFactory.Create(logging =>
            {
                logging.SetMinimumLevel(LogLevel.Warning);
                logging.AddConsoleFormatter<MalievCloudJsonConsoleFormatter, JsonConsoleFormatterOptions>(options =>
                {
                    options.UseUtcTimestamp = true;
                    options.TimestampFormat = "O";
                    options.IncludeScopes = false;
                });
                logging.AddConsole(options => options.FormatterName = MalievCloudJsonConsoleFormatter.FormatterName);
            });
            factory.CreateLogger("HostStartup").LogCritical(new EventId(5102, "StartupFailure"),
                "{EventName} Operation={Operation} ExceptionType={ExceptionType}",
                "StartupFailure", "HostInitialization", exception.GetType().Name);
            return 1;
        }
    }
}
