# Grok Speech

Grok Speech is an optional cloud transcription provider. Azure Speech and Local Whisper remain available; adding Grok does not change the selected provider.

## Settings

Choose **Speech to text → Grok Speech** and enter your xAI API key in Settings. Do not paste credentials into chat. The key is stored using Recap's existing local settings storage (not encrypted by this integration); the local control API never returns it. Keep the settings file private.

Recap uses `grok-voice-transcribe-2.0` at `https://api.x.ai/v1/stt`. It uploads the saved prepared WAV tracks, one at a time, using the meeting's English or Danish language. Remote audio requests speaker diarization; microphone speech uses your configured name. Returned word timestamps and speaker labels are converted to Recap transcript segments. No local alignment model or GPU is needed for Grok processing.

The final Markdown transcript is assembled locally without an additional LLM call. Speaker names can be edited afterward. Original provider responses are saved beside each meeting as `provider-remote-response.json` and `provider-microphone-response.json`; normalized segments retain the existing `raw-*-transcript.json` filenames. Reprocessing removes both response and transcript results after confirmation, retaining audio.

## Costs and privacy

The published REST STT rate checked on 2026-10-16 is **USD $0.10 per audio hour**. Two 30-minute tracks therefore represent approximately one audio hour. Prices, account availability and quotas can change. No paid calls or real-audio uploads have been made to validate this integration.

Audio leaves the PC. The provider's documentation describes default 30-day API input/output retention and no training without explicit permission. The global endpoint does not guarantee a processing region. Review your team's retention settings, current terms and suitability for confidential meetings before uploading. This integration does not enable zero data retention or make a residency guarantee.

## Limits and failure handling

- The documented file limit is 500 MB per request; oversized tracks are rejected rather than silently split or truncated.
- HTTP 429 responses receive bounded backoff. Ambiguous network/server failures are not automatically replayed, to avoid duplicate billable requests.
- Cancellation stops the local HTTP operation, but cannot guarantee cancellation of server processing or its charges after upload.
- Upload/wait phases are shown without invented speech-progress percentages.
- There is no automatic fallback to another cloud provider.
- Real English/Danish accuracy, speaker accuracy, latency and account-specific limits still need an approved comparison test.

## Local API

`PATCH /settings` accepts `speechProviderId: "grok-speech"` and `grokSpeech: { "apiKey": "...", "model": "grok-voice-transcribe-2.0" }`. Credentials are write-only; `GET /settings` returns `grokSpeech.hasCredential`. Omitted keys are preserved. Normal meeting process, cancel, speaker and transcript endpoints apply. Provider responses can be downloaded through the authenticated meeting-file endpoints using `provider-remote-response` or `provider-microphone-response`.

## References

- [Speech-to-text API](https://docs.x.ai/developers/model-capabilities/audio/speech-to-text)
- [Pricing](https://docs.x.ai/developers/pricing)
- [Data handling](https://docs.x.ai/developers/faq/security)
