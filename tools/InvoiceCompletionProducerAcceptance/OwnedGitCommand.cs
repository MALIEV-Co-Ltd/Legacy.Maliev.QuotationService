using System.ComponentModel;
using System.Diagnostics;
using System.Text;
using QualificationOutcomeWireSource;

namespace InvoiceCompletionProducerAcceptance;

/// <summary>Exact held-child Git preflight with bounded output and retained cleanup observations.</summary>
public static class OwnedGitCommand
{
    /// <summary>Runs one bounded Git command without a shell or detached child.</summary>
    public static async Task<string> RunAsync(string repository, IReadOnlyList<string> arguments,
        IList<GitResource> resources, CancellationToken cancellationToken, int maximumOutput = 262144)
    {
        if (maximumOutput is < 1 or > 262144) throw new InvalidDataException("Finite preflight output bound required.");
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(10));
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo("git")
            {
                WorkingDirectory = repository,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            },
        };
        foreach (var argument in arguments) process.StartInfo.ArgumentList.Add(argument);
        if (!process.Start()) throw new InvalidDataException("Source preflight did not start.");
        DateTime? started = null;
        string? identity = null;
        Task<byte[]>? output = null;
        Task<byte[]>? errors = null;
        var position = -1;
        var recorded = false;
        Exception? commandFailure = null;
        Exception? cleanupFailure = null;
        try
        {
            started = ChildStartObservation.Capture(() => process.StartTime.ToUniversalTime(), () => process.HasExited);
            try { identity = process.MainModule?.FileName; }
            catch (Win32Exception) when (process.HasExited) { }
            catch (InvalidOperationException) when (process.HasExited) { }
            position = resources.Count;
            resources.Add(new(process.Id, started, identity, false));
            recorded = true;
            output = ReadBoundedAsync(process.StandardOutput.BaseStream, maximumOutput, deadline.Token);
            errors = ReadBoundedAsync(process.StandardError.BaseStream, 16384, deadline.Token);
            await process.WaitForExitAsync(deadline.Token);
            await errors;
            if (process.ExitCode != 0) throw new InvalidDataException("Source preflight failed.");
            return new UTF8Encoding(false, true).GetString(await output).Trim();
        }
        catch (Exception failure)
        {
            commandFailure = failure;
            throw;
        }
        finally
        {
            try
            {
                process.Refresh();
                if (!process.HasExited)
                {
                    // The held stdin pipe gives Git commands a graceful EOF before exact-child termination.
                    process.StandardInput.Close();
                    using var grace = new CancellationTokenSource(TimeSpan.FromMilliseconds(250));
                    try { await process.WaitForExitAsync(grace.Token); }
                    catch (OperationCanceledException) when (grace.IsCancellationRequested) { }
                    if (!process.HasExited)
                    {
                        if (started is null || process.StartTime.ToUniversalTime() != started.Value)
                            throw new InvalidDataException("Source preflight child ownership is uncertain.");
                        process.Kill(entireProcessTree: false);
                    }
                }
                using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                await process.WaitForExitAsync(cleanup.Token);
                if (!process.HasExited) throw new InvalidDataException("Source preflight child did not exit.");
            }
            catch (Exception failure)
            {
                cleanupFailure = failure;
                throw;
            }
            finally
            {
                deadline.Cancel();
                process.StandardInput.Close();
                process.StandardOutput.Close();
                process.StandardError.Close();
                foreach (var reader in new[] { output, errors })
                    if (reader is not null)
                        try { await reader; }
                        catch (Exception failure) when (failure is IOException or InvalidDataException or OperationCanceledException or ObjectDisposedException)
                        { }
                var terminal = new GitResource(process.Id, started, identity, process.HasExited);
                try
                {
                    if (recorded) resources[position] = terminal;
                    else resources.Add(terminal);
                }
                catch (Exception failure)
                {
                    failure.Data["OwnedGitResource"] = terminal;
                    if (commandFailure is null && cleanupFailure is null) throw;
                }
                var retainedFailure = cleanupFailure ?? commandFailure;
                if (retainedFailure is not null) retainedFailure.Data["OwnedGitResource"] = terminal;
            }
        }
    }

    private static async Task<byte[]> ReadBoundedAsync(Stream input, int maximum, CancellationToken cancellationToken)
    {
        using var output = new MemoryStream();
        byte[] buffer = new byte[4096];
        while (true)
        {
            int count = await input.ReadAsync(buffer, cancellationToken);
            if (count == 0) return output.ToArray();
            if (output.Length + count > maximum) throw new InvalidDataException("Source preflight output exceeded its bound.");
            await output.WriteAsync(buffer.AsMemory(0, count), cancellationToken);
        }
    }
}

/// <summary>Actual owned preflight resource; unavailable exited-child observations remain null.</summary>
public sealed record GitResource(int ProcessId, DateTime? ActualStartUtc, string? Executable, bool Exited);
