using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using NAudio.Wave;
using Recap;
using Recap.Recording;
using Recap.Settings;
using Recap.Transcription;

/// <summary>Call on the existing smoke-test STA with its WPF Application. Fixtures only.</summary>
internal static class SpeakerPaginationTests
{
    internal static void Check(AppSettings settings, string root)
    {
        if (Thread.CurrentThread.GetApartmentState() != ApartmentState.STA)
            throw new InvalidOperationException("Speaker pagination smoke requires STA.");
        var fixtures = Path.Combine(root, "speaker-pagination-" + Guid.NewGuid().ToString("N"));
        try
        {
            using (var fixture = Open(settings, fixtures, "six", "azure-speech", 6, 4))
            {
                var window = fixture.Window;
                Assert(window.Speakers.Count == 6, "Transcript roster must restore all six speakers from a stale four-person map");
                for (var i = 0; i < 4; i++) Assert(window.Speakers[i].DisplayName == "Saved " + (i + 1), "Existing saved names must survive reconciliation");
                Assert(Visible(window).Length == 3, "First page shows exactly three speakers");
                Assert(window.FindName("SpeakerPageText") is null, "Page range text is not displayed");
                Assert(Status(window).Contains("4 of 6 named"), "Named count covers full roster, not visible page");
                Assert(!Button(window, "SaveNamesButton").IsEnabled, "Four named speakers cannot confirm six-person roster");
                var first = window.Speakers[0];
                EditName(window, first, "Edited on first page");
                Click(window, "NextSpeakerPageButton");
                Assert(Visible(window).Length == 3 && ReferenceEquals(Visible(window)[0], window.Speakers[3]), "Second page shows speakers four through six");
                Assert(window.VisibleSpeakers[0].SourceLabel == "Speaker 4", "Second page starts at speaker four");
                Assert(!Button(window, "SaveNamesButton").IsEnabled, "Confirmation still disabled with unnamed off-page speakers");
                Click(window, "PreviousSpeakerPageButton");
                Assert(ReferenceEquals(Visible(window)[0], first) && NameBox(window, first).Text == "Edited on first page", "Paging preserves edits and speaker instances");
                Click(window, "NextUnnamedSpeakerButton");
                Assert(Visible(window).Contains(window.Speakers[4]), "Next unnamed navigates to fifth speaker's page");
                AssertFocused(window, window.Speakers[4]);
                EditName(window, window.Speakers[4], "Fifth person");
                EditName(window, window.Speakers[5], window.Speakers[5].SourceLabel);
                Assert(Status(window).Contains("5 of 6 named") && !Button(window, "SaveNamesButton").IsEnabled,
                    "Azure's original source label is not a confirmed human name");
                Click(window, "NextUnnamedSpeakerButton");
                AssertFocused(window, window.Speakers[5]);
                EditName(window, window.Speakers[5], "Sixth person");
                Assert(Status(window).Contains("6 of 6 named") && Button(window, "SaveNamesButton").IsEnabled, "All six valid names enable confirmation");
                Assert(!Button(window, "NextUnnamedSpeakerButton").IsEnabled, "Next unnamed disabled when everyone is named");
                Click(window, "SaveNamesButton");
                Assert(!Button(window, "SaveNamesButton").IsEnabled && Status(window).Contains("Saved"), "Confirmed roster is marked saved");
                using var map = JsonDocument.Parse(File.ReadAllText(Path.Combine(fixture.Folder, "speaker-map.json")));
                Assert(map.RootElement.GetProperty("Speakers").GetArrayLength() == 6, "Saved map contains all pages");
                Assert(File.ReadAllText(Path.Combine(fixture.Folder, "speaker-map.json")).Contains("Edited on first page"), "First-page edit persists when saving from page two");
            }
            foreach (var count in new[] { 0, 1, 4 })
            {
                using var fixture = Open(settings, fixtures, "bounds-" + count, "azure-speech", count, 0);
                var window = fixture.Window;
                Assert(Visible(window).Length == Math.Min(3, count), "First-page item count at roster boundary");
                Assert(!Button(window, "PreviousSpeakerPageButton").IsEnabled, "Cannot move before first page");
                var firstPage = Visible(window);
                Click(window, "PreviousSpeakerPageButton");
                Assert(Visible(window).SequenceEqual(firstPage), "Previous handler clamps at lower bound even if invoked directly");
                Assert(Button(window, "NextSpeakerPageButton").IsEnabled == (count > 3), "Next availability follows remaining speakers");
                Assert(!Button(window, "SaveNamesButton").IsEnabled, "Empty/unnamed roster cannot confirm");
                if (count == 4)
                {
                    Click(window, "NextSpeakerPageButton");
                    Assert(Visible(window).Length == 1 && ReferenceEquals(Visible(window)[0], window.Speakers[3]), "Four-person roster has one speaker on final page");
                    Assert(!Button(window, "NextSpeakerPageButton").IsEnabled && Button(window, "PreviousSpeakerPageButton").IsEnabled, "Final page navigation bounds");
                    Click(window, "NextSpeakerPageButton");
                    Assert(Visible(window).Select(speaker => speaker.SourceLabel).SequenceEqual(new[] { "Speaker 4" }), "Next handler clamps at upper bound");
                    Click(window, "PreviousSpeakerPageButton");
                    Assert(Visible(window).Length == 3, "Returning from short final page restores full first page");
                }
                if (count == 0) Assert(!Button(window, "NextUnnamedSpeakerButton").IsEnabled, "No unnamed target in empty roster");
            }
            foreach (var provider in new[] { "local-whisper", "grok-speech" })
            {
                using var fixture = Open(settings, fixtures, provider, provider, 1, 0);
                var window = fixture.Window;
                Assert(window.Speakers.Single().DisplayName == window.Speakers.Single().SourceLabel,
                    "Local/Grok default speaker label retained");
                Assert(Status(window).Contains("1 of 1 named") && !Button(window, "NextUnnamedSpeakerButton").IsEnabled,
                    "Local/Grok source labels count as valid names");
            }
            Console.WriteLine("PASS: speaker roster reconciliation, three-per-page navigation, persistent edits, next-unnamed focus, full-roster confirmation and provider-specific name validity.");
        }
        finally { if (Directory.Exists(fixtures)) Directory.Delete(fixtures, true); }
    }

