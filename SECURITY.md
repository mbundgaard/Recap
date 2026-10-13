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
