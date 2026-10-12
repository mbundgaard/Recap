using System.Collections.Concurrent;
using System.IO;
using NAudio.Wave;
using Recap.Recording;
using Recap.Settings;
using Recap.Transcription;

// Offline regression checks: no microphone, Azure account, or network calls.
var root = Path.Combine(Path.GetTempPath(), "Recap-tests-" + Guid.NewGuid());
try
{
    var settings = AppSettingsStore.CreateDefaultSettings();
    settings.RecordingsFolder = root;
    settings.RecordingProcessing.PreserveRawFiles = true;
    var library = new RecordingLibrary(root);
    var folder = library.CreateSessionFolder(DateTime.Now);
    var metadata = new RecordingMetadata { Status = "completed", TranscriptionLanguage = "da-DK" };
    library.SaveMetadata(folder, metadata);
    foreach (var name in new[] { "remote.wav", "microphone.wav", "mixed.wav" })
    {
        using var writer = new WaveFileWriter(Path.Combine(folder, name), new WaveFormat(16000, 16, 1));
        writer.Write(new byte[32000], 0, 32000);
    }
    var item = new RecordingListItem(folder, metadata);
    Check(!item.CanOpen, "Unrecognized meeting must be locked");
    Check(item.PrimaryActionText == "Process" && item.CanUsePrimaryAction, "Unprocessed meeting must offer Process");
    item.SetManuallyQueued(true);
    Check(item.PrimaryActionText == "Queued" && !item.CanUsePrimaryAction, "Queued meeting must reject duplicate requests");
    item.SetManuallyQueued(false);
    var provider = new FakeProvider { FailMicrophone = true };
    var updates = new ConcurrentQueue<MeetingProcessingProgress>();
    var progress = new InlineProgress<MeetingProcessingProgress>(updates.Enqueue);
    var service = new MeetingRecognitionService(library, settings, provider);
    try
    {
        await service.ProcessAsync(item, progress, CancellationToken.None);
        throw new Exception("Expected a microphone failure");
    }
    catch (InvalidOperationException ex) when (ex.Message == "Microphone test failure") { }
    Check(provider.MaxConcurrent == 2, "Both tracks must execute concurrently");
    Check(File.Exists(Path.Combine(folder, "raw-remote-transcript.json")), "Successful track must survive sibling failure");
    Check(!item.CanOpen, "Partial recognition must remain locked");

    provider.FailMicrophone = false;
    await service.ProcessAsync(item, progress, CancellationToken.None);
    Check(provider.RemoteCalls == 1 && provider.MicrophoneCalls == 2, "Retry must only submit missing track");
    Check(item.CanOpen, "Completed meeting must unlock");
    Check(item.PrimaryActionText == "Open", "Completed meeting must offer Open");
    Check(metadata.Files.Mixed == "mixed.wav", "Existing mixed file must be linked in metadata");
    Check(updates.Any(x => x.Percent > 0 && x.Percent < 100), "Intermediate recognition progress must be reported");
    Check(updates.Last().Percent == 100, "Finished recognition must report 100%");
    await service.ProcessAsync(item, progress, CancellationToken.None);
    Check(provider.RemoteCalls == 1 && provider.MicrophoneCalls == 2, "Reopening must not resubmit completed tracks");
    item.UpdateProcessing("Busy", true);
    Check(!item.CanOpen, "Processing meeting must remain locked");
    item.UpdateProcessing(null, false);
    File.Delete(Path.Combine(folder, "raw-microphone-transcript.json"));
    Check(!item.CanOpen, "Deleting a result must lock the meeting again");
    using var cancellation = new CancellationTokenSource();
    cancellation.Cancel();
    try
    {
        await service.ProcessAsync(item, progress, cancellation.Token);
        throw new Exception("Expected cancellation");
    }
    catch (OperationCanceledException) { }
    Check(provider.MicrophoneCalls == 2, "Canceled work must not submit audio");
    provider.BlockMicrophone = true;
    using var pause = new CancellationTokenSource();
    var activeRecognition = service.ProcessAsync(item, progress, pause.Token);
    await provider.MicrophoneBlocked.Task.WaitAsync(TimeSpan.FromSeconds(5));
    pause.Cancel();
    try
    {
        await activeRecognition.WaitAsync(TimeSpan.FromSeconds(5));
        throw new Exception("Expected active recognition to stop");
    }
    catch (OperationCanceledException) { }
    Check(!File.Exists(Path.Combine(folder, "raw-microphone-transcript.json")), "Paused track must not publish incomplete results");
    Check(File.Exists(Path.Combine(folder, "raw-remote-transcript.json")), "Pause must preserve completed tracks");
    provider.BlockMicrophone = false;
    await service.ProcessAsync(item, progress, CancellationToken.None);
    Check(item.CanOpen && provider.RemoteCalls == 1, "Resume must finish without repeating completed tracks");
    settings.SpeechRecognitionPaused = true;
    var savedSettings = System.Text.Json.JsonSerializer.Serialize(settings);
    Check(System.Text.Json.JsonSerializer.Deserialize<AppSettings>(savedSettings)!.SpeechRecognitionPaused,
        "Pause preference must survive settings serialization");
    File.WriteAllText(Path.Combine(folder, "final-transcript.md"), "Old transcript");
    File.WriteAllText(Path.Combine(folder, "speaker-map.json"), "{}");
    MeetingRecognitionService.ResetSpeechResults(library, item, "en-US");
    Check(item.Metadata.TranscriptionLanguage == "en-US" && !item.CanOpen, "Reprocessing must reset readiness and save language");
    Check(File.Exists(Path.Combine(folder, "remote.wav")) && File.Exists(Path.Combine(folder, "mixed.wav")), "Reprocessing must keep audio");
    Check(!File.Exists(Path.Combine(folder, "final-transcript.md")) && !File.Exists(Path.Combine(folder, "speaker-map.json")), "Reprocessing must invalidate dependent results");
    RunWindowSmokeTest(settings);
    Console.WriteLine("PASS: parallel tracks, progress, partial failure/retry, disk readiness, active pause/resume, saved pause preference, meeting-row rendering.");
}
finally
{
    if (Directory.Exists(root)) Directory.Delete(root, true);
}

