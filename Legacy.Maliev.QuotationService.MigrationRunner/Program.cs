namespace Legacy.Maliev.QuotationService.MigrationRunner;

public static class Program
{
    public static Task<int> Main(string[] args)
    {
        var configuration = Environment.GetEnvironmentVariables()
            .Cast<System.Collections.DictionaryEntry>()
            .ToDictionary(
                entry => (string)entry.Key,
                entry => entry.Value?.ToString(),
                StringComparer.Ordinal);
        return MigrationRunnerApplication.RunAsync(args, configuration, Console.Error, CancellationToken.None);
    }
}
