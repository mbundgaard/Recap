using System.IO;
using System.Text;
using System.Text.Json;
using Recap.Recording;
using Recap.Storage;

namespace Recap.Transcription;

/// <summary>Formats recognized words, without calling an LLM or deduplicating/rephrasing speech.</summary>
public static class LocalTranscriptExporter
{
    public static void Export(RecordingLibrary library, RecordingListItem item)
    {
        var names = new Dictionary<string, string>();
        var speakerMap = Path.Combine(item.Folder, "speaker-map.json");
        if (File.Exists(speakerMap))
        {
            using var map = JsonDocument.Parse(File.ReadAllText(speakerMap));
            if (map.RootElement.TryGetProperty("Speakers", out var speakers))
                foreach (var speaker in speakers.EnumerateArray())
                {
                    var label = speaker.GetProperty("SourceLabel").GetString();
                    var name = speaker.GetProperty("DisplayName").GetString();
                    if (!string.IsNullOrWhiteSpace(label) && !string.IsNullOrWhiteSpace(name)) names[label] = name.Trim();
                }
        }
        var segments = new List<TranscriptSegment>();
        foreach (var track in new[] { "remote", "microphone" })
        {
            var source = JsonSerializer.Deserialize<List<TranscriptSegment>>(File.ReadAllText(Path.Combine(item.Folder, $"raw-{track}-transcript.json")))
                ?? throw new InvalidDataException("A raw transcript is missing.");
            segments.AddRange(source.Select(segment => segment with
            {
                Speaker = track == "remote" && names.TryGetValue(segment.Speaker, out var name) ? name : segment.Speaker,
                SourceTrack = track
            }));
        }
        AtomicFile.WriteText(Path.Combine(item.Folder, "final-transcript.md"), Format(item.Title, segments));
        item.Metadata.Files.FinalTranscript = "final-transcript.md";
        library.SaveMetadata(item.Folder, item.Metadata);
    }

    public static string Format(string title, IEnumerable<TranscriptSegment> source)
    {
        static string Escape(string text) => text.Replace("\\", "\\\\").Replace("*", "\\*").Replace("_", "\\_")
            .Replace("[", "\\[").Replace("]", "\\]").Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;")
            .Replace("#", "\\#").Replace("`", "\\`").Replace("\r", " ").Replace("\n", " ");
        var output = new StringBuilder("# " + Escape(title) + "\n\n");
        var segments = source.Where(x => !string.IsNullOrWhiteSpace(x.Text)).OrderBy(x => x.Start).ToArray();
        if (segments.Length == 0) return output.Append("No speech was recognized.\n").ToString();
        TranscriptSegment? previous = null;
        foreach (var segment in segments)
        {
            if (previous is null || previous.Speaker != segment.Speaker || previous.SourceTrack != segment.SourceTrack
                || segment.Start - previous.End > TimeSpan.FromSeconds(2))
            {
                if (previous is not null) output.Append("\n\n");
                output.Append("**").Append(Escape(segment.Speaker)).Append(" - ")
                    .Append($"{(int)segment.Start.TotalHours:00}:{segment.Start.Minutes:00}:{segment.Start.Seconds:00}").Append("**\n");
            }
            else output.Append(' ');
            output.Append(Escape(segment.Text.Trim()));
            previous = segment;
        }
        return output.Append('\n').ToString();
    }
}
