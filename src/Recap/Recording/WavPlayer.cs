using NAudio.Wave;
using System.Windows;
using System.Windows.Threading;
using Recap.Logging;

namespace Recap.Recording;

/// <summary>Local WAV playback using the Windows default output device.</summary>
public sealed class WavPlayer
{
    private readonly Dispatcher _dispatcher = Dispatcher.CurrentDispatcher;
    private AudioFileReader? _reader;
    private WaveOutEvent? _output;
    public event EventHandler? MediaOpened;
    public event EventHandler? MediaEnded;
    public event Action<Exception>? Failed;
    public PlaybackState PlaybackState => _output?.PlaybackState ?? PlaybackState.Stopped;
    public string? SourcePath { get; private set; }
    public Duration NaturalDuration => _reader is null ? Duration.Automatic : new Duration(_reader.TotalTime);
    public TimeSpan Position
    {
        get => _reader?.CurrentTime ?? TimeSpan.Zero;
        set
        {
            if (_reader is not null)
                _reader.CurrentTime = TimeSpan.FromSeconds(Math.Clamp(value.TotalSeconds, 0, _reader.TotalTime.TotalSeconds));
        }
    }

    public void Open(Uri uri)
    {
        Close();
        try
        {
            _reader = new AudioFileReader(uri.LocalPath);
            SourcePath = uri.LocalPath;
            _output = new WaveOutEvent { DesiredLatency = 100 };
            var output = _output;
            output.Init(_reader);
            output.PlaybackStopped += (_, args) => _dispatcher.BeginInvoke(new Action(() =>
            {
                if (_output != output) return;
                if (args.Exception is not null) ReportFailure(args.Exception);
                else MediaEnded?.Invoke(this, EventArgs.Empty);
            }));
            AppLogger.Debug($"Playback opened: {uri.LocalPath}; Duration={_reader.TotalTime}; Format={_reader.WaveFormat}; Output=Windows default");
            MediaOpened?.Invoke(this, EventArgs.Empty);
        }
        catch (Exception ex)
        {
            Close();
            ReportFailure(ex);
        }
    }

    public void Play()
    {
        try
        {
            if (_reader is not null && _reader.Position >= _reader.Length) _reader.Position = 0;
            _output?.Play();
            AppLogger.Debug($"Playback started at {Position}.");
        }
        catch (Exception ex) { ReportFailure(ex); }
    }
    public void Pause() => _output?.Pause();
    public void Stop() { _output?.Stop(); Position = TimeSpan.Zero; }
    public void Close()
    {
        var output = _output;
        _output = null;
        output?.Dispose();
        _reader?.Dispose();
        _reader = null;
        SourcePath = null;
    }
    private void ReportFailure(Exception exception)
    {
        AppLogger.Error(exception, "Audio playback failed.");
        Failed?.Invoke(exception);
    }
}
