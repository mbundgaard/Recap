using System.IO;
using System.Text.Json;
using NAudio.Wave;
using Recap.Agent;
using Recap.LocalWhisper;
using Recap.Recording;
using Recap.Settings;
using Recap.Transcription;

internal static class LocalWhisperTests
{
    public static async Task RunAsync(string root)
    {
        SettingsContract();
        SegmentParsing();
        await ProcessingAsync(Path.Combine(root, "local-whisper-tests"));
        Console.WriteLine("PASS: Local Whisper language models, settings/API round trip, segment parsing, partial retry/reset and offline export.");
    }

    private static void SettingsContract()
    {
        var settings = new LocalWhisperSettings();
        Check(settings.EnglishModel == "base" && settings.DanishModel == "medium", "Language model defaults");
        settings.Validate();
        Check(settings.Diarization, "Local speaker diarization must always be enabled");
        using var disableDiarization = JsonDocument.Parse("""{"localWhisper":{"diarization":false}}""");
        Throws<AgentException>(() => AgentSettings.Apply(AppSettingsStore.CreateDefaultSettings(), disableDiarization.RootElement),
            "API must not expose an option to disable diarization");
        var legacy = JsonSerializer.Deserialize<LocalWhisperSettings>("""{"Diarization":false}""")!;
        Check(legacy.Diarization, "Legacy persisted false must not disable speaker diarization");
        settings.EnglishModel = "tiny";
        settings.DanishModel = "large-v3";
        var english = settings.ForLanguage("en-US");
        var danish = settings.ForLanguage("da-DK");
        Check(english.Model == "tiny" && danish.Model == "large-v3", "Resolve each language independently");
        Check(!ReferenceEquals(english, settings) && !ReferenceEquals(danish, settings), "Language resolution must return copies");
        english.BatchSize = 7;
        Check(settings.BatchSize == 1, "Resolved configuration must not mutate saved settings");
        Throws<ArgumentException>(() => settings.ForLanguage("fr-FR"), "Reject unsupported language");
        foreach (var field in new[] { "EnglishModel", "DanishModel" })
        {
            var invalid = settings.Copy();
            typeof(LocalWhisperSettings).GetProperty(field)!.SetValue(invalid, "base.en");
            Throws<ArgumentException>(invalid.Validate, "Validate " + field);
        }
        var workerOptions = new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
        foreach (var resolved in new[] { english, danish })
        {
            using var worker = JsonDocument.Parse(JsonSerializer.Serialize(resolved.WorkerSettings(), workerOptions));
            var payload = worker.RootElement;
            Check(payload.GetProperty("model").GetString() == resolved.Model, "Worker DTO must include resolved runtime model");
            Check(!payload.TryGetProperty("englishModel", out _) && !payload.TryGetProperty("danishModel", out _),
                "Worker DTO must contain only the operation model");
            Check(payload.GetProperty("batchSize").GetInt32() == resolved.BatchSize
                && payload.GetProperty("device").GetString() == resolved.Device
                && payload.GetProperty("computeType").GetString() == resolved.ComputeType
                && payload.GetProperty("cpuThreads").GetInt32() == resolved.CpuThreads
                && payload.GetProperty("gpuIndex").GetInt32() == resolved.GpuIndex
                && payload.GetProperty("diarization").GetBoolean() == resolved.Diarization
                && payload.GetProperty("speakerCount").GetInt32() == resolved.SpeakerCount,
                "Worker DTO must preserve processing options with Python-compatible property names");
        }
        var json = JsonSerializer.Serialize(danish);
        using var document = JsonDocument.Parse(json);
        Check(!document.RootElement.TryGetProperty("Model", out _), "Runtime model must not be persisted");
        Check(!document.RootElement.TryGetProperty("Diarization", out _), "Always-on runtime diarization must not be persisted");
        var restored = JsonSerializer.Deserialize<LocalWhisperSettings>(json)!;
        Check(restored.EnglishModel == "tiny" && restored.DanishModel == "large-v3", "Both settings must round trip");
        var app = AppSettingsStore.CreateDefaultSettings();
        using var patch = JsonDocument.Parse("""{"localWhisper":{"englishModel":"small","danishModel":"large-v3"}}""");
        var changed = AgentSettings.Apply(app, patch.RootElement);
        Check(changed.LocalWhisper.EnglishModel == "small" && changed.LocalWhisper.DanishModel == "large-v3", "API patch must accept both models");
        Check(app.LocalWhisper.EnglishModel == "base", "API patch must not mutate original settings");
        var api = AgentSettings.Redacted(changed)["localWhisper"]!.AsObject();
        Check(api["englishModel"]!.GetValue<string>() == "small" && api["danishModel"]!.GetValue<string>() == "large-v3"
            && !api.ContainsKey("model") && !api.ContainsKey("diarization"), "API must expose language models, not runtime model or diarization toggle");
        foreach (var field in new[] { "englishModel", "danishModel", "model" })
        {
            using var invalid = JsonDocument.Parse("{\"localWhisper\":{\"" + field + "\":\"invalid\"}}");
            Throws<AgentException>(() => AgentSettings.Apply(app, invalid.RootElement), "API must reject invalid " + field);
        }
    }

