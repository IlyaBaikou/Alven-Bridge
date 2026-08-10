# Threat model

## Protected assets

- the installation credential and short-lived access token;
- minimal job payloads and returned proposals;
- mounted local/NAS content and scoped WebDAV or S3 credentials when storage is enabled;
- Workspace and installation identifiers;
- the integrity of capability, health, completion, and update reports.

## Trust boundaries

The Alven control plane is responsible for current User, Owner, Membership, and Workspace authorization.
Bridge is user-operated and may be stale, misconfigured, offline, or compromised. Ollama, LM Studio,
models, mounted storage, WebDAV/S3 services, and model output are untrusted dependencies.

## Required controls

- outbound HTTPS only; no internet-facing Bridge, Ollama, LM Studio, or NAS listener;
- single-use short-lived pairing code and revocable per-installation credential;
- owner-only state storage and content-redacted logs;
- independently enabled AI and storage capabilities;
- bounded leased jobs with idempotent completion and replay rejection;
- minimum job context and no unrestricted Workspace export;
- typed result validation in the Alven control plane;
- mounted-root allowlist and traversal/symlink escape rejection for storage;
- normalized relative object keys, configured-prefix confinement, bounded payloads, authenticated
  requests, and no credential-bearing URLs for WebDAV/S3 storage;
- persistent mount identity validation so a missing or replaced disk fails closed;
- reserved Bridge metadata paths that control-plane jobs cannot read, overwrite, or delete;
- same-origin setup nonce for every local wizard mutation and loopback-only Docker port binding;
- crash-safe local job receipts so a failed completion request cannot repeat a write or model call;
- job-expiry cancellation propagated into local AI and storage work;
- local unpair revokes the server installation before erasing its credential; an unavailable control
  plane leaves the credential in place unless the operator explicitly confirms a local-only reset, which
  is shown with a required follow-up server-revocation warning;
- loopback Host and Origin checks on the wizard, configuration, diagnostics, pairing, and status surface;
- installation/job replay detection binds an idempotency receipt to capability and payload fingerprint;
- diagnostic output is content-redacted and excludes paths, models, endpoints, tokens, and job payloads;
- saved remote-storage secrets are owner-readable, never returned by the setup API after saving, and
  never emitted by health or diagnostics;
- signed releases, checksums, SBOM, compatibility checks, and manual rollback;
- no automatic paid-cloud fallback and no managed Smart Action charge for local AI.

## Explicit non-goals

Bridge is not a self-hosted Alven backend, VPN, remote shell, generic webhook runner, arbitrary agent,
home-automation controller, or general chat server.
