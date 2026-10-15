using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Threading;
using Recap.Agent;
using Recap.LocalWhisper;
using Recap.Transcription;
using Recap.Logging;
using Recap.Recording;
using Recap.Updates;

namespace Recap;

public partial class MainWindow
{
    private AgentServer? _agentServer;
    private bool _agentClosing;
    private readonly HashSet<string> _agentMutationMeetings = new(StringComparer.OrdinalIgnoreCase);
    private readonly SemaphoreSlim _agentCommands = new(1);
    private readonly Dictionary<string, RecordingDetailWindow> _meetingWindows = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<AgentJob> _agentJobs = [];
    private readonly DispatcherTimer _agentCopyFeedback = new() { Interval = TimeSpan.FromSeconds(2) };

    private async Task StartAgentServerAsync()
    {
        if (_agentServer is not null || _agentClosing) return;
        var server = new AgentServer(ExecuteAgentCommandAsync);
        _agentServer = server;
        try
        {
            await server.StartAsync();
            if (_agentClosing) { await server.DisposeAsync(); return; }
            AgentButton.IsEnabled = true;
            AgentButton.ToolTip = "Copy private agent instructions (URL and access token)";
            _agentCopyFeedback.Tick += (_, _) => { AgentButton.Content = "AI"; _agentCopyFeedback.Stop(); };
        }
        catch (Exception ex)
        {
            AppLogger.Error(ex, "Could not start the local agent API.");
            AgentButton.ToolTip = "Agent API could not start. Restart Recap to retry.";
            await server.DisposeAsync();
        }
    }