static void Check(bool condition, string message)
{
    if (!condition) throw new Exception(message);
}

static void RunWindowSmokeTest(AppSettings settings)
{
    Exception? failure = null;
    var thread = new Thread(() =>
    {
        Recap.MainWindow? window = null;
        Recap.App? app = null;
        try
        {
            app = new Recap.App();
            app.InitializeComponent();
            // An unfinished meeting must not start work in the window constructor.
            settings.SpeechRecognitionPaused = false;
            window = new Recap.MainWindow(new AppSettingsStore(), settings)
            {
                ShowInTaskbar = false,
                ShowActivated = false,
                Left = -10000,
                Top = -10000,
                WindowStartupLocation = System.Windows.WindowStartupLocation.Manual
            };
            Check(!window.Recordings.Single().IsProcessing, "Processing must wait until the window has rendered");
            // Pause before showing so this rendering test never submits audio to Azure.
            settings.SpeechRecognitionPaused = true;
            typeof(Recap.MainWindow).GetMethod("UpdateAutomaticProcessingControl",
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.Invoke(window, null);
            window.Show();
            window.UpdateLayout();
            var row = window.Recordings.Single();
            row.UpdateProcessing("Recognition test", true, 50);
            window.UpdateLayout();
            var bar = FindProgressBar(window);
            Check(bar is not null && bar.Value == 50, "Meeting row must render and display live progress");
            var expandedWidth = window.ActualWidth;
            var toggle = (System.Windows.Controls.Button)window.FindName("MeetingsToggleButton");
            toggle.RaiseEvent(new System.Windows.RoutedEventArgs(System.Windows.Controls.Button.ClickEvent));
            window.UpdateLayout();
            Check(((System.Windows.FrameworkElement)window.FindName("MeetingsPanel")).Visibility == System.Windows.Visibility.Collapsed
                && window.ActualWidth < 500 && row.IsProcessing, "Compact mode must hide meetings without stopping processing");
            Check(toggle.ToolTip.ToString() == "Show meetings"
                && System.Windows.Automation.AutomationProperties.GetName(toggle) == "Show meetings",
                "Compact mode must label the show-meetings icon accessibly");
            toggle.RaiseEvent(new System.Windows.RoutedEventArgs(System.Windows.Controls.Button.ClickEvent));
            window.UpdateLayout();
            Check(((System.Windows.FrameworkElement)window.FindName("MeetingsPanel")).Visibility == System.Windows.Visibility.Visible
                && Math.Abs(window.ActualWidth - expandedWidth) < 1, "Expand must restore the meeting list and previous width");
            row.UpdateProcessing(null, false);
            var action = FindMeetingAction(window);
            Check(action is not null && action.Content.ToString() == "Process" && action.IsEnabled,
                "Paused meeting must render an enabled Process button");
            action!.RaiseEvent(new System.Windows.RoutedEventArgs(System.Windows.Controls.Button.ClickEvent));
            Check(row.IsManuallyQueued && settings.SpeechRecognitionPaused,
                "Process must queue only the selected meeting without enabling automatic processing");
            Check(!action.IsEnabled && action.Content.ToString() == "Queued", "Queued button must prevent duplicate submission");
            row.SetManuallyQueued(false);
            row.UpdateProcessing("Test failure", false, failed: true);
            Check(action.Content.ToString() == "Retry", "Failed meeting must offer Retry");
            row.UpdateProcessing(null, false);
            if (Environment.GetEnvironmentVariable("RECAP_TEST_SCREENSHOT") is string screenshot)
            {
                row.Metadata.Title = "Weekly planning";
                row.Metadata.StartedLocal = new DateTime(2026, 10, 2, 11, 0, 0);
                row.Metadata.EndedLocal = row.Metadata.StartedLocal.AddMinutes(12);
                row.UpdateProcessing(null, false);
                window.Width = 760;
                window.UpdateLayout();
                var bitmap = new System.Windows.Media.Imaging.RenderTargetBitmap((int)window.ActualWidth,
                    (int)window.ActualHeight, 96, 96, System.Windows.Media.PixelFormats.Pbgra32);
                bitmap.Render(window);
                var encoder = new System.Windows.Media.Imaging.PngBitmapEncoder();
                encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(bitmap));
                using var output = File.Create(screenshot);
                encoder.Save(output);
            }
            CheckStableFocusLayout(window);
            CheckSettingsWindow(settings);
            CheckDetailWindow(window.Recordings.Single(), settings);
        }
        catch (Exception ex) { failure = ex; }
        finally { window?.Close(); app?.Shutdown(); }
    });
    thread.SetApartmentState(ApartmentState.STA);
    thread.Start();
    if (!thread.Join(TimeSpan.FromSeconds(15))) throw new Exception("Window smoke test timed out");
    if (failure is not null) throw new Exception("Window smoke test failed", failure);
}

