using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using Recap;
using Recap.Recording;
using Recap.Settings;

/// <summary>Synthetic STA/library fixtures only. Never invokes UI deletion or confirmation.</summary>
internal static class OverviewDeleteTests
{
    internal static void Check(MainWindow window)
    {
        if (Thread.CurrentThread.GetApartmentState() != ApartmentState.STA)
            throw new InvalidOperationException("Overview delete smoke requires STA.");
        var row = window.Recordings.Single();
        Assert(!row.IsProcessing && !row.IsManuallyQueued, "Fixture starts idle");
        var originalStatus = row.Metadata.Status;
        var originalFailed = row.ProcessingFailed;
        var originalCancelled = row.ProcessingCancelled;
        var before = Directory.EnumerateFiles(row.Folder).ToDictionary(file => Path.GetFileName(file)!, File.ReadAllBytes);
        try
        {
            Pump(window);
            var trash = Descendants(window).OfType<Button>().Single(button =>
                ReferenceEquals(button.DataContext, row) && button.Name == "DeleteMeetingButton");
            Assert(trash.IsVisible && trash.IsEnabled && trash.Focusable, "Idle card exposes a keyboard-accessible delete button");
            Assert(AutomationProperties.GetName(trash).Contains("delete", StringComparison.OrdinalIgnoreCase), "Trash icon has descriptive accessible name");
            Assert(trash.ToolTip?.ToString()?.Contains("delete", StringComparison.OrdinalIgnoreCase) == true, "Trash icon has descriptive tooltip");
            CheckGuard(window, row, true);

            row.UpdateProcessing("Synthetic processing", true);
            Pump(window);
            Assert(!trash.IsEnabled, "Processing meeting cannot be deleted from overview");
            CheckGuard(window, row, false);
            row.RequestProcessingCancellation();
            Pump(window);
            Assert(!trash.IsEnabled, "Cancelling meeting remains protected until processing stops");
            CheckGuard(window, row, false);

            row.UpdateProcessing(null, false);
            row.SetManuallyQueued(true);
            Pump(window);
            Assert(!trash.IsEnabled, "Queued meeting cannot be deleted from overview");
            CheckGuard(window, row, false);
            row.SetManuallyQueued(false);
            Pump(window);
            Assert(trash.IsEnabled, "Idle delete availability returns after queue clears");
            CheckGuard(window, row, true);
            row.Metadata.Status = "recording";
            row.UpdateProcessing(null, false);
            Pump(window);
            Assert(trash.IsEnabled, "Interrupted recording metadata alone must not permanently disable deletion");
            CheckGuard(window, row, true);
            CheckLiveRecordingProtection(window, row);
            row.Metadata.Status = originalStatus;
            row.UpdateProcessing(null, false);
            Pump(window);

            var settings = (AppSettings)typeof(MainWindow).GetField("_settings", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(window)!;
            var library = new RecordingLibrary(Path.GetDirectoryName(row.Folder)!);
            var details = new RecordingDetailWindow(library, row,
                (_, _) => throw new Exception("Delete placement test must never reprocess a meeting"), settings)
            {
                ShowInTaskbar = false, ShowActivated = false, Left = -10000, Top = -10000,
                WindowStartupLocation = WindowStartupLocation.Manual
            };
            try
            {
                details.Show();
                Pump(details);
                Assert(details.FindName("DeleteMeetingButton") is null, "Old details delete control is removed");
                Assert(!Descendants(details).OfType<Button>().Any(button =>
                    button.Content?.ToString()?.Contains("Delete meeting", StringComparison.OrdinalIgnoreCase) == true
                    || AutomationProperties.GetName(button).Contains("Delete meeting", StringComparison.OrdinalIgnoreCase)),
                    "Details must not retain a duplicate meeting-delete action");
            }
            finally { details.Close(); }

            Assert(Directory.Exists(row.Folder), "Delete placement checks must retain synthetic meeting");
            var after = Directory.EnumerateFiles(row.Folder).ToArray();
            Assert(after.Length == before.Count && after.All(file => before.TryGetValue(Path.GetFileName(file), out var bytes)
                && bytes.SequenceEqual(File.ReadAllBytes(file))), "Delete placement checks must not alter meeting files");
            CheckInterruptedLibraryDeletion(Path.GetDirectoryName(row.Folder)!);
            Console.WriteLine("PASS: overview delete accessibility/busy guards, interrupted-vs-live recording protection, and opt-in synthetic interrupted deletion with WAV lock safety; no UI deletion invoked.");
        }
        finally
        {
            row.Metadata.Status = originalStatus;
            row.SetManuallyQueued(false);
            row.UpdateProcessing(null, false, failed: originalFailed);
            if (originalCancelled) row.MarkProcessingCancelled();
            Pump(window);
        }
    }

    private static void CheckLiveRecordingProtection(MainWindow window, RecordingListItem row)
    {
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
        var sessionField = typeof(MainWindow).GetField("_session", flags)!;
        var startedField = typeof(MainWindow).GetField("_recordingStarted", flags)!;
        var stopField = typeof(MainWindow).GetField("_recordingStopTask", flags)!;
        var savedSession = sessionField.GetValue(window);
        var savedStarted = startedField.GetValue(window);
        var savedStop = stopField.GetValue(window);
        using var session = new RecordingSession(new RecordingLibrary(Path.GetDirectoryName(row.Folder)!));
        typeof(RecordingSession).GetField("_metadata", flags)!.SetValue(session, row.Metadata);
        typeof(RecordingSession).GetField("_folder", flags)!.SetValue(session, row.Folder);
        try
        {
            // No Start/Stop or device access: only the identity/state needed by the deletion guard.
            sessionField.SetValue(window, session);
            startedField.SetValue(window, DateTime.Now);
            stopField.SetValue(window, Task.CompletedTask);
            CheckGuard(window, row, false);
            sessionField.SetValue(window, null);
            stopField.SetValue(window, new TaskCompletionSource().Task);
            CheckGuard(window, row, false);
        }
        finally
        {
            sessionField.SetValue(window, savedSession);
            startedField.SetValue(window, savedStarted);
            stopField.SetValue(window, savedStop);
        }
        CheckGuard(window, row, true);
    }

    private static void CheckInterruptedLibraryDeletion(string parent)
    {
        var root = Path.Combine(parent, "interrupted-delete-test-" + Guid.NewGuid().ToString("N"));
        var library = new RecordingLibrary(root);
        try
        {
            RecordingListItem CreateFixture()
            {
                var folder = library.CreateSessionFolder(DateTime.Now);
                var metadata = new RecordingMetadata { Title = "Synthetic interrupted meeting", Status = "recording" };
                library.SaveMetadata(folder, metadata);
                File.WriteAllBytes(Path.Combine(folder, "remote.wav"), new byte[16]);
                File.WriteAllBytes(Path.Combine(folder, "microphone.wav"), new byte[16]);
                File.WriteAllText(Path.Combine(folder, "preserve-marker.txt"), "Synthetic preserved data");
                return new RecordingListItem(folder, metadata);
            }
            var interrupted = CreateFixture();
            try { library.Delete(interrupted); throw new Exception("Default library deletion accepted recording status"); }
            catch (InvalidOperationException) { }
            Assert(Directory.Exists(interrupted.Folder) && File.Exists(Path.Combine(interrupted.Folder, "preserve-marker.txt")),
                "Default library deletion must still reject recording metadata without mutation");
            library.Delete(interrupted, allowInterruptedRecording: true);
            Assert(!Directory.Exists(interrupted.Folder), "Explicit interrupted deletion removes unlocked synthetic fixture");

            var locked = CreateFixture();
            var files = Directory.EnumerateFiles(locked.Folder).ToDictionary(file => Path.GetFileName(file)!, File.ReadAllBytes);
            using (var held = new FileStream(Path.Combine(locked.Folder, "remote.wav"), FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            {
                var rejected = false;
                try { library.Delete(locked, allowInterruptedRecording: true); }
                catch (Exception error) when (error is IOException or InvalidOperationException) { rejected = true; }
                Assert(rejected && Directory.Exists(locked.Folder), "Exclusive WAV owner must block interrupted deletion");
            }
            Assert(Directory.EnumerateFiles(locked.Folder).Count() == files.Count
                && files.All(pair => File.Exists(Path.Combine(locked.Folder, pair.Key))
                    && pair.Value.SequenceEqual(File.ReadAllBytes(Path.Combine(locked.Folder, pair.Key)))),
                "Rejected locked-WAV deletion must preserve every fixture file");
            library.Delete(locked, allowInterruptedRecording: true);
            Assert(!Directory.Exists(locked.Folder), "Deletion succeeds only after WAV lock is released");
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    private static void CheckGuard(MainWindow window, RecordingListItem row, bool expected)
    {
        // Verify the handler's guard directly without ever opening its confirmation modal.
        var predicate = typeof(MainWindow).GetMethod("CanDeleteMeeting", BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new Exception("Missing CanDeleteMeeting safety predicate");
        Assert((bool)predicate.Invoke(window, [row])! == expected, "Deletion guard must agree with row state");
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
    private static void Assert(bool value, string message) { if (!value) throw new Exception("Overview delete: " + message); }
}
