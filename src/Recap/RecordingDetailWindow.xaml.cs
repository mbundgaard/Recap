using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Recap.Logging;
using Recap.Recording;
using Recap.Settings;
using Recap.Transcription;

namespace Recap;

public partial class RecordingDetailWindow : Window
{
    private const string SpeakerMapFileName = "speaker-map.json";
    private const string FinalTranscriptFileName = "final-transcript.md";
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    private readonly RecordingLibrary _library;
    private readonly AppSettings _settings;
    private readonly RecordingListItem _item;
    private readonly Action<RecordingListItem, string> _requestReprocess;
    private Dictionary<string, string> _savedSpeakerNames = [];
    private bool _wasProcessing;
    // Both providers already return timed speaker-labelled text; Markdown export needs no LLM.
    private bool IsLocalTranscript => _item.Metadata.TranscriptionProviderId is "local-whisper" or "grok-speech";
    private bool IsProcessing => _item.IsProcessing || _item.IsManuallyQueued;
    internal bool IsBusy => _isPreparing || _isGenerating || IsProcessing;
    private bool _isGenerating;
    private bool SpeakerNamesSaved => SpeakersIdentified() && Speakers.All(x =>
        _savedSpeakerNames.TryGetValue(x.SourceLabel, out var name) && name == x.DisplayName.Trim());
    private readonly WavPlayer _player = new();
    private readonly DispatcherTimer _sampleStopTimer = new() { Interval = TimeSpan.FromMilliseconds(250) };
    private readonly DispatcherTimer _playbackTimer = new() { Interval = TimeSpan.FromMilliseconds(250) };
    private readonly DispatcherTimer _copyFeedbackTimer = new() { Interval = TimeSpan.FromSeconds(2) };
    private DateTime? _sampleStopAt;
    private Button? _activeSampleButton;

    private void ResetSampleButton()
    {
        if (_activeSampleButton is not null) _activeSampleButton.Content = "Play";
        _activeSampleButton = null;
        _sampleStopTimer.Stop();
        _sampleStopAt = null;
    }
    private bool _isMixedPlaybackLoaded;
    private bool _isMixedPlaybackPlaying;
    private bool _isPlaybackSliderDragging;

    public ObservableCollection<SpeakerNameItem> Speakers { get; } = [];

    public RecordingDetailWindow(RecordingLibrary library, RecordingListItem item,
        Action<RecordingListItem, string> requestReprocess, AppSettings? settings = null)
    {
        _library = library;
        _settings = settings ?? App.Settings ?? AppSettingsStore.CreateDefaultSettings();
        _item = item;
        _requestReprocess = requestReprocess;

        InitializeComponent();
        DataContext = this;
        LanguageBox.ItemsSource = new[]
        {
            new { Name = "English", Code = "en-US" },
            new { Name = "Danish", Code = "da-DK" }
        };
        var language = _item.Metadata.TranscriptionLanguage ?? "en-US";
        LanguageBox.SelectedValue = language == "da-DK" ? "da-DK" : "en-US";

        _copyFeedbackTimer.Tick += (_, _) => ResetCopyFeedback();
        _sampleStopTimer.Tick += SampleStopTimer_Tick;
        _playbackTimer.Tick += PlaybackTimer_Tick;
        _player.MediaOpened += Player_MediaOpened;
        _player.MediaEnded += Player_MediaEnded;
        _player.Failed += exception =>
        {
            ResetSampleButton();
            _isMixedPlaybackLoaded = false;
            _isMixedPlaybackPlaying = false;
            _playbackTimer.Stop();
            PlayMixedButton.Content = "▶ Play";
            MixedAudioStatusText.Text = "Playback failed. Check your output device.";
            if (IsVisible) MessageBox.Show(this, exception.Message, "Playback failed", MessageBoxButton.OK, MessageBoxImage.Error);
        };
        ReloadSpeakers();
        _item.PropertyChanged += MeetingProcessingChanged;
        RefreshView();
    }

