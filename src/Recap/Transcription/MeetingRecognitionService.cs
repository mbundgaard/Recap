using System.IO;
using NAudio.Wave;
using Recap.Recording;
using Recap.Settings;

namespace Recap.Transcription;

public sealed record MeetingProcessingProgress(string Message, double Percent, bool Indeterminate = false);

/// <summary>Prepares local audio and recognizes missing tracks without UI dependencies.</summary>
public sealed class MeetingRecognitionService(RecordingLibrary library, AppSettings settings,
    ITranscriptionProvider? transcriptionProvider = null)
{
    public static void ResetSpeechResults(RecordingLibrary library, RecordingListItem item, string language)
    {
        if (item.IsProcessing || item.IsManuallyQueued)
            throw new InvalidOperationException("Wait for current processing to finish before reprocessing.");
        if (language is not ("en-US" or "da-DK"))
            throw new ArgumentException("Choose English or Danish.", nameof(language));
        // Audio is retained; only language-dependent derived results are invalidated.
        foreach (var name in new[] { "raw-remote-transcript.json", "raw-microphone-transcript.json",
            "speaker-map.json", "merged-transcript.md", "final-transcript.md", "summary.md" })
            File.Delete(Path.Combine(item.Folder, name));
        item.Metadata.TranscriptionLanguage = language;
        item.Metadata.Files.FinalTranscript = null;
        library.SaveMetadata(item.Folder, item.Metadata);
    }

    public async Task ProcessAsync(RecordingListItem item, IProgress<MeetingProcessingProgress> progress,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        progress.Report(new("Preparing audio…", 0, true));
        await Task.Run(() => new RecordingPostProcessor(library, settings).Process(item), cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();

        var tracks = new[]
        {
            (Source: "remote", Audio: item.Metadata.Files.Remote, Speaker: "Others"),
            (Source: "microphone", Audio: item.Metadata.Files.Microphone,
                Speaker: string.IsNullOrWhiteSpace(settings.UserName) ? "You" : settings.UserName.Trim())
        };
        if (tracks.All(t => File.Exists(Path.Combine(item.Folder, $"raw-{t.Source}-transcript.json")))) return;
        if (!string.Equals(settings.SpeechProviderId, "azure-speech", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Select a supported speech provider in Settings.");

        var configured = settings.AzureSpeech;
        ITranscriptionProvider provider = transcriptionProvider ?? new AzureSpeechTranscriptionProvider(new AzureSpeechSettings
        {
            SubscriptionKey = configured.SubscriptionKey,
            Endpoint = configured.Endpoint,
            Region = configured.Region,
            ResourceName = configured.ResourceName,
            RecognitionLanguage = item.Metadata.TranscriptionLanguage ?? "en-US"
        });
        var fractions = new double[2];
        var gate = new object();
        void Report(int index, double fraction)
        {
            lock (gate)
            {
                fractions[index] = Math.Max(fractions[index], fraction);
                var percent = fractions.Average() * 100;
                progress.Report(new($"Recognizing speech · {percent:0}%", percent));
            }
        }

        // Both tracks run concurrently. Each successful result is saved independently,
        // so restarting or retrying never resubmits a completed track.
        var tasks = tracks.Select((track, index) => Task.Run(async () =>
        {
            var target = Path.Combine(item.Folder, $"raw-{track.Source}-transcript.json");
            if (File.Exists(target))
            {
                Report(index, 1);
                return;
            }
            var audioPath = Path.Combine(item.Folder, track.Audio);
            double duration;
            using (var reader = new WaveFileReader(audioPath)) duration = reader.TotalTime.TotalSeconds;
            var trackProgress = new InlineProgress<TimeSpan>(position =>
                Report(index, duration > 0 ? Math.Clamp(position.TotalSeconds / duration, 0, 0.99) : 0));
            var result = await provider.TranscribeAsync(
                new(audioPath, track.Source, track.Speaker, trackProgress), cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            var temporary = target + ".tmp";
            await File.WriteAllTextAsync(temporary, result.RawJson, cancellationToken);
            File.Move(temporary, target, overwrite: true);
            Report(index, 1);
        }, cancellationToken)).ToArray();
        await Task.WhenAll(tasks);
    }

    private sealed class InlineProgress<T>(Action<T> report) : IProgress<T>
    {
        public void Report(T value) => report(value);
    }
}
