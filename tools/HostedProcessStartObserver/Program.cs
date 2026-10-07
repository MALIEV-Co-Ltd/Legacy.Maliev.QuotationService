using System.Text.Json;
using System.Text.Json.Serialization;
using HostedProcessStartObserver;

try
{
    if (args is not [var suppliedPath] || !OperatingSystem.IsLinux()
        || Environment.GetEnvironmentVariable("GITHUB_ACTIONS") != "true")
        throw new InvalidDataException("Single-profile hosted Linux observer required.");
    var workspace = Path.GetFullPath(Environment.GetEnvironmentVariable("GITHUB_WORKSPACE") ?? "");
    var ownedRoot = Path.Combine(workspace, "TestResults", "C821ProducerProfiles") + Path.DirectorySeparatorChar;
    var path = Path.GetFullPath(suppliedPath);
    if (path != suppliedPath || !path.StartsWith(ownedRoot, StringComparison.Ordinal))
        throw new InvalidDataException("Bounded run-owned private profile required.");
    FileSystemInfo? current = new FileInfo(path);
    while (current is not null)
    {
        if (current.LinkTarget is not null) throw new InvalidDataException("Redirected profile rejected.");
        current = current is FileInfo file ? file.Directory : ((DirectoryInfo)current).Parent;
    }
    if ((File.GetUnixFileMode(path) & (UnixFileMode.GroupRead | UnixFileMode.GroupWrite | UnixFileMode.GroupExecute
        | UnixFileMode.OtherRead | UnixFileMode.OtherWrite | UnixFileMode.OtherExecute)) != 0)
        throw new InvalidDataException("Owner-only input profile required.");
    using var lifetime = new CancellationTokenSource(TimeSpan.FromSeconds(5));
    var bytes = await ActualHostStartObservation.ReadBounded(path, 16384, lifetime.Token);
    using var document = JsonDocument.Parse(bytes, new JsonDocumentOptions { MaxDepth = 4 });
    RequireUnique(document.RootElement);
    var request = document.Deserialize<HostStartRequest>(new JsonSerializerOptions
    {
        RespectRequiredConstructorParameters = true,
        RespectNullableAnnotations = true,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
    }) ?? throw new InvalidDataException("Observation input missing.");
    Console.WriteLine(JsonSerializer.Serialize(await ActualHostStartObservation.ObserveAsync(request, lifetime.Token)));
}
catch (Exception error)
{
    // No raw exception message, environment, input frame, tokens or credential-derived digest.
    Console.Error.WriteLine("Actual host start observation failed: " + error.GetType().Name);
    Environment.ExitCode = 1;
}

static void RequireUnique(JsonElement value)
{
    if (value.ValueKind == JsonValueKind.Object)
    {
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var property in value.EnumerateObject())
        {
            if (!names.Add(property.Name)) throw new InvalidDataException("Duplicate profile field.");
            RequireUnique(property.Value);
        }
    }
    else if (value.ValueKind == JsonValueKind.Array)
        foreach (var element in value.EnumerateArray()) RequireUnique(element);
}
