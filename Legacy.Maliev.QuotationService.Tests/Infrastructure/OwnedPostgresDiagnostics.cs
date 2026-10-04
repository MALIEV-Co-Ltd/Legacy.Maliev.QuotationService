using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Docker.DotNet;
using Docker.DotNet.Models;
using DotNet.Testcontainers.Containers;
using Npgsql;

namespace Legacy.Maliev.QuotationService.Tests.Infrastructure;

internal enum PostgresDiagnosticEvent
{
    Ready, FastShutdown, ImmediateShutdown, Reinitializing, ProcessSignal, NoSpace, SharedMemoryResize, Panic, UnexpectedPostmasterExit
}

internal sealed record PostgresEvent(PostgresDiagnosticEvent Event, int? Signal = null);

internal enum PostgresCorrelation { Unknown, Matched, Mismatched }
internal sealed record DiagnosticEndpoint(string Host, int Port);
internal sealed record SqlBackendIdentity(int NpgsqlPid, int SqlPid, DateTimeOffset PostmasterStart, ulong SystemId);
internal sealed record OwnedBackendIdentity(int Pid, int ParentPid, ulong StartTicks, int PostmasterPid, DateTimeOffset PostmasterStart, ulong SystemId);
internal enum PostgresFailureCategory { Unknown, Postgres, Npgsql, EndOfStream, Io, Canceled, Timeout }
internal sealed record PostgresFailureSignal(IReadOnlyList<PostgresFailureCategory> Categories, string? SqlState);
internal sealed record PassiveOwnedProcess(int Pid, int ParentPid, ulong StartTicks);

/// <summary>Test-only, owned-container observations; never emits server log text or exception messages.</summary>
internal static class OwnedPostgresDiagnostics
{
    // A passive PID is separate from SQL/owned lineage proof.
    internal static int? ReadPassivePid(bool connectionOpen, Func<int> readPid)
    {
        if (!connectionOpen) return null;
        try { var pid = readPid(); return pid > 0 ? pid : null; }
        catch { return null; }
    }

    internal static async Task<PassiveOwnedProcess?> ReadOwnedProcessAsync(string id, string owner,
        ContainerInspectResponse inspected, int? pid, Func<int, Task<string>> readProc)
    {
        VerifyOwnership(id, owner, inspected);
        if (pid is null or <= 0) return null;
        try
        {
            var input = await readProc(pid.Value);
            CheckBudget(input);
            var fields = input.TrimEnd('\n').Split('\n');
            if (fields.Length != 3
                || !int.TryParse(fields[0].TrimEnd('\r'), NumberStyles.None, CultureInfo.InvariantCulture, out var observedPid)
                || observedPid != pid.Value
                || !int.TryParse(fields[1].TrimEnd('\r'), NumberStyles.None, CultureInfo.InvariantCulture, out var parentPid) || parentPid <= 0
                || !ulong.TryParse(fields[2].TrimEnd('\r'), NumberStyles.None, CultureInfo.InvariantCulture, out var startTicks) || startTicks == 0) return null;
            return new(observedPid, parentPid, startTicks);
        }
        catch { return null; }
    }

    internal static PostgresFailureSignal ClassifyFailure(Exception error)
    {
        var categories = new List<PostgresFailureCategory>(8);
        string? state = null;
        for (Exception? current = error; current is not null && categories.Count < 8; current = current.InnerException)
        {
            categories.Add(current switch
            {
                PostgresException => PostgresFailureCategory.Postgres,
                NpgsqlException => PostgresFailureCategory.Npgsql,
                EndOfStreamException => PostgresFailureCategory.EndOfStream,
                IOException => PostgresFailureCategory.Io,
                OperationCanceledException => PostgresFailureCategory.Canceled,
                TimeoutException => PostgresFailureCategory.Timeout,
                _ => PostgresFailureCategory.Unknown
            });
            if (state is null && current is PostgresException postgres && IsSafeSqlState(postgres.SqlState)) state = postgres.SqlState;
        }
        return new(categories, state);
    }

