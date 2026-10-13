using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Windows.Threading;
using NAudio.Wave;
using Recap.Agent;
using Recap.Recording;
using Recap.Settings;

internal static class AgentTests
{
    private static void Check(bool condition, string message)
    {
        if (!condition) throw new Exception(message);
    }

    public static async Task RunAsync(string root)
    {
        await CheckTimeline(root);
        var settings = AppSettingsStore.CreateDefaultSettings();
        settings.AzureSpeech.SubscriptionKey = "fake-speech-test-key";
        settings.OpenAiCompatible.ApiKey = "fake-llm-test-key";
        var redacted = AgentSettings.Redacted(settings).ToJsonString();
        Check(!redacted.Contains("fake-") && !redacted.Contains("subscriptionKey") && !redacted.Contains("apiKey"), "API settings must never return key fields.");
        var patched = AgentSettings.Apply(settings, JsonSerializer.SerializeToElement(new { userName = "Test", azureSpeech = new { region = "test-region" } }));
        Check(patched.AzureSpeech.SubscriptionKey == settings.AzureSpeech.SubscriptionKey
            && patched.OpenAiCompatible.ApiKey == settings.OpenAiCompatible.ApiKey && settings.UserName != patched.UserName,
            "Partial settings updates must preserve credentials and not mutate the live object before saving.");
        try { AgentSettings.Apply(settings, JsonSerializer.SerializeToElement(new { unknown = true })); throw new Exception("Accepted an unknown setting"); }
        catch (AgentException ex) when (ex.Status == 400) { }

        var commands = new List<AgentCommand>();
        var file = Path.Combine(root, "api-range-test.bin");
        await File.WriteAllBytesAsync(file, Enumerable.Range(0, 100).Select(x => (byte)x).ToArray());
        await using var server = new AgentServer(command =>
        {
            commands.Add(command);
            return Task.FromResult<object?>(command.Name == "meeting.file" ? new AgentFile(file, "application/octet-stream") : new { ok = true });
        });
        const string endpointVariable = "Kestrel__Endpoints__Unexpected__Url";
        var oldEndpoint = Environment.GetEnvironmentVariable(endpointVariable);
        Environment.SetEnvironmentVariable(endpointVariable, "http://0.0.0.0:0");
        try { await server.StartAsync(); }
        finally { Environment.SetEnvironmentVariable(endpointVariable, oldEndpoint); }
        var url = server.BaseUrl!;
        Check(new Uri(url).Host == "127.0.0.1", "Agent API must bind only to IPv4 loopback.");
        // The runtime token stays in memory, not test output or fixture files.
        var token = server.CreateClipboardPrompt().Split("Authorization: Bearer ")[1].Split('\n')[0];
        using var client = new HttpClient { BaseAddress = new Uri(url), Timeout = TimeSpan.FromSeconds(10) };
        using (var response = await client.GetAsync("/state")) Check(response.StatusCode == HttpStatusCode.Unauthorized, "Unauthenticated state must be rejected.");
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "wrong");
        using (var response = await client.GetAsync("/skill")) Check(response.StatusCode == HttpStatusCode.Unauthorized, "Skill endpoint must also authenticate.");
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        using (var response = await client.GetAsync("/skill"))
        {
            var skill = await response.Content.ReadAsStringAsync();
            Check(response.IsSuccessStatusCode && skill.Contains("/recording/pause") && !skill.Contains(token), "Authenticated skill must document controls without exposing its token.");
            Check(response.Headers.CacheControl?.NoStore == true, "Private responses must not be cached.");
        }
        using (var response = await client.GetAsync("/state")) Check(response.IsSuccessStatusCode, "Authenticated agent request failed.");
        foreach (var kind in new[] { "origin", "host", "query" })
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, kind == "query" ? "/state?token=bad" : "/state");
            if (kind == "origin") request.Headers.Add("Origin", "https://example.invalid");
            if (kind == "host") request.Headers.Host = "example.invalid";
            using var response = await client.SendAsync(request);
            Check(response.StatusCode == HttpStatusCode.Forbidden, "Browser origins, rebinding hosts and query credentials must be rejected.");
        }
        using (var response = await client.PostAsync("/recording/start", new StringContent("{", Encoding.UTF8, "application/json")))
            Check(response.StatusCode == HttpStatusCode.BadRequest, "Malformed JSON must be rejected.");
        using (var response = await client.PostAsync("/recording/start", new StringContent("[]", Encoding.UTF8, "application/json")))
            Check(response.StatusCode == HttpStatusCode.BadRequest, "Non-object JSON must be rejected.");
        using (var response = await client.PostAsync("/recording/start", new StringContent("{}")))
            Check(response.StatusCode == HttpStatusCode.UnsupportedMediaType, "Bodies must have the JSON content type.");
        using (var response = await client.PostAsync("/recording/start", new StringContent("{\"title\":\"" + new string('x', 1100000) + "\"}", Encoding.UTF8, "application/json")))
            Check(response.StatusCode == HttpStatusCode.RequestEntityTooLarge, "Oversized bodies must be rejected.");
        using (var request = new HttpRequestMessage(HttpMethod.Get, "/meetings/test/files/remote"))
        {
            request.Headers.Range = new RangeHeaderValue(4, 7);
            using var response = await client.SendAsync(request);
            Check(response.StatusCode == HttpStatusCode.PartialContent && (await response.Content.ReadAsByteArrayAsync()).SequenceEqual(new byte[] { 4, 5, 6, 7 }), "Audio download ranges must work.");
        }
        Check(commands.Count == 2 && commands.Last().Body.GetProperty("file").GetString() == "remote", "Rejected requests must never reach application controls.");
        await server.DisposeAsync();
        await using (var replacement = new AgentServer(_ => Task.FromResult<object?>(null)))
        {
            await replacement.StartAsync();
            Check(!replacement.CreateClipboardPrompt().Contains(token), "Every server instance must issue a fresh token.");
        }
        Console.WriteLine("PASS: recording pause/resume timeline, synchronized tracks, API authentication, host/origin defenses, input limits, private settings and file ranges.");
    }

    private static async Task CheckTimeline(string root)
    {
        var remotePath = Path.Combine(root, "timeline-remote.wav");
        var micPath = Path.Combine(root, "timeline-mic.wav");
        using var timeline = new RecordingTimeline(new WaveFileWriter(remotePath, WaveFormat.CreateIeeeFloatWaveFormat(8000, 1)),
            new WaveFileWriter(micPath, WaveFormat.CreateIeeeFloatWaveFormat(16000, 1)));
        static byte[] Samples(float value)
        {
            var samples = Enumerable.Repeat(value, 80).ToArray();
            var bytes = new byte[samples.Length * sizeof(float)];
            Buffer.BlockCopy(samples, 0, bytes, 0, bytes.Length);
            return bytes;
        }
        var before = Samples(0.25f); var paused = Samples(0.75f); var after = Samples(0.5f);
        await Task.Delay(35);
        timeline.Write(0, before, before.Length); timeline.Write(1, before, before.Length);
        timeline.Pause(); var frozen = timeline.Elapsed;
        await Task.Delay(50);
        timeline.Write(0, paused, paused.Length); timeline.Write(1, paused, paused.Length);
        Check(timeline.Elapsed == frozen && timeline.IsPaused, "Paused active time must freeze and audio must be discarded.");
        timeline.Resume(); await Task.Delay(35);
        timeline.Write(0, after, after.Length); timeline.Write(1, after, after.Length);
        timeline.Pause(); timeline.Stop();
        var duration = timeline.Elapsed;
        timeline.Resume(); await Task.Delay(20);
        Check(timeline.Elapsed == duration, "Stopping while paused must prevent resuming the clock.");
        timeline.Dispose();
        using var remote = new AudioFileReader(remotePath);
        using var mic = new AudioFileReader(micPath);
        Check(Math.Abs(remote.TotalTime.TotalSeconds - mic.TotalTime.TotalSeconds) < 0.001, "Both tracks must share the same active timeline.");
        var values = new float[8192];
        var count = remote.Read(values, 0, values.Length);
        Check(values.Take(count).Contains(0.25f) && values.Take(count).Contains(0.5f) && !values.Take(count).Contains(0.75f), "Audio before/after pause must survive, with no paused audio.");
        var metadata = new RecordingMetadata { StartedLocal = DateTime.Now.AddMinutes(-10), EndedLocal = DateTime.Now, RecordedDuration = duration };
        Check(metadata.Duration == duration, "Meeting duration must exclude pauses, not use wall-clock end minus start.");
    }

    public static void CheckController(Recap.MainWindow window, AppSettings settings)
    {
        var method = typeof(Recap.MainWindow).GetMethod("ExecuteAgentCommandAsync", BindingFlags.Instance | BindingFlags.NonPublic)!;
        object? Run(string name, string? id = null, object? body = null)
        {
            var command = new AgentCommand(name, id, body is null ? default : JsonSerializer.SerializeToElement(body));
            var task = (Task<object?>)method.Invoke(window, [command])!;
            var deadline = DateTime.UtcNow.AddSeconds(5);
            while (!task.IsCompleted && DateTime.UtcNow < deadline)
            {
                var frame = new DispatcherFrame();
                _ = window.Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() => frame.Continue = false));
                Dispatcher.PushFrame(frame);
            }
            if (!task.IsCompleted) throw new Exception("Agent controller timed out.");
            return task.GetAwaiter().GetResult();
        }
        void Reject(int status, string name, string? id = null, object? body = null)
        {
            try { Run(name, id, body); throw new Exception("Agent controller accepted an unsafe operation: " + name); }
            catch (AgentException ex) when (ex.Status == status) { }
        }
        Check(Run("state") is not null, "Agent state must be available.");
        Reject(400, "recording.start", body: new { language = "bad" });
        Reject(409, "recording.pause");
        Reject(400, "ui", body: new { surprise = true });
        var row = window.Recordings.Single(); var id = Path.GetFileName(row.Folder);
        Reject(404, "meeting", "..");
        Reject(400, "meeting.delete", id);
        Check(Directory.Exists(row.Folder), "Unconfirmed deletion must not touch the recording.");
        row.UpdateProcessing("busy", true);
        Reject(409, "meeting.delete", id, new { confirm = true });
        row.UpdateProcessing(null, false);
        Run("meeting.rename", id, new { title = "Agent test meeting" });
        Check(new RecordingLibrary(settings.RecordingsFolder).LoadRecordings().Single().Title == "Agent test meeting", "API rename must persist through the same library as the UI.");
        row = window.Recordings.Single();
        Run("speakers", id);
        Run("speakers.save", id, new { names = new Dictionary<string, string> { ["Speaker 1"] = "Taylor" } });
        Check(File.ReadAllText(Path.Combine(row.Folder, "speaker-map.json")).Contains("Taylor"), "API speaker names must be saved in the shared format.");
        var remote = row.Metadata.Files.Remote;
        row.Metadata.Files.Remote = "../settings.json";
        Reject(403, "meeting.file", id, new { file = "remote" });
        row.Metadata.Files.Remote = remote;
        Run("meeting.close", id);
        Run("meeting.delete", id, new { confirm = true });
        Check(!Directory.Exists(row.Folder), "Confirmed API deletion must remove only the selected meeting.");

        // Exercise the real session/command/UI pause path with WAV writers, not audio devices.
        var library = new RecordingLibrary(settings.RecordingsFolder);
        var recordingFolder = library.CreateSessionFolder(DateTime.Now.AddSeconds(1));
        var metadata = new RecordingMetadata
        {
            Id = Path.GetFileName(recordingFolder), StartedLocal = DateTime.Now, StartedUtc = DateTime.UtcNow,
            Files = new RecordingFiles { RawRemote = "remote.raw.wav", RawMicrophone = "microphone.raw.wav" }
        };
        library.SaveMetadata(recordingFolder, metadata);
        using var session = new RecordingSession(library);
        var timeline = new RecordingTimeline(new WaveFileWriter(Path.Combine(recordingFolder, "remote.raw.wav"), new WaveFormat(8000, 16, 1)),
            new WaveFileWriter(Path.Combine(recordingFolder, "microphone.raw.wav"), new WaveFormat(8000, 16, 1)));
        var flags = BindingFlags.NonPublic | BindingFlags.Instance;
        typeof(RecordingSession).GetField("_timeline", flags)!.SetValue(session, timeline);
        typeof(RecordingSession).GetField("_metadata", flags)!.SetValue(session, metadata);
        typeof(RecordingSession).GetField("_folder", flags)!.SetValue(session, recordingFolder);
        typeof(Recap.MainWindow).GetField("_session", flags)!.SetValue(window, session);
        typeof(Recap.MainWindow).GetField("_recordingStarted", flags)!.SetValue(window, DateTime.Now);
        var pause = (System.Windows.Controls.Button)window.FindName("PauseRecordingButton");
        pause.Visibility = System.Windows.Visibility.Visible;
        Run("recording.pause");
        var elapsed = session.Elapsed;
        Thread.Sleep(20);
        Check(session.IsPaused && session.Elapsed == elapsed && pause.Content.ToString() == "Resume",
            "API pause must stop the shared timeline and update the UI.");
        Run("recording.resume");
        Check(!session.IsPaused && pause.Content.ToString() == "Pause", "API resume must continue the same session and update the UI.");
        pause.RaiseEvent(new System.Windows.RoutedEventArgs(System.Windows.Controls.Button.ClickEvent));
        Check(session.IsPaused, "The UI pause button must use the same session as the API.");
        Run("recording.stop");
        Check(metadata.Status == "completed" && metadata.RecordedDuration is not null
            && pause.Visibility == System.Windows.Visibility.Collapsed, "Stopping a paused recording must finish its files, duration and UI state.");
    }
}
