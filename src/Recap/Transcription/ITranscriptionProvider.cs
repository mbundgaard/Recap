namespace Recap.Transcription;

public interface ITranscriptionProvider
{
    string Id { get; }
    string DisplayName { get; }
    Task<TrackTranscriptResult> TranscribeAsync(TrackTranscriptionRequest request, CancellationToken cancellationToken = default);
}

public sealed record TrackTranscriptionRequest(
    string AudioFilePath,
    string SourceTrack,
    string SpeakerLabel,
    IProgress<TimeSpan>? Progress = null,
    IProgress<string>? PhaseProgress = null);

public sealed record TrackTranscriptResult(
    string SourceTrack,
    string SpeakerLabel,
    IReadOnlyList<TranscriptSegment> Segments,
    string RawJson);
