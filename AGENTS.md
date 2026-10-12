# AGENTS.md

Guidance for coding agents working on Recap.

## Project overview

Recap is a local Windows meeting recorder.

Core principles:

- No meeting-provider integration. Do not integrate with Teams, Google Meet, Zoom, calendar APIs, bots, browser extensions, or meeting agents for capture.
- Recording is controlled manually by the user: Start -> Stop.
- Audio and app data are stored locally.
- Azure STT and LLM features should be provider-based and replaceable.

## Repository structure

```text
src/Recap/          WPF .NET app
src/Recap/Assets/   app logo/icon assets
src/Recap/Recording audio capture and local recording library
docs/mockup/        UI mockups
docs/images/        source image assets, if present
```

## Build

```powershell
cd src/Recap
dotnet build
```

Target framework:

```text
net8.0-windows
```

Main dependencies:

- WPF
- NAudio 2.2.1

## Recording architecture

The POC records two separate WAV files per meeting:

```text
remote.wav       # WASAPI loopback / system audio / meeting participants
microphone.wav   # local microphone / user voice
metadata.json    # meeting metadata
```

Recordings are stored under:

```text
%LOCALAPPDATA%\Recap\Recordings\yyyy-MM-dd_HH-mm-ss\
```

The folder name is a stable technical ID based on local recording start time including seconds. User-visible meeting names are stored in `metadata.json`; renaming should not rename the folder.

## AI/STT provider design

All AI/STT integrations must be swappable.

Use these interfaces:

```text
src/Recap/Transcription/ITranscriptionProvider.cs
src/Recap/Transcription/IAiProvider.cs
```

Expected direction:

```text
remote.wav       -> ITranscriptionProvider -> timestamped segments labeled Others
microphone.wav   -> ITranscriptionProvider -> timestamped segments labeled Me
segments merged  -> IAiProvider            -> final transcript / summary
```

Azure may be the first implementation, but do not couple UI or domain logic directly to Azure SDKs. Put provider-specific code behind provider implementations.

Keep raw STT output locally so final transcripts can be regenerated later.

Suggested future files per meeting:

```text
raw-remote-transcript.json
raw-microphone-transcript.json
merged-transcript.md
final-transcript.md
summary.md
```

## UI direction

Initial POC UI follows:

```text
docs/mockup/poc-v1.html
```

Keep the POC simple:

- Start/Stop recording
- show elapsed timer
- show basic audio meters
- list previous recordings
- rename recording
- open recording folder
- placeholder Transcribe/Download button behavior

Live transcription is future work and should be optional. If implemented, it should be treated as a preview/convenience. The authoritative final transcript should be generated after recording from the saved local audio.

## Coding guidelines

- Keep capture logic separate from UI logic.
- Keep provider-specific Azure/LLM code separate from the app UI.
- Prefer small classes with clear responsibilities.
- Do not add cloud storage unless explicitly requested.
- Do not add meeting-provider integrations unless explicitly requested.
- Build after code changes when practical.