    private static void SegmentParsing()
    {
        using var data = JsonDocument.Parse("""{"segments":[{"start":2,"end":3.5,"text":" later ","speaker":"Speaker 2"},{"start":0,"end":1,"text":" first ","speaker":""},{"start":1,"end":2,"text":"  ","speaker":"Speaker 1"}]}""");
        var request = new TrackTranscriptionRequest("unused.wav", "remote", "Others");
        var remote = LocalWhisperTranscriptionProvider.ParseSegments(data.RootElement, request, 3);
        Check(remote.Count == 2 && remote[0].Text == "first" && remote[0].Speaker == "Others", "Sort, trim, skip blanks and fall back to Others");
        Check(remote[1].Speaker == "Speaker 2" && remote[1].End == TimeSpan.FromSeconds(3), "Preserve remote speaker and clamp tolerated end overrun");
        var microphone = LocalWhisperTranscriptionProvider.ParseSegments(data.RootElement,
            new("unused.wav", "microphone", "Test User"), 3);
        Check(microphone.All(x => x.Speaker == "Test User" && x.SourceTrack == "microphone"), "Microphone must retain user label");
        foreach (var times in new[] { "\"start\":-1,\"end\":1", "\"start\":2,\"end\":1", "\"start\":0,\"end\":5" })
        {
            using var invalid = JsonDocument.Parse("{\"segments\":[{" + times + ",\"text\":\"bad\",\"speaker\":\"Others\"}]}");
            Throws<InvalidDataException>(() => LocalWhisperTranscriptionProvider.ParseSegments(invalid.RootElement, request, 3), "Reject invalid timestamps");
        }
        using var overrun = JsonDocument.Parse("""{"segments":[{"start":3.2,"end":3.5,"text":"tail","speaker":"Others"}]}""");
        var clamped = LocalWhisperTranscriptionProvider.ParseSegments(overrun.RootElement, request, 3).Single();
        Check(clamped.Start == TimeSpan.FromSeconds(3) && clamped.End == clamped.Start, "Tolerated timestamp overrun must never invert a segment");
        var formatted = LocalTranscriptExporter.Format("Title *", remote);
        Check(formatted.Contains("# Title \\*") && formatted.IndexOf("first", StringComparison.Ordinal) < formatted.IndexOf("later", StringComparison.Ordinal), "Export escapes Markdown and preserves chronological words");
        Check(LocalTranscriptExporter.Format("Empty", []).Contains("No speech was recognized."), "Empty speech exports explicitly");
    }