    private void MeetingProcessingChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (_wasProcessing && !IsProcessing)
        {
            ReloadSpeakers();
            LanguageBox.SelectedValue = _item.Metadata.TranscriptionLanguage ?? "en-US";
        }
        _wasProcessing = IsProcessing;
        RefreshView();
    }

    private void ReloadSpeakers()
    {
        Speakers.Clear();
        _savedSpeakerNames.Clear();
        LoadSpeakerMap();
        // The transcript defines the current roster; saved names must not hide new speakers.
        var rawPath = Path.Combine(_item.Folder, "raw-remote-transcript.json");
        if (File.Exists(rawPath))
        {
            var segments = JsonSerializer.Deserialize<List<TranscriptSegment>>(File.ReadAllText(rawPath)) ?? [];
            PopulateSpeakersFromSegments(segments);
        }
        ResetSpeakerPage();
    }

    private void RefreshView()
    {
        Title = _item.Metadata.Title;
        TitleText.Text = _item.Metadata.Title;
        MetaText.Text = $"{_item.Metadata.StartedLocal:dddd, dd MMM yyyy HH:mm} · {_item.DurationDisplay}";

        var state = GetMeetingState();
        SpeechProgress.Visibility = IsProcessing ? Visibility.Visible : Visibility.Collapsed;
        SpeechProgress.IsIndeterminate = _item.IsManuallyQueued || _item.IsProgressIndeterminate;
        SpeechProgress.Value = _item.ProcessingPercent;
        SpeechProgress.ToolTip = _item.IsManuallyQueued ? "Queued for processing" : _item.StatusDisplay;
        SttStatusText.Visibility = IsProcessing ? Visibility.Collapsed : Visibility.Visible;

        SttStatusText.Text = _item.ProcessingFailed ? "Processing failed. Retry recognition."
            : state.HasTranscripts
            ? "Recognition complete"
            : state.AudioPrepared
                ? "Ready to recognize speech"
                : "Prepare audio first";

        EmptySpeakersPanel.Visibility = Speakers.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        EmptySpeakersText.Text = state.HasTranscripts
            ? "No speakers detected. You can create the final transcript without naming speakers."
            : "Transcribe first. Then listen to a short sample and name each speaker.";
        var namedSpeakers = Speakers.Count(IsSpeakerNamed);
        SpeakerStatusText.Text = Speakers.Count == 0
            ? state.HasTranscripts ? "No speakers detected" : "No speakers loaded"
            : SpeakerNamesSaved
                ? $"{Speakers.Count} of {Speakers.Count} named · Saved"
                : $"{namedSpeakers} of {Speakers.Count} named";
        RefreshSpeakerPage();

        var finalPath = Path.Combine(_item.Folder, FinalTranscriptFileName);
        var finalMarkdown = !IsProcessing && File.Exists(finalPath) ? File.ReadAllText(finalPath) : null;
        var hasFinalTranscript = IsCompletedTranscript(finalMarkdown);
        CopyTranscriptButton.IsEnabled = hasFinalTranscript;
        SaveTranscriptButton.IsEnabled = hasFinalTranscript;
        if (hasFinalTranscript)
        {
            FinalStatusText.Text = "Ready";
            TranscriptPreview.Document = MarkdownPreview.Render(finalMarkdown!);
        }
        else
        {
            FinalStatusText.Text = IsProcessing ? "Waiting for speech recognition"
                : state.CanGenerate
                ? "Ready to generate"
                : state.HasTranscripts
                    ? "Locked until speakers are named"
                    : "Recognize speech first";
            TranscriptPreview.Document = MarkdownPreview.Render(IsProcessing
                ? "Speech recognition is running in the background. You can close this meeting and return later."
                : state.CanGenerate
                ? "Click **Create transcript** to display the transcript."
                : "Recognize speech and name any detected speakers, then click **Create transcript**.");
        }

        var prepared = state.AudioPrepared && state.MixedPlaybackReady;
        PrepareAudioButton.IsEnabled = !prepared && !_isPreparing && !IsProcessing;
        PrepareAudioButton.Content = prepared ? "Audio prepared" : "Prepare audio";
        PlayMixedButton.IsEnabled = state.MixedPlaybackReady;
        SkipBackButton.IsEnabled = state.MixedPlaybackReady;
        SkipForwardButton.IsEnabled = state.MixedPlaybackReady;
        PlaybackSlider.IsEnabled = state.MixedPlaybackReady;
        MixedAudioStatusText.Text = state.MixedPlaybackReady ? "Ready for playback" : "Prepare audio before playback";
        LanguageBox.IsEnabled = !IsProcessing;
        TranscribeButton.Content = IsProcessing ? "Processing…" : state.HasTranscripts ? "Reprocess speech" : "Recognize speech";
        TranscribeButton.IsEnabled = state.AudioPrepared && !IsProcessing;
        SpeakersList.IsEnabled = !IsProcessing;
        SaveNamesButton.Content = SpeakerNamesSaved ? "Speakers confirmed" : "Confirm speakers";
        SaveNamesButton.IsEnabled = state.SpeakersLoaded && state.SpeakersIdentified && !SpeakerNamesSaved && !IsProcessing;
        GenerateButton.Content = hasFinalTranscript ? "Transcript created" : "Create transcript";
        GenerateButton.IsEnabled = state.CanGenerate && !hasFinalTranscript && !IsProcessing;
    }

    private void ResetCopyFeedback()
    {
        _copyFeedbackTimer.Stop();
        CopyTranscriptButton.Content = "\uE8C8";
        CopyTranscriptButton.ToolTip = "Copy transcript";
        System.Windows.Automation.AutomationProperties.SetName(CopyTranscriptButton, "Copy transcript");
    }

    private void CopyTranscript_Click(object sender, RoutedEventArgs e)
    {
        ResetCopyFeedback();
        try
        {
            Clipboard.SetText(File.ReadAllText(Path.Combine(_item.Folder, FinalTranscriptFileName)));
            CopyTranscriptButton.Content = "\uE73E";
            CopyTranscriptButton.ToolTip = "Copied!";
            System.Windows.Automation.AutomationProperties.SetName(CopyTranscriptButton, "Transcript copied");
            _copyFeedbackTimer.Start();
        }
        catch (Exception ex)
        {
            AppLogger.Error(ex, "Could not copy transcript.");
            MessageBox.Show(this, "Could not copy the transcript. Please try again.", "Copy failed", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void SaveTranscript_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var markdown = File.ReadAllText(Path.Combine(_item.Folder, FinalTranscriptFileName));
            var invalid = Path.GetInvalidFileNameChars();
            var name = new string(_item.Title.Select(c => invalid.Contains(c) || char.IsControl(c) ? '_' : c).ToArray()).Trim().TrimEnd('.');
            if (string.IsNullOrWhiteSpace(name)) name = "Meeting";
            if (name.Length > 100) name = name[..100].TrimEnd(' ', '.');
            var dialog = new Microsoft.Win32.SaveFileDialog
            {
                Title = "Save transcript",
                Filter = "Markdown files (*.md)|*.md",
                DefaultExt = ".md",
                AddExtension = true,
                OverwritePrompt = true,
                FileName = $"{_item.Metadata.StartedLocal:yyyy-MM-dd_HH-mm-ss}_{name}.md"
            };
            if (dialog.ShowDialog(this) == true)
                File.WriteAllText(dialog.FileName, markdown);
        }
        catch (Exception ex)
        {
            AppLogger.Error(ex, "Could not save transcript copy.");
            MessageBox.Show(this, "Could not save the transcript. Check the destination and try again.", "Save failed", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void Rename_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new RenameDialog(_item.Metadata.Title) { Owner = this };
        if (dialog.ShowDialog() == true)
        {
            _library.Rename(_item, dialog.TitleText);
            RefreshView();
        }
    }

    private void OpenFolder_Click(object sender, RoutedEventArgs e)
    {
        _library.OpenFolder(_item.Folder);
    }

    private void PlayRemote_Click(object sender, RoutedEventArgs e)
    {
        PlayFile(Path.Combine(_item.Folder, _item.Metadata.Files.Remote));
    }

    private void PlayMicrophone_Click(object sender, RoutedEventArgs e)
    {
        PlayFile(Path.Combine(_item.Folder, _item.Metadata.Files.Microphone));
    }

    private void PlayMixed_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(_item.Metadata.Files.Mixed))
        {
            MessageBox.Show(this, "Meeting playback is not available until audio has been prepared.", "Audio not prepared", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var mixedPath = Path.Combine(_item.Folder, _item.Metadata.Files.Mixed);
        if (!File.Exists(mixedPath))
        {
            MessageBox.Show(this, "Meeting playback is not available until audio has been prepared.", "Audio not prepared", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        if (!_isMixedPlaybackLoaded)
        {
            ResetSampleButton();
            _sampleStopTimer.Stop();
            _sampleStopAt = null;
            _player.Open(new Uri(mixedPath));
            if (!_player.NaturalDuration.HasTimeSpan) return;
            _isMixedPlaybackLoaded = true;
        }

        if (_isMixedPlaybackPlaying)
        {
            _player.Pause();
            _isMixedPlaybackPlaying = false;
            PlayMixedButton.Content = "▶";
            return;
        }

        _player.Play();
        _playbackTimer.Start();
        _isMixedPlaybackPlaying = true;
        PlayMixedButton.Content = "Pause";
    }

    private bool _isPreparing;

    private async void PrepareAudio_Click(object sender, RoutedEventArgs e)
    {
        try { await PrepareAudioAsync(); }
        catch (Exception ex) { MessageBox.Show(this, ex.Message, "Prepare audio failed", MessageBoxButton.OK, MessageBoxImage.Error); }
    }

    internal async Task PrepareAudioAsync()
    {
        var state = GetMeetingState();
        if (_isPreparing || (state.AudioPrepared && state.MixedPlaybackReady)) return;
        _isPreparing = true;
        IsEnabled = false;
        _player.Close();
        _playbackTimer.Stop();
        _sampleStopTimer.Stop();
        MixedAudioStatusText.Text = "Preparing audio…";
        try
        {
            var processor = new RecordingPostProcessor(_library, _settings);
            await Task.Run(() => processor.Process(_item));
            _isMixedPlaybackLoaded = false;
            _isMixedPlaybackPlaying = false;
            PlayMixedButton.Content = "▶";
            RefreshView();
        }
        catch (Exception ex)
        {
            AppLogger.Error(ex, $"Prepare audio failed: {_item.Folder}");
            throw;
        }
        finally
        {
            _isPreparing = false;
            _isMixedPlaybackLoaded = false;
            _isMixedPlaybackPlaying = false;
            IsEnabled = true;
            RefreshView();
        }
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        if (_isPreparing || _isGenerating) e.Cancel = true;
        base.OnClosing(e);
    }

    private void PreviousSample_Click(object sender, RoutedEventArgs e) => MoveSample(sender, -1);
    private void NextSample_Click(object sender, RoutedEventArgs e) => MoveSample(sender, 1);

    private void MoveSample(object sender, int direction)
    {
        if (sender is not FrameworkElement { DataContext: SpeakerNameItem speaker }) return;
        if (_activeSampleButton is not null)
        {
            ResetSampleButton();
            // Close releases the current sample and ignores queued stopped events.
            _player.Close();
        }
        speaker.MoveSample(direction);
    }

    private void PlaySample_Click(object sender, RoutedEventArgs e)
    {
        if (sender == _activeSampleButton)
        {
            ResetSampleButton();
            _player.Stop();
            return;
        }
        ResetSampleButton();
        if (((FrameworkElement)sender).DataContext is not SpeakerNameItem speaker)
        {
            return;
        }

        var remotePath = Path.Combine(_item.Folder, _item.Metadata.Files.Remote);
        if (!File.Exists(remotePath))
        {
            MessageBox.Show(this, "remote.wav was not found.", "Missing audio", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        if (speaker.SampleStart is null || speaker.SampleEnd is null || speaker.SampleEnd <= speaker.SampleStart)
        {
            MessageBox.Show(this, "This speaker does not have a timestamped sample yet. Run STT with diarization first.", "No sample timestamp", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        _playbackTimer.Stop();
        _isMixedPlaybackLoaded = false;
        _isMixedPlaybackPlaying = false;
        PlayMixedButton.Content = "▶ Play";
        _player.Open(new Uri(remotePath));
        if (!_player.NaturalDuration.HasTimeSpan) return;
        _player.Position = speaker.SampleStart.Value;
        _sampleStopAt = DateTime.Now + (speaker.SampleEnd.Value - speaker.SampleStart.Value);
        _activeSampleButton = (Button)sender;
        _activeSampleButton.Content = "Stop";
        _player.Play();
        _sampleStopTimer.Start();
    }

    private void Transcribe_Click(object sender, RoutedEventArgs e)
    {
        if (IsProcessing) return;
        if (LanguageBox.SelectedValue is not string language || language is not ("da-DK" or "en-US")) return;
        var result = MessageBox.Show(this,
            "Reprocess speech with the provider currently selected in Settings and the selected language?\n\nExisting speech results, speaker names, final transcript and summary will be replaced. Audio is kept. Azure processing sends audio to Azure and may incur charges; Local Whisper stays on this PC.\n\nProcessing continues if you close this meeting.",
            "Reprocess speech", MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No);
        if (result != MessageBoxResult.Yes) return;
        try
        {
            _requestReprocess(_item, language);
            Speakers.Clear();
            _savedSpeakerNames.Clear();
        }
        catch (Exception ex)
        {
            AppLogger.Error(ex, "Could not queue meeting reprocessing.");
            MessageBox.Show(this, ex.Message, "Reprocessing failed", MessageBoxButton.OK, MessageBoxImage.Error);
            ReloadSpeakers();
        }
        RefreshView();
    }

    private void SaveSpeakers_Click(object sender, RoutedEventArgs e)
    {
        if (IsProcessing || SpeakerNamesSaved || !GetMeetingState().SpeakersIdentified) return;
        SaveSpeakerMap();
        AppLogger.Info($"Speaker names saved: {_item.Folder}");
        RefreshView();
    }

    private static bool IsCompletedTranscript(string? markdown) =>
        !string.IsNullOrWhiteSpace(markdown)
        && !markdown.Contains("This placeholder was created only after speaker identification was completed.", StringComparison.Ordinal);

    private async void Generate_Click(object sender, RoutedEventArgs e)
    {
        try { await GenerateTranscriptAsync(); }
        catch (Exception ex) { MessageBox.Show(this, ex.Message, "Generation failed", MessageBoxButton.OK, MessageBoxImage.Error); }
    }

    internal async Task GenerateTranscriptAsync()
    {
        if (IsBusy) throw new InvalidOperationException("The meeting is busy.");
        var existingPath = Path.Combine(_item.Folder, FinalTranscriptFileName);
        if (File.Exists(existingPath) && IsCompletedTranscript(File.ReadAllText(existingPath)))
        {
            RefreshView();
            return;
        }

        if (IsLocalTranscript)
        {
            if (!GetMeetingState().HasTranscripts) throw new InvalidOperationException("Recognize speech first.");
            SaveSpeakerMap(); // Also rebuilds local Markdown without an LLM.
            RefreshView();
            return;
        }

        if (!GetMeetingState().CanGenerate)
        {
            throw new InvalidOperationException("Name every detected remote speaker before generating the final transcript.");
        }

        _isGenerating = true;
        IsEnabled = false;
        FinalStatusText.Text = "Generating transcript…";
        try
        {
            if (_settings.LlmProviderId != "openai-compatible")
                throw new InvalidOperationException("The selected LLM provider is not supported.");
            SaveSpeakerMap();
            var names = Speakers.ToDictionary(x => x.SourceLabel, x => x.DisplayName.Trim());
            var remote = JsonSerializer.Deserialize<List<TranscriptSegment>>(
                File.ReadAllText(Path.Combine(_item.Folder, "raw-remote-transcript.json")))
                ?? throw new InvalidDataException("Remote transcript is missing.");
            var microphone = JsonSerializer.Deserialize<List<TranscriptSegment>>(
                File.ReadAllText(Path.Combine(_item.Folder, "raw-microphone-transcript.json")))
                ?? throw new InvalidDataException("Microphone transcript is missing.");
            if (names.Count > 0 && remote.Any(x => !names.ContainsKey(x.Speaker)))
                throw new InvalidOperationException("Identify every remote speaker before generating.");
            var segments = remote.Select(x => x with
                {
                    Speaker = names.TryGetValue(x.Speaker, out var name) ? name : "Others",
                    SourceTrack = "remote"
                })
                .Concat(microphone.Select(x => x with { Speaker = LocalSpeakerName, SourceTrack = "microphone" }))
                .OrderBy(x => x.Start).ToList();
            if (segments.Count == 0) throw new InvalidOperationException("There is no recognized speech to generate from.");
            IAiProvider provider = new AzureOpenAiProvider(_settings.OpenAiCompatible);
            AppLogger.Info($"Final transcript generation started. Segments={segments.Count}; Echo deduplication enabled.");
            var result = await provider.CreateFinalTranscriptAsync(new FinalTranscriptRequest(_item.Title, segments));
            var finalPath = Path.Combine(_item.Folder, FinalTranscriptFileName);
            await File.WriteAllTextAsync(finalPath + ".tmp", result.Markdown);
            File.Move(finalPath + ".tmp", finalPath, overwrite: true);
            _item.Metadata.Files.FinalTranscript = FinalTranscriptFileName;
            _library.SaveMetadata(_item.Folder, _item.Metadata);
            AppLogger.Info("Final transcript generation completed.");
        }
        catch (Exception ex)
        {
            AppLogger.Error(ex, "Final transcript generation failed.");
            throw;
        }
        finally
        {
            _isGenerating = false;
            IsEnabled = true;
            RefreshView();
        }


    }

    private void PlayFile(string path)
    {
        if (!File.Exists(path))
        {
            MessageBox.Show(this, $"Audio file was not found:\n{path}", "Missing audio", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        _playbackTimer.Stop();
        _isMixedPlaybackLoaded = false;
        _isMixedPlaybackPlaying = false;
        PlayMixedButton.Content = "▶";
        _sampleStopTimer.Stop();
        _sampleStopAt = null;
        _player.Open(new Uri(path));
        _player.Play();
    }

    private void SampleStopTimer_Tick(object? sender, EventArgs e)
    {
        if (_sampleStopAt is not null && DateTime.Now >= _sampleStopAt.Value)
        {
            ResetSampleButton();
            _player.Stop();
            _sampleStopTimer.Stop();
            _sampleStopAt = null;
        }
    }

    private void Player_MediaOpened(object? sender, EventArgs e)
    {
        if (_player.NaturalDuration.HasTimeSpan)
        {
            PlaybackSlider.Maximum = _player.NaturalDuration.TimeSpan.TotalSeconds;
            PlaybackDurationText.Text = FormatPlaybackTime(_player.NaturalDuration.TimeSpan);
        }
    }

    private void Player_MediaEnded(object? sender, EventArgs e)
    {
        ResetSampleButton();
        _isMixedPlaybackPlaying = false;
        PlayMixedButton.Content = "▶";
        _playbackTimer.Stop();
        _player.Position = TimeSpan.Zero;
        PlaybackSlider.Value = 0;
        PlaybackPositionText.Text = "00:00";
    }

    private void PlaybackTimer_Tick(object? sender, EventArgs e)
    {
        if (!_isPlaybackSliderDragging)
        {
            PlaybackSlider.Value = _player.Position.TotalSeconds;
        }

        PlaybackPositionText.Text = FormatPlaybackTime(_player.Position);
    }

    private void PlaybackSlider_PreviewMouseDown(object sender, MouseButtonEventArgs e)
    {
        _isPlaybackSliderDragging = true;
    }

    private void PlaybackSlider_PreviewMouseUp(object sender, MouseButtonEventArgs e)
    {
        _isPlaybackSliderDragging = false;
        if (!_isMixedPlaybackLoaded) return;
        _player.Position = TimeSpan.FromSeconds(PlaybackSlider.Value);
        PlaybackPositionText.Text = FormatPlaybackTime(_player.Position);
    }

    private void SkipBack_Click(object sender, RoutedEventArgs e)
    {
        SeekRelative(TimeSpan.FromSeconds(-10));
    }

    private void SkipForward_Click(object sender, RoutedEventArgs e)
    {
        SeekRelative(TimeSpan.FromSeconds(10));
    }

    private void SeekRelative(TimeSpan offset)
    {
        if (!_isMixedPlaybackLoaded) return;
        var target = _player.Position + offset;
        if (target < TimeSpan.Zero) target = TimeSpan.Zero;
        if (_player.NaturalDuration.HasTimeSpan && target > _player.NaturalDuration.TimeSpan)
        {
            target = _player.NaturalDuration.TimeSpan;
        }

        _player.Position = target;
        PlaybackSlider.Value = target.TotalSeconds;
        PlaybackPositionText.Text = FormatPlaybackTime(target);
    }

    private static string FormatPlaybackTime(TimeSpan value)
    {
        return value.TotalHours >= 1 ? value.ToString(@"h\:mm\:ss") : value.ToString(@"mm\:ss");
    }

    private MeetingState GetMeetingState()
    {
        bool Exists(string? name) => !string.IsNullOrWhiteSpace(name)
            && File.Exists(Path.Combine(_item.Folder, name));

        var transcripts = Exists("raw-remote-transcript.json")
            && Exists("raw-microphone-transcript.json");
        return new MeetingState(
            Exists(_item.Metadata.Files.Remote) && Exists(_item.Metadata.Files.Microphone),
            Exists(_item.Metadata.Files.Mixed),
            transcripts,
            transcripts && Speakers.Count > 0,
            SpeakersIdentified());
    }

    private sealed record MeetingState(
        bool AudioPrepared,
        bool MixedPlaybackReady,
        bool HasTranscripts,
        bool SpeakersLoaded,
        bool SpeakersIdentified)
    {
        public bool CanGenerate => HasTranscripts && (!SpeakersLoaded || SpeakersIdentified);
    }

    private bool SpeakersIdentified()
    {
        if (Speakers.Count == 0)
        {
            return false;
        }

        return Speakers.All(IsSpeakerNamed);
    }

    private void LoadSpeakerMap()
    {
        var path = Path.Combine(_item.Folder, SpeakerMapFileName);
        if (!File.Exists(path))
        {
            return;
        }

        try
        {
            var map = JsonSerializer.Deserialize<SpeakerMapFile>(File.ReadAllText(path), JsonOptions);
            if (map?.Speakers is null)
            {
                return;
            }

            _savedSpeakerNames = map.Speakers.ToDictionary(x => x.SourceLabel, x => x.DisplayName.Trim());
            Speakers.Clear();
            foreach (var speaker in map.Speakers)
            {
                var item = new SpeakerNameItem
                {
                    SourceLabel = speaker.SourceLabel,
                    DisplayName = speaker.DisplayName,
                    PreviewText = string.IsNullOrWhiteSpace(speaker.PreviewText) ? "No text clue saved." : speaker.PreviewText,
                    SampleStart = ParseTimeSpan(speaker.SampleStart),
                    SampleEnd = ParseTimeSpan(speaker.SampleEnd)
                };
                var transcriptPath = Path.Combine(_item.Folder, "raw-remote-transcript.json");
                if (File.Exists(transcriptPath))
                {
                    var segments = JsonSerializer.Deserialize<List<TranscriptSegment>>(File.ReadAllText(transcriptPath)) ?? [];
                    item.SetSamples(segments.Where(x => x.Speaker == item.SourceLabel));
                }
                item.PropertyChanged += (_, args) => { if (args.PropertyName == nameof(SpeakerNameItem.DisplayName)) RefreshView(); };
                Speakers.Add(item);
            }
        }
        catch
        {
            // Ignore invalid draft maps; the user can save a clean one from the UI.
        }
    }

    private void PopulateSpeakersFromSegments(IEnumerable<TranscriptSegment> remoteSegments)
    {
        Speakers.Clear();

        foreach (var group in remoteSegments
            .Where(x => !string.IsNullOrWhiteSpace(x.Speaker))
            .GroupBy(x => x.Speaker)
            .OrderBy(x => x.Key))
        {
            var sample = group
                .OrderByDescending(x => ScoreSample(x))
                .FirstOrDefault();

            if (sample is null)
            {
                continue;
            }

            var item = new SpeakerNameItem
            {
                SourceLabel = group.Key,
                DisplayName = _savedSpeakerNames.TryGetValue(group.Key, out var savedName)
                    ? savedName : IsLocalTranscript ? group.Key : string.Empty,
                PreviewText = sample.Text,
                SampleStart = sample.Start,
                SampleEnd = sample.End
            };
            item.SetSamples(group);
            if (IsLocalTranscript && !_savedSpeakerNames.ContainsKey(item.SourceLabel))
                _savedSpeakerNames[item.SourceLabel] = item.DisplayName;
            item.PropertyChanged += (_, args) => { if (args.PropertyName == nameof(SpeakerNameItem.DisplayName)) RefreshView(); };
            Speakers.Add(item);
        }
    }

    private static double ScoreSample(TranscriptSegment segment)
    {
        var duration = Math.Clamp(segment.End.Subtract(segment.Start).TotalSeconds, 0, 12);
        var textScore = Math.Min(segment.Text.Length / 12.0, 10);
        return duration + textScore;
    }

    private string LocalSpeakerName => string.IsNullOrWhiteSpace(_settings.UserName)
        ? "You" : _settings.UserName.Trim();

    private void SaveSpeakerMap()
    {
        foreach (var speaker in Speakers)
        {
            speaker.DisplayName = speaker.DisplayName.Trim();
        }

        var map = new SpeakerMapFile
        {
            Speakers = Speakers.Select(x => new SpeakerMapEntry
            {
                SourceLabel = x.SourceLabel,
                DisplayName = x.DisplayName,
                PreviewText = x.PreviewText,
                SampleStart = x.SampleStart?.ToString("c"),
                SampleEnd = x.SampleEnd?.ToString("c")
            }).ToList()
        };

        Recap.Storage.AtomicFile.WriteText(Path.Combine(_item.Folder, SpeakerMapFileName), JsonSerializer.Serialize(map, JsonOptions));
        _savedSpeakerNames = Speakers.ToDictionary(x => x.SourceLabel, x => x.DisplayName.Trim());
        if (IsLocalTranscript && GetMeetingState().HasTranscripts)
            LocalTranscriptExporter.Export(_library, _item);
    }

    private static TimeSpan? ParseTimeSpan(string? value)
    {
        return TimeSpan.TryParse(value, out var result) ? result : null;
    }

    protected override void OnClosed(EventArgs e)
    {
        _item.PropertyChanged -= MeetingProcessingChanged;
        _copyFeedbackTimer.Stop();
        _sampleStopTimer.Stop();
        _playbackTimer.Stop();
        _player.Close();
        base.OnClosed(e);
    }

    private sealed class SpeakerMapFile
    {
        public List<SpeakerMapEntry> Speakers { get; set; } = [];
    }

    private sealed class SpeakerMapEntry
    {
        public string SourceLabel { get; set; } = string.Empty;
        public string DisplayName { get; set; } = string.Empty;
        public string PreviewText { get; set; } = string.Empty;
        public string? SampleStart { get; set; }
        public string? SampleEnd { get; set; }
    }
}

public sealed class SpeakerNameItem : INotifyPropertyChanged
{
    private string _displayName = string.Empty;
    private List<TranscriptSegment> _samples = [];
    private int _sampleIndex;
    internal int SampleIndex => _sampleIndex;
    internal IReadOnlyList<TranscriptSegment> Samples => _samples;
    public bool HasPreviousSample => _sampleIndex > 0;
    public bool HasNextSample => _sampleIndex + 1 < _samples.Count;
    public string SampleCountDisplay => _samples.Count == 0 ? string.Empty : $"{_sampleIndex + 1} of {_samples.Count}";

    public void SetSamples(IEnumerable<TranscriptSegment> segments)
    {
        _samples = segments.Where(x => x.End > x.Start).OrderBy(x => x.Start).ToList();
        _sampleIndex = Math.Max(0, _samples.FindIndex(x => x.Start == SampleStart));
        UpdateSample();
    }

    public void MoveSample(int direction)
    {
        if (_samples.Count == 0) return;
        _sampleIndex = Math.Clamp(_sampleIndex + direction, 0, _samples.Count - 1);
        UpdateSample();
    }

    private void UpdateSample()
    {
        if (_samples.Count > 0)
        {
            var sample = _samples[_sampleIndex];
            SampleStart = sample.Start;
            SampleEnd = sample.End;
            PreviewText = sample.Text;
        }
        foreach (var property in new[] { nameof(SampleDisplay), nameof(PreviewText), nameof(HasPreviousSample), nameof(HasNextSample), nameof(SampleCountDisplay) })
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(property));
    }

    public string SourceLabel { get; set; } = string.Empty;
    public string PreviewText { get; set; } = string.Empty;
    public TimeSpan? SampleStart { get; set; }
    public TimeSpan? SampleEnd { get; set; }

    public string DisplayName
    {
        get => _displayName;
        set
        {
            if (_displayName == value) return;
            _displayName = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(DisplayName)));
        }
    }

    public string SampleDisplay => SampleStart is null || SampleEnd is null
        ? "No sample yet"
        : $"{(int)SampleStart.Value.TotalHours:00}:{SampleStart.Value:mm\\:ss}–{(int)SampleEnd.Value.TotalHours:00}:{SampleEnd.Value:mm\\:ss}";

    public event PropertyChangedEventHandler? PropertyChanged;
}
