using System.IO;
using System.Reflection;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Recap;
using Recap.Recording;

/// <summary>STA synthetic-window regression. Never starts capture or opens rename dialogs.</summary>
internal static class TitleEditingTests
{
    internal static void Check(MainWindow window)
    {
        var initial = window.Recordings.Single();
        Assert(!initial.IsProcessing && !initial.IsManuallyQueued, "Title fixture starts idle");
        var folder = initial.Folder;
        var originalTitle = initial.Title;
        var originalStatus = initial.Metadata.Status;
        try
        {
            var editor = Editor(initial, "  Updated overview title  ");
            Save(window, editor);
            Assert(editor.Text == "Updated overview title" && PersistedTitle(folder) == editor.Text, "Idle overview edit trims and persists metadata title");
            Assert(window.Recordings.Single().Folder == folder && Directory.Exists(folder), "Renaming must preserve stable recording folder");

            foreach (var invalid in new[] { "   ", new string('x', 201), "Invalid\tcontrol" })
            {
                var current = window.Recordings.Single();
                var rejected = Editor(current, invalid);
                Save(window, rejected);
                Assert(rejected.Text == current.Title && PersistedTitle(folder) == current.Title, "Invalid title restores saved name without writing");
            }

            var row = window.Recordings.Single();
            row.UpdateProcessing("Synthetic processing", true);
            RejectBusy(window, row, folder);
            row.UpdateProcessing(null, false);
            row.SetManuallyQueued(true);
            RejectBusy(window, row, folder);
            row.SetManuallyQueued(false);
            row.Metadata.Status = "recording";
            RejectBusy(window, row, folder);
            row.Metadata.Status = originalStatus;

            var escape = Editor(row, "Discard this draft");
            var source = PresentationSource.FromVisual(window) ?? throw new Exception("Expected shown smoke window");
            var escapeKey = new KeyEventArgs(Keyboard.PrimaryDevice, source, Environment.TickCount, Key.Escape)
                { RoutedEvent = Keyboard.PreviewKeyDownEvent };
            Invoke(window, "MeetingTitle_PreviewKeyDown", escape, escapeKey);
            Assert(escapeKey.Handled && escape.Text == row.Title && PersistedTitle(folder) == row.Title,
                "Escape restores original title without committing draft");

            var enter = Editor(row, "Enter committed title");
            var enterKey = new KeyEventArgs(Keyboard.PrimaryDevice, source, Environment.TickCount, Key.Enter)
                { RoutedEvent = Keyboard.PreviewKeyDownEvent };
            Invoke(window, "MeetingTitle_PreviewKeyDown", enter, enterKey);
            Assert(enterKey.Handled && PersistedTitle(folder) == "Enter committed title", "Enter commits overview title");

            var currentRow = window.Recordings.Single();
            var blur = Editor(currentRow, "Focus loss committed title");
            var blurEvent = new KeyboardFocusChangedEventArgs(Keyboard.PrimaryDevice, Environment.TickCount, blur, null);
            Invoke(window, "MeetingTitle_LostKeyboardFocus", blur, blurEvent);
            Assert(PersistedTitle(folder) == "Focus loss committed title", "Focus loss commits overview title");

            // Refresh replaces idle rows. A delayed editor for a removed row cannot overwrite the current title.
            if (!window.Recordings.Contains(initial))
            {
                var stale = Editor(initial, "Stale editor overwrite");
                Save(window, stale);
                Assert(PersistedTitle(folder) == "Focus loss committed title", "Removed row's stale editor must not overwrite persisted title");
            }
            Assert(window.Recordings.Single().Folder == folder, "All edit paths keep technical folder ID");
            CheckActiveMetadata(window, Path.GetDirectoryName(folder)!);
            Console.WriteLine("PASS: overview title persistence, stable folder, validation/busy guards, keyboard/focus commit and active-recording metadata persistence/stop retention.");
        }
        finally
        {
            var row = window.Recordings.Single();
            row.Metadata.Status = originalStatus;
            row.SetManuallyQueued(false);
            row.UpdateProcessing(null, false);
            Save(window, Editor(row, originalTitle));
        }
    }

