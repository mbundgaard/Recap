# Local Whisper

Local Whisper is an optional Windows x64 speech provider using WhisperX 3.8.6, word alignment and community-1 speaker diarization. Recordings stay on your PC. Local speech recognition does not send audio to a cloud provider.

## Models and settings

In **Settings → Speech to text**, choose **Local Whisper**. English (`en-US`) defaults to Base and Danish (`da-DK`) to Medium. Each language offers Tiny (~75 MB), Base (~150 MB), Small (~500 MB), Medium (~1.5 GB) and Large v3 (~3 GB). These approximate model sizes exclude runtime, dependencies, alignment, speaker models and caches.

The meeting language selects its model. Device, precision, batch size, CPU threads and GPU index are shared. **Detect best settings for my PC** directly applies conservative hardware recommendations based on the larger selected model, without changing either model choice or showing a confirmation popup. Save settings to apply your selections. Choosing a model alone does not download it.

Speaker diarization is always enabled for remote audio. Labels are local to each meeting, and speaker renaming is optional. Remote speaker count 0 estimates the count. Overlap, noise and microphone echo can affect results.

## Automatic download and verification

Start processing a meeting. If the required runtime or model assets are missing, Recap downloads them automatically, verifies model loading offline, then continues local processing. First processing can download several GB and take a long time; an internet connection and sufficient disk space are required. Installed assets are reused on later runs.

Community-1 assets come from a GitHub mirror. **No Hugging Face login or token is required.** There are no manual Download/Verify buttons or token fields in Settings. The API retains optional manual setup for automation, without a model-download token; normal processing does not require that step.

Recognition runs locally after automatic offline verification. It does not silently switch to a cloud provider if setup fails. Per-language CPU/GPU readiness indicates which installations are verified; it does not mean models are loaded in memory. Changing backend or model, or updating engine assets, can require further downloads and verification.

## Resources and recording

- Allow at least 10 GB free for CPU or 16 GB for GPU setup, including caches. Multiple large models may need more; these are minimum checks, not total-size guarantees.
- CPU recognition can take longer than the recording, especially with Large v3. Lower batch sizes reduce memory demand.
- Automatic GPU recommendations require NVIDIA compute capability 7.5 or newer, driver major version 570 or newer and sufficient free GPU memory. Runtime verification still checks CUDA support.
- Advanced limits: batch size 1–32, CPU threads 1–64, GPU index 0–15, remote speaker count 0–30. CPU precision must be Auto, int8 or float32. Detection generally recommends half the logical cores, capped at 16, and a small batch.
- Local model work is serialized through an exclusive processor lock. This limits concurrency, not total RAM/VRAM consumption.
- Processing does not automatically pause recording. There is no automatic recording/transcription pause preference. Recording and processing can compete for resources.

## Storage, prepared audio and output

Python, models, caches, worker scripts and readiness receipts stay under:

```text
%LOCALAPPDATA%\Recap\LocalWhisper\
```

This is separate from the executable and does not modify a system Python installation. Meetings remain in the configured recordings directory, defaulting to `%LOCALAPPDATA%\Recap\Recordings\`.

Recognition uses prepared mono 16 kHz PCM16 WAV files, processing remote audio and microphone separately. It is not live transcription. Raw results stay local:

```text
raw-remote-transcript.json
raw-microphone-transcript.json
final-transcript.md
```

The local exporter creates timestamped Markdown without an LLM or mandatory speaker naming. Microphone speech uses your configured name or “You”. The output is a transcript, not an AI-generated summary. Optional cloud transcript/summary features remain separate.

## Benchmarks are not app readiness

The isolated faster-whisper benchmark environment (`tests/smoke-env`) and benchmark models (`models/smoke-*`) are test artifacts. A successful benchmark does not verify the app's full WhisperX runtime, alignment or community-1 diarization. Normal processing still prepares and verifies its required app assets.

## Worker lifetime

Workers and models load per operation. Persistent workers, model residency between operations and preload controls are **not implemented**; they are a separate next stage. Downloaded files on disk do not imply a model remains loaded in RAM or VRAM.