static void CheckSettingsWindow(AppSettings settings)
{
    var dialog = new Recap.SettingsWindow(new AppSettingsStore(), settings)
    {
        ShowInTaskbar = false, ShowActivated = false, Left = -10000, Top = -10000,
        WindowStartupLocation = System.Windows.WindowStartupLocation.Manual
    };
    try
    {
        dialog.Show();
        dialog.UpdateLayout();
        CheckStableFocusLayout(dialog);
        var navigation = (System.Windows.Controls.ListBox)dialog.FindName("SettingsNavigation");
        Check(navigation.Items.Count == 3, "Settings must have three consolidated pages");
        foreach (var name in new[] { "ProfileSection", "StorageSection", "DevicesSection" })
            Check(((System.Windows.FrameworkElement)dialog.FindName(name)).Visibility == System.Windows.Visibility.Visible,
                "General must include profile, storage and devices");
        var automatic = (System.Windows.Controls.CheckBox)dialog.FindName("AutomaticProcessingBox");
        Check(automatic.IsChecked == !settings.SpeechRecognitionPaused, "Automatic processing must reflect the saved preference");
        navigation.SelectedIndex = 1;
        Check(((System.Windows.FrameworkElement)dialog.FindName("SpeechSection")).Visibility == System.Windows.Visibility.Visible
            && ((System.Windows.FrameworkElement)dialog.FindName("StorageSection")).Visibility == System.Windows.Visibility.Collapsed,
            "Speech page must hide all General sections");
        navigation.SelectedIndex = 2;
        Check(((System.Windows.FrameworkElement)dialog.FindName("LlmSection")).Visibility == System.Windows.Visibility.Visible,
            "Transcript and summary must remain a separate page");
    }
    finally { dialog.Close(); }
}

