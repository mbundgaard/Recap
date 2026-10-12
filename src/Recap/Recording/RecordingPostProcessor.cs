using System.IO;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;
using Recap.Logging;
using Recap.Settings;

namespace Recap.Recording;

public sealed class RecordingPostProcessor(RecordingLibrary library, AppSettings settings)
{
    private static readonly WaveFormat ArchiveFormat = new(16000, 16, 1);

    public void ProcessPendingRecordings()
    {
        foreach (var item in library.LoadRecordings())
        {
            try
            {
                Process(item);
            }
            catch (Exception ex)
            {
                AppLogger.Error(ex, $"Recording post-processing failed: {item.Folder}");
            }
        }
    }

    public void Process(RecordingListItem item)
    {
        var elapsed = System.Diagnostics.Stopwatch.StartNew();
        AppLogger.Debug($"Preparation started. Folder={item.Folder}; PreserveRaw={settings.RecordingProcessing.PreserveRawFiles}");
        var metadata = item.Metadata;
        var rawRemote = GetRawPath(item.Folder, metadata.Files.RawRemote, "remote.raw.wav");
        var rawMicrophone = GetRawPath(item.Folder, metadata.Files.RawMicrophone, "microphone.raw.wav");
        var remote = Path.Combine(item.Folder, metadata.Files.Remote);
        var microphone = Path.Combine(item.Folder, metadata.Files.Microphone);
        var mixed = Path.Combine(item.Folder, metadata.Files.Mixed ?? "mixed.wav");

        var changed = false;

        if (File.Exists(rawRemote) && !IsArchiveReady(remote))
        {
            NormalizeToArchiveWav(rawRemote, remote, "remote");
            changed = true;
        }

        if (File.Exists(rawMicrophone) && !IsArchiveReady(microphone))
        {
            NormalizeToArchiveWav(rawMicrophone, microphone, "microphone");
            changed = true;
        }

        if (File.Exists(remote) && File.Exists(microphone) && !File.Exists(mixed))
        {
            CreateMixedWav(remote, microphone, mixed);
            metadata.Files.Mixed = Path.GetFileName(mixed);
            changed = true;
        }

        if (File.Exists(mixed) && metadata.Files.Mixed != Path.GetFileName(mixed))
        {
            metadata.Files.Mixed = Path.GetFileName(mixed);
            changed = true;
        }

        if (changed)
        {
            metadata.Files.RawRemote ??= Path.GetFileName(rawRemote);
            metadata.Files.RawMicrophone ??= Path.GetFileName(rawMicrophone);
            library.SaveMetadata(item.Folder, metadata);
        }

        AppLogger.Debug($"Preparation outputs saved. Elapsed={elapsed.Elapsed}; Folder={item.Folder}");
        if (!settings.RecordingProcessing.PreserveRawFiles)
        {
            DeleteIfArchiveExists(rawRemote, remote);
            DeleteIfArchiveExists(rawMicrophone, microphone);
        }
    }

    private static string GetRawPath(string folder, string? metadataPath, string fallbackName)
    {
        return Path.Combine(folder, string.IsNullOrWhiteSpace(metadataPath) ? fallbackName : metadataPath);
    }

    private static bool IsArchiveReady(string path)
    {
        if (!File.Exists(path)) return false;

        try
        {
            using var reader = new WaveFileReader(path);
            return reader.WaveFormat.SampleRate == ArchiveFormat.SampleRate
                && reader.WaveFormat.Channels == ArchiveFormat.Channels
                && reader.WaveFormat.BitsPerSample == ArchiveFormat.BitsPerSample;
        }
        catch
        {
            return false;
        }
    }

    private static void NormalizeToArchiveWav(string sourcePath, string destinationPath, string track)
    {
        var tempPath = destinationPath + ".tmp";
        AppLogger.Info($"Normalizing raw recording. Track={track}; Source={sourcePath}; Destination={destinationPath}");

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
            throw new NotSupportedException($"Cannot normalize {reader.WaveFormat.Channels}-channel audio for {track}.");
        }

        var resampled = new WdlResamplingSampleProvider(sampleProvider, ArchiveFormat.SampleRate);
        WaveFileWriter.CreateWaveFile16(tempPath, resampled);
        File.Move(tempPath, destinationPath, overwrite: true);

        AppLogger.Info($"Normalized raw recording complete. Track={track}; SizeBytes={new FileInfo(destinationPath).Length}");
    }

    private static void CreateMixedWav(string remotePath, string microphonePath, string destinationPath)
    {
        var tempPath = destinationPath + ".tmp";
        AppLogger.Info($"Creating mixed playback WAV. Remote={remotePath}; Microphone={microphonePath}; Destination={destinationPath}");

        using var remote = new AudioFileReader(remotePath);
        using var microphone = new AudioFileReader(microphonePath);

        ISampleProvider remoteProvider = remote;
        ISampleProvider microphoneProvider = microphone;

        AppLogger.Debug($"Mix inputs: remote={remote.TotalTime}, {remote.WaveFormat}; microphone={microphone.TotalTime}, {microphone.WaveFormat}");
        // Offline writing must stop at EOF, not generate endless silence.
        var mixer = new MixingSampleProvider([remoteProvider, microphoneProvider])
        {
            ReadFully = false
        };
        var reduced = new VolumeSampleProvider(mixer) { Volume = 0.5f };
        try
        {
            WaveFileWriter.CreateWaveFile16(tempPath, reduced);
            using (var verification = new WaveFileReader(tempPath))
            {
                var expected = remote.TotalTime > microphone.TotalTime ? remote.TotalTime : microphone.TotalTime;
                if (Math.Abs((verification.TotalTime - expected).TotalSeconds) > 0.1)
                    throw new InvalidDataException("Mixed audio duration does not match the source tracks.");
                AppLogger.Debug($"Mixed audio verified. Duration={verification.TotalTime}; Bytes={verification.Length}");
            }
            File.Move(tempPath, destinationPath, overwrite: true);
        }
        finally
        {
            if (File.Exists(tempPath)) File.Delete(tempPath);
        }

        AppLogger.Info($"Mixed playback WAV complete. SizeBytes={new FileInfo(destinationPath).Length}");
    }

    private static void DeleteIfArchiveExists(string rawPath, string archivePath)
    {
        if (!File.Exists(rawPath) || !File.Exists(archivePath))
        {
            return;
        }

        try
        {
            File.Delete(rawPath);
            AppLogger.Info($"Deleted raw recording: {rawPath}");
        }
        catch (Exception ex)
        {
            AppLogger.Warning($"Could not delete raw recording {rawPath}: {ex.Message}");
        }
    }
}
