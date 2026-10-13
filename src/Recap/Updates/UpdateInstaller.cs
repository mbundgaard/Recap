using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using System.Text.Json;

namespace Recap.Updates;

public sealed record UpdateInstallRequest(string Target, int ParentPid, long ParentStartedUtcTicks, string OriginalSha256);

public static class UpdateInstaller
{
    public static string UpdatesFolder => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Recap", "Updates");

    public static async Task LaunchHelperAsync(string directory, CancellationToken token)
    {
        if (!UpdateTrust.SupportsInstallation) throw new InvalidOperationException("Install a published Windows x64 single-file release to use in-app updates.");
        var requestPath = Path.Combine(directory, "request.json");
        ValidateRequestPath(requestPath);
        var target = Environment.ProcessPath ?? throw new InvalidOperationException("Cannot locate Recap.exe.");
        // Check write permission before exiting. Never request elevation.
        var probe = Path.Combine(Path.GetDirectoryName(target)!, $".recap-write-test-{Guid.NewGuid():N}");
        using (File.Create(probe)) { }
        File.Delete(probe);
        var request = new UpdateInstallRequest(target, Environment.ProcessId,
            Process.GetCurrentProcess().StartTime.ToUniversalTime().Ticks, await HashAsync(target));
        await File.WriteAllTextAsync(requestPath, JsonSerializer.Serialize(request), token);
        var helper = Path.Combine(directory, "updater.exe");
        File.Copy(target, helper, false);
        var start = new ProcessStartInfo(helper) { UseShellExecute = false, WorkingDirectory = directory };
        start.ArgumentList.Add("--apply-update");
        start.ArgumentList.Add(requestPath);
        using var process = Process.Start(start) ?? throw new IOException("Could not start the update helper.");
        try
        {
            var timeout = Stopwatch.StartNew();
            while (!File.Exists(Path.Combine(directory, "helper-ready")))
            {
                token.ThrowIfCancellationRequested();
                if (process.HasExited || timeout.Elapsed > TimeSpan.FromSeconds(20))
                    throw new IOException("The update helper did not start. Recap has not been changed.");
                await Task.Delay(100, token);
            }
            token.ThrowIfCancellationRequested();
        }
        catch
        {
            await File.WriteAllTextAsync(Path.Combine(directory, "cancel"), "cancel");
            throw;
        }
    }

