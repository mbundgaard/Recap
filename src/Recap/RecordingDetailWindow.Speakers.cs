using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;

namespace Recap;

public partial class RecordingDetailWindow
{
    private const int SpeakersPerPage = 3;
    private int _speakerPage;
    private SpeakerNameItem? _lastUnnamedSpeaker;

    // References to the authoritative roster, never copies of editable names.
    public ObservableCollection<SpeakerNameItem> VisibleSpeakers { get; } = [];

    private bool IsSpeakerNamed(SpeakerNameItem speaker) =>
        !string.IsNullOrWhiteSpace(speaker.DisplayName)
        && (IsLocalTranscript || !string.Equals(speaker.DisplayName.Trim(), speaker.SourceLabel,
            StringComparison.OrdinalIgnoreCase));

    private void ResetSpeakerPage()
    {
        _speakerPage = 0;
        _lastUnnamedSpeaker = null;
        RefreshSpeakerPage();
    }

    private void RefreshSpeakerPage()
    {
        if (SpeakerPager is null) return;
        var lastPage = Math.Max(0, (Speakers.Count - 1) / SpeakersPerPage);
        _speakerPage = Math.Clamp(_speakerPage, 0, lastPage);
        var start = _speakerPage * SpeakersPerPage;
        var slice = Speakers.Skip(start).Take(SpeakersPerPage).ToArray();
        // RefreshView runs on each keystroke. Do not rebuild unchanged containers:
        // that would destroy the active editor, caret, selection and IME state.
        if (VisibleSpeakers.Count != slice.Length
            || slice.Where((speaker, index) => !ReferenceEquals(speaker, VisibleSpeakers[index])).Any())
        {
            VisibleSpeakers.Clear();
            foreach (var speaker in slice) VisibleSpeakers.Add(speaker);
        }

        SpeakerPager.Visibility = Speakers.Count > SpeakersPerPage ? Visibility.Visible : Visibility.Collapsed;
        PreviousSpeakerPageButton.IsEnabled = !IsProcessing && _speakerPage > 0;
        NextSpeakerPageButton.IsEnabled = !IsProcessing && _speakerPage < lastPage;
        var missing = Speakers.Count(speaker => !IsSpeakerNamed(speaker));
        NextUnnamedSpeakerButton.IsEnabled = !IsProcessing && missing > 0;
        SpeakerNamingNotice.Visibility = missing > 0 ? Visibility.Visible : Visibility.Collapsed;
        SpeakerNamingHint.Text = missing > 0
            ? $"{missing} {(missing == 1 ? "speaker needs a name" : "speakers need names")} before confirmation."
            : "";
    }

    private void PreviousSpeakerPage_Click(object sender, RoutedEventArgs e)
    {
        if (IsProcessing || _speakerPage == 0) return;
        _speakerPage--;
        RefreshSpeakerPage();
    }

    private void NextSpeakerPage_Click(object sender, RoutedEventArgs e)
    {
        if (IsProcessing || (_speakerPage + 1) * SpeakersPerPage >= Speakers.Count) return;
        _speakerPage++;
        RefreshSpeakerPage();
    }

    private void NextUnnamedSpeaker_Click(object sender, RoutedEventArgs e)
    {
        if (IsProcessing || Speakers.Count == 0) return;
        var start = _speakerPage * SpeakersPerPage;
        if (_lastUnnamedSpeaker is not null && VisibleSpeakers.Contains(_lastUnnamedSpeaker))
            start = (Speakers.IndexOf(_lastUnnamedSpeaker) + 1) % Speakers.Count;
        if (Keyboard.FocusedElement is TextBox { DataContext: SpeakerNameItem focused })
        {
            var index = Speakers.IndexOf(focused);
            if (index >= 0) start = (index + 1) % Speakers.Count;
        }
        var target = Enumerable.Range(0, Speakers.Count)
            .Select(offset => Speakers[(start + offset) % Speakers.Count])
            .FirstOrDefault(speaker => !IsSpeakerNamed(speaker));
        if (target is null) return;
        _lastUnnamedSpeaker = target;
        _speakerPage = Speakers.IndexOf(target) / SpeakersPerPage;
        RefreshSpeakerPage();
        Dispatcher.BeginInvoke(DispatcherPriority.Input, new Action(() =>
        {
            if (IsProcessing || !VisibleSpeakers.Contains(target)) return;
            SpeakersList.UpdateLayout();
            if (SpeakersList.ItemContainerGenerator.ContainerFromItem(target) is DependencyObject container
                && FindSpeakerNameEditor(container) is TextBox editor)
            {
                editor.Focus();
                editor.SelectAll();
            }
        }));
    }

    private static TextBox? FindSpeakerNameEditor(DependencyObject parent)
    {
        if (parent is TextBox { Name: "SpeakerNameInput" } editor) return editor;
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(parent); index++)
        {
            var match = FindSpeakerNameEditor(VisualTreeHelper.GetChild(parent, index));
            if (match is not null) return match;
        }
        return null;
    }
}