    internal static async Task EmitFailureAsync(Func<Task<PostgresFailureSignal>> observe, Action<string> output)
    {
        try
        {
            var signal = await observe();
            var safe = JsonSerializer.Serialize(new
            {
                Categories = signal.Categories.Take(8).Select(category => Enum.IsDefined(category) ? category.ToString() : "Unknown").ToArray(),
                SqlState = IsSafeSqlState(signal.SqlState) ? signal.SqlState : null
            });
            if (safe.Length <= 2048) output(safe);
        }
        catch { /* Diagnostics/output must never replace the original caught failure. */ }
    }

    private static bool IsSafeSqlState(string? state) => state is { Length: 5 }
        && state.All(character => character is >= 'A' and <= 'Z' or >= '0' and <= '9');

    internal static PostgresCorrelation CorrelateEndpoint(DiagnosticEndpoint? selected, IReadOnlyList<DiagnosticEndpoint>? published)
    {
        if (selected is null || published is null || published.Count == 0) return PostgresCorrelation.Unknown;
        return selected.Port is > 0 and <= 65535 && selected.Host is "localhost" or "127.0.0.1" or "::1"
            && published.All(binding => binding.Port == selected.Port && binding.Host is "localhost" or "127.0.0.1" or "::1" or "0.0.0.0" or "::")
            ? PostgresCorrelation.Matched : PostgresCorrelation.Mismatched;
    }

    internal static PostgresCorrelation CorrelateBackend(SqlBackendIdentity? sql, OwnedBackendIdentity? owned)
    {
        if (sql is null || owned is null) return PostgresCorrelation.Unknown;
        return sql.NpgsqlPid > 0 && sql.NpgsqlPid == sql.SqlPid && sql.SqlPid == owned.Pid
            && owned.PostmasterPid > 0 && owned.ParentPid == owned.PostmasterPid && owned.StartTicks > 0
            && sql.PostmasterStart != default && sql.PostmasterStart == owned.PostmasterStart
            && sql.SystemId > 0 && sql.SystemId == owned.SystemId
            ? PostgresCorrelation.Matched : PostgresCorrelation.Mismatched;
    }
    internal const int InputBudget = 16 * 1024;
    private static readonly string[] Phases = ["ready", "second-database", "both-migrations", "initialization-failed"];
    private const string MetricsCommand = "awk '{sub(/^.*\\) /,\"\"); split($0,a,\" \"); print \"pid1_ppid=\" a[2]; print \"pid1_start=\" a[20]}' /proc/1/stat; "
        + "p=; IFS= read -r p < /var/lib/postgresql/18/docker/postmaster.pid; case \"$p\" in ''|*[!0-9]*) ;; *) echo postmaster_pid=$p; "
        + "if [ -r /proc/$p/stat ]; then awk '{sub(/^.*\\) /,\"\"); split($0,a,\" \"); print \"postmaster_ppid=\" a[2]; print \"postmaster_start=\" a[20]}' /proc/$p/stat; fi ;; esac; "
        + "for f in memory.current memory.max pids.current pids.max; do if [ -r /sys/fs/cgroup/$f ]; then printf '%s=' \"$f\"; head -c 32 /sys/fs/cgroup/$f; echo; fi; done; "
        + "if [ -r /sys/fs/cgroup/memory.events ]; then awk '{print \"memory.events.\" $1 \"=\" $2}' /sys/fs/cgroup/memory.events; fi; "
        + "df -kP /dev/shm | awk 'NR==2 {print \"shm_total=\" $2; print \"shm_used=\" $3; print \"shm_free=\" $4}'; "
        + "df -kP /var/lib/postgresql | awk 'NR==2 {print \"pgdata_total=\" $2; print \"pgdata_used=\" $3; print \"pgdata_free=\" $4}'; "
        + "du -sk /var/lib/postgresql | awk '{print \"pgdata_du=\" $1}'; "
        + "du -sk /var/lib/postgresql/18/docker/pg_wal | awk '{print \"wal_du=\" $1}'";
    private static readonly HashSet<string> MetricKeys = new(StringComparer.Ordinal)
    {
        "pid1_ppid", "pid1_start", "postmaster_pid", "postmaster_ppid", "postmaster_start",
        "memory.current", "memory.max", "pids.current", "pids.max", "memory.events.low",
        "memory.events.high", "memory.events.max", "memory.events.oom", "memory.events.oom_kill",
        "memory.events.oom_group_kill", "shm_total", "shm_used", "shm_free", "pgdata_total",
        "pgdata_used", "pgdata_free", "pgdata_du", "wal_du"
    };

