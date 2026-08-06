# Threat model

## Protected assets

- the installation credential and short-lived access token;
- minimal job payloads and returned proposals;
- mounted local or NAS content when storage is enabled;
- Workspace and installation identifiers;
- the integrity of capability, health, completion, and update reports.

## Trust boundaries

The Alven control plane is responsible for current User, Owner, Membership, and Workspace authorization.
Bridge is user-operated and may be stale, misconfigured, offline, or compromised. Ollama, LM Studio,
models, mounted storage, and model output are untrusted dependencies.

## Required controls

- outbound HTTPS only; no internet-facing Bridge, Ollama, LM Studio, or NAS listener;
- single-use short-lived pairing code and revocable per-installation credential;
- owner-only state storage and content-redacted logs;
- independently enabled AI and storage capabilities;
- bounded leased jobs with idempotent completion and replay rejection;
- minimum job context and no unrestricted Workspace export;
- typed result validation in the Alven control plane;
- mounted-root allowlist and traversal/symlink escape rejection for storage;
- signed releases, checksums, SBOM, compatibility checks, and manual rollback;
- no automatic paid-cloud fallback and no managed Smart Action charge for local AI.

## Explicit non-goals

Bridge is not a self-hosted Alven backend, VPN, remote shell, generic webhook runner, arbitrary agent,
home-automation controller, or general chat server.
