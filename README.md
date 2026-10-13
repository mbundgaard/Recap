# Recap

**[⬇ Download the latest release for Windows (.exe)](https://github.com/mbundgaard/Recap/releases/latest/download/Recap-win-x64.exe)** · [Release notes](https://github.com/mbundgaard/Recap/releases/latest)

## Be present in the meeting. Come back to the details.

Recap turns conversations into readable transcripts—without adding a bot to your
meeting or connecting your calendar. Record on your Windows PC, put names to the
voices, and revisit what was actually said.

One executable. No installer or separate .NET installation needed.

## From conversation to transcript

### Record what you hear—and what you say

Capture system audio and your microphone as separate tracks. Start and stop on your
terms, watch the audio levels, and choose English or Danish before recording.
Recap records your computer's audio rather than integrating with a particular meeting
service: no meeting bots, browser extensions, or calendar permissions.

### Let the preparation happen in the background

Process meetings individually or enable automatic meeting processing. Recap prepares
the audio and recognizes both tracks in parallel, working through your oldest meetings
first. Progress bars show what's happening, and completed tracks are kept if you need
to retry.

On an unreliable connection? Pause automatic processing and return to it later.
You can keep recording while processing is paused.

### Put names to the voices

Listen to short speaker samples and assign names before creating the final transcript.
Meeting playback, a seek bar, and skip controls make it easy to revisit a moment.
Need a different recognition language? Change it and reprocess without recording again.

### Create something you can use

Generate an AI-assisted transcript with speaker names and timestamps. Recap asks the
AI to preserve the spoken language and remove clear microphone echoes of remote audio.
Review the result, copy it to your clipboard, or save it as Markdown for your notes,
project documentation, or follow-up.

Speech recognition and AI can make mistakes—review important details before relying
on or sharing a transcript.

## A small app that stays out of your way

- **Your meeting library:** rename recordings and return to the audio and transcripts
  when you need them.
- **Compact recorder:** hide the meeting list when you only want the recording controls.
  Background work continues.
- **Your devices:** choose the microphone and system-audio device that suit your setup.
- **Your storage:** choose where recordings and logs live, and whether to keep the
  original audio after preparation.
- **Updates on your terms:** opt into update checks, then choose **Update and restart**
  when you're ready. Signed update manifests verify downloads, and startup rollback
  helps recover from a failed update. Updates don't interrupt recording or processing.

## Local recordings. Clear choices about the cloud.

Recordings and app data are stored on your computer. Automatic meeting processing is
off by default—you decide when to process a meeting or enable background processing.

Transcription is **not entirely offline**:

- Speech recognition sends audio to **Azure Speech**.
- Final transcript generation sends recognized text to **Azure OpenAI**.
- You supply your own provider credentials; service charges may apply.

Recap doesn't add cloud storage for your recording library. Obtain any required consent
before recording or sending a conversation to a provider, and don't share settings files
containing API keys.

Read more about [security and privacy](SECURITY.md).

## Get started

1. **[Download the Windows executable](https://github.com/mbundgaard/Recap/releases/latest/download/Recap-win-x64.exe)** and run it.
2. Open **Settings** to choose your devices, storage, name, and speech/AI credentials.
3. Choose the spoken language and click **Start recording**.
4. Stop when you're done, then click **Process**—or let automatic processing handle it.
5. Name any detected speakers, create the transcript, and copy or export it.

The Windows executable is not Authenticode-signed, so Windows may show an
unknown-publisher warning. Download from this repository's releases, not an unofficial mirror.

## For developers

Recap is built with WPF and .NET. Audio capture, speech recognition, and AI generation
are kept separate, with provider interfaces for replaceable integrations.

Build a self-contained executable on Windows with the .NET SDK:

```powershell
.\build.ps1
```

Output: `artifacts/win-x64/Recap.exe`.

Run the offline regression checks:

```powershell
dotnet run --project tests/Recap.ProcessingTests --configuration Release
```

These checks use temporary data and a fake speech provider; no Azure calls are made.
For updater maintenance, see [update signing and recovery](docs/updates.md).

## Licence

Recap source is available under the [MIT licence](LICENSE). Dependencies have separate
terms; see [third-party components](THIRD-PARTY-NOTICES.md).