    private void AgentButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            if (_agentServer?.BaseUrl is null) return;
            Clipboard.SetText(_agentServer.CreateClipboardPrompt());
            AgentButton.Content = "✓";
            _agentCopyFeedback.Stop();
            _agentCopyFeedback.Start();
        }
        catch { MessageBox.Show(this, "Could not copy the agent instructions. Try again.", "Clipboard busy"); }
    }

    private void PauseRecordingButton_Click(object sender, RoutedEventArgs e)
    {
        try { SetRecordingPaused(_session?.IsPaused != true); }
        catch (Exception ex) { MessageBox.Show(this, ex.Message, "Could not change recording state", MessageBoxButton.OK, MessageBoxImage.Error); }
    }

    private void SetRecordingPaused(bool paused)
    {
        if (_session is null) throw new AgentException(409, "No recording is active.");
        if (paused) _session.Pause(); else _session.Resume();
        PauseRecordingButton.Content = paused ? "Resume" : "Pause";
        StatusText.Text = paused ? "Paused" : "● Recording";
        StatusText.Foreground = paused ? System.Windows.Media.Brushes.Goldenrod : System.Windows.Media.Brushes.IndianRed;
        HintText.Text = paused ? "No audio is being saved. Resume to continue this meeting." : "Capturing meeting audio and microphone to a local folder.";
        if (paused) { _lastRemoteLevel = 0; _lastMicrophoneLevel = 0; RemoteMeter.Value = 0; MicrophoneMeter.Value = 0; }
    }

    private bool MeetingWindowBusy(RecordingListItem item) => _meetingWindows.TryGetValue(item.Folder, out var window) && window.IsBusy;

    private RecordingDetailWindow MeetingWindow(RecordingListItem item)
    {
        if (_meetingWindows.TryGetValue(item.Folder, out var existing)) return existing;
        var window = new RecordingDetailWindow(_library, item, ReprocessMeeting, _settings) { Owner = this };
        _meetingWindows.Add(item.Folder, window);
        window.Closed += (_, _) =>
        {
            _meetingWindows.Remove(item.Folder);
            if (_agentClosing) return;
            if (!_meetingWindows.Values.Any(x => x.IsVisible)) { Show(); Activate(); }
            RefreshRecordings();
        };
        return window;
    }

    private void ShowMeeting(RecordingListItem item)
    {
        var window = MeetingWindow(item);
        window.Show();
        window.Activate();
        Hide();
    }

    internal async Task<object?> ExecuteAgentCommandAsync(AgentCommand command)
    {
        var read = IsAgentRead(command.Name);
        if (!read) await _agentCommands.WaitAsync(command.Cancellation);
        try
        {
            return await Dispatcher.InvokeAsync(() => ExecuteAgentOnUiAsync(command),
                DispatcherPriority.Normal, command.Cancellation).Task.Unwrap();
        }
        finally { if (!read) _agentCommands.Release(); }
    }

    private static bool IsAgentRead(string name) => name is "state" or "devices" or "meetings" or "meeting" or "settings" or "jobs"
        or "speakers" or "transcript.get" or "meeting.file" or "playback.state" or "local-whisper.status" or "local-whisper.recommend";

    private object AgentState() => new
    {
        recording = new
        {
            state = _session is null ? _recordingStarted is null ? "idle" : "stopping" : _session.IsPaused ? "paused" : "recording",
            meetingId = _session?.Metadata.Id,
            elapsedSeconds = _session?.Elapsed.TotalSeconds ?? 0,
            language = RecordingLanguageBox.SelectedValue,
            remoteLevel = _lastRemoteLevel, microphoneLevel = _lastMicrophoneLevel
        },
        automaticProcessing = !_settings.SpeechRecognitionPaused,
        processing = !_processingTask.IsCompleted,
        compact = _meetingsHidden, visible = IsVisible,
        openMeetings = _meetingWindows.Where(x => x.Value.IsVisible).Select(x => Path.GetFileName(x.Key)).ToArray(),
        update = new { supported = UpdateTrust.SupportsInstallation, checking = _isCheckingUpdates, installing = _isUpdating,
            available = _availableUpdate?.Manifest.Version, current = UpdateTrust.CurrentVersion.ToString() },
        recordingsFolder = _library.RootFolder,
        jobs = _agentJobs.ToArray()
    };

    private object MeetingInfo(RecordingListItem item) => new
    {
        id = Path.GetFileName(item.Folder), item.Title, item.Metadata.StartedLocal,
        durationSeconds = _session?.Folder == item.Folder ? _session.Elapsed.TotalSeconds : item.Metadata.Duration.TotalSeconds,
        item.Metadata.Status,
        recordingState = _session?.Folder == item.Folder ? _session.IsPaused ? "paused" : "recording" : null,
        language = item.Metadata.TranscriptionLanguage ?? "en-US",
        provider = item.Metadata.TranscriptionProviderId ?? "azure-speech", model = item.Metadata.TranscriptionModel, item.IsProcessing, item.IsManuallyQueued,
        item.ProcessingFailed, item.ProcessingPercent, item.IsProgressIndeterminate,
        item.CanOpen, busy = MeetingWindowBusy(item), hasTranscript = File.Exists(Path.Combine(item.Folder, "final-transcript.md"))
    };

    private async Task<object?> ExecuteAgentOnUiAsync(AgentCommand command)
    {
        if (_agentClosing) throw new AgentException(503, "Recap is closing.");
        var args = new AgentArguments(command.Body);
        command.Cancellation.ThrowIfCancellationRequested();
        var read = IsAgentRead(command.Name);
        if (!read && (_isClosingAfterStop || _waitingForProcessingShutdown || _waitingForRecordingStop || _cancelingUpdateShutdown))
            throw new AgentException(503, "Recap is closing.");
        if (!read && (_isUpdating || _recordingStarted is not null && _session is null))
            throw new AgentException(409, "Wait for recording shutdown or the update to finish.");
        RecordingListItem? item = null;
        if (command.MeetingId is not null)
        {
            item = Recordings.FirstOrDefault(x => Path.GetFileName(x.Folder) == command.MeetingId)
                ?? _library.LoadRecordings().FirstOrDefault(x => Path.GetFileName(x.Folder) == command.MeetingId)
                ?? throw new AgentException(404, "Meeting not found.");
            if (_meetingWindows.TryGetValue(item.Folder, out var cached)) item = cached.MeetingItem;
            ValidateMeetingPaths(item);
        }
        switch (command.Name)
        {
            case "state": args.Only(); return AgentState();
            case "devices": args.Only(); RefreshAudioDevices(); return new { remote = RemoteDevices.ToArray(), microphone = MicrophoneDevices.ToArray(), SelectedRemoteDevice, SelectedMicrophoneDevice };
            case "recording.start":
                args.Only("language", "title");
                if (_session is not null) throw new AgentException(409, "A recording is already active.");
                var language = args.Text("language", RecordingLanguageBox.SelectedValue as string ?? "en-US"); ValidateLanguage(language);
                var title = args.Text("title", "Untitled recording"); ValidateTitle(title);
                RecordingLanguageBox.SelectedValue = language;
                StartRecording(false);
                _session!.Metadata.Title = title;
                _library.SaveMetadata(_session.Folder, _session.Metadata);
                return AgentState();
            case "recording.pause": args.Only(); SetRecordingPaused(true); return AgentState();
            case "recording.resume": args.Only(); SetRecordingPaused(false); return AgentState();
            case "recording.stop":
                args.Only();
                var stoppedId = _session?.Metadata.Id;
                await StopRecordingAsync(false);
                return new { meetingId = stoppedId, state = AgentState() };
            case "meetings": args.Only(); RefreshRecordings(); return Recordings.Select(MeetingInfo).ToArray();
            case "meeting": args.Only(); return MeetingInfo(item!);
            case "meeting.rename":
                args.Only("title"); EnsureMeetingIdle(item!);
                var newTitle = args.Text("title"); ValidateTitle(newTitle);
                _library.Rename(item!, newTitle);
                if (_meetingWindows.TryGetValue(item!.Folder, out var renamed)) renamed.RefreshAgentView();
                RefreshRecordings(); return MeetingInfo(item);
            case "meeting.delete":
                args.Only("confirm"); args.Confirm(); EnsureMeetingIdle(item!);
                // Closing a detail window refreshes the queue. Exclude this folder until deletion finishes.
                _agentMutationMeetings.Add(item!.Folder);
                try
                {
                    if (_meetingWindows.TryGetValue(item.Folder, out var removed)) removed.Close();
                    _library.Delete(item);
                    Recordings.Remove(item);
                }
                finally { _agentMutationMeetings.Remove(item.Folder); }
                RefreshRecordings(); return new { deleted = command.MeetingId };
            case "meeting.process":
                args.Only(); EnsureMeetingIdle(item!);
                if (!item!.CanOpen)
                {
                    if (MeetingRecognitionService.RequiresProviderReset(item, _settings))
                        throw new AgentException(409, "Existing results use another provider/model. Use reprocess with confirmation to replace them.");
                    if (_isPausingProcessing) throw new AgentException(409, "Processing is still pausing.");
                    if (!Recordings.Contains(item)) Recordings.Add(item);
                    item.UpdateProcessing(null, false); item.SetManuallyQueued(true); StartPendingProcessing();
                }
                return new AgentAccepted(MeetingInfo(item));
            case "meeting.reprocess":
                args.Only("language", "confirm"); args.Confirm(); EnsureMeetingIdle(item!);
                var newLanguage = args.Text("language"); ValidateLanguage(newLanguage);
                if (!Recordings.Contains(item!)) Recordings.Add(item!);
                ReprocessMeeting(item!, newLanguage); return new AgentAccepted(MeetingInfo(item!));
            case "processing.pause":
            case "processing.resume":
                args.Only();
                if (_isPausingProcessing) throw new AgentException(409, "Processing is already pausing.");
                var wasPaused = _settings.SpeechRecognitionPaused;
                _settings.SpeechRecognitionPaused = command.Name == "processing.pause";
                _settingsStore.Save(_settings);
                await ApplyAutomaticProcessingAsync(wasPaused); return AgentState();
            case "meeting.prepare":
            case "transcript.generate":
                args.Only(); EnsureMeetingIdle(item!);
                if (_meetingWindows.Values.Any(x => x.IsBusy && !x.IsSpeechProcessing))
                    throw new AgentException(409, "Another preparation or transcript job is running.");
                var window = MeetingWindow(item!);
                if (command.Name == "transcript.generate" && !item!.CanOpen)
                    throw new AgentException(409, "Process the meeting before generating a transcript.");
                return StartAgentJob(command.Name, command.MeetingId, command.Name == "meeting.prepare" ? window.PrepareAudioAsync : window.GenerateTranscriptAsync);
            case "speakers":
                args.Only(); EnsureSpeechReady(item!); return MeetingWindow(item!).AgentSpeakers();
            case "speakers.save":
                args.Only("names"); EnsureMeetingIdle(item!); EnsureSpeechReady(item!);
                MeetingWindow(item!).SaveAgentSpeakers(args.Names()); return MeetingWindow(item!).AgentSpeakers();
            case "transcript.get":
                args.Only(); return new AgentFile(MeetingFile(item!, "transcript"), "text/markdown; charset=utf-8");
            case "transcript.copy":
                args.Only(); Clipboard.SetText(File.ReadAllText(MeetingFile(item!, "transcript"))); return new { copied = true };
            case "meeting.file":
                args.Only("file"); var path = MeetingFile(item!, args.Text("file"));
                return new AgentFile(path, path.EndsWith(".wav", StringComparison.OrdinalIgnoreCase) ? "audio/wav" : path.EndsWith(".json", StringComparison.OrdinalIgnoreCase) ? "application/json" : "text/markdown; charset=utf-8");
            case "playback.state": args.Only(); return MeetingWindow(item!).AgentPlaybackState();
            case "playback.control":
                args.Only("action", "track", "positionSeconds", "offsetSeconds", "speaker", "sampleIndex");
                EnsureMeetingIdle(item!);
                return MeetingWindow(item!).ControlAgentPlayback(args);
            case "meeting.open":
                args.Only(); EnsureSpeechReady(item!); ShowMeeting(item!); return MeetingInfo(item!);
            case "meeting.close":
                args.Only();
                if (_meetingWindows.TryGetValue(item!.Folder, out var closed))
                {
                    if (closed.IsBusy && !closed.IsSpeechProcessing) throw new AgentException(409, "Wait for preparation or transcript generation to finish.");
                    closed.Close();
                }
                return new { closed = true };
            case "meeting.folder": args.Only(); _library.OpenFolder(item!.Folder); return new { opened = true };
            case "recordings.folder": args.Only(); _library.OpenFolder(); return new { opened = true };
            case "settings": args.Only(); return AgentSettings.Redacted(_settings);
            case "settings.save":
                if (OwnedWindows.OfType<SettingsWindow>().Any()) throw new AgentException(409, "Close the Settings window before changing settings through the API.");
                if (_isPausingProcessing) throw new AgentException(409, "Processing is still pausing.");
                var updated = AgentSettings.Apply(_settings, command.Body);
                var previousPaused = _settings.SpeechRecognitionPaused;
                var restartRequired = updated.RecordingsFolder != _settings.RecordingsFolder || updated.LogsFolder != _settings.LogsFolder;
                _settingsStore.Save(updated); AgentSettings.Copy(updated, _settings);
                RefreshAudioDevices(); await ApplyAutomaticProcessingAsync(previousPaused);
                return new { settings = AgentSettings.Redacted(_settings), restartRequired };
            case "settings.file": args.Only(); _settingsStore.OpenSettingsFile(); return new { opened = true };
            case "ui":
                args.Only("compact", "show", "settings");
                if (args.Has("compact") && args.Bool("compact") != _meetingsHidden) ToggleMeetings_Click(this, new RoutedEventArgs());
                if (args.Bool("show")) { Show(); Activate(); }
                if (args.Has("settings"))
                {
                    if (args.Bool("settings"))
                    {
                        if (OwnedWindows.OfType<SettingsWindow>().Any()) throw new AgentException(409, "Settings is already open.");
                        _ = Dispatcher.BeginInvoke(new Action(() => OpenSettings_Click(this, new RoutedEventArgs())));
                    }
                    else foreach (var dialog in OwnedWindows.OfType<SettingsWindow>().ToArray()) dialog.Close();
                }
                return AgentState();
            case "jobs": args.Only(); return _agentJobs.ToArray();
            case "updates.check":
                args.Only();
                if (!UpdateTrust.SupportsInstallation) throw new AgentException(409, "This build does not support in-app installation.");
                return StartAgentJob("updates.check", null, async () =>
                {
                    if (_isCheckingUpdates) throw new AgentException(409, "An update check is already running.");
                    _isCheckingUpdates = true;
                    try
                    {
                        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(_updateCancellation.Token);
                        timeout.CancelAfter(TimeSpan.FromSeconds(30));
                        _availableUpdate = await _updates.CheckAsync(timeout.Token);
                        UpdateButton.Visibility = _availableUpdate is null ? Visibility.Collapsed : Visibility.Visible;
                        UpdateButton.Content = _availableUpdate is null ? "Updates" : $"Update to {_availableUpdate.Manifest.Version}";
                    }
                    finally { _isCheckingUpdates = false; }
                });
            case "updates.install":
                args.Only("confirm"); args.Confirm();
                foreach (var hidden in _meetingWindows.Values.Where(x => !x.IsVisible && !x.IsBusy).ToArray()) hidden.Close();
                if (WorkPreventsUpdate || _availableUpdate is null) throw new AgentException(409, "Check for an update first; stop recording/processing and close meeting windows before installing.");
                return StartAgentJob("updates.install", null, async () => { _installTask = InstallAvailableUpdateAsync(true); await _installTask; });
            case "local-whisper.status":
                args.Only();
                return new { runtime = LocalWhisperRuntime.Status(_settings.LocalWhisper.ForLanguage("en-US")),
                    languages = LocalWhisperRuntime.StatusSelected(_settings.LocalWhisper) };
            case "local-whisper.recommend":
                args.Only("language");
                var recommendationLanguage = args.Text("language", "");
                if (recommendationLanguage.Length != 0) ValidateLanguage(recommendationLanguage);
                var selected = recommendationLanguage.Length != 0
                    ? _settings.LocalWhisper.ForLanguage(recommendationLanguage)
                    : new[] { _settings.LocalWhisper.ForLanguage("en-US"), _settings.LocalWhisper.ForLanguage("da-DK") }
                        .OrderByDescending(config => LocalWhisperSettings.Models.Single(model => model.Id == config.Model).SuggestedGpuGb).First();
                try { selected.Validate(); }
                catch (ArgumentException ex) { throw new AgentException(400, ex.Message); }
                return HardwareDetection.Recommend(selected, await HardwareDetection.DetectAsync(command.Cancellation));
            case "local-whisper.setup":
            case "local-whisper.verify":
                args.Only("confirm");
                var download = command.Name == "local-whisper.setup";
                if (download) args.Confirm();
                if (LocalWhisperRuntime.SetupRunning) throw new AgentException(409, "Local setup is already running.");
                var localSettings = _settings.LocalWhisper.Copy();
                return StartAgentJob(command.Name, null, () => LocalWhisperRuntime.SetupSelectedAsync(localSettings, download));
            case "local-whisper.cancel":
                args.Only(); LocalWhisperRuntime.CancelSetup(); return new { cancellationRequested = true };
            case "quit":
                args.Only("confirm"); args.Confirm();
                if (LocalWhisperRuntime.SetupRunning || _session is not null || OwnedWindows.OfType<SettingsWindow>().Any()
                    || _meetingWindows.Values.Any(x => x.IsBusy && !x.IsSpeechProcessing))
                    throw new AgentException(409, "Stop recording, close Settings, and wait for preparation/transcript jobs before quitting.");
                return new AgentAccepted(new { closing = true }, () =>
                    Dispatcher.BeginInvoke(DispatcherPriority.ContextIdle, new Action(Close)));
            default: throw new AgentException(404, "Unknown command.");
        }
    }

    private AgentAccepted StartAgentJob(string type, string? meetingId, Func<Task> work)
    {
        _agentJobs.RemoveAll(x => x.State != "running" && _agentJobs.Count > 99);
        var job = new AgentJob { Id = Guid.NewGuid().ToString("N"), Type = type, MeetingId = meetingId };
        _agentJobs.Add(job);
        _ = Run();
        return new AgentAccepted(job);
        async Task Run()
        {
            try { await work(); job.State = "completed"; }
            catch (OperationCanceledException) { job.State = "cancelled"; }
            catch (Exception ex)
            {
                job.State = "failed";
                job.Error = ex is AgentException ? ex.Message : "Operation failed. Check provider configuration and local logs before retrying.";
                AppLogger.Error(ex, $"Agent operation failed: {type}");
            }
            finally
            {
                if (!_agentClosing && type is "meeting.prepare" or "transcript.generate") RefreshRecordings();
            }
        }
    }

    private void EnsureMeetingIdle(RecordingListItem item)
    {
        if (item.Metadata.Status == "recording" || item.IsProcessing || item.IsManuallyQueued || MeetingWindowBusy(item))
            throw new AgentException(409, "The meeting is recording, queued, or busy. Wait for it to finish.");
    }

    private static void EnsureSpeechReady(RecordingListItem item)
    {
        if (!item.CanOpen) throw new AgentException(409, "Process the meeting first.");
    }

    private static void ValidateLanguage(string language)
    {
        if (language is not ("en-US" or "da-DK")) throw new AgentException(400, "Language must be en-US or da-DK.");
    }
    private static void ValidateTitle(string title)
    {
        if (string.IsNullOrWhiteSpace(title) || title.Length > 200 || title.Any(char.IsControl))
            throw new AgentException(400, "Title must contain 1-200 printable characters.");
    }
    private static void ValidateMeetingPaths(RecordingListItem item)
    {
        if ((File.GetAttributes(item.Folder) & FileAttributes.ReparsePoint) != 0) throw new AgentException(403, "Linked meeting folders are not exposed through the API.");
        foreach (var name in new[] { item.Metadata.Files.Remote, item.Metadata.Files.Microphone, item.Metadata.Files.Mixed,
            item.Metadata.Files.RawRemote, item.Metadata.Files.RawMicrophone, item.Metadata.Files.FinalTranscript })
            if (!string.IsNullOrEmpty(name) && (Path.GetFileName(name) != name || name.Contains(':') || Path.IsPathRooted(name)))
                throw new AgentException(403, "Meeting metadata contains an invalid file path.");
        foreach (var file in Directory.EnumerateFiles(item.Folder))
            if ((File.GetAttributes(file) & FileAttributes.ReparsePoint) != 0) throw new AgentException(403, "Linked meeting files are not exposed through the API.");
    }
    private static string MeetingFile(RecordingListItem item, string file)
    {
        var name = file switch
        {
            "remote" => item.Metadata.Files.Remote, "microphone" => item.Metadata.Files.Microphone,
            "mixed" => item.Metadata.Files.Mixed, "raw-remote" => item.Metadata.Files.RawRemote,
            "raw-microphone" => item.Metadata.Files.RawMicrophone, "transcript" => "final-transcript.md",
            "metadata" => "metadata.json", "speakers" => "speaker-map.json", "summary" => "summary.md",
            "raw-remote-transcript" => "raw-remote-transcript.json", "raw-microphone-transcript" => "raw-microphone-transcript.json",
            "merged-transcript" => "merged-transcript.md", _ => throw new AgentException(400, "Unknown meeting file.")
        };
        if (string.IsNullOrEmpty(name) || !File.Exists(Path.Combine(item.Folder, name))) throw new AgentException(404, "Meeting file is not available.");
        return Path.GetFullPath(Path.Combine(item.Folder, name));
    }

    private sealed class AgentJob
    {
        public string Id { get; init; } = "";
        public string Type { get; init; } = "";
        public string? MeetingId { get; init; }
        public string State { get; set; } = "running";
        public string? Error { get; set; }
    }
}