static void CheckDetailWindow(RecordingListItem item, AppSettings settings)
{
    File.WriteAllText(Path.Combine(item.Folder, "raw-remote-transcript.json"), "[]");
    File.WriteAllText(Path.Combine(item.Folder, "raw-microphone-transcript.json"), "[]");
    File.WriteAllText(Path.Combine(item.Folder, "speaker-map.json"),
        "{\"Speakers\":[{\"SourceLabel\":\"Speaker 1\",\"DisplayName\":\"Alex\"}]}");
    var detail = new Recap.RecordingDetailWindow(new RecordingLibrary(settings.RecordingsFolder), item, (_, _) => { })
    {
        ShowInTaskbar = false, ShowActivated = false, Left = -10000, Top = -10000,
        WindowStartupLocation = System.Windows.WindowStartupLocation.Manual
    };
    try
    {
        detail.Show();
        detail.UpdateLayout();
        CheckStableFocusLayout(detail);
        var confirm = (System.Windows.Controls.Button)detail.FindName("SaveNamesButton");
        Check(!confirm.IsEnabled, "Saved speaker names must disable confirmation");
        detail.Speakers[0].DisplayName = "Sam";
        Check(confirm.IsEnabled, "Editing a speaker name must re-enable confirmation");
        confirm.RaiseEvent(new System.Windows.RoutedEventArgs(System.Windows.Controls.Button.ClickEvent));
        Check(!confirm.IsEnabled, "Saving names must disable confirmation again");
        var language = (System.Windows.Controls.ListBox)detail.FindName("LanguageBox");
        Check(language.IsEnabled, "Recognized meetings must allow a new language selection");
        item.UpdateProcessing("Recognizing speech", true, 42);
        var progress = (System.Windows.Controls.ProgressBar)detail.FindName("SpeechProgress");
        Check(progress.Visibility == System.Windows.Visibility.Visible && progress.Value == 42,
            "Details must follow shared processing progress");
        Check(((System.Windows.Controls.TextBlock)detail.FindName("SttStatusText")).Visibility == System.Windows.Visibility.Collapsed,
            "Progress must replace status text during recognition");
        detail.Close();
        Check(item.IsProcessing, "Closing details must not cancel shared processing");
        item.UpdateProcessing(null, false);
    }
    finally { detail.Close(); }
}

