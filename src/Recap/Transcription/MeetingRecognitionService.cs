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
    public static bool RequiresProviderReset(RecordingListItem item, AppSettings settings) =>
        new[] { "remote", "microphone" }.Any(track => File.Exists(Path.Combine(item.Folder, $"raw-{track}-transcript.json")))
        && ((item.Metadata.TranscriptionProviderId ?? "azure-speech") != settings.SpeechProviderId
            || settings.SpeechProviderId == "local-whisper" && item.Metadata.TranscriptionModel != settings.LocalWhisper.ForLanguage(item.Metadata.TranscriptionLanguage ?? "en-US").Model
            || settings.SpeechProviderId == "grok-speech" && item.Metadata.TranscriptionModel != settings.GrokSpeech.Model);

    public static void ResetSpeechResults(RecordingLibrary library, RecordingListItem item, string language)
    {
        if (item.IsProcessing || item.IsManuallyQueued)
            throw new InvalidOperationException("Wait for current processing to finish before reprocessing.");
        if (language is not ("en-US" or "da-DK"))
            throw new ArgumentException("Choose English or Danish.", nameof(language));
        // Audio is retained; only language-dependent derived results are invalidated.
        foreach (var name in new[] { "raw-remote-transcript.json", "raw-microphone-transcript.json",
            "speaker-map.json", "merged-transcript.md", "final-transcript.md", "summary.md",
            "provider-remote-response.json", "provider-microphone-response.json" })
            File.Delete(Path.Combine(item.Folder, name));
        foreach (var track in new[] { "remote", "microphone" })
            foreach (var stage in new[] { "asr", "aligned", "diarization", "result" })
            {
                File.Delete(Path.Combine(item.Folder, $"local-{track}-{stage}.json"));
                File.Delete(Path.Combine(item.Folder, $"local-{track}-{stage}.json.tmp"));
            }
        item.Metadata.TranscriptionProviderId = null;
        item.Metadata.TranscriptionModel = null;
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
        if (tracks.All(t => File.Exists(Path.Combine(item.Folder, $"raw-{t.Source}-transcript.json"))))
        {
            if (item.Metadata.TranscriptionProviderId is "local-whisper" or "grok-speech" && !item.HasFinalTranscript)
                LocalTranscriptExporter.Export(library, item);
            return;
        }
        var local = settings.SpeechProviderId == "local-whisper";
        await using var localBatch = local ? Recap.LocalWhisper.LocalWhisperRuntime.BeginProcessingBatch() : null;
        var grok = settings.SpeechProviderId == "grok-speech";
        if (!local && !grok && settings.SpeechProviderId != "azure-speech")
            throw new InvalidOperationException("Select a supported speech provider in Settings.");
        if (RequiresProviderReset(item, settings))
            throw new InvalidOperationException("Partial speech results use a different provider or model. Reprocess this meeting to switch without mixing results.");

        var language = item.Metadata.TranscriptionLanguage ?? "en-US";
        var configured = settings.AzureSpeech;
        ITranscriptionProvider provider = transcriptionProvider ?? (local
            ? new LocalWhisperTranscriptionProvider(settings.LocalWhisper, language)
            : grok ? new GrokSpeechTranscriptionProvider(settings.GrokSpeech, language)
            : new AzureSpeechTranscriptionProvider(new AzureSpeechSettings
            {
                SubscriptionKey = configured.SubscriptionKey,
                Endpoint = configured.Endpoint,
                Region = configured.Region,
                ResourceName = configured.ResourceName,
                RecognitionLanguage = language
            }));
        item.Metadata.TranscriptionProviderId = settings.SpeechProviderId;
        item.Metadata.TranscriptionModel = local ? settings.LocalWhisper.ForLanguage(language).Model : grok ? settings.GrokSpeech.Model : null;
        library.SaveMetadata(item.Folder, item.Metadata);
        var fractions = new double[2];
        var phases = new string?[2];
        var gate = new object();
        void Report(int index, double fraction)
        {
            lock (gate)
            {
                fractions[index] = Math.Max(fractions[index], fraction);
                var percent = fractions.Average() * 100;
                var message = local || grok ? phases[index] ?? (local ? "Local speech processing" : "Grok speech processing") : "Recognizing speech";
                var indeterminate = (local || grok) && fractions[index] == 0;
                progress.Report(new(indeterminate ? message : $"{message} · {percent:0}%", percent, indeterminate));
            }
        }

        // Azure tracks run concurrently; local tracks run sequentially to bound memory.
        // Each successful result is saved independently for partial retries.
        Task ProcessTrack(int index) => Task.Run(async () =>
        {
            var track = tracks[index];
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
            var stageProgress = new InlineProgress<string>(phase =>
            {
                lock (gate)
                {
                    phases[index] = phase;
                    Report(index, fractions[index]);
                }
            });
            var result = await provider.TranscribeAsync(
                new(audioPath, track.Source, track.Speaker, trackProgress, stageProgress), cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            if (result.ProviderResponseJson is not null)
            {
                cancellationToken.ThrowIfCancellationRequested();
                Recap.Storage.AtomicFile.WriteText(Path.Combine(item.Folder, $"provider-{track.Source}-response.json"), result.ProviderResponseJson);
            }
            var temporary = target + ".tmp";
            await File.WriteAllTextAsync(temporary, result.RawJson, cancellationToken);
            File.Move(temporary, target, overwrite: true);
            Report(index, 1);
        }, cancellationToken);
        if (local || grok)
            for (var index = 0; index < tracks.Length; index++) await ProcessTrack(index);
        else
            await Task.WhenAll(Enumerable.Range(0, tracks.Length).Select(ProcessTrack));
        cancellationToken.ThrowIfCancellationRequested();
        if (local || grok) LocalTranscriptExporter.Export(library, item);
    }

    private sealed class InlineProgress<T>(Action<T> report) : IProgress<T>
    {
        public void Report(T value) => report(value);
    }
}
