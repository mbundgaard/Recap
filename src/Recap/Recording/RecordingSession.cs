using System.IO;
using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace Recap.Recording;

public sealed class RecordingSession : IDisposable
{
    private readonly RecordingLibrary _library;
    private readonly AudioDeviceOption? _remoteDevice;
    private readonly AudioDeviceOption? _microphoneDevice;
    private readonly object _remoteLock = new();
    private readonly object _microphoneLock = new();
    private WasapiLoopbackCapture? _remoteCapture;
    private WasapiCapture? _microphoneCapture;
    private WaveFileWriter? _remoteWriter;
    private WaveFileWriter? _microphoneWriter;
    private RecordingMetadata? _metadata;
    private string? _folder;

    public event Action<float, float>? LevelsChanged;

    public RecordingSession(RecordingLibrary library, AudioDeviceOption? remoteDevice = null, AudioDeviceOption? microphoneDevice = null)
    {
        _library = library;
        _remoteDevice = remoteDevice;
        _microphoneDevice = microphoneDevice;
    }

    public RecordingMetadata Metadata => _metadata ?? throw new InvalidOperationException("Session has not started.");
    public string Folder => _folder ?? throw new InvalidOperationException("Session has not started.");

    public void Start(string transcriptionLanguage = "en-US")
    {
        var startedLocal = DateTime.Now;
        _folder = _library.CreateSessionFolder(startedLocal);

        _metadata = new RecordingMetadata
        {
            Id = Path.GetFileName(_folder),
            Title = "Untitled recording",
            StartedLocal = startedLocal,
            StartedUtc = startedLocal.ToUniversalTime(),
            Status = "recording",
            TranscriptionLanguage = transcriptionLanguage,
            Devices = new RecordingDevices
            {
                RemoteId = _remoteDevice?.Id,
                RemoteName = _remoteDevice?.Name,
                MicrophoneId = _microphoneDevice?.Id,
                MicrophoneName = _microphoneDevice?.Name
            }
        };

        _library.SaveMetadata(_folder, _metadata);

        using var devices = new MMDeviceEnumerator();
        var remoteDevice = _remoteDevice is null
            ? devices.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia)
            : devices.GetDevice(_remoteDevice.Id);
        _remoteCapture = new WasapiLoopbackCapture(remoteDevice);
        _metadata.Files.RawRemote = "remote.raw.wav";
        _metadata.Files.RawMicrophone = "microphone.raw.wav";
        _remoteWriter = new WaveFileWriter(Path.Combine(_folder, _metadata.Files.RawRemote), _remoteCapture.WaveFormat);
        _remoteCapture.DataAvailable += RemoteDataAvailable;
        _remoteCapture.StartRecording();

        var microphoneDevice = _microphoneDevice is null
            ? devices.GetDefaultAudioEndpoint(DataFlow.Capture, Role.Communications)
            : devices.GetDevice(_microphoneDevice.Id);
        _microphoneCapture = new WasapiCapture(microphoneDevice);
        _microphoneWriter = new WaveFileWriter(Path.Combine(_folder, _metadata.Files.RawMicrophone), _microphoneCapture.WaveFormat);
        _microphoneCapture.DataAvailable += MicrophoneDataAvailable;
        _microphoneCapture.StartRecording();
    }

    public void Stop()
    {
        if (_metadata is null || _folder is null)
        {
            return;
        }

        if (_remoteCapture is not null)
        {
            _remoteCapture.DataAvailable -= RemoteDataAvailable;
            try { _remoteCapture.StopRecording(); } catch { /* best effort */ }
        }

        if (_microphoneCapture is not null)
        {
            _microphoneCapture.DataAvailable -= MicrophoneDataAvailable;
            try { _microphoneCapture.StopRecording(); } catch { /* best effort */ }
        }

        DisposeAudio();

        _metadata.EndedLocal = DateTime.Now;
        _metadata.EndedUtc = _metadata.EndedLocal.Value.ToUniversalTime();
        _metadata.Status = "completed";
        _library.SaveMetadata(_folder, _metadata);
    }

    private void RemoteDataAvailable(object? sender, WaveInEventArgs e)
    {
        lock (_remoteLock)
        {
            _remoteWriter?.Write(e.Buffer, 0, e.BytesRecorded);
        }

        var remoteLevel = _remoteCapture is null ? 0 : AudioLevel.Calculate(e.Buffer, e.BytesRecorded, _remoteCapture.WaveFormat);
        LevelsChanged?.Invoke(remoteLevel, -1);
    }

    private void MicrophoneDataAvailable(object? sender, WaveInEventArgs e)
    {
        lock (_microphoneLock)
        {
            _microphoneWriter?.Write(e.Buffer, 0, e.BytesRecorded);
        }

        var microphoneLevel = _microphoneCapture is null ? 0 : AudioLevel.Calculate(e.Buffer, e.BytesRecorded, _microphoneCapture.WaveFormat);
        LevelsChanged?.Invoke(-1, microphoneLevel);
    }

    public void Dispose()
    {
        DisposeAudio();
    }

    private void DisposeAudio()
    {
        _remoteCapture?.Dispose();
        _remoteCapture = null;

        _microphoneCapture?.Dispose();
        _microphoneCapture = null;

        lock (_remoteLock)
        {
            _remoteWriter?.Dispose();
            _remoteWriter = null;
        }

        lock (_microphoneLock)
        {
            _microphoneWriter?.Dispose();
            _microphoneWriter = null;
        }
    }
}