    public static async Task<int> ApplyAsync(string requestPath)
    {
        string? directory = null;
        try
        {
            directory = ValidateRequestPath(requestPath);
            if (!string.Equals(Environment.ProcessPath, Path.Combine(directory, "updater.exe"), StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("The updater must run from its staging directory.");
            var request = ReadRequest(requestPath);
            var manifest = UpdateTrust.Verify(await File.ReadAllBytesAsync(Path.Combine(directory, "update-manifest.json")),
                await File.ReadAllBytesAsync(Path.Combine(directory, "update-manifest.sig")), UpdateTrust.PublicKey);
            if (UpdateTrust.ParseVersion(manifest.Version) <= UpdateTrust.CurrentVersion)
                throw new InvalidDataException("Update version must be newer than the installed version.");
            var package = Path.Combine(directory, UpdateTrust.ExecutableName);
            await UpdateTrust.VerifyFileAsync(package, manifest);
            using var parent = Process.GetProcessById(request.ParentPid);
            if (!string.Equals(parent.MainModule?.FileName, request.Target, StringComparison.OrdinalIgnoreCase)
                || parent.StartTime.ToUniversalTime().Ticks != request.ParentStartedUtcTicks)
                throw new InvalidDataException("The update request does not match the running application.");
            await File.WriteAllTextAsync(Path.Combine(directory, "helper-ready"), "ready");
            using var wait = new CancellationTokenSource(TimeSpan.FromMinutes(2));
            await parent.WaitForExitAsync(wait.Token);
            if (File.Exists(Path.Combine(directory, "cancel"))) return 1;
            if (!string.Equals(await HashAsync(request.Target), request.OriginalSha256, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("The installed executable changed while waiting for shutdown.");

            // Copy onto the destination volume first; File.Replace is then an atomic replacement.
            var replacement = request.Target + ".update-" + Path.GetFileName(directory);
            var backup = request.Target + ".previous";
            try
            {
                File.Copy(package, replacement, false);
                await UpdateTrust.VerifyFileAsync(replacement, manifest);
                await ReplaceWithRollbackAsync(replacement, request.Target, backup, async () =>
                {
                    var start = new ProcessStartInfo(request.Target) { UseShellExecute = false, WorkingDirectory = Path.GetDirectoryName(request.Target)! };
                    start.ArgumentList.Add("--update-complete");
                    start.ArgumentList.Add(requestPath);
                    using var updated = Process.Start(start) ?? throw new IOException("Could not launch the new version.");
                    var confirmed = false;
                    try
                    {
                        var timer = Stopwatch.StartNew();
                        while (timer.Elapsed < TimeSpan.FromSeconds(45))
                        {
                            var ready = Path.Combine(directory, "app-ready");
                            string? readyPid = null;
                            try { if (File.Exists(ready)) readyPid = await File.ReadAllTextAsync(ready); }
                            catch (IOException) { /* The new process may still be writing its acknowledgement. */ }
                            if (readyPid == updated.Id.ToString()) { confirmed = true; return; }
                            if (updated.HasExited) throw new IOException("The new version exited before showing its window.");
                            await Task.Delay(200);
                        }
                        throw new IOException("The new version did not finish starting; restoring the previous version.");
                    }
                    finally
                    {
                        // Release the executable before rollback. Only stop the new process we launched.
                        if (!confirmed && !updated.HasExited)
                        {
                            updated.Kill();
                            await updated.WaitForExitAsync();
                        }
                    }
                });
                try { File.Delete(package); } catch { }
                return 0;
            }
            catch
            {
                // ReplaceWithRollbackAsync restores the previous executable when startup fails.
                if (File.Exists(request.Target))
                    Process.Start(new ProcessStartInfo(request.Target) { UseShellExecute = true, WorkingDirectory = Path.GetDirectoryName(request.Target)! });
                throw;
            }
            finally
            {
                try { File.Delete(replacement); } catch { }
            }
        }
        catch (Exception ex)
        {
            if (directory is not null)
                try { await File.WriteAllTextAsync(Path.Combine(directory, "update-error.txt"), ex.ToString()); } catch { }
            return 1;
        }
    }

    // The callback succeeds only after the replacement version has rendered its main window.
    public static async Task ReplaceWithRollbackAsync(string replacement, string target, string backup, Func<Task> launchAndConfirm)
    {
        File.Replace(replacement, target, backup, ignoreMetadataErrors: false);
        try { await launchAndConfirm(); }
        catch
        {
            File.Replace(backup, target, null, ignoreMetadataErrors: false);
            throw;
        }
    }

    public static void MarkStartupSuccessful(string requestPath)
    {
        var directory = ValidateRequestPath(requestPath);
        var request = ReadRequest(requestPath);
        if (!string.Equals(Environment.ProcessPath, request.Target, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Update startup acknowledgement does not match this executable.");
        File.WriteAllText(Path.Combine(directory, "app-ready"), Environment.ProcessId.ToString());
    }

    public static void CleanupOldStaging()
    {
        if (!Directory.Exists(UpdatesFolder)) return;
        foreach (var directory in Directory.EnumerateDirectories(UpdatesFolder))
        {
            try
            {
                if (!Guid.TryParseExact(Path.GetFileName(directory), "N", out _)
                    || (File.GetAttributes(directory) & FileAttributes.ReparsePoint) != 0
                    || Directory.GetLastWriteTimeUtc(directory) > DateTime.UtcNow.AddDays(-7)) continue;
                // Only remove updater-owned files, never arbitrary data or nested directories.
                foreach (var name in new[] { "request.json", "update-manifest.json", "update-manifest.sig",
                    "updater.exe", UpdateTrust.ExecutableName, "helper-ready", "app-ready", "cancel", "update-error.txt" })
                    File.Delete(Path.Combine(directory, name));
                Directory.Delete(directory, false);
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    private static UpdateInstallRequest ReadRequest(string path)
    {
        if (new FileInfo(path).Length > 16384) throw new InvalidDataException("Invalid update request.");
        var request = JsonSerializer.Deserialize<UpdateInstallRequest>(File.ReadAllText(path))
            ?? throw new InvalidDataException("Missing update request.");
        if (!Path.IsPathFullyQualified(request.Target) || !request.Target.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
            || (File.GetAttributes(request.Target) & FileAttributes.ReparsePoint) != 0)
            throw new InvalidDataException("Invalid update destination.");
        return request;
    }

    private static string ValidateRequestPath(string path)
    {
        var fullPath = Path.GetFullPath(path);
        var directory = Path.GetDirectoryName(fullPath)!;
        if (Path.GetFileName(fullPath) != "request.json"
            || !Guid.TryParseExact(Path.GetFileName(directory), "N", out _)
            || !string.Equals(Path.GetDirectoryName(directory), Path.GetFullPath(UpdatesFolder), StringComparison.OrdinalIgnoreCase)
            || (File.GetAttributes(directory) & FileAttributes.ReparsePoint) != 0)
            throw new InvalidDataException("Invalid update staging directory.");
        return directory;
    }

    private static async Task<string> HashAsync(string path)
    {
        await using var stream = File.OpenRead(path);
        return Convert.ToHexString(await SHA256.HashDataAsync(stream));
    }
}
