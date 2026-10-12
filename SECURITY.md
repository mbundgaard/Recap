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

Self-updating is not implemented. Current release drafts are unsigned. Do not treat
an unsigned download or a checksum from the same download source as cryptographic
proof of authenticity. Trusted automatic updates require independently verifiable
release signing, safe replacement/rollback, and preservation of settings and recordings.
