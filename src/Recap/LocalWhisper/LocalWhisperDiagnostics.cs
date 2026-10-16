using System.IO;
using System.Text.Json;

namespace Recap.LocalWhisper;

internal enum LocalDiagnosticEvent
{
    SetupRequested, SetupResolved, SetupCompleted, SetupCancelled, SetupFailed,
    RecognitionRequested, RecognitionResolved, RecognitionCompleted, RecognitionCancelled, RecognitionFailed,
    ProcessStarting, ProcessStarted, ProcessReady, ProcessExited, ProcessFailed,
    RequestStarted, RequestCompleted, RequestCancelled, RequestFailed,
    UnloadStarted, UnloadCompleted, Terminating, ModelLoaded, WorkerError, Phase
}

/// <summary>Small allowlisted crash breadcrumbs; never accepts requests, paths, exceptions or audio/text.</summary>
internal sealed class LocalWhisperDiagnostics
{
    internal static LocalWhisperDiagnostics Shared { get; } = new(Path.Combine(LocalWhisperSettings.Root, "diagnostics"));
    private readonly string _folder;
    private readonly long _maxBytes;
    private readonly object _gate = new();
    private readonly string _run = Guid.NewGuid().ToString("N");
    private const int Archives = 3;

    internal LocalWhisperDiagnostics(string folder, long maxBytes = 1024 * 1024)
    {
        _folder = folder;
        _maxBytes = Math.Max(1024, maxBytes);
    }

    internal void Record(LocalDiagnosticEvent kind, LocalWhisperSettings? settings = null,
        int? childPid = null, int? exitCode = null, string? phase = null)
    {
        try
        {
            // All string values are selected from fixed vocabularies, never copied from errors/protocol payloads.
            var entry = new
            {
                utc = DateTimeOffset.UtcNow, run = _run, pid = Environment.ProcessId,
                kind = Enum.IsDefined(kind) ? kind.ToString() : "Unknown",
                childPid, exitCode,
                phase = SafePhase(phase),
                model = settings is null ? null : settings.Model is "tiny" or "base" or "small" or "medium" or "large-v3" ? settings.Model : "invalid",
                device = settings is null ? null : settings.Device is "auto" or "cpu" or "cuda" ? settings.Device : "invalid",
                precision = settings is null ? null : settings.ComputeType is "auto" or "int8" or "float32" or "float16" or "int8_float16" ? settings.ComputeType : "invalid",
                gpuIndex = settings is null ? (int?)null : Math.Clamp(settings.GpuIndex, -1, 16),
                threads = settings is null ? (int?)null : Math.Clamp(settings.CpuThreads, 0, 65),
                batch = settings is null ? (int?)null : Math.Clamp(settings.BatchSize, 0, 33)
            };
            var bytes = System.Text.Encoding.UTF8.GetBytes(JsonSerializer.Serialize(entry) + "\n");
            lock (_gate)
            {
                Directory.CreateDirectory(_folder);
                var path = Path.Combine(_folder, "runtime.jsonl");
                if (File.Exists(path) && new FileInfo(path).Length + bytes.Length > _maxBytes)
                {
                    File.Delete(path + "." + Archives);
                    for (var i = Archives - 1; i >= 1; i--)
                        if (File.Exists(path + "." + i)) File.Move(path + "." + i, path + "." + (i + 1), true);
                    File.Move(path, path + ".1", true);
                }
                using var stream = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.Read);
                stream.Write(bytes);
                stream.Flush(flushToDisk: true);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            // Diagnostics must never prevent recognition or reveal filesystem/error details.
        }
    }

    internal static string? SafePhase(string? phase) => phase switch
    {
        "Recognizing speech" or "Aligning words" or "Identifying speakers" or "Checking transcription model"
        or "Checking en alignment model" or "Checking da alignment model" or "Checking speaker diarization model"
        or "Waiting for the local processor" or "Downloading the isolated Python runtime and dependencies"
        or "Preparing speaker model files from GitHub" or "Verifying the downloaded models with networking disabled"
        or "asr" or "align" or "diarization"
        or "cuda_unavailable" or "compute_unsupported" or "out_of_memory" or "access_denied" or "runtime_failed" => phase,
        null => null,
        _ => "Local processing"
    };

    internal Action<LocalProgress> PhaseTracker(int? childPid = null)
    {
        string? previous = null;
        return value =>
        {
            var phase = SafePhase(value.Message);
            if (phase == previous) return;
            previous = phase;
            Record(LocalDiagnosticEvent.Phase, childPid: childPid, phase: phase);
        };
    }
}
