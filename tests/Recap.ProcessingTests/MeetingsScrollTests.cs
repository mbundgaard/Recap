using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using Recap;
using Recap.Recording;

/// <summary>In-memory overview items only. No files, recording sessions or processing requests.</summary>
internal static class MeetingsScrollTests
{
    internal static void Check(MainWindow window)
    {
        var list = (ListView)window.FindName("RecordingsList");
        var more = (Button)window.FindName("MoreMeetingsButton");
        var original = window.Recordings.ToArray();
        var flags = BindingFlags.Instance | BindingFlags.NonPublic;
        var viewerMethod = typeof(MainWindow).GetMethod("MeetingScrollViewer", flags)!;
        ScrollViewer Viewer() => (ScrollViewer)(viewerMethod.Invoke(window, null)
            ?? throw new Exception("Missing meetings ScrollViewer"));
        Pump(window);
        var oldOffset = Viewer().VerticalOffset;
        var processingField = typeof(MainWindow).GetField("_processingTask", flags)!;
        var oldProcessing = processingField.GetValue(window);
        try
        {
            // Additional safety against a pending dispatcher callback starting work.
            processingField.SetValue(window, new TaskCompletionSource().Task);
            window.Recordings.Clear();
            var syntheticRoot = Path.Combine(Path.GetTempPath(), "Recap-in-memory-scroll-" + Guid.NewGuid().ToString("N"));
            for (var i = 0; i < 40; i++)
                window.Recordings.Add(new RecordingListItem(Path.Combine(syntheticRoot, i.ToString()), new RecordingMetadata
                {
                    Title = "Synthetic scroll meeting " + (i + 1),
                    Status = "recording", // Never an eligible automatic-processing item.
                    StartedLocal = DateTime.Now.AddMinutes(-i)
                }));
            Pump(window);
            var viewer = Viewer();
            viewer.ScrollToTop();
            Pump(window);
            Assert(ScrollViewer.GetVerticalScrollBarVisibility(list) == ScrollBarVisibility.Hidden,
                "Scrollbar is hidden rather than disabled, preserving normal wheel/keyboard scrolling");
            Assert(viewer.ScrollableHeight > 0 && more.Visibility == Visibility.Visible,
                "Overflowing meeting list shows the down-arrow affordance");
            var before = viewer.VerticalOffset;
            more.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Pump(window);
            Assert(viewer.VerticalOffset > before, "Down arrow advances the viewport");
            viewer.ScrollToBottom();
            Pump(window);
            Assert(Math.Abs(viewer.VerticalOffset - viewer.ScrollableHeight) < 0.1
                && more.Visibility == Visibility.Hidden, "At bottom the arrow hides while reserving footer space");
            viewer.ScrollToTop();
            Pump(window);
            Assert(more.Visibility == Visibility.Visible, "Scrolling back to top restores the arrow");
            var one = window.Recordings[0];
            window.Recordings.Clear();
            window.Recordings.Add(one);
            Pump(window);
            Assert(viewer.ScrollableHeight <= 0 && more.Visibility == Visibility.Collapsed,
                "No overflow collapses the unused arrow/footer affordance");
            Console.WriteLine("PASS: in-memory meeting-list overflow arrow, page advance, bottom/top visibility and hidden-not-disabled scrollbar.");
        }
        finally
        {
            try
            {
                window.Recordings.Clear();
                foreach (var row in original) window.Recordings.Add(row);
                Pump(window);
                Viewer().ScrollToVerticalOffset(oldOffset);
                Pump(window);
            }
            finally { processingField.SetValue(window, oldProcessing); }
        }
    }

    private static void Pump(Window window)
    {
        window.UpdateLayout();
        var frame = new DispatcherFrame();
        window.Dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, new Action(() => frame.Continue = false));
        Dispatcher.PushFrame(frame);
        window.UpdateLayout();
    }
    private static void Assert(bool value, string message) { if (!value) throw new Exception("Meetings scroll: " + message); }
}
