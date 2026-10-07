using System.Diagnostics;
using System.Runtime.InteropServices;
using FinancialHttpCounters;

internal static class AdmissionNativeControls
{
    internal static async Task RunAsync()
    {
        if (!OperatingSystem.IsLinux()) throw new InvalidDataException("Real Linux admission witness required");
        NativeDiagnostics.Mark(NativeControl.AdmissionRootCreate);
        string root = Path.Combine(Path.GetTempPath(), "financial-counter-admission-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        async Task Reject(Func<CancellationToken, Task> action)
        {
            using var finite = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            var watch = Stopwatch.StartNew(); bool rejected = false;
            try { await action(finite.Token).WaitAsync(TimeSpan.FromSeconds(3)); }
            catch (InvalidDataException) { rejected = true; }
            CounterState.Require(rejected && watch.Elapsed < TimeSpan.FromSeconds(2), "Nonregular/replaced admission did not promptly reject");
        }
        try
        {
            string fifo = Path.Combine(root, "fifo");
            NativeDiagnostics.Mark(NativeControl.AdmissionFifoCreate);
            CounterState.Require(mkfifo(fifo, 0x180) == 0, "Owned FIFO control creation failed");
            NativeDiagnostics.Mark(NativeControl.AdmissionFifoReject);
            await Reject(async t => { _ = await ProcessAdmission.BoundedFileAsync(fifo, 4096, t, privateOwner: true); });
            NativeDiagnostics.Mark(NativeControl.AdmissionDeviceReject);
            await Reject(async t => { _ = await ProcessAdmission.BoundedFileAsync("/dev/zero", 4096, t); });
            NativeDiagnostics.Mark(NativeControl.AdmissionDirectoryReject);
            await Reject(async t => { _ = await ProcessAdmission.BoundedFileAsync(root, 4096, t); });
            NativeDiagnostics.Mark(NativeControl.AdmissionPrivateFileCreate);
            string original = Path.Combine(root, "original"), moved = Path.Combine(root, "moved");
            await File.WriteAllTextAsync(original, "owned synthetic input"); File.SetUnixFileMode(original, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            NativeDiagnostics.Mark(NativeControl.AdmissionSymlinkReplacement);
            await Reject(async t =>
            {
                _ = await ProcessAdmission.BoundedFileAsync(original, 4096, t, privateOwner: true,
                    afterOpen: () => { File.Move(original, moved); File.CreateSymbolicLink(original, fifo); });
            });
            NativeDiagnostics.Mark(NativeControl.AdmissionSymlinkRecovery);
            File.Delete(original); File.Move(moved, original);
            NativeDiagnostics.Mark(NativeControl.AdmissionSameBytesReplacement);
            await Reject(async t =>
            {
                _ = await ProcessAdmission.BoundedFileAsync(original, 4096, t, afterOpen: () =>
                    { File.Move(original, moved); File.WriteAllText(original, "owned synthetic input"); });
            });
            NativeDiagnostics.Mark(NativeControl.AdmissionReplacementRecovery);
            File.Delete(original); File.Move(moved, original); File.SetUnixFileMode(original, UnixFileMode.UserRead | UnixFileMode.GroupRead);
            NativeDiagnostics.Mark(NativeControl.AdmissionModeReject);
            await Reject(async t => { _ = await ProcessAdmission.BoundedFileAsync(original, 4096, t, privateOwner: true); });
            NativeDiagnostics.Mark(NativeControl.AdmissionExpiredReject);
            using (var cancelled = new CancellationTokenSource())
            {
                cancelled.Cancel(); bool observed = false;
                try { _ = await ProcessAdmission.BoundedFileAsync(fifo, 4096, cancelled.Token); }
                catch (OperationCanceledException) { observed = true; }
                CounterState.Require(observed, "Expired admission must reject before open");
            }
            NativeDiagnostics.Mark(NativeControl.AdmissionOutputPrepare);
            string destination = Path.Combine(root, "destination"); Directory.CreateDirectory(destination);
            string alias = Path.Combine(root, "alias"); Directory.CreateSymbolicLink(alias, destination);
            NativeDiagnostics.Mark(NativeControl.AdmissionRedirectedOutput);
            bool refused = false;
            try { using var output = DescriptorBoundOutput.Admit(Path.Combine(alias, "refused.json"), CancellationToken.None); }
            catch (InvalidDataException) { refused = true; }
            CounterState.Require(refused && !File.Exists(Path.Combine(destination, "refused.json")), "Refused output wrote through redirected parent");
            NativeDiagnostics.Mark(NativeControl.AdmissionReplacedOutput);
            using (var output = DescriptorBoundOutput.Admit(Path.Combine(destination, "fresh.json"), CancellationToken.None))
            {
                Directory.Move(destination, destination + "-held"); Directory.CreateDirectory(destination);
                await Reject(async _ => await output.WriteAsync("{}"u8.ToArray()));
                CounterState.Require(!File.Exists(Path.Combine(destination, "fresh.json")) && !File.Exists(Path.Combine(destination + "-held", "fresh.json")), "Replaced parent received refused output");
            }
            NativeDiagnostics.Mark(NativeControl.AdmissionPositiveOutput);
            using (var output = DescriptorBoundOutput.Admit(Path.Combine(root, "positive.json"), CancellationToken.None))
                await output.WriteAsync("{}"u8.ToArray());
            CounterState.Require(await File.ReadAllTextAsync(Path.Combine(root, "positive.json")) == "{}", "Positive descriptor output failed");
        }
        finally
        {
            NativeDiagnostics.Mark(NativeControl.AdmissionCleanup);
            Directory.Delete(root, recursive: true);
        }
    }
    [DllImport("libc", SetLastError = true)] private static extern int mkfifo(string path, uint mode);
}
