using System.IO;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Recap;

/// <summary>Geometry-only STA checks. Shows controls synthetically; never starts a session.</summary>
internal static class RecorderLayoutTests
{
    internal static void Check(MainWindow window)
    {
        var meetings = Element(window, "MeetingsPanel");
        Assert(meetings.Visibility == Visibility.Visible, "Layout fixture starts expanded");
        var oldWidth = window.Width;
        var oldHeight = window.Height;
        var oldState = window.WindowState;
        var titlePanel = Element(window, "ActiveRecordingTitlePanel");
        var title = (TextBox)window.FindName("ActiveRecordingTitleBox");
        var pause = (Button)window.FindName("PauseRecordingButton");
        var record = (Button)window.FindName("RecordButton");
        var oldTitleVisibility = titlePanel.Visibility;
        var oldPauseVisibility = pause.Visibility;
        var oldTitle = title.Text;
        var oldRecordContent = record.Content;
        try
        {
            window.WindowState = WindowState.Normal;
            window.Width = Math.Max(920, window.MinWidth);
            window.Height = Math.Max(900, window.MinHeight + 100);
            Pump(window);
            var expandedWidth = window.ActualWidth;
            var expandedHeight = window.ActualHeight;
            CheckFooter(window);
            Toggle(window);
            Assert(meetings.Visibility == Visibility.Collapsed && window.ActualWidth < expandedWidth,
                "Compact mode hides the overview and narrows the window");
            Assert(window.ActualHeight < expandedHeight, "Compact mode removes unused expanded height");
            Assert(AutomationProperties.GetName((Button)window.FindName("MeetingsToggleButton")) == "Show meetings",
                "Compact arrow describes restoring overview");

            // Purely visual fixture: no RecordingSession, device access, Start/Stop or processing calls.
            titlePanel.Visibility = Visibility.Visible;
            title.Text = "Synthetic active meeting title";
            pause.Visibility = Visibility.Visible;
            record.Content = "Stop recording";
            window.Width = window.MinWidth;
            window.Height = window.MinHeight;
            Pump(window);
            CheckFooter(window);
            CheckActiveCompactGeometry(window);
            SaveOptionalScreenshot(window, "recorder-compact-active.png");
            var compactMinimumHeight = window.ActualHeight;
            Assert(compactMinimumHeight >= window.MinHeight - 1, "Minimum compact height is respected");

            Toggle(window);
            Assert(meetings.Visibility == Visibility.Visible, "Expand restores overview");
            Assert(Math.Abs(window.ActualWidth - expandedWidth) <= 1 && Math.Abs(window.ActualHeight - expandedHeight) <= 1,
                "Expanding restores both saved width and height after compact resizing");
            Assert(AutomationProperties.GetName((Button)window.FindName("MeetingsToggleButton")) == "Hide meetings",
                "Expanded arrow describes hiding overview");
            CheckFooter(window);
            SaveOptionalScreenshot(window, "recorder-expanded-active.png");

            // A second cycle must not replace the expanded restore size with the compact one.
            Toggle(window);
            window.Height = window.MinHeight;
            Pump(window);
            CheckActiveCompactGeometry(window);
            Toggle(window);
            Assert(Math.Abs(window.ActualWidth - expandedWidth) <= 1 && Math.Abs(window.ActualHeight - expandedHeight) <= 1,
                "Repeated compact cycles preserve expanded geometry");
            Console.WriteLine("PASS: compact/expanded width-height restoration, footer placement and minimum-height active-recorder geometry without capture.");
        }
        finally
        {
            titlePanel.Visibility = oldTitleVisibility;
            pause.Visibility = oldPauseVisibility;
            title.Text = oldTitle;
            record.Content = oldRecordContent;
            if (meetings.Visibility != Visibility.Visible) Toggle(window);
            window.WindowState = WindowState.Normal;
            window.Width = oldWidth;
            window.Height = oldHeight;
            window.WindowState = oldState;
            Pump(window);
        }
    }

