using System.Diagnostics;
using System.IO;
using System.Text.Json;
using Microsoft.CognitiveServices.Speech;
using Microsoft.CognitiveServices.Speech.Audio;
using Microsoft.CognitiveServices.Speech.Transcription;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;
using Recap.Logging;
using Recap.Settings;

namespace Recap.Transcription;

public sealed class AzureSpeechTranscriptionProvider(AzureSpeechSettings settings) : ITranscriptionProvider
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    public string Id => "azure-speech";
    public string DisplayName => "Azure Speech";

    public async Task<TrackTranscriptResult> TranscribeAsync(TrackTranscriptionRequest request, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (string.IsNullOrWhiteSpace(settings.SubscriptionKey))
        {
            throw new InvalidOperationException("Azure Speech subscription key is missing.");
        }

        if (!File.Exists(request.AudioFilePath))
        {
            throw new FileNotFoundException("Audio file was not found.", request.AudioFilePath);
        }

        var speechConfig = CreateSpeechConfig(settings);
        speechConfig.SpeechRecognitionLanguage = settings.RecognitionLanguage;
        speechConfig.RequestWordLevelTimestamps();
        speechConfig.OutputFormat = OutputFormat.Detailed;

        var sourceFile = new FileInfo(request.AudioFilePath);
        var sourceWaveInfo = GetWaveInfo(request.AudioFilePath);
        AppLogger.Info($"Azure Speech transcribe requested. Track={request.SourceTrack}; SourceFile={request.AudioFilePath}; SourceSizeBytes={sourceFile.Length}; SourceSizeMB={sourceFile.Length / 1024d / 1024d:F2}; LastWrite={sourceFile.LastWriteTime:O}; {sourceWaveInfo}; Language={settings.RecognitionLanguage}; Endpoint={settings.Endpoint}; Region={settings.Region}");

        var shouldDeleteSpeechInput = false;
        var speechInputPath = request.AudioFilePath;
        if (!IsSpeechReadyWav(request.AudioFilePath))
        {
            speechInputPath = CreateTemporarySpeechWav(request.AudioFilePath, request.SourceTrack);
            shouldDeleteSpeechInput = true;
        }

        try
        {
            var speechFile = new FileInfo(speechInputPath);
            AppLogger.Info($"Azure Speech input ready. Track={request.SourceTrack}; File={speechInputPath}; Temporary={shouldDeleteSpeechInput}; SizeBytes={speechFile.Length}; SizeMB={speechFile.Length / 1024d / 1024d:F2}; {GetWaveInfo(speechInputPath)}");

            using var audioConfig = AudioConfig.FromWavFileInput(speechInputPath);

            if (request.SourceTrack.Equals("remote", StringComparison.OrdinalIgnoreCase))
            {
                return await TranscribeWithDiarizationAsync(speechConfig, audioConfig, request, cancellationToken);
            }

            return await TranscribeSingleSpeakerAsync(speechConfig, audioConfig, request, cancellationToken);
        }
        finally
        {
            if (shouldDeleteSpeechInput)
            {
                TryDeleteTemporaryFile(speechInputPath);
            }
        }
    }

    private static SpeechConfig CreateSpeechConfig(AzureSpeechSettings settings)
    {
        if (!string.IsNullOrWhiteSpace(settings.Endpoint))
        {
            return SpeechConfig.FromEndpoint(new Uri(settings.Endpoint), settings.SubscriptionKey);
        }

        if (string.IsNullOrWhiteSpace(settings.Region))
        {
            throw new InvalidOperationException("Azure Speech region is missing.");
        }

        return SpeechConfig.FromSubscription(settings.SubscriptionKey, settings.Region);
    }

    private static async Task<TrackTranscriptResult> TranscribeWithDiarizationAsync(
        SpeechConfig speechConfig,
        AudioConfig audioConfig,
        TrackTranscriptionRequest request,
        CancellationToken cancellationToken)
    {
        var stopwatch = Stopwatch.StartNew();
        var lastProgressLog = DateTime.MinValue;
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var raw = new List<RawTranscriptSegment>();
        var segments = new List<TranscriptSegment>();

        using var transcriber = new ConversationTranscriber(speechConfig, audioConfig);

        transcriber.Transcribing += (_, e) =>
            request.Progress?.Report(TimeSpan.FromTicks(e.Result.OffsetInTicks) + e.Result.Duration);
        transcriber.Transcribed += (_, e) =>
        {
            request.Progress?.Report(TimeSpan.FromTicks(e.Result.OffsetInTicks) + e.Result.Duration);
            if (e.Result.Reason != ResultReason.RecognizedSpeech || string.IsNullOrWhiteSpace(e.Result.Text))
            {
                return;
            }

            var speaker = string.IsNullOrWhiteSpace(e.Result.SpeakerId) ? "Speaker 1" : NormalizeSpeakerId(e.Result.SpeakerId);
            var start = TimeSpan.FromTicks(e.Result.OffsetInTicks);
            var duration = e.Result.Duration;
            var end = start + duration;

            raw.Add(new RawTranscriptSegment(request.SourceTrack, speaker, start, end, e.Result.Text, e.Result.ResultId, e.Result.Properties.GetProperty(PropertyId.SpeechServiceResponse_JsonResult)));
            segments.Add(new TranscriptSegment(start, end, speaker, e.Result.Text, request.SourceTrack));

            if ((DateTime.Now - lastProgressLog).TotalSeconds >= 10 || segments.Count <= 3)
            {
                lastProgressLog = DateTime.Now;
                AppLogger.Info($"Azure Speech diarization progress. Track={request.SourceTrack}; Segments={segments.Count}; LastStart={start:c}; LastEnd={end:c}; Elapsed={stopwatch.Elapsed:c}");
            }
        };

        transcriber.Canceled += (_, e) =>
        {
            AppLogger.Warning($"Azure Speech diarization canceled. Track={request.SourceTrack}; Reason={e.Reason}; ErrorCode={e.ErrorCode}; Details={e.ErrorDetails}");
            if (e.Reason == CancellationReason.EndOfStream)
            {
                completion.TrySetResult();
                return;
            }

            completion.TrySetException(new InvalidOperationException($"Azure Speech canceled: {e.Reason} {e.ErrorDetails}"));
        };
        transcriber.SessionStarted += (_, e) => AppLogger.Info($"Azure Speech diarization session started. Track={request.SourceTrack}; SessionId={e.SessionId}");
        transcriber.SessionStopped += (_, e) =>
        {
            AppLogger.Info($"Azure Speech diarization session stopped. Track={request.SourceTrack}; SessionId={e.SessionId}; Segments={segments.Count}; Elapsed={stopwatch.Elapsed:c}");
            completion.TrySetResult();
        }; 

        cancellationToken.ThrowIfCancellationRequested();
        await transcriber.StartTranscribingAsync().ConfigureAwait(false);
        await using var registration = cancellationToken.Register(() => completion.TrySetCanceled(cancellationToken));

        try
        {
            await completion.Task.ConfigureAwait(false);
        }
        finally
        {
            await transcriber.StopTranscribingAsync().ConfigureAwait(false);
        }

        stopwatch.Stop();
        AppLogger.Info($"Azure Speech diarization finished. Track={request.SourceTrack}; Segments={segments.Count}; SpeakerCount={segments.Select(x => x.Speaker).Distinct().Count()}; Elapsed={stopwatch.Elapsed:c}");
        return new TrackTranscriptResult(request.SourceTrack, request.SpeakerLabel, segments.OrderBy(x => x.Start).ToList(), JsonSerializer.Serialize(raw, JsonOptions));
    }

    private static async Task<TrackTranscriptResult> TranscribeSingleSpeakerAsync(
        SpeechConfig speechConfig,
        AudioConfig audioConfig,
        TrackTranscriptionRequest request,
        CancellationToken cancellationToken)
    {
        var stopwatch = Stopwatch.StartNew();
        var lastProgressLog = DateTime.MinValue;
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var raw = new List<RawTranscriptSegment>();
        var segments = new List<TranscriptSegment>();

        using var recognizer = new SpeechRecognizer(speechConfig, audioConfig);

        recognizer.Recognizing += (_, e) =>
            request.Progress?.Report(TimeSpan.FromTicks(e.Result.OffsetInTicks) + e.Result.Duration);
        recognizer.Recognized += (_, e) =>
        {
            request.Progress?.Report(TimeSpan.FromTicks(e.Result.OffsetInTicks) + e.Result.Duration);
            if (e.Result.Reason != ResultReason.RecognizedSpeech || string.IsNullOrWhiteSpace(e.Result.Text))
            {
                return;
            }

            var start = TimeSpan.FromTicks(e.Result.OffsetInTicks);
            var end = start + e.Result.Duration;
            raw.Add(new RawTranscriptSegment(request.SourceTrack, request.SpeakerLabel, start, end, e.Result.Text, e.Result.ResultId, e.Result.Properties.GetProperty(PropertyId.SpeechServiceResponse_JsonResult)));
            segments.Add(new TranscriptSegment(start, end, request.SpeakerLabel, e.Result.Text, request.SourceTrack));

            if ((DateTime.Now - lastProgressLog).TotalSeconds >= 10 || segments.Count <= 3)
            {
                lastProgressLog = DateTime.Now;
                AppLogger.Info($"Azure Speech recognition progress. Track={request.SourceTrack}; Segments={segments.Count}; LastStart={start:c}; LastEnd={end:c}; Elapsed={stopwatch.Elapsed:c}");
            }
        };

        recognizer.Canceled += (_, e) =>
        {
            AppLogger.Warning($"Azure Speech recognition canceled. Track={request.SourceTrack}; Reason={e.Reason}; ErrorCode={e.ErrorCode}; Details={e.ErrorDetails}");
            if (e.Reason == CancellationReason.EndOfStream)
            {
                completion.TrySetResult();
                return;
            }

            completion.TrySetException(new InvalidOperationException($"Azure Speech canceled: {e.Reason} {e.ErrorDetails}"));
        };
        recognizer.SessionStarted += (_, e) => AppLogger.Info($"Azure Speech recognition session started. Track={request.SourceTrack}; SessionId={e.SessionId}");
        recognizer.SessionStopped += (_, e) =>
        {
            AppLogger.Info($"Azure Speech recognition session stopped. Track={request.SourceTrack}; SessionId={e.SessionId}; Segments={segments.Count}; Elapsed={stopwatch.Elapsed:c}");
            completion.TrySetResult();
        };

        cancellationToken.ThrowIfCancellationRequested();
        await recognizer.StartContinuousRecognitionAsync().ConfigureAwait(false);
        await using var registration = cancellationToken.Register(() => completion.TrySetCanceled(cancellationToken));

        try
        {
            await completion.Task.ConfigureAwait(false);
        }
        finally
        {
            await recognizer.StopContinuousRecognitionAsync().ConfigureAwait(false);
        }

        stopwatch.Stop();
        AppLogger.Info($"Azure Speech recognition finished. Track={request.SourceTrack}; Segments={segments.Count}; Elapsed={stopwatch.Elapsed:c}");
        return new TrackTranscriptResult(request.SourceTrack, request.SpeakerLabel, segments.OrderBy(x => x.Start).ToList(), JsonSerializer.Serialize(raw, JsonOptions));
    }

    private static bool IsSpeechReadyWav(string path)
    {
        try
        {
            using var reader = new WaveFileReader(path);
            return reader.WaveFormat.Encoding == WaveFormatEncoding.Pcm
                && reader.WaveFormat.SampleRate == 16000
                && reader.WaveFormat.Channels == 1
                && reader.WaveFormat.BitsPerSample == 16;
        }
        catch
        {
            return false;
        }
    }

    private static string CreateTemporarySpeechWav(string sourcePath, string sourceTrack)
    {
        var tempPath = Path.Combine(Path.GetTempPath(), $"recap-{sourceTrack}-{Guid.NewGuid():N}-speech.wav");
        var stopwatch = Stopwatch.StartNew();

        using var reader = new AudioFileReader(sourcePath);
        ISampleProvider sampleProvider = reader;

        if (reader.WaveFormat.Channels == 2)
        {
            sampleProvider = new StereoToMonoSampleProvider(sampleProvider)
            {
                LeftVolume = 0.5f,
                RightVolume = 0.5f
            };
        }
        else if (reader.WaveFormat.Channels != 1)
        {
            throw new NotSupportedException($"Cannot normalize {reader.WaveFormat.Channels}-channel audio for speech transcription yet.");
        }

        var resampled = new WdlResamplingSampleProvider(sampleProvider, 16000);
        WaveFileWriter.CreateWaveFile16(tempPath, resampled);

        stopwatch.Stop();
        AppLogger.Info($"Created temporary speech WAV. Track={sourceTrack}; Source={sourcePath}; Temp={tempPath}; Elapsed={stopwatch.Elapsed:c}");
        return tempPath;
    }

    private static void TryDeleteTemporaryFile(string path)
    {
        try
        {
            File.Delete(path);
            AppLogger.Info($"Deleted temporary speech WAV: {path}");
        }
        catch (Exception ex)
        {
            AppLogger.Warning($"Could not delete temporary speech WAV {path}: {ex.Message}");
        }
    }

    private static string GetWaveInfo(string path)
    {
        try
        {
            using var reader = new WaveFileReader(path);
            var format = reader.WaveFormat;
            return $"Duration={reader.TotalTime:c}; WaveFormat={format.Encoding}; SampleRate={format.SampleRate}; Channels={format.Channels}; BitsPerSample={format.BitsPerSample}; AvgBytesPerSec={format.AverageBytesPerSecond}";
        }
        catch (Exception ex)
        {
            AppLogger.Warning($"Could not read WAV metadata for {path}: {ex.Message}");
            return "Duration=unknown; WaveFormat=unknown";
        }
    }

    private static string Truncate(string text, int maxLength)
    {
        text = text.ReplaceLineEndings(" ");
        return text.Length <= maxLength ? text : text[..maxLength] + "...";
    }

    private static string NormalizeSpeakerId(string speakerId)
    {
        var digits = new string(speakerId.Where(char.IsDigit).ToArray());
        return int.TryParse(digits, out var number) ? $"Speaker {number}" : speakerId;
    }

    private sealed record RawTranscriptSegment(
        string SourceTrack,
        string Speaker,
        TimeSpan Start,
        TimeSpan End,
        string Text,
        string ResultId,
        string RawJson);
}
