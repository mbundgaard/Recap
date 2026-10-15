using System.IO;
using System.Text.Json;
using NAudio.Wave;
using Recap.LocalWhisper;

namespace Recap.Transcription;

public sealed class LocalWhisperTranscriptionProvider(LocalWhisperSettings settings, string language) : ITranscriptionProvider
{
    private readonly LocalWhisperSettings _settings = settings.ForLanguage(language);
    public string Id => "local-whisper";
    public string DisplayName => "Local Whisper";

    public async Task<TrackTranscriptResult> TranscribeAsync(TrackTranscriptionRequest request, CancellationToken cancellationToken = default)
    {
        _settings.Validate();
        if (request.SourceTrack is not ("remote" or "microphone")) throw new ArgumentException("Unknown recording track.");
        var code = language switch { "en-US" => "en", "da-DK" => "da", _ => throw new ArgumentException("Unsupported recognition language.") };
        var folder = Path.GetDirectoryName(Path.GetFullPath(request.AudioFilePath))!;
        var prefix = Path.Combine(folder, "local-" + request.SourceTrack);
        var resultPath = prefix + "-result.json";
        double duration;
        using (var reader = new WaveFileReader(request.AudioFilePath)) duration = reader.TotalTime.TotalSeconds;
        var progress = new LocalTrackProgress(update =>
        {
            request.PhaseProgress?.Report(update.Message);
            // Download/model checks are not recognized audio progress.
            if (update.Message is "Recognizing speech" or "Aligning words" or "Identifying speakers")
                request.Progress?.Report(TimeSpan.FromSeconds(duration * Math.Min(update.Percent, 99) / 100));
        });
        await LocalWhisperRuntime.TranscribeAsync(_settings, request.AudioFilePath, request.SourceTrack, code,
            prefix, resultPath, progress, cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        using var result = JsonDocument.Parse(await File.ReadAllTextAsync(resultPath, cancellationToken));
        var segments = ParseSegments(result.RootElement, request, duration);
        return new(request.SourceTrack, request.SpeakerLabel, segments, JsonSerializer.Serialize(segments));
    }

    public static IReadOnlyList<TranscriptSegment> ParseSegments(JsonElement result, TrackTranscriptionRequest request, double duration)
    {
        var segments = new List<TranscriptSegment>();
        foreach (var entry in result.GetProperty("segments").EnumerateArray())
        {
            var start = entry.GetProperty("start").GetDouble();
            var end = entry.GetProperty("end").GetDouble();
            if (!double.IsFinite(start) || !double.IsFinite(end) || start < 0 || end < start || end > duration + 1)
                throw new InvalidDataException("Local recognition returned invalid timestamps.");
            var text = entry.GetProperty("text").GetString() ?? "";
            if (string.IsNullOrWhiteSpace(text)) continue;
            var speaker = request.SourceTrack == "microphone" ? request.SpeakerLabel : entry.GetProperty("speaker").GetString();
            segments.Add(new(TimeSpan.FromSeconds(Math.Min(start, duration)), TimeSpan.FromSeconds(Math.Min(end, duration)),
                string.IsNullOrWhiteSpace(speaker) ? "Others" : speaker, text.Trim(), request.SourceTrack));
        }
        return segments.OrderBy(x => x.Start).ToArray();
    }
    private sealed class LocalTrackProgress(Action<LocalProgress> report) : IProgress<LocalProgress>
    {
        public void Report(LocalProgress value) => report(value);
    }
}
