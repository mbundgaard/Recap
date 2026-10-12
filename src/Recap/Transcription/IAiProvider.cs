namespace Recap.Transcription;

public interface IAiProvider
{
    string Id { get; }
    string DisplayName { get; }
    Task<FinalTranscriptResult> CreateFinalTranscriptAsync(FinalTranscriptRequest request, CancellationToken cancellationToken = default);
    Task<MeetingSummaryResult> CreateSummaryAsync(MeetingSummaryRequest request, CancellationToken cancellationToken = default);
}

public sealed record FinalTranscriptRequest(
    string MeetingTitle,
    IReadOnlyList<TranscriptSegment> Segments);

public sealed record FinalTranscriptResult(string Markdown);

public sealed record MeetingSummaryRequest(
    string MeetingTitle,
    string FinalTranscriptMarkdown);

public sealed record MeetingSummaryResult(string Markdown);

public sealed record TranscriptSegment(
    TimeSpan Start,
    TimeSpan End,
    string Speaker,
    string Text,
    string SourceTrack);
