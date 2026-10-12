# Recap

Local Windows meeting recorder built with WPF and .NET 8. Capture system audio and
your microphone, recognize speech, name speakers, and create a final transcript.
No meeting-provider integration or calendar access is required.

## Getting started

Source: <https://github.com/mbundgaard/Recap>

Configure your own Azure Speech and Azure OpenAI credentials in **Settings** before
processing meetings. No shared credentials are included. Audio and app data stay on
your computer until you explicitly process a meeting or enable automatic processing;
recognition sends audio to Azure Speech and final transcript generation sends text
to the configured AI service. Provider charges and recording-consent requirements apply.

See [Security and privacy](SECURITY.md) before sharing settings, logs or recordings.

## Build a single executable

On Windows with the .NET 8 SDK installed, run from the repository root:

```powershell
.\build.ps1
```

The script produces only `artifacts/win-x64/Recap.exe` (Release,
self-contained); the target machine does not need .NET installed. It cleans
that output directory before publishing and fails if extra files remain.
For Windows ARM64, use `.\build.ps1 -Runtime win-arm64` (not covered by CI).
Specify a release version with `.\build.ps1 -Version 0.1.0`.

Native dependencies are bundled inside the executable and extracted by .NET
at runtime. Recordings and application settings remain separate local app data;
“single executable” applies to distribution, not runtime data.

## Settings

At startup, Recap uses `settings.json` beside `Recap.exe` if it exists.
Otherwise, it loads or creates `%LOCALAPPDATA%\Recap\settings.json`.
Changes are saved to the selected file. Command-line settings paths are not supported.

## Background processing

Automatic processing is off for new installations. When enabled in Settings, on
startup and after recording stops Recap prepares pending meetings and recognizes speech using the configured provider and each meeting's saved language
(English for older meetings without a language). This sends audio to the configured
speech service and may incur provider charges.

Meetings are queued one at a time; remote and microphone recognition run in parallel.
The meeting stays locked until prepared audio and both raw transcript files exist.
The progress bar estimates recognition progress from audio timestamps, so silence may
pause updates. Failed meetings show **Retry**; saving corrected settings also retries
failures. Successfully saved tracks are reused. Closing Recap cancels recognition and
waits for active work to stop; incomplete work resumes on the next launch.

In **Settings → General**, uncheck **Automatic meeting processing** and save to stop
active recognition and hold queued work until your connection is ready. Check it again to restart
unfinished tracks; completed tracks are retained. The choice survives app restarts. Local recording remains available while paused. An audio preparation step
already in progress finishes locally before pausing; queued preparation also waits.

Each meeting offers **Process** (or **Retry** after a failure) until its audio and
speech results are ready, then **Open**. Process queues only that meeting, even when
automatic processing is off, without changing the saved automatic-processing choice.
Active and queued meetings show disabled **Processing…** and **Queued** actions.

In meeting details, choose a language and use **Reprocess speech** to queue new
recognition through the same background processor. After confirmation, old speech
results, speaker names and generated transcripts/summaries are removed; audio is kept.
You can close the details window while processing continues. Saved speaker names
lock **Confirm speakers** until a name is edited.

Speaker naming and final transcript creation remain manual. Progress is based on
files on disk, not persisted workflow flags. Delete downstream results too when
redoing an earlier step, then reopen the app.

Use **Hide meetings** beside the Settings icon for a compact recorder-only window.
**Show meetings** restores the list and the previous window width. Recording and
background processing continue while the list is hidden.

Settings are grouped into **General** (name, automatic processing, storage and devices),
**Speech to text**, and **Transcript and summary**. Processing changes apply on Save.

## Offline processing checks

```powershell
dotnet run --project tests/Recap.ProcessingTests --configuration Release
```

These checks use a fake speech provider and temporary WAV files; no Azure calls occur.

## Releases

GitHub Actions builds and tests Windows x64 on pushes and pull requests, and scans
history for secrets. Push a version tag such as `v0.1.0` to generate a **draft** GitHub
Release containing the self-contained executable and its SHA-256 checksum.

Release drafts are currently **unsigned**. Before publishing a binary, review third-party
redistribution obligations and test on a clean Windows installation. A checksum is not
an authenticity signature. Code signing and an opt-in updater are future work; Recap
does not currently download or install updates automatically.

For a manual upgrade, stop recording/processing and close Recap, then replace only the
executable. Preserve `settings.json` and your recordings folder.

## Licence

Recap source is available under the [MIT licence](LICENSE). Dependencies have separate
terms; see [Third-party components](THIRD-PARTY-NOTICES.md).
