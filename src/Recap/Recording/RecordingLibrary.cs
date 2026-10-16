using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Text.Json;

namespace Recap.Recording;

public sealed class RecordingLibrary
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    public string RootFolder { get; }

    public RecordingLibrary(string? rootFolder = null)
    {
        RootFolder = string.IsNullOrWhiteSpace(rootFolder)
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Recap", "Recordings")
            : Environment.ExpandEnvironmentVariables(rootFolder);

        Directory.CreateDirectory(RootFolder);
    }

    public string CreateSessionFolder(DateTime startedLocal)
    {
        var id = startedLocal.ToString("yyyy-MM-dd_HH-mm-ss");
        var folder = Path.Combine(RootFolder, id);

        var suffix = 1;
        while (Directory.Exists(folder))
        {
            folder = Path.Combine(RootFolder, $"{id}_{suffix++}");
        }

        Directory.CreateDirectory(folder);
        return folder;
    }

    public IReadOnlyList<RecordingListItem> LoadRecordings()
    {
        if (!Directory.Exists(RootFolder))
        {
            return [];
        }

        return Directory.EnumerateDirectories(RootFolder)
            .Select(TryLoad)
            .Where(x => x is not null)
            .Cast<RecordingListItem>()
            .OrderByDescending(x => x.Metadata.StartedLocal)
            .ToList();
    }

    public void SaveMetadata(string folder, RecordingMetadata metadata)
    {
        var path = Path.Combine(folder, "metadata.json");
        Recap.Storage.AtomicFile.WriteText(path, JsonSerializer.Serialize(metadata, JsonOptions));
    }

    public void Rename(RecordingListItem item, string title)
    {
        item.Metadata.Title = string.IsNullOrWhiteSpace(title) ? "Untitled recording" : title.Trim();
        SaveMetadata(item.Folder, item.Metadata);
    }

    public void Delete(RecordingListItem item)
    {
        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(RootFolder));
        var folder = Path.TrimEndingDirectorySeparator(Path.GetFullPath(item.Folder));
        if (!string.Equals(Path.GetDirectoryName(folder), root, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Only meetings inside the recordings library can be deleted.");
        if (!Directory.Exists(folder)) return;
        if ((File.GetAttributes(folder) & FileAttributes.ReparsePoint) != 0)
            throw new InvalidOperationException("Cannot delete a linked meeting folder.");
        var current = TryLoad(folder) ?? throw new InvalidOperationException("Meeting metadata could not be read.");
        if (current.Metadata.Status == "recording")
            throw new InvalidOperationException("Stop the recording before deleting this meeting.");
        Directory.Delete(folder, recursive: true);
    }

    public void OpenFolder(string? folder = null)
    {
        var target = folder ?? RootFolder;
        Directory.CreateDirectory(target);
        Process.Start(new ProcessStartInfo("explorer.exe", $"\"{target}\"") { UseShellExecute = true });
    }

    private RecordingListItem? TryLoad(string folder)
    {
        try
        {
            var metadataPath = Path.Combine(folder, "metadata.json");
            if (!File.Exists(metadataPath))
            {
                return null;
            }

            var json = File.ReadAllText(metadataPath);
            var metadata = JsonSerializer.Deserialize<RecordingMetadata>(json);
            return metadata is null ? null : new RecordingListItem(folder, metadata);
        }
        catch
        {
            return null;
        }
    }
}