static void CheckStableFocusLayout(System.Windows.Window window)
{
    foreach (var control in Descendants(window).OfType<System.Windows.Controls.Control>()
        .Where(x => x is System.Windows.Controls.Button or System.Windows.Controls.ListBoxItem).ToArray())
    {
        if (!control.IsEnabled || !control.IsVisible) continue;
        var border = new[] { "Root", "Option", "NavigationItem" }
            .Select(name => control.Template?.FindName(name, control)).OfType<System.Windows.Controls.Border>().FirstOrDefault();
        if (border is null) continue;
        var thickness = border.BorderThickness;
        var size = control.RenderSize;
        var child = border.Child as System.Windows.FrameworkElement;
        var contentPosition = child?.TranslatePoint(new System.Windows.Point(), control);
        control.Focus();
        window.UpdateLayout();
        Check(border.BorderThickness == thickness && control.RenderSize == size
            && child?.TranslatePoint(new System.Windows.Point(), control) == contentPosition,
            "Button and language/navigation focus must not add borders or move content");
    }
}

static IEnumerable<System.Windows.DependencyObject> Descendants(System.Windows.DependencyObject root)
{
    for (var i = 0; i < System.Windows.Media.VisualTreeHelper.GetChildrenCount(root); i++)
    {
        var child = System.Windows.Media.VisualTreeHelper.GetChild(root, i);
        yield return child;
        foreach (var descendant in Descendants(child)) yield return descendant;
    }
}

static System.Windows.Controls.Button? FindMeetingAction(System.Windows.DependencyObject root)
{
    if (root is System.Windows.Controls.Button button && button.DataContext is RecordingListItem
        && button.Content is string text && text is "Process" or "Open" or "Retry" or "Queued" or "Processing…") return button;
    for (var i = 0; i < System.Windows.Media.VisualTreeHelper.GetChildrenCount(root); i++)
    {
        var found = FindMeetingAction(System.Windows.Media.VisualTreeHelper.GetChild(root, i));
        if (found is not null) return found;
    }
    return null;
}

static System.Windows.Controls.ProgressBar? FindProgressBar(System.Windows.DependencyObject root)
{
    if (root is System.Windows.Controls.ProgressBar bar
        && System.Windows.Automation.AutomationProperties.GetName(bar) == "Speech recognition progress") return bar;
    for (var i = 0; i < System.Windows.Media.VisualTreeHelper.GetChildrenCount(root); i++)
    {
        var found = FindProgressBar(System.Windows.Media.VisualTreeHelper.GetChild(root, i));
        if (found is not null) return found;
    }
    return null;
}

sealed class InlineProgress<T>(Action<T> report) : IProgress<T>
{
    public void Report(T value) => report(value);
}

sealed class FakeProvider : ITranscriptionProvider
{
    private readonly TaskCompletionSource _bothStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int _active;
    public int MaxConcurrent;
    public int RemoteCalls;
    public int MicrophoneCalls;
    public bool FailMicrophone;
    public bool BlockMicrophone;
    public TaskCompletionSource MicrophoneBlocked { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public string Id => "test";
    public string DisplayName => "Offline test";

    public async Task<TrackTranscriptResult> TranscribeAsync(TrackTranscriptionRequest request, CancellationToken cancellationToken = default)
    {
        var active = Interlocked.Increment(ref _active);
        if (active == 2) { MaxConcurrent = 2; _bothStarted.TrySetResult(); }
        if (request.SourceTrack == "remote") Interlocked.Increment(ref RemoteCalls);
        else Interlocked.Increment(ref MicrophoneCalls);
        try
        {
            await _bothStarted.Task.WaitAsync(TimeSpan.FromSeconds(5), cancellationToken);
            request.Progress?.Report(TimeSpan.FromSeconds(0.5));
            if (request.SourceTrack == "microphone" && BlockMicrophone)
            {
                MicrophoneBlocked.TrySetResult();
                await Task.Delay(Timeout.Infinite, cancellationToken);
            }
            if (request.SourceTrack == "microphone" && FailMicrophone)
                throw new InvalidOperationException("Microphone test failure");
            return new(request.SourceTrack, request.SpeakerLabel, [], "[]");
        }
        finally { Interlocked.Decrement(ref _active); }
    }
}
