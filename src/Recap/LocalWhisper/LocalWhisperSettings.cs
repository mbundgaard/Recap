using System.IO;
using System.Text.Json.Serialization;

namespace Recap.LocalWhisper;

public sealed class LocalWhisperSettings
{
    public string EnglishModel { get; set; } = "base";
    public string DanishModel { get; set; } = "medium";

    // Resolved per operation, never a second persisted model selection.
    [JsonIgnore]
    public string Model { get; set; } = "base";

    public LocalWhisperSettings ForLanguage(string language)
    {
        Validate();
        var copy = Copy();
        copy.Model = language switch
        {
            "en-US" => EnglishModel,
            "da-DK" => DanishModel,
            _ => throw new ArgumentException("Only English (en-US) and Danish (da-DK) are supported.", nameof(language))
        };
        return copy;
    }

    public object WorkerSettings() => new
    {
        Model, Device, ComputeType, BatchSize, CpuThreads, GpuIndex, Diarization, SpeakerCount
    };
    public string Device { get; set; } = "auto";
    public string ComputeType { get; set; } = "auto";
    public int BatchSize { get; set; } = 1;
    public int CpuThreads { get; set; } = Math.Clamp(Environment.ProcessorCount / 2, 1, 16);
    public int GpuIndex { get; set; }
    [JsonIgnore]
    public bool Diarization => true;
    public int SpeakerCount { get; set; }

    public LocalWhisperSettings Copy() => (LocalWhisperSettings)MemberwiseClone();
    public void Validate()
    {
        if (new[] { Model, EnglishModel, DanishModel }.Any(model => !Models.Any(x => x.Id == model)))
            throw new ArgumentException("Choose a supported multilingual Whisper model for English and Danish.");
        if (Device is not ("auto" or "cpu" or "cuda")) throw new ArgumentException("Device must be auto, cpu, or cuda.");
        if (ComputeType is not ("auto" or "int8" or "float32" or "float16" or "int8_float16")) throw new ArgumentException("Unsupported compute type.");
        if (Device == "cpu" && ComputeType is "float16" or "int8_float16") throw new ArgumentException("CPU processing needs int8, float32, or auto precision.");
        if (BatchSize is < 1 or > 32 || CpuThreads is < 1 or > 64 || GpuIndex is < 0 or > 15 || SpeakerCount is < 0 or > 30)
            throw new ArgumentException("Batch size: 1-32; threads: 1-64; GPU index: 0-15; speaker count: 0-30 (0 estimates).");
    }

    public static string Root => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Recap", "LocalWhisper");
    public static IReadOnlyList<WhisperModelOption> Models { get; } =
    [
        new("tiny", "Tiny - fastest (~75 MB)", 1.5, 2),
        new("base", "Base - lightweight (~150 MB)", 2, 3),
        new("small", "Small - balanced (~500 MB)", 3, 4),
        new("medium", "Medium - more accurate (~1.5 GB)", 5, 6),
        new("large-v3", "Large v3 - accuracy first (~3 GB)", 8, 10)
    ];
}

public sealed record WhisperModelOption(string Id, string Name, double SuggestedGpuGb, double SuggestedCpuGb);
