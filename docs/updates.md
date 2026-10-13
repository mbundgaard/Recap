# Updates: signing, installation and recovery

## User behaviour

- Update checks are off by default. Enable them in Settings or use Save and check for updates.
- Checks read GitHub's latest published stable release. No token, meeting data or provider credentials are sent.
- A visible update action asks for confirmation. No download/installation runs while recording, finishing a recording, processing meetings, or viewing meeting details.
- During a confirmed download, new recording/processing work is blocked. Closing the window cancels the download.
- Only Windows x64 single-file releases can install updates in place. The executable directory must be writable; no administrator privileges are requested.
- Upgrade older releases manually once to obtain the updater. Drafts, prereleases, same/older versions and unsigned manifests are not eligible.

## Trust and release assets

Each eligible release has:

- `Recap-win-x64.exe`
- `update-manifest.json` (UTF-8 without BOM)
- `update-manifest.sig` (base64 RSA-3072/SHA-256 PKCS#1 v1.5 signature of the exact JSON bytes)

Schema 1 signs the version, runtime, fixed executable name, byte length and SHA-256 hash.
The release tag must agree with the signed version. The client only follows HTTPS redirects
to explicitly allowed GitHub asset hosts and enforces metadata/package size limits.

`src/Recap/Updates/update-public-key.pem` is intentionally public and embedded into builds.
`RECAP_UPDATE_SIGNING_KEY` is the private PKCS#8 PEM stored as a GitHub Actions repository
secret. Never commit it, pass it as a command-line argument, attach it to releases, or print it.
`tools/Sign-Update.ps1` signs assets and checks that the private key matches the embedded
public key before writing the signature. Missing or mismatched signing material fails the build.
PR/CI builds do not need this secret. Release signing runs only for version-tag pushes.

The initial maintainer key also has a Windows CurrentUser DPAPI-encrypted local backup at
`%LOCALAPPDATA%\Recap\ReleaseSigning\update-signing-key.dpapi`. It is outside Git and
recoverable only with the corresponding Windows user protection keys. Keep an appropriate
secure recovery backup; GitHub does not allow downloading a saved Actions secret.

Key rotation needs a transition release signed by the old key that embeds the new public key,
or a manual reinstall of a trusted build. Do not silently replace the Actions secret alone.
Protect GitHub account access and the signing workflow, use MFA, and restrict trusted tag pushes.
A malicious workflow with access to the secret can sign arbitrary code.

Manifest signatures do not provide Authenticode publisher identity or bypass SmartScreen.
The executable is currently not Authenticode-signed. Binary redistribution obligations for
third-party components remain separate from update verification.

## Replacement and rollback

1. Download into a unique directory under `%LOCALAPPDATA%\Recap\Updates`.
2. Verify the signed manifest, length and hash; test install-directory write access.
3. Copy the current single-file executable into staging and launch it in restricted helper mode.
4. The helper verifies signatures, payload, parent process identity and start time, then signals readiness.
5. Recap exits. The helper waits for that exact process to exit and verifies the original executable has not changed.
6. Copy the verified payload onto the destination volume, recheck its hash, then atomically replace only the executable.
7. Keep the old executable as `<executable>.previous`. Start the new version and wait for its first-window-render acknowledgement.
8. If launch fails or no acknowledgement arrives, stop only the newly launched process, restore the previous executable, and restart it.

Settings, recordings, speaker maps and transcripts are not update targets. The new application
still performs its normal startup/settings loading; rollback is not a general database migration system.
The startup check confirms that the UI rendered, not that every feature is healthy.

Failure diagnostics are written to the staging directory's `update-error.txt`. If necessary,
close Recap and restore `<executable>.previous` manually. Do not delete or overwrite settings
or recordings when recovering. Old updater-owned staging files are cleaned on later downloads
after seven days; the previous installed executable is retained until the next replacement.

Internal helper arguments (`--apply-update`, `--update-complete`) are not general-purpose
installer commands. Settings-file command-line overrides remain unsupported.

## Tests

The offline test executable verifies signatures, wrong keys, malformed manifests, partial/tampered
packages, replacement rollback and preservation of nearby user files. A separate isolated manual
single-file integration test was used during implementation to verify the actual helper handoff,
restart and main-window acknowledgement without real recordings or cloud calls.