    internal static Task<string> ObserveConnectionAsync(IContainer container, string owner, string phase, NpgsqlConnection connection) =>
        PreserveFailureAsync(async () =>
        {
            if (phase is not ("open-start" or "open-complete" or "create-start" or "create-complete")) throw new InvalidOperationException();
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            using var docker = new DockerClientBuilder().WithEndpoint(new Uri(await DisposableContainerStartup.LocalDockerEndpointAsync(deadline.Token))).Build();
            var inspected = await docker.Containers.InspectContainerAsync(container.Id, deadline.Token);
            VerifyOwnership(container.Id, owner, inspected);
            var selected = connection.State == System.Data.ConnectionState.Open
                ? new DiagnosticEndpoint(connection.Host ?? string.Empty, connection.Port) : null;
            var passivePid = ReadPassivePid(connection.State == System.Data.ConnectionState.Open, () => connection.ProcessID);
            var passiveProcess = await ReadOwnedProcessAsync(container.Id, owner, inspected, passivePid, async pid =>
            {
                var observed = await container.ExecAsync(["sh", "-c", "awk '{print $1; sub(/^.*\\) /,\"\"); split($0,a,\" \"); print a[2]; print a[20]}' /proc/"
                    + pid.ToString(CultureInfo.InvariantCulture) + "/stat"], deadline.Token);
                return observed.ExitCode == 0 ? observed.Stdout : string.Empty;
            });
            var published = new List<DiagnosticEndpoint>();
            if (inspected.NetworkSettings?.Ports?.TryGetValue("5432/tcp", out var bindings) == true && bindings is not null)
                foreach (var binding in bindings)
                    published.Add(new(binding.HostIP, int.TryParse(binding.HostPort, NumberStyles.None, CultureInfo.InvariantCulture, out var port) ? port : 0));
            var sql = await ReadCallerIdentityAsync(phase, connection.State == System.Data.ConnectionState.Open, async () =>
            {
                await using var command = new NpgsqlCommand("SELECT pg_backend_pid(), (extract(epoch from pg_postmaster_start_time()) * 1000000)::bigint, system_identifier::text FROM pg_control_system()", connection);
                await using var reader = await command.ExecuteReaderAsync(deadline.Token);
                if (!await reader.ReadAsync(deadline.Token)) throw new InvalidOperationException();
                return new SqlBackendIdentity(connection.ProcessID, reader.GetInt32(0), FromMicroseconds(reader.GetInt64(1)), ulong.Parse(reader.GetString(2), CultureInfo.InvariantCulture));
            });
            OwnedBackendIdentity? owned = null;
            if (sql is not null)
            {
                if (sql.SqlPid <= 0) throw new InvalidOperationException();
                // The independently owned socket supplies only cluster/start identity, not a surrogate backend identity.
                var control = await container.ExecAsync(["psql", "--no-psqlrc", "-X", "-h", "/var/run/postgresql", "-U", "postgres", "-d", "postgres", "-Atqc",
                    "SELECT (extract(epoch from pg_postmaster_start_time()) * 1000000)::bigint, system_identifier::text FROM pg_control_system()"], deadline.Token);
                CheckBudget(control.Stdout);
                var fields = control.Stdout.Trim().Split('|');
                if (control.ExitCode != 0 || fields.Length != 2) throw new InvalidOperationException();
                var ownedStart = FromMicroseconds(long.Parse(fields[0], NumberStyles.None, CultureInfo.InvariantCulture));
                var system = ulong.Parse(fields[1], NumberStyles.None, CultureInfo.InvariantCulture);
                var pid = sql.SqlPid.ToString(CultureInfo.InvariantCulture);
                var proc = await container.ExecAsync(["sh", "-c", "awk '{print $1; sub(/^.*\\) /,\"\"); split($0,a,\" \"); print a[2]; print a[20]}' /proc/" + pid + "/stat; head -n 1 /var/lib/postgresql/18/docker/postmaster.pid"], deadline.Token);
                CheckBudget(proc.Stdout);
                var numbers = proc.Stdout.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries);
                if (proc.ExitCode != 0 || numbers.Length != 4) throw new InvalidOperationException();
                owned = new(int.Parse(numbers[0], CultureInfo.InvariantCulture), int.Parse(numbers[1], CultureInfo.InvariantCulture),
                    ulong.Parse(numbers[2], CultureInfo.InvariantCulture), int.Parse(numbers[3], CultureInfo.InvariantCulture), ownedStart, system);
            }
            return JsonSerializer.Serialize(new
            {
                phase,
                Endpoint = CorrelateEndpoint(selected, published).ToString(),
                SelectedPort = selected?.Port,
                PassivePid = passivePid,
                PassiveOwnedProcess = passiveProcess,
                PublishedPorts = published.Select(item => item.Port).Distinct().Take(4).ToArray(),
                Backend = CorrelateBackend(sql, owned).ToString(),
                Sql = sql,
                Owned = owned
            });
        });

    // Never let active diagnostics run on the borrowed connection before its original SQL completes.
    internal static async Task<SqlBackendIdentity?> ReadCallerIdentityAsync(string phase, bool connectionOpen, Func<Task<SqlBackendIdentity>> query)
    {
        if (phase is not ("open-start" or "open-complete" or "create-start" or "create-complete")) throw new InvalidOperationException();
        return connectionOpen && phase == "create-complete" ? await query() : null;
    }

    private static DateTimeOffset FromMicroseconds(long value) => DateTimeOffset.UnixEpoch.AddTicks(checked(value * 10));

    internal static async Task<string> ObserveAsync(IContainer container, string owner, string phase)
    {
        return await PreserveFailureAsync(async () =>
        {
            if (!Phases.Contains(phase, StringComparer.Ordinal)) throw new InvalidOperationException();
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            var endpoint = new Uri(await DisposableContainerStartup.LocalDockerEndpointAsync(deadline.Token));
            using var docker = new DockerClientBuilder().WithEndpoint(endpoint).Build();
            var inspected = await docker.Containers.InspectContainerAsync(container.Id, deadline.Token);
            VerifyOwnership(container.Id, owner, inspected);
            var state = inspected.State ?? throw new InvalidOperationException();
            var metrics = new Dictionary<string, string>();
            if (state.Running)
            {
                // Fixed files/keys only; cap inside the owned container too. Stderr is never emitted.
                var result = await container.ExecAsync(["sh", "-c", "{ " + MetricsCommand + "; } 2>/dev/null | head -c 16385"], deadline.Token);
                metrics = ParseMetrics(result.Stdout);
            }
            using var logs = await docker.Containers.GetContainerLogsAsync(container.Id,
                new ContainerLogsParameters { ShowStdout = true, ShowStderr = true, Follow = false, Tail = "100" }, deadline.Token);
            var raw = await ReadBoundedAsync(async (buffer, token) =>
                (await logs.ReadOutputAsync(buffer, 0, buffer.Length, token)).Count, deadline.Token);
            var events = Classify(raw);
            return JsonSerializer.Serialize(new
            {
                phase,
                ContainerId = inspected.ID,
                inspected.Image,
                state.Running,
                state.ExitCode,
                state.OOMKilled,
                HostPid = state.Pid,
                state.StartedAt,
                state.FinishedAt,
                inspected.RestartCount,
                Metrics = metrics,
                Events = events
            });
        });
    }

    internal static async Task<string> PreserveFailureAsync(Func<Task<string>> observe)
    {
        try { return await observe(); }
        catch (Exception) { return "{\"DiagnosticUnavailable\":true}"; }
    }

    internal static void VerifyOwnership(string id, string owner, ContainerInspectResponse inspected)
    {
        if (!Regex.IsMatch(id, "\\A[a-f0-9]{64}\\z", RegexOptions.CultureInvariant)
            || inspected.ID != id || inspected.Config?.Labels is not { } labels
            || !labels.TryGetValue("maliev.proof.owner", out var actualOwner) || actualOwner != owner
            || !labels.TryGetValue("maliev.proof.run", out var run) || !Regex.IsMatch(run, "\\A[a-f0-9]{32}\\z", RegexOptions.CultureInvariant)
            || !labels.TryGetValue("maliev.proof.resource", out var resource) || resource != "pg"
            || !labels.TryGetValue("maliev.proof.attempt", out var attempt) || attempt is not ("1" or "2" or "3")
            || inspected.Name != $"/quotation97-{run}-pg-{attempt}") throw new InvalidOperationException("Diagnostic ownership refused.");
    }

    internal static Dictionary<string, string> ParseMetrics(string input)
    {
        CheckBudget(input);
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var line in input.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var pair = line.TrimEnd('\r').Split('=');
            if (pair.Length != 2 || !MetricKeys.Contains(pair[0]) || result.ContainsKey(pair[0])
                || !(ulong.TryParse(pair[1], NumberStyles.None, CultureInfo.InvariantCulture, out _)
                    || pair[1] == "max" && pair[0] is "memory.max" or "pids.max")) throw new InvalidOperationException();
            result.Add(pair[0], pair[1]);
        }
        return result;
    }

    internal static IReadOnlyList<PostgresEvent> Classify(string input)
    {
        CheckBudget(input);
        var result = new List<PostgresEvent>();
        foreach (var line in input.Split('\n'))
        {
            var match = Regex.Match(line.TrimEnd('\r'), "\\A\\d{4}-\\d{2}-\\d{2} [0-9:.]+ [A-Z]+ \\[\\d+\\] (LOG|ERROR|FATAL|PANIC):  (.*)\\z", RegexOptions.CultureInvariant);
            if (!match.Success) continue;
            var text = match.Groups[2].Value;
            var structuralLevel = match.Groups[1].Value == "LOG";
            var signal = Regex.Match(text, "\\Aserver process \\(PID \\d+\\) was terminated by signal (\\d{1,2})(?::|\\z)", RegexOptions.CultureInvariant);
            PostgresEvent? observed = null;
            if (structuralLevel && signal.Success && int.TryParse(signal.Groups[1].Value, out var number) && number is > 0 and <= 64)
                observed = new(PostgresDiagnosticEvent.ProcessSignal, number);
            else if (structuralLevel && text == "database system is ready to accept connections") observed = new(PostgresDiagnosticEvent.Ready);
            else if (structuralLevel && text == "received fast shutdown request") observed = new(PostgresDiagnosticEvent.FastShutdown);
            else if (structuralLevel && text == "received immediate shutdown request") observed = new(PostgresDiagnosticEvent.ImmediateShutdown);
            else if (structuralLevel && text == "all server processes terminated; reinitializing") observed = new(PostgresDiagnosticEvent.Reinitializing);
            else if (match.Groups[1].Value == "FATAL" && text == "terminating connection due to unexpected postmaster exit") observed = new(PostgresDiagnosticEvent.UnexpectedPostmasterExit);
            else if (text.Contains("No space left on device", StringComparison.Ordinal)) observed = new(PostgresDiagnosticEvent.NoSpace);
            else if (text.StartsWith("could not resize shared memory segment", StringComparison.Ordinal)) observed = new(PostgresDiagnosticEvent.SharedMemoryResize);
            else if (match.Groups[1].Value == "PANIC") observed = new(PostgresDiagnosticEvent.Panic);
            if (observed is not null && !result.Contains(observed)) result.Add(observed);
        }
        return result;
    }

    internal static async Task<string> ReadBoundedAsync(Func<byte[], CancellationToken, Task<int>> read, CancellationToken token = default)
    {
        var buffer = new byte[1024];
        using var output = new MemoryStream();
        while (true)
        {
            var count = await read(buffer, token);
            if (count == 0) return Encoding.UTF8.GetString(output.ToArray());
            if (count < 0 || count > buffer.Length || output.Length + count > InputBudget) throw new IOException("Diagnostic input budget exceeded.");
            output.Write(buffer, 0, count);
        }
    }

    private static void CheckBudget(string input)
    {
        if (Encoding.UTF8.GetByteCount(input) > InputBudget) throw new IOException("Diagnostic input budget exceeded.");
    }
}