    private static void CheckActiveMetadata(MainWindow window, string root)
    {
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
        var sessionField = typeof(MainWindow).GetField("_session", flags)!;
        var startedField = typeof(MainWindow).GetField("_recordingStarted", flags)!;
        var stoppingField = typeof(MainWindow).GetField("_recordingStopTask", flags)!;
        var oldSession = sessionField.GetValue(window);
        var oldStarted = startedField.GetValue(window);
        var oldStopping = stoppingField.GetValue(window);
        var languageField = typeof(MainWindow).GetField("_lastRecordingLanguage", flags)!;
        var refreshField = typeof(MainWindow).GetField("_refreshingRecordingFields", flags)!;
        var languageBox = (ListBox)window.FindName("RecordingLanguageBox");
        var oldLanguage = languageField.GetValue(window);
        var oldSelection = languageBox.SelectedValue;
        var testRoot = Path.Combine(root, "active-metadata-test-" + Guid.NewGuid().ToString("N"));
        var library = new RecordingLibrary(testRoot);
        var folder = library.CreateSessionFolder(DateTime.Now);
        var metadata = new RecordingMetadata { Title = "Synthetic active recording", Status = "recording", TranscriptionLanguage = "en-US" };
        library.SaveMetadata(folder, metadata);
        // Constructor does not acquire devices; never call RecordingSession.Start.
        using var session = new RecordingSession(library);
        typeof(RecordingSession).GetField("_metadata", flags)!.SetValue(session, metadata);
        typeof(RecordingSession).GetField("_folder", flags)!.SetValue(session, folder);
        bool SaveActive(string name, string value) => (bool)(typeof(MainWindow).GetMethod(name, flags)
            ?? throw new Exception("Missing active metadata helper: " + name)).Invoke(window, [value])!;
        bool Editable() => (bool)typeof(MainWindow).GetProperty("CanEditActiveRecording", flags)!.GetValue(window)!;
        try
        {
            sessionField.SetValue(window, session);
            startedField.SetValue(window, DateTime.Now);
            stoppingField.SetValue(window, Task.CompletedTask);
            Invoke(window, "RefreshActiveRecordingFields");
            Assert(Editable(), "Synthetic active recording permits metadata edits without capture");
            var activeEditor = (TextBox)window.FindName("ActiveRecordingTitleBox");
            Assert(activeEditor.IsEnabled, "Active title field enables during recording");
            var source = PresentationSource.FromVisual(window)!;
            activeEditor.Text = "Discard active draft";
            var escape = new KeyEventArgs(Keyboard.PrimaryDevice, source, Environment.TickCount, Key.Escape) { RoutedEvent = Keyboard.KeyDownEvent };
            Invoke(window, "ActiveRecordingTitle_KeyDown", activeEditor, escape);
            Assert(escape.Handled && activeEditor.Text == metadata.Title && PersistedTitle(folder) == metadata.Title,
                "Escape discards an active recording title draft");
            activeEditor.Text = "Active Enter title";
            var enter = new KeyEventArgs(Keyboard.PrimaryDevice, source, Environment.TickCount, Key.Enter) { RoutedEvent = Keyboard.KeyDownEvent };
            Invoke(window, "ActiveRecordingTitle_KeyDown", activeEditor, enter);
            Assert(enter.Handled && PersistedTitle(folder) == "Active Enter title", "Enter commits active title without restarting capture");
            Assert(SaveActive("TrySaveActiveRecordingTitle", "Updated active title"), "Active title helper accepts valid name");
            Assert(metadata.Title == "Updated active title" && PersistedTitle(folder) == metadata.Title,
                "Active title updates session metadata and durable metadata file");
            Assert(SaveActive("TrySaveActiveRecordingLanguage", "da-DK"), "Active language helper accepts Danish");
            using (var saved = JsonDocument.Parse(File.ReadAllText(Path.Combine(folder, "metadata.json"))))
                Assert(saved.RootElement.GetProperty("TranscriptionLanguage").GetString() == "da-DK", "Active language persists immediately");
            Assert(!SaveActive("TrySaveActiveRecordingTitle", "   ") && !SaveActive("TrySaveActiveRecordingTitle", new string('x', 201))
                && !SaveActive("TrySaveActiveRecordingLanguage", "fr-FR"), "Invalid active metadata is rejected without modal or save");
            Assert(metadata.Title == "Updated active title" && metadata.TranscriptionLanguage == "da-DK", "Rejected active edits preserve prior values");
            stoppingField.SetValue(window, new TaskCompletionSource().Task);
            Invoke(window, "RefreshActiveRecordingFields");
            Assert(!activeEditor.IsEnabled, "Active title editor disables while stop is in progress");
            Assert(!Editable() && !SaveActive("TrySaveActiveRecordingTitle", "Too late")
                && !SaveActive("TrySaveActiveRecordingLanguage", "en-US"), "Stop in progress blocks active metadata edits");
            stoppingField.SetValue(window, Task.CompletedTask);
            session.Stop(); // No capture/timeline exists; only finalizes the synthetic metadata.
            Assert(metadata.Status == "completed" && metadata.Title == "Updated active title"
                && metadata.TranscriptionLanguage == "da-DK" && session.Folder == folder,
                "Stopping retains updated title/language and stable recording folder");
            Assert(PersistedTitle(folder) == "Updated active title", "Stop persists edited active title");
        }
        finally
        {
            sessionField.SetValue(window, oldSession);
            startedField.SetValue(window, oldStarted);
            stoppingField.SetValue(window, oldStopping);
            Invoke(window, "RefreshActiveRecordingFields");
            refreshField.SetValue(window, true);
            try
            {
                languageField.SetValue(window, oldLanguage);
                languageBox.SelectedValue = oldSelection;
            }
            finally { refreshField.SetValue(window, false); }
            if (Directory.Exists(testRoot)) Directory.Delete(testRoot, true);
        }
    }

    private static void RejectBusy(MainWindow window, RecordingListItem row, string folder)
    {
        var title = PersistedTitle(folder);
        var editor = Editor(row, "Busy edit must not persist");
        Save(window, editor);
        Assert(editor.Text == row.Title && PersistedTitle(folder) == title, "Busy, queued or recording row blocks overview rename");
    }
    private static TextBox Editor(RecordingListItem row, string text) => new() { DataContext = row, Text = text };
    private static void Save(MainWindow window, TextBox editor) => Invoke(window, "SaveOverviewTitle", editor);
    private static void Invoke(MainWindow window, string name, params object?[] arguments)
    {
        var method = typeof(MainWindow).GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new Exception("Missing title handler: " + name);
        try { method.Invoke(window, arguments); }
        catch (TargetInvocationException error) when (error.InnerException is not null)
        { System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(error.InnerException).Throw(); }
    }
    private static string PersistedTitle(string folder)
    {
        using var metadata = JsonDocument.Parse(File.ReadAllText(Path.Combine(folder, "metadata.json")));
        return metadata.RootElement.GetProperty("Title").GetString()!;
    }
    private static void Assert(bool value, string message) { if (!value) throw new Exception("Title editing: " + message); }
}
