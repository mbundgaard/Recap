using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using Recap.Agent;
using Recap.Recording;
using NAudio.Wave;
using Recap.Settings;
using Recap.Transcription;

internal static class GrokSpeechTests
{
    private const string Response = """{"text":"Hello there friend","language":"en","duration":3,"words":[{"text":"Hello","start":0,"end":1,"speaker":0},{"text":"there","start":1,"end":2,"speaker":0},{"text":"friend","start":2,"end":3,"speaker":1}]}""";

    internal static async Task RunAsync(string root)
    {
        var folder = Path.Combine(root, "grok-tests");
        Directory.CreateDirectory(folder);
        var audio = Path.Combine(folder, "private-meeting-title.wav");
        await File.WriteAllBytesAsync(audio, [82, 73, 70, 70, 0, 0, 0, 0]);
        var settings = new GrokSpeechSettings { ApiKey = "test-secret" };
        foreach (var track in new[] { "remote", "microphone" })
        {
            var phases = new List<string>();
            using var client = Client(async (message, token) =>
            {
                Check(message.Method == HttpMethod.Post && message.RequestUri!.AbsoluteUri == "https://api.x.ai/v1/stt", "Fixed endpoint");
                Check(message.Headers.Authorization?.Scheme == "Bearer" && message.Headers.Authorization.Parameter == "test-secret", "Per-request authorization");
                var parts = ((MultipartFormDataContent)message.Content!).ToArray();
                var names = parts.Select(p => p.Headers.ContentDisposition!.Name!.Trim('"')).ToArray();
                Check(names.SequenceEqual(track == "remote" ? new[] { "model", "language", "diarize", "file" } : new[] { "model", "language", "file" }), "Fields and file-last order");
                Check(await parts[0].ReadAsStringAsync(token) == GrokSpeechSettings.DefaultModel, "Fixed supported model");
                Check(await parts[1].ReadAsStringAsync(token) == (track == "remote" ? "en" : "da"), "Language mapping");
                if (track == "remote") Check(await parts[2].ReadAsStringAsync(token) == "true", "Remote diarization requested");
                Check(parts[^1].Headers.ContentType!.MediaType == "audio/wav", "Audio MIME");
                Check(!parts[^1].Headers.ContentDisposition!.ToString().Contains("private-meeting"), "No local title/path in filename");
                await message.Content!.CopyToAsync(Stream.Null, token);
                return Json(Response);
            });
            var provider = new GrokSpeechTranscriptionProvider(settings, track == "remote" ? "en-US" : "da-DK", client);
            var result = await provider.TranscribeAsync(new(audio, track, "Me", new ProgressTrap(), new Phases(phases)));
            Check(phases.SequenceEqual(new[] { "Uploading audio to Grok", "Waiting for Grok speech recognition" }), "Upload/wait phases without fake audio progress");
            Check(result.ProviderResponseJson == Response, "Original provider JSON preserved separately");
            Check(JsonSerializer.Deserialize<List<TranscriptSegment>>(result.RawJson)!.Count == result.Segments.Count, "Normalized raw transcript contract");
            Check(result.Segments.Count == (track == "remote" ? 2 : 1), "Contiguous speaker grouping");
            Check(result.Segments[0].Speaker == (track == "remote" ? "Speaker 1" : "Me"), "Speaker labels");
        }
        using (var missing = Client((_, _) => Task.FromResult(Json("""{"text":"Hi","language":"en","duration":1,"words":[{"text":"Hi","start":0,"end":1}]}"""))))
        {
            var result = await new GrokSpeechTranscriptionProvider(settings, "en-US", missing).TranscribeAsync(new(audio, "remote", "Others"));
            Check(result.Segments.Single().Speaker == "Others", "Missing remote speaker falls back safely");
        }
        var groupingCases = new[]
        {
            """{"text":"a b","duration":6,"words":[{"text":"a","start":0,"end":1,"speaker":0},{"text":"b","start":5,"end":6,"speaker":0}]}""",
            """{"text":"a b","duration":16,"words":[{"text":"a","start":0,"end":10,"speaker":0},{"text":"b","start":10,"end":16,"speaker":0}]}""",
            JsonSerializer.Serialize(new { text = "long", duration = 2, words = new[] {
                new { text = new string('a', 1100), start = 0, end = 1, speaker = 0 },
                new { text = new string('b', 1100), start = 1, end = 2, speaker = 0 } } })
        };
        foreach (var body in groupingCases)
        {
            using var client = Client((_, _) => Task.FromResult(Json(body)));
            var result = await new GrokSpeechTranscriptionProvider(settings, "en-US", client).TranscribeAsync(new(audio, "microphone", "Me"));
            Check(result.Segments.Count == 2 && result.Segments.All(segment => segment.Text.Length <= 2000),
                "Same speaker splits at silence, duration or character limit for chronological merging");
        }
        using (var rounded = Client((_, _) => Task.FromResult(Json("""{"text":"a","duration":1.23,"words":[{"text":"a","start":1,"end":1.234,"speaker":0}]}"""))))
        {
            var result = await new GrokSpeechTranscriptionProvider(settings, "en-US", rounded).TranscribeAsync(new(audio, "remote", "Others"));
            Check(result.Segments.Single().End == TimeSpan.FromSeconds(1.23), "Rounded duration tolerates and clamps finer word timestamp");
        }
        foreach (var bad in new[] { "SECRET_RESPONSE", "{}", Response.Replace("\"duration\":3", "\"duration\":\"SECRET\""),
                     Response.Replace("\"start\":0", "\"start\":-1"), Response.Replace("\"end\":1", "\"end\":99"),
                     Response.Replace("\"start\":0", "\"start\":1e999"), Response.Replace("\"speaker\":0", "\"speaker\":\"SECRET\"") })
        {
            using var client = Client((_, _) => Task.FromResult(Json(bad)));
            await Reject(() => new GrokSpeechTranscriptionProvider(settings, "en-US", client).TranscribeAsync(new(audio, "remote", "Others")));
        }
        foreach (var status in new[] { HttpStatusCode.InternalServerError, HttpStatusCode.Unauthorized, HttpStatusCode.TemporaryRedirect })
        {
            var calls = 0;
            using var client = Client((_, _) => { calls++; return Task.FromResult(new HttpResponseMessage(status) { Content = new StringContent("SECRET_RESPONSE") }); });
            await Reject(() => new GrokSpeechTranscriptionProvider(settings, "en-US", client).TranscribeAsync(new(audio, "remote", "Others")));
            Check(calls == 1, "Never automatically replay ambiguous/non-429 failures");
        }
        var retries = 0;
        using (var client = Client((_, _) =>
        {
            retries++;
            if (retries == 3) return Task.FromResult(Json(Response));
            var response = new HttpResponseMessage(HttpStatusCode.TooManyRequests);
            response.Headers.RetryAfter = new RetryConditionHeaderValue(TimeSpan.Zero);
            return Task.FromResult(response);
        }))
        {
            await new GrokSpeechTranscriptionProvider(settings, "en-US", client).TranscribeAsync(new(audio, "remote", "Others"));
            Check(retries == 3, "Retry definite 429 at most twice");
        }
        var limitedCalls = 0;
        using (var client = Client((_, _) =>
        {
            limitedCalls++;
            var response = new HttpResponseMessage(HttpStatusCode.TooManyRequests);
            response.Headers.RetryAfter = new RetryConditionHeaderValue(TimeSpan.Zero);
            return Task.FromResult(response);
        }))
        {
            await Reject(() => new GrokSpeechTranscriptionProvider(settings, "en-US", client).TranscribeAsync(new(audio, "remote", "Others")));
            Check(limitedCalls == 3, "Persistent rate limits must stop after two retries");
        }
        var oversized = Path.Combine(folder, "oversized.wav");
        try
        {
            using (var file = File.Create(oversized)) file.SetLength(500_000_001);
            using var client = Client((_, _) => throw new Exception("Oversized audio must never be uploaded"));
            await Reject(() => new GrokSpeechTranscriptionProvider(settings, "en-US", client).TranscribeAsync(new(oversized, "remote", "Others")));
        }
        finally { File.Delete(oversized); }
        var failedCalls = 0;
        using (var client = Client((_, _) => { failedCalls++; throw new HttpRequestException("SECRET_RESPONSE"); }))
        {
            await Reject(() => new GrokSpeechTranscriptionProvider(settings, "en-US", client).TranscribeAsync(new(audio, "remote", "Others")));
            Check(failedCalls == 1, "Never replay network failures");
        }
        using (var cancellation = new CancellationTokenSource())
        using (var client = Client(async (_, token) => { cancellation.Cancel(); await Task.Delay(Timeout.Infinite, token); return Json(Response); }))
        {
            try { await new GrokSpeechTranscriptionProvider(settings, "en-US", client).TranscribeAsync(new(audio, "remote", "Others"), cancellation.Token); throw new Exception("Expected cancellation"); }
            catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
        }
        using (var client = Client((_, _) =>
        {
            var response = Json(Response);
            response.Content.Headers.ContentLength = 33L * 1024 * 1024;
            return Task.FromResult(response);
        })) await Reject(() => new GrokSpeechTranscriptionProvider(settings, "en-US", client).TranscribeAsync(new(audio, "remote", "Others")));

        var app = AppSettingsStore.CreateDefaultSettings();
        using var patch = JsonDocument.Parse("""{"speechProviderId":"grok-speech","grokSpeech":{"apiKey":"test-secret","model":"grok-voice-transcribe-2.0"}}""");
        var changed = AgentSettings.Apply(app, patch.RootElement);
        var redacted = AgentSettings.Redacted(changed)["grokSpeech"]!.AsObject();
        Check(!redacted.ContainsKey("apiKey") && redacted["hasCredential"]!.GetValue<bool>() && !redacted.ToJsonString().Contains("test-secret"), "API key redaction");
        Check(changed.GrokSpeech.ApiKey == "test-secret" && app.GrokSpeech.ApiKey == "", "API patch copies settings");
        using var preserve = JsonDocument.Parse("""{"grokSpeech":{"model":"grok-voice-transcribe-2.0"}}""");
        Check(AgentSettings.Apply(changed, preserve.RootElement).GrokSpeech.ApiKey == "test-secret", "Patch without key must preserve credential");
        using (var client = Client((_, _) => throw new Exception("Invalid key must never reach HTTP")))
            await Reject(() => new GrokSpeechTranscriptionProvider(new GrokSpeechSettings { ApiKey = "SECRET\r\nINJECT" }, "en-US", client)
                .TranscribeAsync(new(audio, "remote", "Others")));
        await ServiceIntegrationAsync(Path.Combine(folder, "meetings"));
        Console.WriteLine("PASS: mocked Grok multipart/labels/raw preservation, validation, safe errors, retries/cancellation and API credential redaction.");
    }

