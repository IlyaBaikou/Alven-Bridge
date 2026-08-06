# Alven Bridge

Alven Bridge connects one Alven Family Workspace to user-operated capabilities without exposing a home
network service to the internet.

The first supported capabilities are:

- private AI through Ollama or LM Studio's OpenAI-compatible endpoint;
- a mounted local or NAS folder as a later Family File Store capability.

Bridge is an outbound-only, revocable worker. The hosted Alven backend remains responsible for User and
Workspace authorization, typed job creation, result validation, and Smart Action settlement. A local AI
result is always an untrusted proposal and consumes zero managed Smart Actions.

## Quick start for development

Requirements: .NET 10 SDK and an Ollama or LM Studio endpoint.

```bash
cp .env.example .env
docker compose up --build
```

Open `http://127.0.0.1:7433`. The service starts unpaired and does no remote work until a valid control
plane URL and one-time Owner pairing code are supplied.

No production endpoint, credential, tenant identifier, or household fixture belongs in this repository.

## Status

This repository is an early security-first foundation. Compatibility and signed release channels will
be published before external-family installation.

See [SECURITY.md](SECURITY.md), [THREAT_MODEL.md](THREAT_MODEL.md), and [SUPPORT.md](SUPPORT.md).