    private static async Task ProcessingAsync(string root)
    {
        foreach (var language in new[] { "en-US", "da-DK" })
        {
            var settings = AppSettingsStore.CreateDefaultSettings();
            settings.SpeechProviderId = "local-whisper";
            settings.LocalWhisper.EnglishModel = "tiny";
            settings.LocalWhisper.DanishModel = "small";
            settings.RecordingProcessing.PreserveRawFiles = true;
            settings.RecordingsFolder = Path.Combine(root, language);
            var library = new RecordingLibrary(settings.RecordingsFolder);
            var folder = library.CreateSessionFolder(DateTime.Now);
            var metadata = new RecordingMetadata { Status = "completed", TranscriptionLanguage = language };
            library.SaveMetadata(folder, metadata);
            foreach (var name in new[] { "remote.wav", "microphone.wav", "mixed.wav" })
            {
                using var writer = new WaveFileWriter(Path.Combine(folder, name), new WaveFormat(16000, 16, 1));
                writer.Write(new byte[32000], 0, 32000);
            }
            var item = new RecordingListItem(folder, metadata);
            var provider = new SyntheticProvider();
            var service = new MeetingRecognitionService(library, settings, provider);
            var progress = new SilentProgress();
            try { await service.ProcessAsync(item, progress, CancellationToken.None); throw new Exception("Expected synthetic track failure"); }
            catch (InvalidOperationException ex) when (ex.Message == "Synthetic failure") { }
            Check(metadata.TranscriptionModel == (language == "en-US" ? "tiny" : "small"), "Processing must save meeting-language model");
            Check(File.Exists(Path.Combine(folder, "raw-remote-transcript.json")), "Successful local track must survive partial failure");
            Check(!MeetingRecognitionService.RequiresProviderReset(item, settings), "Same language model must allow retry");
            var changed = JsonSerializer.Deserialize<AppSettings>(JsonSerializer.Serialize(settings))!;
            if (language == "en-US") changed.LocalWhisper.DanishModel = "medium";
            else changed.LocalWhisper.EnglishModel = "medium";
            Check(!MeetingRecognitionService.RequiresProviderReset(item, changed), "Other language model must not invalidate partial results");
            if (language == "en-US") changed.LocalWhisper.EnglishModel = "base";
            else changed.LocalWhisper.DanishModel = "base";
            Check(MeetingRecognitionService.RequiresProviderReset(item, changed), "Changed meeting model requires reset");
            try { await new MeetingRecognitionService(library, changed, provider).ProcessAsync(item, progress, CancellationToken.None); throw new Exception("Expected mixed model rejection"); }
            catch (InvalidOperationException) { }
            Check(provider.MicrophoneCalls == 1, "Mismatched partial results must not submit missing track");
            changed.SpeechProviderId = "azure-speech";
            Check(MeetingRecognitionService.RequiresProviderReset(item, changed), "Changed provider requires reset");
            provider.FailMicrophone = false;
            await service.ProcessAsync(item, progress, CancellationToken.None);
            Check(provider.RemoteCalls == 1 && provider.MicrophoneCalls == 2, "Retry must reuse successful local track");
            Check(item.HasFinalTranscript && File.ReadAllText(Path.Combine(folder, "final-transcript.md")).Contains("Synthetic remote"), "Local completion must export without LLM");
            File.Delete(Path.Combine(folder, "final-transcript.md"));
            await service.ProcessAsync(item, progress, CancellationToken.None);
            Check(item.HasFinalTranscript && provider.RemoteCalls == 1 && provider.MicrophoneCalls == 2, "Completed tracks regenerate missing export without recognition");
            foreach (var track in new[] { "remote", "microphone" })
                foreach (var stage in new[] { "asr", "aligned", "diarization", "result" })
                    foreach (var suffix in new[] { ".json", ".json.tmp" })
                        File.WriteAllText(Path.Combine(folder, $"local-{track}-{stage}{suffix}"), "{}");
            MeetingRecognitionService.ResetSpeechResults(library, item, language);
            Check(!Directory.EnumerateFiles(folder, "local-*").Any() && !File.Exists(Path.Combine(folder, "raw-remote-transcript.json")), "Reset removes local checkpoints and raw results");
            Check(metadata.TranscriptionModel is null && metadata.TranscriptionProviderId is null && File.Exists(Path.Combine(folder, "remote.wav")), "Reset clears provenance and retains audio");
        }
    }

    private sealed class SyntheticProvider : ITranscriptionProvider
    {
        public string Id => "local-whisper";
        public string DisplayName => "Synthetic local provider";
        public bool FailMicrophone = true;
        public int RemoteCalls;
        public int MicrophoneCalls;
        public Task<TrackTranscriptResult> TranscribeAsync(TrackTranscriptionRequest request, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (request.SourceTrack == "remote") Interlocked.Increment(ref RemoteCalls);
            else
            {
                Interlocked.Increment(ref MicrophoneCalls);
                if (FailMicrophone) throw new InvalidOperationException("Synthetic failure");
            }
            TranscriptSegment[] segments = [new(TimeSpan.Zero, TimeSpan.FromSeconds(1), request.SpeakerLabel, "Synthetic " + request.SourceTrack, request.SourceTrack)];
            return Task.FromResult(new TrackTranscriptResult(request.SourceTrack, request.SpeakerLabel, segments, JsonSerializer.Serialize(segments)));
        }
    }
    private sealed class SilentProgress : IProgress<MeetingProcessingProgress> { public void Report(MeetingProcessingProgress value) { } }
    private static void Check(bool condition, string message) { if (!condition) throw new Exception("Local Whisper: " + message); }
    private static void Throws<T>(Action action, string message) where T : Exception
    {
        try { action(); } catch (T) { return; }
        throw new Exception("Local Whisper: " + message);
    }
}