    private static Fixture Open(AppSettings settings, string root, string name, string provider, int count, int saved)
    {
        var library = new RecordingLibrary(Path.Combine(root, name));
        var folder = library.CreateSessionFolder(DateTime.Now);
        var metadata = new RecordingMetadata { Status = "completed", TranscriptionLanguage = "en-US", TranscriptionProviderId = provider };
        library.SaveMetadata(folder, metadata);
        foreach (var file in new[] { "remote.wav", "microphone.wav", "mixed.wav" })
        {
            using var writer = new WaveFileWriter(Path.Combine(folder, file), new WaveFormat(16000, 16, 1));
            writer.Write(new byte[32000], 0, 32000);
        }
        var segments = Enumerable.Range(1, count).Select(i => new TranscriptSegment(TimeSpan.Zero,
            TimeSpan.FromSeconds(1), "Speaker " + i, "Synthetic spoken sample " + i, "remote")).ToArray();
        File.WriteAllText(Path.Combine(folder, "raw-remote-transcript.json"), JsonSerializer.Serialize(segments));
        File.WriteAllText(Path.Combine(folder, "raw-microphone-transcript.json"), "[]");
        if (saved > 0) File.WriteAllText(Path.Combine(folder, "speaker-map.json"), JsonSerializer.Serialize(new
        {
            Speakers = Enumerable.Range(1, saved).Select(i => new { SourceLabel = "Speaker " + i, DisplayName = "Saved " + i })
        }));
        var window = new RecordingDetailWindow(library, new RecordingListItem(folder, metadata),
            (_, _) => throw new Exception("Pagination must never request reprocessing"), settings)
        {
            ShowInTaskbar = false, ShowActivated = false, Left = -10000, Top = -10000,
            WindowStartupLocation = WindowStartupLocation.Manual
        };
        try { window.Show(); Pump(window); return new Fixture(window, folder); }
        catch { window.Close(); throw; }
    }

    private sealed class Fixture(RecordingDetailWindow window, string folder) : IDisposable
    {
        internal RecordingDetailWindow Window => window;
        internal string Folder => folder;
        public void Dispose() => window.Close();
    }
    private static SpeakerNameItem[] Visible(RecordingDetailWindow window) => ((ItemsControl)window.FindName("SpeakersList")).Items.Cast<SpeakerNameItem>().ToArray();
    private static string Status(RecordingDetailWindow window) => ((TextBlock)window.FindName("SpeakerStatusText")).Text;
    private static Button Button(RecordingDetailWindow window, string name) => (Button)(window.FindName(name) ?? throw new Exception("Missing speaker control: " + name));
    private static void Click(RecordingDetailWindow window, string name)
    {
        Button(window, name).RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Button.ClickEvent));
        Pump(window);
    }
    private static TextBox NameBox(RecordingDetailWindow window, SpeakerNameItem speaker) => Descendants(window).OfType<TextBox>()
        .Single(box => ReferenceEquals(box.DataContext, speaker) && box.GetBindingExpression(TextBox.TextProperty)?.ParentBinding.Path.Path == "DisplayName");
    private static void EditName(RecordingDetailWindow window, SpeakerNameItem speaker, string name)
    {
        var box = NameBox(window, speaker);
        box.Text = name;
        box.GetBindingExpression(TextBox.TextProperty)!.UpdateSource();
        Pump(window);
    }
    private static void AssertFocused(RecordingDetailWindow window, SpeakerNameItem speaker)
    {
        var focused = FocusManager.GetFocusedElement(window) as FrameworkElement ?? Keyboard.FocusedElement as FrameworkElement;
        Assert(focused is TextBox && ReferenceEquals(focused.DataContext, speaker), "Next unnamed focuses the target name field");
    }
    private static void Pump(Window window)
    {
        window.UpdateLayout();
        var frame = new DispatcherFrame();
        window.Dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, new Action(() => frame.Continue = false));
        Dispatcher.PushFrame(frame);
        window.UpdateLayout();
    }
    private static IEnumerable<DependencyObject> Descendants(DependencyObject root)
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            yield return child;
            foreach (var nested in Descendants(child)) yield return nested;
        }
    }
    private static void Assert(bool value, string message) { if (!value) throw new Exception("Speaker pagination: " + message); }
}
