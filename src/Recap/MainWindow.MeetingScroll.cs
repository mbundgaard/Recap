using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace Recap;

public partial class MainWindow
{
    private ScrollViewer? MeetingScrollViewer() => FindMeetingScrollViewer(RecordingsList);

    private static ScrollViewer? FindMeetingScrollViewer(DependencyObject parent)
    {
        if (parent is ScrollViewer viewer) return viewer;
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var found = FindMeetingScrollViewer(VisualTreeHelper.GetChild(parent, i));
            if (found is not null) return found;
        }
        return null;
    }

    private void Meetings_ScrollChanged(object sender, ScrollChangedEventArgs e)
    {
        if (MoreMeetingsButton is null || MeetingScrollViewer() is not { } viewer
            || !ReferenceEquals(e.OriginalSource, viewer)) return;
        // Keep the footer's space at the bottom to avoid a last-page layout jump.
        MoreMeetingsButton.Visibility = viewer.ScrollableHeight <= 0 ? Visibility.Collapsed
            : viewer.VerticalOffset < viewer.ScrollableHeight - 0.01 ? Visibility.Visible : Visibility.Hidden;
    }

    private void MoreMeetings_Click(object sender, RoutedEventArgs e) => MeetingScrollViewer()?.PageDown();
}
