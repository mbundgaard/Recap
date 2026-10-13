using System.Diagnostics;
using NAudio.Wave;

namespace Recap.Recording;

// Both tracks use the same active-time clock. Paused audio is discarded, not buffered.
public sealed class RecordingTimeline : IDisposable
{
    private readonly object _gate = new();
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private readonly WaveFileWriter[] _writers;
    private bool _paused;
    private bool _stopped;
    private bool _disposed;

    public RecordingTimeline(WaveFileWriter remote, WaveFileWriter microphone) => _writers = [remote, microphone];
    public TimeSpan Elapsed { get { lock (_gate) return _clock.Elapsed; } }
    public bool IsPaused { get { lock (_gate) return _paused; } }

    public void Pause()
    {
        lock (_gate)
        {
            if (_stopped || _paused) return;
            _clock.Stop();
            _paused = true;
            PadTracks();
        }
    }

    public void Resume()
    {
        lock (_gate)
        {
            if (_stopped || !_paused) return;
            _paused = false;
            _clock.Start();
        }
    }

    public void Write(int track, byte[] buffer, int count)
    {
        lock (_gate)
        {
            if (_paused || _stopped) return;
            var writer = _writers[track];
            var block = writer.WaveFormat.BlockAlign;
            count -= count % block;
            var end = Position(writer);
            var start = end - count;
            Pad(writer, Math.Max(0, start));
            // Discard the part of a capture packet that overlaps a pause/start boundary.
            var skip = (int)Math.Min(count, Math.Max(0, writer.Length - start));
            if (count > skip) writer.Write(buffer, skip, count - skip);
        }
    }

    public void Stop()
    {
        lock (_gate)
        {
            if (_stopped) return;
            _clock.Stop();
            _stopped = true;
            PadTracks();
        }
    }

    private long Position(WaveFileWriter writer) =>
        (long)(_clock.Elapsed.TotalSeconds * writer.WaveFormat.SampleRate) * writer.WaveFormat.BlockAlign;

    private void PadTracks()
    {
        foreach (var writer in _writers) { Pad(writer, Position(writer)); writer.Flush(); }
    }

    private static void Pad(WaveFileWriter writer, long position)
    {
        if (position <= writer.Length) return;
        var silence = new byte[writer.WaveFormat.BlockAlign * 4096];
        if (writer.WaveFormat.Encoding == WaveFormatEncoding.Pcm && writer.WaveFormat.BitsPerSample == 8)
            Array.Fill(silence, (byte)128);
        while (writer.Length < position)
            writer.Write(silence, 0, (int)Math.Min(silence.Length, position - writer.Length));
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            Stop();
            foreach (var writer in _writers) writer.Dispose();
            _disposed = true;
        }
    }
}
