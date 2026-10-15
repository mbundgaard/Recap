using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Threading;
using NAudio.CoreAudioApi;
using Recap.Logging;
using Recap.Recording;
using Recap.Settings;
using Recap.Transcription;

namespace Recap;

public partial class MainWindow : Window
{
    private readonly AppSettingsStore _settingsStore;
    private readonly AppSettings _settings;
    private readonly RecordingLibrary _library;
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromMilliseconds(250) };
    private readonly CancellationTokenSource _processingCancellation = new();
    private Task _processingTask = Task.CompletedTask;
    private Task _recordingStopTask = Task.CompletedTask;
    private bool _waitingForRecordingStop;
    private CancellationTokenSource? _activeProcessingCancellation;
    private bool _waitingForProcessingShutdown;
    private bool _startupRendered;
    private bool _isPausingProcessing;
    private bool _meetingsHidden;
    private double _expandedWidth;
    private WindowState _expandedWindowState;
    private RecordingSession? _session;
    private bool _isClosingAfterStop;
    private DateTime? _recordingStarted;
    private float _lastRemoteLevel;
    private float _lastMicrophoneLevel;

    public ObservableCollection<RecordingListItem> Recordings { get; } = [];
    public ObservableCollection<AudioDeviceOption> RemoteDevices { get; } = [];
    public ObservableCollection<AudioDeviceOption> MicrophoneDevices { get; } = [];

    public AudioDeviceOption? SelectedRemoteDevice { get; set; }
    public AudioDeviceOption? SelectedMicrophoneDevice { get; set; }

    public MainWindow(AppSettingsStore settingsStore, AppSettings settings)
    {
        _settingsStore = settingsStore;
        _settings = settings;
        _library = new RecordingLibrary(_settings.RecordingsFolder);

        InitializeComponent();
        DataContext = this;
        RecordingLanguageBox.ItemsSource = new[]
        {
            new { Name = "English", Code = "en-US" },
            new { Name = "Danish", Code = "da-DK" }
        };
        RecordingLanguageBox.SelectedValue = "en-US";
        _timer.Tick += Timer_Tick;
        RefreshAudioDevices();
        UpdateAutomaticProcessingControl();
        RefreshRecordings();
        ContentRendered += MainWindow_ContentRendered;
    }

    private void ToggleMeetings_Click(object sender, RoutedEventArgs e)
    {
        _meetingsHidden = !_meetingsHidden;
        if (_meetingsHidden)
        {
            _expandedWindowState = WindowState;
            _expandedWidth = WindowState == WindowState.Normal ? ActualWidth : RestoreBounds.Width;
            var compactWidth = 360 + ActualWidth - MainLayout.ActualWidth;
            WindowState = WindowState.Normal;
            MeetingsPanel.Visibility = Visibility.Collapsed;
            MeetingsGapColumn.Width = new GridLength(0);
            MeetingsColumn.Width = new GridLength(0);
            RecorderColumn.Width = new GridLength(1, GridUnitType.Star);
            MinWidth = 400;
            Width = Math.Max(MinWidth, compactWidth);
        }
        else
        {
            MeetingsPanel.Visibility = Visibility.Visible;
            RecorderColumn.Width = new GridLength(360);
            MeetingsGapColumn.Width = new GridLength(20);
            MeetingsColumn.Width = new GridLength(1, GridUnitType.Star);
            MinWidth = 760;
            Width = Math.Max(MinWidth, _expandedWidth);
            WindowState = _expandedWindowState;
        }
        var label = _meetingsHidden ? "Show meetings" : "Hide meetings";
        MeetingsToggleButton.Content = _meetingsHidden ? "\uE76C" : "\uE76B";
        MeetingsToggleButton.ToolTip = label;
        System.Windows.Automation.AutomationProperties.SetName(MeetingsToggleButton, label);
    }

    private async void RecordButton_Click(object sender, RoutedEventArgs e)
    {
        if (_isUpdating || _agentClosing || _isClosingAfterStop || (_session is null && _recordingStarted is not null)) return;
        if (_session is null)
        {
            StartRecording();
        }
        else
        {
            await StopRecordingAsync();
        }
    }

    private void StartRecording(bool reportErrors = true)
    {
        if (_isUpdating || _session is not null || _recordingStarted is not null)
            throw new InvalidOperationException("Recording cannot start while the app is busy.");
        try
        {
            if (RecordingLanguageBox.SelectedValue is not string language || (language != "en-US" && language != "da-DK"))
                throw new InvalidOperationException("Choose a transcription language before recording.");

            _session = new RecordingSession(_library, SelectedRemoteDevice, SelectedMicrophoneDevice);
            _session.LevelsChanged += Session_LevelsChanged;
            _session.Start(language);
            RecordingLanguageBox.IsEnabled = false;
            AppLogger.Info($"Recording started: {_session.Folder}");
            _recordingStarted = DateTime.Now;

            StatusText.Text = "● Recording";
            StatusText.Foreground = System.Windows.Media.Brushes.IndianRed;
            RecordButton.Content = "Stop recording";
            RecordButton.Background = System.Windows.Media.Brushes.IndianRed;
            HintText.Text = "Capturing meeting audio and microphone to a local folder.";

            PauseRecordingButton.Visibility = Visibility.Visible;
            PauseRecordingButton.Content = "Pause";
            _timer.Start();
        }
        catch (Exception ex)
        {
            try
            {
                if (_session is not null)
                {
                    _session.Metadata.Status = "failed";
                    _session.Metadata.EndedLocal = DateTime.Now;
                    _session.Metadata.EndedUtc = DateTime.UtcNow;
                    _library.SaveMetadata(_session.Folder, _session.Metadata);
                }
            }
            catch { /* Preserve the original startup error. */ }
            _session?.Dispose();
            _session = null;
            RecordingLanguageBox.IsEnabled = true;
            AppLogger.Error(ex, "Could not start recording.");
            if (!reportErrors) throw;
            MessageBox.Show(this, ex.Message, "Could not start recording", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private Task StopRecordingAsync(bool reportErrors = true)
    {
        if (!_recordingStopTask.IsCompleted) return _recordingStopTask;
        _recordingStopTask = StopRecordingCoreAsync(reportErrors);
        return _recordingStopTask;
    }

    private async Task StopRecordingCoreAsync(bool reportErrors)
    {
        var session = _session;
        if (session is null)
        {
            return;
        }

        _session = null;
        _timer.Stop();
        PauseRecordingButton.Visibility = Visibility.Collapsed;
        RecordButton.IsEnabled = false;
        RecordButton.Content = "Stopping...";
        HintText.Text = "Finishing local audio files...";

        try
        {
            await Task.Run(() =>
            {
                session.Stop();
                AppLogger.Info($"Recording stopped: {session.Folder}");
                session.Dispose();
            });

            _recordingStarted = null;
            _lastRemoteLevel = 0;
            _lastMicrophoneLevel = 0;

            TimerText.Text = "00:00:00";
            RemoteMeter.Value = 0;
            MicrophoneMeter.Value = 0;
            StatusText.Text = "Ready";
            StatusText.Foreground = System.Windows.Media.Brushes.ForestGreen;
            RecordButton.Content = "Start recording";
            RecordButton.Background = System.Windows.Media.Brushes.ForestGreen;
            HintText.Text = "Captures meeting audio and microphone to a local folder.";

            RefreshRecordings();
        }
        catch (Exception ex)
        {
            AppLogger.Error(ex, "Could not stop recording.");
            if (!reportErrors) throw;
            MessageBox.Show(this, ex.Message, "Could not stop recording", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            session.Dispose();
            _recordingStarted = null;
            RecordButton.Content = "Start recording";
            RecordButton.IsEnabled = true;
            RecordingLanguageBox.IsEnabled = true;
        }
    }

    private void RefreshAudioDevices()
    {
        RemoteDevices.Clear();
        MicrophoneDevices.Clear();

        try
        {
            using var devices = new MMDeviceEnumerator();

            foreach (var device in devices.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active))
            {
                RemoteDevices.Add(new AudioDeviceOption(device.ID, device.FriendlyName));
            }

            foreach (var device in devices.EnumerateAudioEndPoints(DataFlow.Capture, DeviceState.Active))
            {
                MicrophoneDevices.Add(new AudioDeviceOption(device.ID, device.FriendlyName));
            }

            SelectedRemoteDevice = RemoteDevices.FirstOrDefault(x => x.Id == _settings.AudioDevices.RemoteDeviceId)
                ?? FindDefaultDevice(RemoteDevices, devices, DataFlow.Render, Role.Multimedia)
                ?? RemoteDevices.FirstOrDefault();
            SelectedMicrophoneDevice = MicrophoneDevices.FirstOrDefault(x => x.Id == _settings.AudioDevices.MicrophoneDeviceId)
                ?? FindDefaultDevice(MicrophoneDevices, devices, DataFlow.Capture, Role.Communications)
                ?? FindDefaultDevice(MicrophoneDevices, devices, DataFlow.Capture, Role.Multimedia)
                ?? MicrophoneDevices.FirstOrDefault();

        }
        catch (Exception ex)
        {
            AppLogger.Error(ex, "Could not list audio devices.");
            HintText.Text = $"Could not list audio devices: {ex.Message}";
        }
    }

    private static AudioDeviceOption? FindDefaultDevice(IEnumerable<AudioDeviceOption> options, MMDeviceEnumerator devices, DataFlow flow, Role role)
    {
        try
        {
            var defaultDevice = devices.GetDefaultAudioEndpoint(flow, role);
            return options.FirstOrDefault(x => x.Id == defaultDevice.ID);
        }
        catch
        {
            return null;
        }
    }

    private void Session_LevelsChanged(float remoteLevel, float microphoneLevel)
    {
        Dispatcher.BeginInvoke(() =>
        {
            if (_session is null || _session.IsPaused) return;
            if (remoteLevel >= 0) _lastRemoteLevel = remoteLevel;
            if (microphoneLevel >= 0) _lastMicrophoneLevel = microphoneLevel;
        });
    }

    private void Timer_Tick(object? sender, EventArgs e)
    {
        if (_recordingStarted is not null)
        {
            TimerText.Text = (_session?.Elapsed ?? TimeSpan.Zero).ToString(@"hh\:mm\:ss");
        }

        RemoteMeter.Value = Math.Min(100, _lastRemoteLevel * 250);
        MicrophoneMeter.Value = Math.Min(100, _lastMicrophoneLevel * 250);
    }

    private void RefreshRecordings()
    {
        var existing = Recordings.ToDictionary(x => x.Folder, StringComparer.OrdinalIgnoreCase);
        Recordings.Clear();
        foreach (var item in _library.LoadRecordings())
        {
            // Keep active jobs and their metadata instance stable across list refreshes.
            Recordings.Add(existing.TryGetValue(item.Folder, out var previous)
                && (previous.IsProcessing || previous.IsManuallyQueued || previous.ProcessingFailed
                    || _meetingWindows.ContainsKey(previous.Folder)) ? previous : item);
        }
        UpdatePausedMeetings();
        StartPendingProcessing();
    }

    private void UpdateAutomaticProcessingControl()
    {
        var paused = _settings.SpeechRecognitionPaused;
        ProcessingModeText.Text = paused
            ? "Automatic processing is off. Process meetings individually below."
            : "Automatic processing is on. Oldest meetings are processed first.";
    }

    private void UpdatePausedMeetings()
    {
        foreach (var item in Recordings.Where(x => x.Metadata.Status == "completed"
            && !x.CanOpen && !x.IsProcessing && !x.IsManuallyQueued && !x.ProcessingFailed))
            item.UpdateProcessing(_settings.SpeechRecognitionPaused ? "Automatic processing paused" : null, false);
    }

    private async Task ApplyAutomaticProcessingAsync(bool wasPaused)
    {
        UpdateAutomaticProcessingControl();
        if (!wasPaused && _settings.SpeechRecognitionPaused)
        {
            _isPausingProcessing = true;
            foreach (var item in Recordings.Where(x => x.IsManuallyQueued))
                item.SetManuallyQueued(false);
            UpdatePausedMeetings();
            _activeProcessingCancellation?.Cancel();
            try { await _processingTask; }
            finally
            {
                _isPausingProcessing = false;
                UpdateAutomaticProcessingControl();
                UpdatePausedMeetings();
            }
        }
        else if (!_settings.SpeechRecognitionPaused)
        {
            foreach (var item in Recordings.Where(x => x.ProcessingFailed))
                item.UpdateProcessing(null, false);
            UpdatePausedMeetings();
            StartPendingProcessing();
        }
    }

    private void MainWindow_ContentRendered(object? sender, EventArgs e)
    {
        ContentRendered -= MainWindow_ContentRendered;
        // Yield until the first window render and pending UI work have completed.
        Dispatcher.BeginInvoke(DispatcherPriority.ContextIdle, new Action(() =>
        {
            _startupRendered = true;
            StartPendingProcessing();
            if (Application.Current is App) _ = StartAgentServerAsync();
            if (_settings.CheckForUpdatesOnStartup) _ = CheckForUpdatesAsync(false);
        }));
    }

    private void StartPendingProcessing()
    {
        if (_isUpdating || !_startupRendered || !_processingTask.IsCompleted || _processingCancellation.IsCancellationRequested
            || (_settings.SpeechRecognitionPaused && !Recordings.Any(x => x.IsManuallyQueued))) return;
        _processingTask = ProcessPendingMeetingsAsync();
    }

    private async Task ProcessPendingMeetingsAsync()
    {
        while (!_processingCancellation.IsCancellationRequested)
        {
            var item = Recordings
                .Where(x => x.Metadata.Status == "completed" && !x.CanOpen && !x.ProcessingFailed && !MeetingWindowBusy(x) && !_agentMutationMeetings.Contains(x.Folder)
                    && (!_settings.SpeechRecognitionPaused || x.IsManuallyQueued))
                .OrderBy(x => x.Metadata.StartedLocal)
                .FirstOrDefault();
            if (item is null) return;
            item.SetManuallyQueued(false);
            using var jobCancellation = CancellationTokenSource.CreateLinkedTokenSource(_processingCancellation.Token);
            _activeProcessingCancellation = jobCancellation;
            item.UpdateProcessing("Preparing audio…", true, indeterminate: true);
            var progress = new Progress<MeetingProcessingProgress>(update =>
            {
                if (item.IsProcessing)
                    item.UpdateProcessing(update.Message, true, update.Percent, update.Indeterminate);
            });
            try
            {
                await new MeetingRecognitionService(_library, _settings)
                    .ProcessAsync(item, progress, jobCancellation.Token);
                item.UpdateProcessing(null, false);
                if (!item.CanOpen)
                    throw new InvalidOperationException("Prepared audio or speech results are missing.");
            }
            catch (OperationCanceledException) when (jobCancellation.IsCancellationRequested)
            {
                item.UpdateProcessing("Automatic processing paused", false);
                return;
            }
            catch (Exception ex)
            {
                AppLogger.Error(ex, $"Background meeting processing failed: {item.Folder}");
                item.UpdateProcessing($"Processing failed: {ex.Message}", false, failed: true);
            }
            finally
            {
                _activeProcessingCancellation = null;
            }
        }
    }

    private void MeetingAction_Click(object sender, RoutedEventArgs e)
    {
        if (((FrameworkElement)sender).DataContext is not RecordingListItem item || !item.CanUsePrimaryAction) return;
        if (item.CanOpen)
        {
            OpenRecordingDetail_Click(sender, e);
            return;
        }
        if (_isUpdating || _isPausingProcessing) return; // Cancellation or an update is still settling.
        if (MeetingRecognitionService.RequiresProviderReset(item, _settings))
        {
            if (MessageBox.Show(this, "This meeting has results from another provider or model. Replace its speech results, speaker names and generated transcript using the current Settings? Audio is kept.",
                "Switch speech provider", MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No) != MessageBoxResult.Yes) return;
            ReprocessMeeting(item, item.Metadata.TranscriptionLanguage ?? "en-US");
            return;
        }
        item.UpdateProcessing(null, false);
        item.SetManuallyQueued(true);
        StartPendingProcessing();
    }

    private void ReprocessMeeting(RecordingListItem item, string language)
    {
        if (_isUpdating || _isPausingProcessing || _processingCancellation.IsCancellationRequested)
            throw new InvalidOperationException("Wait for processing to pause before starting again.");
        if (MeetingWindowBusy(item) || item.IsProcessing || item.IsManuallyQueued)
            throw new InvalidOperationException("Wait for the meeting's current operation to finish.");
        MeetingRecognitionService.ResetSpeechResults(_library, item, language);
        item.UpdateProcessing(null, false);
        item.SetManuallyQueued(true);
        StartPendingProcessing();
    }

    private void OpenRecordingsFolder_Click(object sender, RoutedEventArgs e)
    {
        _library.OpenFolder();
    }

    private async void OpenSettings_Click(object sender, RoutedEventArgs e)
    {
        if (_isUpdating || _isPausingProcessing) return;
        var wasPaused = _settings.SpeechRecognitionPaused;
        var dialog = new SettingsWindow(_settingsStore, _settings) { Owner = this };
        bool? saved;
        Hide();
        try
        {
            saved = dialog.ShowDialog();
        }
        finally
        {
            Show();
            Activate();
        }
        if (saved == true)
        {
            _settings.SpeechRecognitionPaused = dialog.SpeechRecognitionPaused;
            _settings.CheckForUpdatesOnStartup = dialog.CheckForUpdatesOnStartup;
            RefreshAudioDevices();
            await ApplyAutomaticProcessingAsync(wasPaused);
            UpdatePausedMeetings();
            if (dialog.CheckForUpdatesRequested)
            {
                await CheckForUpdatesAsync(true);
                return;
            }
            MessageBox.Show(
                this,
                "Settings saved. Device changes apply to the next recording. Restart Recap to apply startup-only changes such as the recordings folder.",
                "Settings saved",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
        }
    }

    private void OpenRecording_Click(object sender, RoutedEventArgs e)
    {
        if (((FrameworkElement)sender).DataContext is RecordingListItem item)
        {
            _library.OpenFolder(item.Folder);
        }
    }

    private void RenameRecording_Click(object sender, RoutedEventArgs e)
    {
        if (((FrameworkElement)sender).DataContext is not RecordingListItem item)
        {
            return;
        }

        var dialog = new RenameDialog(item.Title) { Owner = this };
        if (dialog.ShowDialog() == true)
        {
            _library.Rename(item, dialog.TitleText);
            RefreshRecordings();
        }
    }

    private void OpenRecordingDetail_Click(object sender, RoutedEventArgs e)
    {
        if (((FrameworkElement)sender).DataContext is not RecordingListItem item)
        {
            return;
        }

        if (!item.CanOpen)
        {
            RefreshRecordings();
            return;
        }
        ShowMeeting(item);
    }

    protected override async void OnClosing(CancelEventArgs e)
    {
        if (Recap.LocalWhisper.LocalWhisperRuntime.SetupRunning)
        {
            e.Cancel = true;
            MessageBox.Show(this, "Cancel local model setup in Settings (or through the API), or wait for it to finish before closing.", "Local Whisper setup is running");
            return;
        }
        if (_meetingWindows.Values.Any(window => window.IsBusy && !window.IsSpeechProcessing))
        {
            e.Cancel = true;
            MessageBox.Show(this, "Wait for audio preparation or transcript generation to finish before closing.", "Recap is busy");
            return;
        }
        if (!_recordingStopTask.IsCompleted)
        {
            e.Cancel = true;
            if (_waitingForRecordingStop) return;
            _waitingForRecordingStop = true;
            try { await _recordingStopTask; }
            catch { /* The recording command already reports its failure. */ }
            Close();
            return;
        }
        if (_isUpdating && !_handoffToUpdater)
        {
            e.Cancel = true;
            if (_cancelingUpdateShutdown) return;
            _cancelingUpdateShutdown = true;
            _updateCancellation.Cancel();
            _processingCancellation.Cancel();
            await _installTask;
            Close();
            return;
        }
        if (_session is not null && !_isClosingAfterStop)
        {
            var result = MessageBox.Show(this, "A recording is still running. Stop it and exit?", "Recording active", MessageBoxButton.YesNo, MessageBoxImage.Warning);
            if (result != MessageBoxResult.Yes)
            {
                e.Cancel = true;
                return;
            }

            e.Cancel = true;
            _isClosingAfterStop = true;
            _waitingForRecordingStop = true;
            _processingCancellation.Cancel();
            await StopRecordingAsync();
            Close();
            return;
        }

        if (!_processingTask.IsCompleted)
        {
            e.Cancel = true;
            if (_waitingForProcessingShutdown) return;
            _waitingForProcessingShutdown = true;
            _processingCancellation.Cancel();
            await _processingTask;
            Close();
            return;
        }
        _processingCancellation.Cancel();
        _updateCancellation.Cancel();
        _agentClosing = true;
        foreach (var window in _meetingWindows.Values.ToArray()) window.Close();
        _ = _agentServer?.DisposeAsync();
        base.OnClosing(e);
    }
}
