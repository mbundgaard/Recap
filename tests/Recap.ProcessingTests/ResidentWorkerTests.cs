using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Text;
using System.Text.Json;
using Recap.LocalWhisper;

internal static class ResidentWorkerTests
{
    // Child mode must run before any WPF, settings, recording or test initialization.
    public static async Task RunFixtureAsync()
    {
        Console.InputEncoding = Encoding.UTF8;
        Console.OutputEncoding = new UTF8Encoding(false);
        var loaded = false;
        async Task Emit(object message)
        {
            await Console.Out.WriteLineAsync("RECAP|" + JsonSerializer.Serialize(message));
            await Console.Out.FlushAsync();
        }
        await Emit(new { kind = "ready", pid = Environment.ProcessId });
        while (await Console.In.ReadLineAsync() is { } line)
        {
            using var request = JsonDocument.Parse(line);
            var mode = request.RootElement.GetProperty("mode").GetString();
            // More than pipe capacity: a missing stderr drain would deadlock these tests.
            await Console.Error.WriteAsync(new string('x', 100_000));
            await Console.Error.FlushAsync();
            if (mode == "error")
            {
                await Emit(new { kind = "error", code = "SECRET_FIXTURE_CONTENT" });
                continue;
            }
            if (mode == "invalid")
            {
                await Console.Out.WriteLineAsync("RECAP|{SECRET_FIXTURE_CONTENT");
                await Console.Out.FlushAsync();
                continue;
            }
            if (mode == "wait")
            {
                await Emit(new { kind = "progress", phase = "Recognizing speech", percent = 1 });
                await Task.Delay(Timeout.InfiniteTimeSpan);
            }
            if (!loaded)
            {
                await Emit(new { kind = "loaded", role = "asr" });
                await Emit(new { kind = "loaded", role = "align" });
                await Emit(new { kind = "loaded", role = "diarization" });
                loaded = true;
            }
            await Emit(new { kind = "progress", phase = "SECRET_FIXTURE_CONTENT", percent = 150 });
            await Emit(new { kind = "done", ok = true });
        }
    }

    private static ResidentWhisperWorker Start()
    {
        var executable = Environment.ProcessPath ?? throw new Exception("Missing test executable path");
        var start = new ProcessStartInfo(executable)
        {
            UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
            StandardInputEncoding = new UTF8Encoding(false), StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8
        };
        if (Path.GetFileNameWithoutExtension(executable).Equals("dotnet", StringComparison.OrdinalIgnoreCase))
            start.ArgumentList.Add(Assembly.GetExecutingAssembly().Location);
        start.ArgumentList.Add("--resident-fixture");
        return new ResidentWhisperWorker(start);
    }

    public static async Task RunAsync()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var worker = Start();
        using var process = Process.GetProcessById(worker.ProcessId);
        try
        {
            LocalProgress? progress = null;
            await worker.ExecuteAsync("{\"mode\":\"ok\"}", p => progress = p, timeout.Token);
            await worker.ExecuteAsync("{\"mode\":\"ok\"}", null, timeout.Token);
            Check(!process.HasExited && worker.ProcessId == process.Id, "Two requests must reuse the live PID");
            Check(worker.AsrLoadCount == 1 && worker.AlignmentLoadCount == 1 && worker.DiarizationLoadCount == 1,
                "Load diagnostics must count actual load events, not requests");
            Check(progress is { Message: "Local processing", Percent: 100 }, "Progress must whitelist phases and clamp percentage");
        }
        finally { await worker.DisposeAsync(); }
        Check(process.HasExited, "Idle disposal must stop the process");
        await worker.DisposeAsync();

        foreach (var mode in new[] { "error", "invalid" })
        {
            await using var broken = Start();
            using var child = Process.GetProcessById(broken.ProcessId);
            try
            {
                await broken.ExecuteAsync(JsonSerializer.Serialize(new { mode }), null, timeout.Token);
                throw new Exception("Expected protocol failure");
            }
            catch (InvalidOperationException ex)
            {
                Check(!ex.ToString().Contains("SECRET_FIXTURE_CONTENT"), "Protocol failures must never disclose worker payloads");
            }
            Check(child.HasExited, "Protocol failure must terminate worker before returning");
        }

        await using var cancellable = Start();
        using var cancellationChild = Process.GetProcessById(cancellable.ProcessId);
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(timeout.Token);
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var pending = cancellable.ExecuteAsync("{\"mode\":\"wait\"}", _ => started.TrySetResult(), cancellation.Token);
        await started.Task.WaitAsync(timeout.Token);
        try
        {
            await cancellable.ExecuteAsync("{\"mode\":\"ok\"}", null, timeout.Token);
            throw new Exception("Expected overlapping request rejection");
        }
        catch (InvalidOperationException) { }
        cancellation.Cancel();
        try { await pending; throw new Exception("Expected request cancellation"); }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
        Check(cancellationChild.HasExited, "Cancellation must await worker exit");
        Console.WriteLine("PASS: resident transport reuse/load counters, stderr drainage, sanitized failures, overlap rejection, cancellation and disposal.");
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new Exception("Resident worker: " + message);
    }
}
