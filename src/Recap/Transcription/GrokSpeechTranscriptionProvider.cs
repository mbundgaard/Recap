using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Recap.Settings;

namespace Recap.Transcription;

/// <summary>Explicit paid cloud recognition. Only a definite 429 is retried.</summary>
public sealed class GrokSpeechTranscriptionProvider : ITranscriptionProvider
{
    private const long MaximumAudioBytes = 500_000_000;
    private const int MaximumResponseBytes = 32 * 1024 * 1024;
    private static readonly Uri Endpoint = new("https://api.x.ai/v1/stt");
    private static readonly HttpClient DefaultClient = new(new HttpClientHandler { AllowAutoRedirect = false })
        { Timeout = Timeout.InfiniteTimeSpan };
    private readonly HttpClient _client;
    private readonly string _key;
    private readonly string _model;
    private readonly string _language;

    // Injected clients are trusted test/application transports; production defaults never follow redirects.
    public GrokSpeechTranscriptionProvider(GrokSpeechSettings settings, string language, HttpClient? client = null)
    {
        _key = settings.ApiKey?.Trim() ?? "";
        _model = settings.Model?.Trim() ?? "";
        _language = language switch { "en-US" => "en", "da-DK" => "da", _ => throw new ArgumentException("Choose English or Danish.") };
        _client = client ?? DefaultClient;
    }

    public string Id => "grok-speech";
    public string DisplayName => "Grok Speech";