    private static async Task ServiceIntegrationAsync(string root)
    {
        var settings = AppSettingsStore.CreateDefaultSettings();
        settings.RecordingsFolder = root;
        settings.SpeechProviderId = "grok-speech";
        settings.GrokSpeech.ApiKey = "fake-key";
        settings.RecordingProcessing.PreserveRawFiles = true;
        var library = new RecordingLibrary(root);
        var folder = library.CreateSessionFolder(DateTime.Now);
        var metadata = new RecordingMetadata { Status = "completed", TranscriptionLanguage = "en-US" };
        library.SaveMetadata(folder, metadata);
        foreach (var name in new[] { "remote.wav", "microphone.wav", "mixed.wav" })
        {
            using var writer = new WaveFileWriter(Path.Combine(folder, name), new WaveFormat(16000, 16, 1));
            writer.Write(new byte[96000], 0, 96000);
        }
        var remoteCalls = 0;
        var microphoneCalls = 0;
        using var client = Client(async (message, token) =>
        {
            var remote = ((MultipartFormDataContent)message.Content!).Any(part => part.Headers.ContentDisposition!.Name!.Trim('"') == "diarize");
            await message.Content!.CopyToAsync(Stream.Null, token);
            if (remote) remoteCalls++;
            else if (++microphoneCalls == 1) return new HttpResponseMessage(HttpStatusCode.InternalServerError);
            return Json(Response);
        });
        var item = new RecordingListItem(folder, metadata);
        var service = new MeetingRecognitionService(library, settings,
            new GrokSpeechTranscriptionProvider(settings.GrokSpeech, "en-US", client));
        try { await service.ProcessAsync(item, new MeetingProgress(), CancellationToken.None); throw new Exception("Expected partial Grok failure"); }
        catch (InvalidOperationException) { }
        Check(remoteCalls == 1 && microphoneCalls == 1, "First failure must not replay billed track");
        Check(File.ReadAllText(Path.Combine(folder, "provider-remote-response.json")) == Response, "Service persists exact response");
        Check(!File.Exists(Path.Combine(folder, "raw-microphone-transcript.json")), "Failed track must not publish transcript");
        await service.ProcessAsync(item, new MeetingProgress(), CancellationToken.None);
        Check(remoteCalls == 1 && microphoneCalls == 2 && item.HasFinalTranscript, "Retry reuses remote track and exports Markdown locally");
        Check(File.ReadAllText(Path.Combine(folder, "final-transcript.md")).Contains("Hello there"), "Local export reads normalized Grok words");
        MeetingRecognitionService.ResetSpeechResults(library, item, "en-US");
        Check(!Directory.EnumerateFiles(folder, "provider-*-response.json").Any(), "Reset removes original provider responses");
    }
    private sealed class MeetingProgress : IProgress<MeetingProcessingProgress> { public void Report(MeetingProcessingProgress value) { } }

    private sealed class Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> handle) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => handle(request, cancellationToken);
    }
    private static HttpClient Client(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> handle) => new(new Handler(handle));
    private static HttpResponseMessage Json(string json) => new(HttpStatusCode.OK) { Content = new StringContent(json) };
    private sealed class Phases(List<string> phases) : IProgress<string> { public void Report(string value) => phases.Add(value); }
    private sealed class ProgressTrap : IProgress<TimeSpan> { public void Report(TimeSpan value) => throw new Exception("Fake audio progress is forbidden"); }
    private static async Task Reject(Func<Task<TrackTranscriptResult>> action)
    {
        try { await action(); } catch (InvalidOperationException ex)
        {
            Check(!ex.ToString().Contains("SECRET") && !ex.ToString().Contains("test-secret"), "Safe errors must not retain response or credentials");
            return;
        }
        throw new Exception("Expected safe Grok rejection");
    }
    private static void Check(bool value, string message) { if (!value) throw new Exception("Grok: " + message); }
}
