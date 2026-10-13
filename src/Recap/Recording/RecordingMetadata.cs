using System.Text.Json.Serialization;

namespace Recap.Recording;

public sealed class RecordingMetadata
{
    public string Id { get; set; } = string.Empty;
    public string Title { get; set; } = "Untitled recording";
    public DateTime StartedLocal { get; set; }
    public DateTime StartedUtc { get; set; }
    public DateTime? EndedLocal { get; set; }
    public DateTime? EndedUtc { get; set; }
    public string Status { get; set; } = "recording";
    public string? TranscriptionLanguage { get; set; }
    public TimeSpan? RecordedDuration { get; set; }
    public RecordingDevices Devices { get; set; } = new();
    public RecordingFiles Files { get; set; } = new();

    [JsonIgnore]
    public TimeSpan Duration => RecordedDuration ?? (EndedLocal ?? DateTime.Now) - StartedLocal;
}

public sealed class RecordingDevices
{
    public string? RemoteId { get; set; }
    public string? RemoteName { get; set; }
    public string? MicrophoneId { get; set; }
    public string? MicrophoneName { get; set; }
}

public sealed class RecordingFiles
{
    public string Remote { get; set; } = "remote.wav";
    public string Microphone { get; set; } = "microphone.wav";
    public string? Mixed { get; set; }
    public string? RawRemote { get; set; }
    public string? RawMicrophone { get; set; }
    public string? FinalTranscript { get; set; }
}