    private static void SaveOptionalScreenshot(MainWindow window, string filename)
    {
        var directory = Environment.GetEnvironmentVariable("RECAP_LAYOUT_SCREENSHOT_DIR");
        if (string.IsNullOrWhiteSpace(directory)) return;
        Directory.CreateDirectory(directory);
        window.UpdateLayout();
        var bitmap = new RenderTargetBitmap((int)Math.Ceiling(window.ActualWidth),
            (int)Math.Ceiling(window.ActualHeight), 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(window);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var output = File.Create(Path.Combine(directory, filename));
        encoder.Save(output);
    }

    private static void CheckFooter(MainWindow window)
    {
        var recorder = Recorder(window);
        var settings = Descendants(recorder).OfType<Button>()
            .Single(button => AutomationProperties.GetName(button) == "Settings");
        var agent = Element(window, "AgentButton");
        var toggle = Element(window, "MeetingsToggleButton");
        var settingsBounds = Bounds(settings, recorder);
        var agentBounds = Bounds(agent, recorder);
        var toggleBounds = Bounds(toggle, recorder);
        Assert(settingsBounds.Right <= agentBounds.Left + 1 && agentBounds.Right < toggleBounds.Left,
            "Settings and AI sit together on left; overview arrow sits separately on right");
        Assert(settingsBounds.Left < recorder.ActualWidth / 3 && toggleBounds.Right > recorder.ActualWidth * 0.7,
            "Footer arrow occupies right-hand side, not the settings group");
        Assert(Math.Abs(settingsBounds.Top - agentBounds.Top) <= 2 && Math.Abs(settingsBounds.Top - toggleBounds.Top) <= 2,
            "Footer controls share a row");
        foreach (var element in new FrameworkElement[] { settings, agent, toggle }) Contained(element, recorder);
    }

    private static void CheckActiveCompactGeometry(MainWindow window)
    {
        var recorder = Recorder(window);
        var title = Element(window, "ActiveRecordingTitleBox");
        var record = Element(window, "RecordButton");
        var pause = Element(window, "PauseRecordingButton");
        var remote = Element(window, "RemoteMeter");
        var microphone = Element(window, "MicrophoneMeter");
        var toggle = Element(window, "MeetingsToggleButton");
        var language = Element(window, "RecordingLanguageBox");
        var timer = Element(window, "TimerText");
        var controls = new[] { timer, language, title, record, pause, remote, microphone, toggle };
        foreach (var control in controls)
        {
            Assert(control.IsVisible && control.ActualWidth > 0 && control.ActualHeight > 0,
                "Active recorder control is rendered: " + control.Name);
            Contained(control, recorder);
        }
        for (var i = 0; i < controls.Length; i++)
            for (var j = i + 1; j < controls.Length; j++)
            {
                var intersection = Rect.Intersect(Bounds(controls[i], recorder), Bounds(controls[j], recorder));
                Assert(intersection.IsEmpty || intersection.Width <= 1 || intersection.Height <= 1,
                    "Compact controls must not overlap: " + controls[i].Name + "/" + controls[j].Name);
            }
        Assert(Bounds(title, recorder).Bottom <= Bounds(language, recorder).Top + 1, "Active title stays above language control");
        Assert(Bounds(language, recorder).Bottom <= Bounds(record, recorder).Top + 1, "Language stays above recording actions");
        Assert(Bounds(record, recorder).Right <= Bounds(pause, recorder).Left + 1, "Stop and Pause remain separate");
        Assert(Bounds(microphone, recorder).Bottom <= Bounds(toggle, recorder).Top + 1, "Footer cannot cover microphone meter");
    }

    private static Border Recorder(MainWindow window)
    {
        var layout = (Grid)window.FindName("MainLayout");
        return layout.Children.OfType<Border>().Single(border => Grid.GetColumn(border) == 0);
    }
    private static FrameworkElement Element(MainWindow window, string name) => (FrameworkElement)(window.FindName(name)
        ?? throw new Exception("Missing recorder control: " + name));
    private static Rect Bounds(FrameworkElement element, Visual ancestor) => element.TransformToAncestor(ancestor)
        .TransformBounds(new Rect(new Point(), element.RenderSize));
    private static void Contained(FrameworkElement element, FrameworkElement parent)
    {
        var bounds = Bounds(element, parent);
        Assert(bounds.Left >= -1 && bounds.Top >= -1 && bounds.Right <= parent.ActualWidth + 1 && bounds.Bottom <= parent.ActualHeight + 1,
            "Control is not clipped by recorder card: " + element.Name);
    }
    private static void Toggle(MainWindow window)
    {
        ((Button)window.FindName("MeetingsToggleButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Pump(window);
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
    private static void Assert(bool value, string message) { if (!value) throw new Exception("Recorder layout: " + message); }
}
