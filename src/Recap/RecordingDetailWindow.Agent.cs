using System.IO;
using Recap.Agent;
using Recap.Recording;

namespace Recap;

public partial class RecordingDetailWindow
{
    internal RecordingListItem MeetingItem => _item;
    internal bool IsSpeechProcessing => IsProcessing;
    internal void RefreshAgentView() => RefreshView();
    internal object AgentSpeakers() => new
    {
        confirmed = SpeakerNamesSaved || Speakers.Count == 0,
        speakers = Speakers.Select(speaker => new
        {
            speaker.SourceLabel, speaker.DisplayName, speaker.SampleIndex,
            samples = speaker.Samples.Select((sample, index) => new
            {
                index, startSeconds = sample.Start.TotalSeconds, endSeconds = sample.End.TotalSeconds, sample.Text
            }).ToArray()
        }).ToArray()
    };

    internal void SaveAgentSpeakers(Dictionary<string, string> names)
    {
        if (IsBusy) throw new AgentException(409, "The meeting is busy.");
        if (names.Count != Speakers.Count || Speakers.Any(x => !names.ContainsKey(x.SourceLabel)
            || !IsLocalTranscript && string.Equals(names[x.SourceLabel], x.SourceLabel, StringComparison.OrdinalIgnoreCase)))
            throw new AgentException(400, "Supply a name for every detected speaker, using the source labels from GET speakers.");
        foreach (var speaker in Speakers) speaker.DisplayName = names[speaker.SourceLabel];
        SaveSpeakerMap();
        RefreshView();
    }

    internal object AgentPlaybackState() => new
    {
        state = _player.PlaybackState.ToString().ToLowerInvariant(),
        file = _player.SourcePath is null ? null : Path.GetFileName(_player.SourcePath),
        positionSeconds = _player.Position.TotalSeconds,
        durationSeconds = _player.NaturalDuration.HasTimeSpan ? _player.NaturalDuration.TimeSpan.TotalSeconds : 0
    };

    internal object ControlAgentPlayback(AgentArguments args)
    {
        var action = args.Text("action");
        switch (action)
        {
            case "play":
                var track = args.Text("track", "mixed");
                var name = track switch
                {
                    "mixed" => _item.Metadata.Files.Mixed, "remote" => _item.Metadata.Files.Remote,
                    "microphone" => _item.Metadata.Files.Microphone,
                    _ => throw new AgentException(400, "Track must be mixed, remote, or microphone.")
                };
                if (name is null || !File.Exists(Path.Combine(_item.Folder, name))) throw new AgentException(404, "Prepare the audio before playing it.");
                ResetSampleButton();
                _player.Open(new Uri(Path.GetFullPath(Path.Combine(_item.Folder, name))));
                RequirePlayback();
                _isMixedPlaybackLoaded = track == "mixed";
                _isMixedPlaybackPlaying = _isMixedPlaybackLoaded;
                _player.Position = TimeSpan.FromSeconds(Nonnegative(args.Number("positionSeconds")));
                _player.Play(); _playbackTimer.Start();
                PlayMixedButton.Content = _isMixedPlaybackPlaying ? "Pause" : "▶";
                break;
            case "pause":
                RequirePlayback(); ResetSampleButton(); _player.Pause(); _isMixedPlaybackPlaying = false;
                PlayMixedButton.Content = "▶"; break;
            case "resume":
                RequirePlayback(); _player.Play(); _playbackTimer.Start();
                _isMixedPlaybackPlaying = _isMixedPlaybackLoaded;
                PlayMixedButton.Content = _isMixedPlaybackPlaying ? "Pause" : "▶"; break;
            case "stop":
                ResetSampleButton(); _player.Stop(); _isMixedPlaybackPlaying = false;
                PlayMixedButton.Content = "▶"; break;
            case "seek":
                RequirePlayback(); ResetSampleButton();
                var seconds = args.Has("positionSeconds") ? Nonnegative(args.Number("positionSeconds"))
                    : _player.Position.TotalSeconds + args.Number("offsetSeconds");
                if (Math.Abs(seconds) > TimeSpan.MaxValue.TotalSeconds / 2) throw new AgentException(400, "Invalid position.");
                _player.Position = TimeSpan.FromSeconds(seconds); break;
            case "sample":
                var speaker = Speakers.FirstOrDefault(x => x.SourceLabel == args.Text("speaker"))
                    ?? throw new AgentException(404, "Speaker not found.");
                var index = args.Number("sampleIndex", speaker.SampleIndex);
                if (index < 0 || index >= speaker.Samples.Count || index != Math.Truncate(index)) throw new AgentException(400, "Invalid sample index.");
                var sample = speaker.Samples[(int)index];
                ResetSampleButton();
                speaker.MoveSample((int)index - speaker.SampleIndex);
                var path = Path.Combine(_item.Folder, _item.Metadata.Files.Remote);
                if (!File.Exists(path)) throw new AgentException(404, "Remote audio is missing.");
                _player.Open(new Uri(Path.GetFullPath(path))); RequirePlayback();
                _isMixedPlaybackLoaded = false; _isMixedPlaybackPlaying = false;
                PlayMixedButton.Content = "▶";
                _player.Position = sample.Start;
                _sampleStopAt = DateTime.Now + (sample.End - sample.Start);
                _player.Play(); _sampleStopTimer.Start(); break;
            default: throw new AgentException(400, "Unknown playback action.");
        }
        return AgentPlaybackState();
    }

    private void RequirePlayback()
    {
        if (!_player.NaturalDuration.HasTimeSpan) throw new AgentException(409, "Audio could not be opened. Check the file and output device.");
    }
    private static double Nonnegative(double value)
    {
        if (value < 0 || value > TimeSpan.MaxValue.TotalSeconds / 2) throw new AgentException(400, "Invalid position.");
        return value;
    }
}
