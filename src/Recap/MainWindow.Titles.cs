using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Recap.Logging;
using Recap.Recording;

namespace Recap;

public partial class MainWindow
{
    private void Window_PreviewMouseDown(object sender, MouseButtonEventArgs e)
    {
        // Blank card/window space is not focusable, so WPF otherwise leaves the
        // editor active. Clear focus explicitly without consuming the click.
        if (Keyboard.FocusedElement is TextBox editor
            && editor.Name is "OverviewTitleBox" or "ActiveRecordingTitleBox"
            && !editor.IsMouseOver)
            Keyboard.ClearFocus();
    }

    private void MeetingTitle_GotKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        if (sender is TextBox editor && !editor.IsReadOnly) editor.SelectAll();
    }

    private void MeetingTitle_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (sender is not TextBox editor || editor.DataContext is not RecordingListItem item) return;
        if (e.Key == Key.Escape)
        {
            editor.Text = item.Title;
            Keyboard.ClearFocus();
            e.Handled = true;
        }
        else if (e.Key == Key.Enter)
        {
            SaveOverviewTitle(editor);
            Keyboard.ClearFocus();
            e.Handled = true;
        }
    }

    private void MeetingTitle_LostKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        if (sender is TextBox editor) SaveOverviewTitle(editor);
    }

    private void SaveOverviewTitle(TextBox editor)
    {
        if (editor.DataContext is not RecordingListItem item) return;
        var title = editor.Text.Trim();
        if (title == item.Title) return;
        if (_session is not null && string.Equals(_session.Folder, item.Folder, StringComparison.OrdinalIgnoreCase))
        {
            if (TrySaveActiveRecordingTitle(title))
            {
                item.Metadata.Title = _session.Metadata.Title;
                editor.Text = item.Title;
                RefreshRecordings();
            }
            else editor.Text = item.Title;
            return;
        }
        if (string.IsNullOrWhiteSpace(title) || title.Length > 200 || title.Any(char.IsControl)
            || item.IsProcessing || item.IsManuallyQueued || item.Metadata.Status == "recording"
            || MeetingWindowBusy(item) || _agentMutationMeetings.Contains(item.Folder)
            || !Recordings.Contains(item))
        {
            editor.Text = item.Title;
            return;
        }
        var oldTitle = item.Metadata.Title;
        try
        {
            _library.Rename(item, title);
            editor.Text = item.Title;
            if (_meetingWindows.TryGetValue(item.Folder, out var detail)) detail.RefreshAgentView();
            RefreshRecordings();
        }
        catch (Exception ex)
        {
            item.Metadata.Title = oldTitle;
            editor.Text = oldTitle;
            AppLogger.Error(ex, "Could not rename meeting.");
            MessageBox.Show(this, "The meeting title could not be saved. Please try again.",
                "Rename failed", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }
}