public sealed class RecordingListItem(string folder, RecordingMetadata metadata) : INotifyPropertyChanged
{
    private string? _processingMessage;
    public bool IsProcessing { get; private set; }
    public bool IsCancellationPending { get; private set; }
    public bool IsManuallyQueued { get; private set; }
    public string PrimaryActionText => IsProcessing ? IsCancellationPending ? "Cancelling…" : "Cancel"
        : IsManuallyQueued ? "Queued" : CanOpen ? "Open" : ProcessingFailed || ProcessingCancelled ? "Retry" : "Process";
    public bool CanUsePrimaryAction => IsProcessing ? !IsCancellationPending
        : !IsManuallyQueued && Metadata.Status == "completed";
    public string PrimaryActionHint => IsProcessing
        ? IsCancellationPending ? "Waiting for this meeting's processing to stop" : "Cancel processing for this meeting"
        : CanOpen ? "Open meeting details"
        : "Prepare audio and recognize speech for this meeting only. Uses your configured speech service; charges may apply.";
    public string MeetingStatusText => IsProcessing ? _processingMessage ?? "Processing…"
        : IsManuallyQueued ? "Queued for processing"
        : ProcessingCancelled ? "Processing cancelled — retry when ready"
        : ProcessingFailed ? "Processing failed — try again"
        : CanOpen ? HasFinalTranscript ? "Transcript ready" : "Ready to review"
        : Metadata.Status == "recording" ? "Recording" : "Ready to process";

    public void RequestProcessingCancellation()
    {
        if (!IsProcessing || IsCancellationPending) return;
        IsCancellationPending = true;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(string.Empty));
    }

    public void MarkProcessingCancelled()
    {
        ProcessingCancelled = true;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(string.Empty));
    }

    public void SetManuallyQueued(bool queued)
    {
        IsManuallyQueued = queued;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(string.Empty));
    }
    public bool ProcessingFailed { get; private set; }
    public bool ProcessingCancelled { get; private set; }
    public double ProcessingPercent { get; private set; }
    public bool IsProgressIndeterminate { get; private set; }
    public bool ShowProgress => IsProcessing;
    public bool CanOpen => !IsProcessing && Metadata.Status != "recording"
        && File.Exists(Path.Combine(Folder, Metadata.Files.Remote))
        && File.Exists(Path.Combine(Folder, Metadata.Files.Microphone))
        && File.Exists(Path.Combine(Folder, Metadata.Files.Mixed ?? "mixed.wav"))
        && File.Exists(Path.Combine(Folder, "raw-remote-transcript.json"))
        && File.Exists(Path.Combine(Folder, "raw-microphone-transcript.json"));

    public event PropertyChangedEventHandler? PropertyChanged;

    public void UpdateProcessing(string? message, bool processing, double percent = 0,
        bool indeterminate = false, bool failed = false)
    {
        _processingMessage = message;
        IsProcessing = processing;
        if (!processing) IsCancellationPending = false;
        ProcessingCancelled = false;
        ProcessingFailed = failed;
        ProcessingPercent = percent;
        IsProgressIndeterminate = indeterminate;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(string.Empty));
    }

    public string Folder { get; } = folder;
    public RecordingMetadata Metadata { get; } = metadata;
    public string Title => Metadata.Title;
    public string StartedDisplay => Metadata.StartedLocal.ToString("ddd HH:mm");
    public string DurationDisplay => Metadata.EndedLocal is null ? "Recording" : FormatDuration(Metadata.Duration);
    public string StatusDisplay => _processingMessage ?? GetStatusDisplay();
    public bool HasFinalTranscript => !string.IsNullOrWhiteSpace(Metadata.Files.FinalTranscript) && File.Exists(Path.Combine(Folder, Metadata.Files.FinalTranscript));
    public bool CanTranscribe => Metadata.Status == "completed" && !HasFinalTranscript;

    private string GetStatusDisplay()
    {
        if (Metadata.Status == "recording") return "recording";
        if (HasFinalTranscript) return "final transcript ready";
        return CanOpen ? "speech recognized" : "waiting for processing";
    }

    private static string FormatDuration(TimeSpan duration)
    {
        if (duration.TotalHours >= 1)
        {
            return $"{(int)duration.TotalHours}h {duration.Minutes}m";
        }

        return $"{Math.Max(1, (int)Math.Round(duration.TotalMinutes))} min";
    }
}
