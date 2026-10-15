# Recap

**[Download the latest release for Windows (.exe)](https://github.com/mbundgaard/Recap/releases/latest/download/Recap-win-x64.exe)** - [Release notes](https://github.com/mbundgaard/Recap/releases/latest)

Recap records meetings on your Windows PC and turns them into transcripts with speaker
names and timestamps. It captures system audio and your microphone, so there's no bot
to invite and no meeting service or calendar to connect.

Download the executable and run it. No installer or separate .NET installation is needed.

## Recording and transcription

Choose your audio devices and spoken language, then start recording. Recap saves your
microphone and system audio as separate tracks on your computer. English and Danish
are available in the language selector. **Pause** stops saving audio from both tracks
and freezes the timer. **Resume** continues the same meeting, without the paused interval.

After recording, process a meeting yourself or enable automatic processing. Recap
prepares the audio and recognizes both tracks (in parallel with Azure; serialized locally). Meetings are processed
oldest first, with progress shown in the list. If something fails, you can retry without
reprocessing a track that has already finished.

You can pause automatic processing when your connection is unreliable and continue
later. Recording still works while processing is paused.

With Azure, once speech recognition is done, listen to short samples to identify the
speakers, then create the final transcript. The LLM is instructed to keep the original
language, include timestamps, and remove clear microphone echoes of remote speech.
Local Whisper creates timestamped Markdown locally without an LLM; naming speakers is optional.

Copy the result to your clipboard or save it as Markdown. You can also play back the
meeting to check a passage, or change the language and reprocess the saved audio.
Reprocessing replaces the existing speech results and generated transcript, not the audio.

Recognition and AI output can be wrong. Check important details against the recording.

## Other useful features

- Rename meetings and keep their recordings and transcripts together.
- Hide the meeting list for a compact recording window.
- Choose where recordings and logs are stored.
- Keep the original audio after preparation if you want it.
- Enable update checks and install updates when you're ready. Downloads are verified
  against a signed manifest, and the previous executable is restored if an update
  fails to start. Updates don't interrupt recording or processing.

## Let an agent control Recap

Click **AI** beside Settings to copy a short prompt with the local API URL, an access
token, and instructions for your agent. The agent can fetch `/skill` for the command
reference, then record, pause, stop, manage meetings, name speakers, create transcripts,
control playback, change settings, and manage updates.

The server listens only on `127.0.0.1`. Every request needs the token, including the
skill endpoint. The token grants control of the app, so only share the prompt with an
agent you trust. It isn't saved to disk or logs and changes when Recap restarts.
Provider credentials are write-only through the API. Deletion, reprocessing, updates,
and quitting require explicit confirmation fields.

See the [agent API reference](src/Recap/Agent/skill.md) for commands and safety guidance.

## Speech and LLM providers

Local Whisper is available in the development build; it has not yet been published in a release.

Choose **Azure Speech** or **Local Whisper** behind the replaceable `ITranscriptionProvider`
interface. Local Whisper defaults to Base for English and Medium for Danish, with separate
model choices for each language. Missing runtime and model files download automatically
when processing starts. First use can download several GB; offline verification then
runs automatically before local recognition. Community-1 speaker models come from a
GitHub mirror, with no Hugging Face login or token required.

Local workers and models load per operation; persistent model residency and preload
are not implemented. Processing does not automatically pause recording. Optional manual
setup remains available through the API for automation, without a model-download token.
See [Local Whisper](docs/local-whisper.md) for settings, storage and resource requirements.

The LLM integration is separate, behind `IAiProvider`. Other LLM APIs can be supported
by adding an adapter. The included adapter currently uses **Azure OpenAI's Responses
API**; generic chat-completion endpoints are not supported by that adapter yet.

Cloud providers require your own credentials and may charge for usage. Local Whisper
requires no speech-provider credentials.

## Your data

Recordings and app data are stored locally. Automatic processing is off by default.
Azure Speech processing sends audio to Azure. Cloud transcript generation sends recognized
text to the configured LLM provider. Local Whisper downloads its runtime and models as
needed, then recognizes prepared audio and creates Markdown locally without uploading
recordings or calling an LLM.

Recap doesn't add cloud storage for your recording library. Get any required consent
before recording or sending a conversation to a provider. Settings files contain API
keys, so don't include them in bug reports or screenshots.

See [security and privacy](SECURITY.md) for more detail.

## Get started

1. [Download the executable](https://github.com/mbundgaard/Recap/releases/latest/download/Recap-win-x64.exe) and run it.
2. Open **Settings** to enter your name, choose devices and storage, and configure your providers.
3. Choose the language and click **Start recording**.
4. Stop when you're done, then click **Process** or let automatic processing handle it.
5. With Azure, name detected speakers and create the transcript. Local Whisper creates it automatically; naming is optional. Copy or export the result.

Update checks are optional and can be enabled under **Settings - General - Updates**.
The executable is not Authenticode-signed, so Windows may show an unknown-publisher
warning. Download it from this repository's releases rather than an unofficial mirror.

## Build and test

Recap is built with WPF and .NET. On Windows with the .NET SDK installed, run:

```powershell
.\build.ps1
```

The single executable is written to `artifacts/win-x64/Recap.exe`.

Run the offline regression checks with:

```powershell
dotnet run --project tests/Recap.ProcessingTests --configuration Release
```

The checks use temporary data, a fake speech provider, and loopback HTTP requests.
They don't capture microphone audio or make Azure calls.
See [update signing and recovery](docs/updates.md) for updater maintenance.

## Licence

Recap source is available under the [MIT licence](LICENSE). Dependencies have separate
terms; see [third-party components](THIRD-PARTY-NOTICES.md).