    public async Task<TrackTranscriptResult> TranscribeAsync(TrackTranscriptionRequest request, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (_key.Length == 0 || _key.Contains('\r') || _key.Contains('\n'))
            throw new InvalidOperationException("Enter a valid Grok API key in Settings.");
        if (_model != GrokSpeechSettings.DefaultModel)
            throw new InvalidOperationException("Choose a valid Grok speech model in Settings.");
        if (request.SourceTrack is not ("remote" or "microphone")) throw new ArgumentException("Unknown recording track.");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromMinutes(30));
        var token = timeout.Token;
        try
        {
            for (var attempt = 0; ; attempt++)
            {
                await using var audio = new FileStream(request.AudioFilePath, FileMode.Open, FileAccess.Read, FileShare.Read,
                    81920, FileOptions.Asynchronous | FileOptions.SequentialScan);
                if (audio.Length > MaximumAudioBytes) throw new InvalidOperationException("Grok accepts audio files up to 500 MB. Use a shorter recording.");
                if (audio.Length == 0) throw new InvalidOperationException("The audio file is empty.");
                using var message = new HttpRequestMessage(HttpMethod.Post, Endpoint);
                message.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _key);
                using var form = new MultipartFormDataContent();
                form.Add(new StringContent(_model), "model");
                form.Add(new StringContent(_language), "language");
                if (request.SourceTrack == "remote") form.Add(new StringContent("true"), "diarize");
                // Fixed filename prevents leaking local recording paths/titles. File is always last.
                form.Add(new AudioContent(audio, () => request.PhaseProgress?.Report("Waiting for Grok speech recognition")), "file", "audio.wav");
                message.Content = form;
                request.PhaseProgress?.Report("Uploading audio to Grok");
                using var response = await _client.SendAsync(message, HttpCompletionOption.ResponseHeadersRead, token);
                if (response.StatusCode == HttpStatusCode.TooManyRequests && attempt < 2)
                {
                    var retry = response.Headers.RetryAfter;
                    var delay = retry?.Delta ?? (retry?.Date is { } date ? date - DateTimeOffset.UtcNow : TimeSpan.FromSeconds(2));
                    if (delay < TimeSpan.Zero) delay = TimeSpan.Zero;
                    // Do not ignore an excessive server delay by retrying earlier than requested.
                    if (delay <= TimeSpan.FromSeconds(30))
                    {
                        request.PhaseProgress?.Report("Grok rate limit; waiting to retry");
                        await Task.Delay(delay, token);
                        continue;
                    }
                }
                if (!response.IsSuccessStatusCode)
                    throw new InvalidOperationException(response.StatusCode == HttpStatusCode.TooManyRequests
                        ? "Grok rate limit reached. Retry later."
                        : "Grok speech recognition failed. Check your account and settings before retrying; the request may have been billed.");
                if (response.Content.Headers.ContentLength > MaximumResponseBytes) throw Malformed();
                await using var input = await response.Content.ReadAsStreamAsync(token);
                using var output = new MemoryStream();
                var buffer = new byte[81920];
                int count;
                while ((count = await input.ReadAsync(buffer, token)) != 0)
                {
                    if (output.Length + count > MaximumResponseBytes) throw Malformed();
                    output.Write(buffer, 0, count);
                }
                var raw = new UTF8Encoding(false, true).GetString(output.ToArray());
                var segments = Parse(raw, request);
                return new(request.SourceTrack, request.SpeakerLabel, segments, JsonSerializer.Serialize(segments), raw);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw new OperationCanceledException(cancellationToken); }
        catch (OperationCanceledException) { throw new InvalidOperationException("Grok speech recognition timed out. The request may have been billed; check before retrying."); }
        catch (HttpRequestException) { throw new InvalidOperationException("Grok speech connection failed. The request may have been billed; check before retrying."); }
        catch (IOException) { throw new InvalidOperationException("Could not read audio or the Grok response. Check before retrying; the request may have been billed."); }
        catch (Exception ex) when (ex is JsonException or DecoderFallbackException or FormatException or OverflowException)
        { throw Malformed(); }
    }

    private static IReadOnlyList<TranscriptSegment> Parse(string raw, TrackTranscriptionRequest request)
    {
        using var document = JsonDocument.Parse(raw);
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("text", out var fullText)
            || fullText.ValueKind != JsonValueKind.String || !root.TryGetProperty("duration", out var durationNode)
            || durationNode.ValueKind != JsonValueKind.Number || !durationNode.TryGetDouble(out var duration) || !double.IsFinite(duration) || duration < 0
            || duration > TimeSpan.MaxValue.TotalSeconds
            || !root.TryGetProperty("words", out var words) || words.ValueKind != JsonValueKind.Array) throw Malformed();
        var segments = new List<TranscriptSegment>();
        var groupText = new StringBuilder();
        string? groupSpeaker = null;
        TimeSpan groupStart = default, groupEnd = default;
        void FlushGroup()
        {
            if (groupSpeaker is not null) segments.Add(new(groupStart, groupEnd, groupSpeaker, groupText.ToString(), request.SourceTrack));
            groupText.Clear();
        }
        double previousStart = -1;
        foreach (var word in words.EnumerateArray())
        {
            if (word.ValueKind != JsonValueKind.Object || !word.TryGetProperty("text", out var textNode)
                || textNode.ValueKind != JsonValueKind.String || !word.TryGetProperty("start", out var startNode)
                || startNode.ValueKind != JsonValueKind.Number || !startNode.TryGetDouble(out var start)
                || !word.TryGetProperty("end", out var endNode) || endNode.ValueKind != JsonValueKind.Number || !endNode.TryGetDouble(out var end)
                || !double.IsFinite(start) || !double.IsFinite(end) || start < 0 || end < start || end > duration + 0.05
                || start < previousStart) throw Malformed();
            previousStart = start;
            var text = textNode.GetString()!.Trim();
            if (text.Length == 0) continue;
            var speaker = request.SpeakerLabel;
            if (request.SourceTrack == "remote")
            {
                speaker = "Others";
                if (word.TryGetProperty("speaker", out var label) && label.ValueKind != JsonValueKind.Null)
                {
                    if (label.ValueKind != JsonValueKind.Number || !label.TryGetInt32(out var index) || index < 0 || index == int.MaxValue) throw Malformed();
                    speaker = "Speaker " + (index + 1);
                }
            }
            // Duration can be rounded to two decimal places while words retain finer precision.
            var from = TimeSpan.FromSeconds(Math.Min(start, duration));
            var to = TimeSpan.FromSeconds(Math.Min(end, duration));
            if (groupSpeaker == speaker && from - groupEnd <= TimeSpan.FromSeconds(1)
                && to - groupStart <= TimeSpan.FromSeconds(15) && groupText.Length + text.Length + 1 <= 2000)
            {
                groupText.Append(' ').Append(text);
                if (to > groupEnd) groupEnd = to;
            }
            else
            {
                FlushGroup();
                groupSpeaker = speaker;
                groupStart = from;
                groupEnd = to;
                groupText.Append(text);
            }
        }
        FlushGroup();
        // Nonempty text without word timings cannot be represented faithfully as timed segments.
        if (segments.Count == 0 && !string.IsNullOrWhiteSpace(fullText.GetString())) throw Malformed();
        return segments;
    }

    private static InvalidOperationException Malformed() => new("Grok returned an invalid or oversized speech response. No transcript was saved.");

    private sealed class AudioContent : HttpContent
    {
        private readonly Stream _audio;
        private readonly Action _uploaded;
        internal AudioContent(Stream audio, Action uploaded)
        {
            _audio = audio;
            _uploaded = uploaded;
            Headers.ContentType = new MediaTypeHeaderValue("audio/wav");
        }
        protected override bool TryComputeLength(out long length) { length = _audio.Length; return true; }
        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) => SerializeToStreamAsync(stream, context, CancellationToken.None);
        protected override async Task SerializeToStreamAsync(Stream stream, TransportContext? context, CancellationToken cancellationToken)
        {
            await _audio.CopyToAsync(stream, cancellationToken);
            _uploaded();
        }
    }
}
