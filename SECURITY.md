# Security and privacy

Report vulnerabilities through this repository's private vulnerability reporting
feature rather than a public issue. Do not include API keys, recordings or transcripts
in public issues, screenshots, logs or pull requests.

Recap stores recordings and settings locally. Settings currently store provider keys
in plaintext JSON protected by the user's filesystem permissions. Treat settings,
including backups, as credentials. Cloud speech/AI features send the selected audio
or transcript to the configured service and may incur charges. Obtain any required
consent before recording or sending meeting content to a provider.

Logs can contain local paths, resource endpoints and service diagnostics. Review and
redact them before sharing. Recognized speech text is not deliberately written to
ordinary progress logs.

## Local Whisper

Processing with Local Whisper automatically downloads missing Python dependencies and
selected models. Initial setup can transfer several GB. Downloads contact public package/model
hosts and GitHub; recognition itself uses local prepared audio, with Hugging Face offline mode
and a Python socket-connect guard. The guard is not an operating-system network sandbox.
Recap does not fall back to cloud transcription if local processing fails.

Community-1 is mirrored with attribution in this repository. The app fetches it from an
immutable commit and verifies every asset against an embedded size/SHA-256 manifest.
No Hugging Face token is required. The uv bootstrap archive is hash checked and Python
packages are hash locked. Other language/transcription models come from their upstream
hosts; their cache readiness checks are not an independent full cryptographic audit.

Runtime files and model caches live under `%LOCALAPPDATA%\\Recap\\LocalWhisper`.
The Python environment isolates dependencies, not operating-system privileges: it runs as
the current user and loads third-party code/model files. Do not substitute untrusted models
or dependencies. Local intermediate transcripts contain meeting content and must remain private.

## Local agent API

Recap starts a local HTTP listener on a randomly assigned IPv4 loopback port. It never
binds to a LAN interface. All endpoints require a random 256-bit bearer token, including
`/skill`. The AI button deliberately copies that token into the clipboard prompt.
The token is kept in memory, is not returned by HTTP, and expires when the app exits.
Clipboard history or clipboard-sync software may retain the copied prompt. Share it only
with a trusted agent, keep it out of logs and repositories, and restart Recap to revoke it.

This token grants broad app control, including recording, deleting meetings and changing
provider configuration. It is not a read-only integration. Confirmation fields are
required for destructive actions but are not proof of human consent; the agent must
obtain authorization from the owner. Meeting text and names must be treated as data,
not instructions. The skill endpoint explains this boundary.

The server rejects browser Origin headers, non-loopback Host headers and query parameters.
It does not enable CORS, serve an unauthenticated web UI, expose a shell, or accept arbitrary
file paths. Requests are bounded, known meeting-file downloads are authenticated, and
provider key fields are write-only. Runtime HTTP logging is disabled. Other applications
running as the same Windows user are not a security boundary: they may access the clipboard
or the user's existing settings files. Do not proxy or forward this listener onto a network.

## Updates

The opt-in updater accepts only newer stable releases with an RSA-SHA256 manifest
signature that verifies against the public key embedded in the installed executable.
It also verifies the package's signed size and SHA-256 hash before replacement.
The private signing key is not in this repository. Releases lacking valid signatures
are not installable through the updater.

Manifest signing is distinct from Windows Authenticode: executables currently have
no Authenticode certificate and may show unknown-publisher warnings. A plain checksum
alone does not establish authenticity. Protect the GitHub account, signing secret and
release workflow: access to run a modified signing workflow can compromise updates.
Startup acknowledgement supports rollback but is not a complete application health test.
See [Update signing and recovery](docs/updates.md).
