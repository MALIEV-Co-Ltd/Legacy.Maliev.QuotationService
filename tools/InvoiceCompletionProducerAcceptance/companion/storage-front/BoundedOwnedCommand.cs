using System.ComponentModel;
using System.Diagnostics;

namespace InvoiceCompletionProducerAcceptance.Companion;

internal static class BoundedOwnedCommand
{
    internal static async Task<byte[]> RunAsync(string executable, IReadOnlyList<string> arguments,
        int maximumOutput, CancellationToken cancellationToken)
    {
        if (maximumOutput is < 1 or > 2097152) throw new InvalidDataException("Finite observation output bound required.");
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(5));
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo(executable)
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            },
        };
        foreach (var argument in arguments) process.StartInfo.ArgumentList.Add(argument);
        if (!process.Start()) throw new InvalidDataException("Owned observation helper did not start.");
        DateTime? started = null;
        Task<byte[]>? output = null;
        Task<byte[]>? error = null;
        try
        {
            try { started = process.StartTime.ToUniversalTime(); }
            catch (Win32Exception) when (process.HasExited) { }
            catch (InvalidOperationException) when (process.HasExited) { }
            output = ReadAsync(process.StandardOutput.BaseStream, maximumOutput, deadline.Token);
            error = ReadAsync(process.StandardError.BaseStream, 16384, deadline.Token);
            await process.WaitForExitAsync(deadline.Token);
            await error;
            if (process.ExitCode != 0) throw new InvalidDataException("Owned observation helper failed.");
            return await output;
        }
        finally
        {
            try
            {
                process.Refresh();
                if (!process.HasExited)
                {
                    if (started is null || process.StartTime.ToUniversalTime() != started.Value)
                        throw new InvalidDataException("Observation helper ownership became uncertain.");
                    // CLI tools have no interactive shutdown protocol. Try their window close before exact-child termination.
                    process.CloseMainWindow();
                    if (!process.HasExited)
                    {
                        if (process.StartTime.ToUniversalTime() != started.Value)
                            throw new InvalidDataException("Observation helper generation changed before termination.");
                        process.Kill();
                    }
                    using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                    await process.WaitForExitAsync(cleanup.Token);
                    if (!process.HasExited) throw new InvalidDataException("Owned observation helper did not exit.");
                }
            }
            finally
            {
                deadline.Cancel();
                process.StandardOutput.Close();
                process.StandardError.Close();
                // Always observe both bounded readers, including when wait/ownership/cleanup failed.
                foreach (var reader in new[] { output, error })
                    if (reader is not null)
                        try { await reader; }
                        catch (Exception failure) when (failure is IOException or InvalidDataException or OperationCanceledException or ObjectDisposedException)
                        { }
            }
        }
    }

    private static async Task<byte[]> ReadAsync(Stream stream, int maximum, CancellationToken cancellationToken)
    {
        if (maximum is < 1 or > 2097152) throw new InvalidDataException("Finite observation output bound required.");
        using var output = new MemoryStream();
        byte[] buffer = new byte[4096];
        while (true)
        {
            int count = await stream.ReadAsync(buffer, cancellationToken);
            if (count == 0) return output.ToArray();
            if (output.Length + count > maximum) throw new InvalidDataException("Observation output exceeded its bound.");
            await output.WriteAsync(buffer.AsMemory(0, count), cancellationToken);
        }
    }
}
