using System.IO;
using System.Text.Json;
using Recap.LocalWhisper;

internal static class LocalDiagnosticsTests
{
    internal static void Run(string root)
    {
        var folder = Path.Combine(root, "diagnostics-unit");
        var logger = new LocalWhisperDiagnostics(folder);
        var settings = new LocalWhisperSettings
        {
            Model = "PRIVATE_MODEL_PATH", Device = "PRIVATE_DEVICE", ComputeType = "PRIVATE_TOKEN",
            GpuIndex = 1, CpuThreads = 4, BatchSize = 2
        };
        logger.Record(LocalDiagnosticEvent.RecognitionRequested, settings);
        var phase = logger.PhaseTracker(123);
        for (var i = 0; i < 100; i++) phase(new("Recognizing speech", i));
        phase(new("SECRET_TRANSCRIPT", 0));
        phase(new("OTHER_SECRET_TRANSCRIPT", 1));
        phase(new("Aligning words", 10));
        var path = Path.Combine(folder, "runtime.jsonl");
        var lines = File.ReadAllLines(path);
        Check(lines.Length == 4, "Only sanitized phase transitions must be written");
        Check(!File.ReadAllText(path).Contains("PRIVATE") && !File.ReadAllText(path).Contains("SECRET"), "Reject sensitive settings/phase values");
        using var first = JsonDocument.Parse(lines[0]);
        Check(first.RootElement.GetProperty("gpuIndex").GetInt32() == 1, "Record selected GPU index");
        Check(first.RootElement.GetProperty("utc").GetDateTimeOffset().Offset == TimeSpan.Zero, "Timestamp must be UTC");
        new LocalWhisperDiagnostics(folder).Record(LocalDiagnosticEvent.ProcessStarting);
        Check(File.ReadAllLines(path).Length == 5, "New logger must append, not truncate prior run");

        var rotated = Path.Combine(root, "diagnostics-rotation");
        var small = new LocalWhisperDiagnostics(rotated, 1024);
        for (var i = 0; i < 40; i++) small.Record(LocalDiagnosticEvent.ProcessStarting);
        var files = Directory.GetFiles(rotated);
        Check(files.Length == 4 && files.All(file => new FileInfo(file).Length <= 1024), "Bound current plus three previous archives");
        foreach (var file in files)
            foreach (var line in File.ReadLines(file))
                using (JsonDocument.Parse(line)) { }
        var blocked = Path.Combine(root, "diagnostics-blocked");
        File.WriteAllText(blocked, "not a directory");
        new LocalWhisperDiagnostics(blocked).Record(LocalDiagnosticEvent.ProcessStarting);
        Console.WriteLine("PASS: durable diagnostics sanitization, UTC, phase deduplication, prior-run preservation, rotation and fail-open IO.");
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new Exception("Local diagnostics: " + message);
    }
}
