using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Recap.Logging;

namespace Recap;

public partial class MainWindow
{
    private bool _recordingMetadataTransition;
    private bool _refreshingRecordingFields;
    private string _lastRecordingLanguage = "en-US";

    private bool CanEditActiveRecording => !_recordingMetadataTransition && !_agentClosing && !_isUpdating
        && _session is not null && _recordingStarted is not null && _recordingStopTask.IsCompleted
        && _session.Metadata.Status == "recording";

    private void RefreshActiveRecordingFields()
    {
        if (ActiveRecordingTitlePanel is null || RecordingLanguageBox is null) return;
        _refreshingRecordingFields = true;
        try
        {
            var active = _session is not null && _recordingStarted is not null;
            ActiveRecordingTitlePanel.Visibility = active || _recordingStarted is not null
                ? Visibility.Visible : Visibility.Collapsed;
            ActiveRecordingTitleBox.IsEnabled = CanEditActiveRecording;
            RecordingLanguageBox.IsEnabled = !_recordingMetadataTransition && !_agentClosing && !_isUpdating;
            if (active)
            {
                if (!ActiveRecordingTitleBox.IsKeyboardFocusWithin)
                    ActiveRecordingTitleBox.Text = _session!.Metadata.Title;
                _lastRecordingLanguage = _session!.Metadata.TranscriptionLanguage ?? "en-US";
                RecordingLanguageBox.SelectedValue = _lastRecordingLanguage;
            }
            else if (!_recordingMetadataTransition)
            {
                ActiveRecordingTitleBox.Text = "";
            }
        }
        finally { _refreshingRecordingFields = false; }
    }

    private bool TrySaveActiveRecordingTitle(string title)
    {
        if (!CanEditActiveRecording) return false;
        title = title.Trim();
        if (string.IsNullOrWhiteSpace(title) || title.Length > 200 || title.Any(char.IsControl)) return false;
        var session = _session!;
        var previous = session.Metadata.Title;
        if (title != previous)
        {
            session.Metadata.Title = title;
            try { _library.SaveMetadata(session.Folder, session.Metadata); }
            catch (Exception ex)
            {
                session.Metadata.Title = previous;
                ActiveRecordingTitleBox.Text = previous;
                AppLogger.Error(ex, "Could not save active recording title.");
                MessageBox.Show(this, "The title could not be saved. The previous title has been kept.",
                    "Rename failed", MessageBoxButton.OK, MessageBoxImage.Warning);
                return false;
            }
        }
        // An explicit successful save (including an overview edit) synchronizes the editor.
        ActiveRecordingTitleBox.Text = title;
        return true;
    }

    private bool TrySaveActiveRecordingLanguage(string language)
    {
        if (!CanEditActiveRecording || language is not ("en-US" or "da-DK")) return false;
        var session = _session!;
        var previous = session.Metadata.TranscriptionLanguage;
        if (previous != language)
        {
            session.Metadata.TranscriptionLanguage = language;
            try { _library.SaveMetadata(session.Folder, session.Metadata); }
            catch (Exception ex)
            {
                session.Metadata.TranscriptionLanguage = previous;
                RefreshActiveRecordingFields();
                AppLogger.Error(ex, "Could not save active recording language.");
                MessageBox.Show(this, "The language could not be saved. The previous language has been kept.",
                    "Language not saved", MessageBoxButton.OK, MessageBoxImage.Warning);
                return false;
            }
        }
        _lastRecordingLanguage = language;
        RefreshActiveRecordingFields();
        return true;
    }

    private void RecordingLanguage_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_refreshingRecordingFields || RecordingLanguageBox is null) return;
        if (RecordingLanguageBox.SelectedValue is not string language || language is not ("en-US" or "da-DK")) return;
        if (_session is null && _recordingStarted is null && !_recordingMetadataTransition)
        {
            _lastRecordingLanguage = language; // Idle selection applies to the next recording only.
            return;
        }
        if (TrySaveActiveRecordingLanguage(language)) return;
        _refreshingRecordingFields = true;
        try { RecordingLanguageBox.SelectedValue = _lastRecordingLanguage; }
        finally { _refreshingRecordingFields = false; }
    }

    private void CommitActiveRecordingTitle()
    {
        if (!CanEditActiveRecording) return;
        if (TrySaveActiveRecordingTitle(ActiveRecordingTitleBox.Text))
            RefreshRecordings();
        else if (_session is not null)
            ActiveRecordingTitleBox.Text = _session.Metadata.Title;
    }

    private void ActiveRecordingTitle_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            e.Handled = true;
            CommitActiveRecordingTitle();
        }
        else if (e.Key == Key.Escape)
        {
            e.Handled = true;
            if (_session is not null && CanEditActiveRecording)
                ActiveRecordingTitleBox.Text = _session.Metadata.Title;
        }
    }

    private void ActiveRecordingTitle_LostKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        if (!_refreshingRecordingFields) CommitActiveRecordingTitle();
    }
}
