# Connect private AI

Alven Bridge can process supported Alven requests with a model that runs on a trusted computer or home
server. Local processing uses zero Smart Actions. Alven still applies the same authorization, grounded
context, structured-output validation, and action confirmation rules as managed processing.

Bridge is not a general chat proxy. It receives bounded Alven jobs, returns structured proposals, and
makes only outbound connections to the Alven control plane.

## Choose a model server

| Server | Wizard type | Typical endpoint from Bridge |
| --- | --- | --- |
| Ollama | `Ollama` | `http://host.docker.internal:11434/v1/` |
| LM Studio | `LM Studio / OpenAI-compatible` | `http://host.docker.internal:1234/v1/` |
| Another compatible local server | `LM Studio / OpenAI-compatible` | its OpenAI-compatible `/v1/` base URL |

The endpoint must be reachable from the Bridge container. Do not enter `localhost`: inside Docker that
means the Bridge container itself. Use `host.docker.internal` for a service running on the Docker host.

## Ollama

1. Install and start [Ollama](https://ollama.com/).
2. Pull a model that can reliably produce structured JSON. For example:

   ```bash
   ollama pull qwen3:8b
   ```

3. Confirm Ollama is running:

   ```bash
   curl http://127.0.0.1:11434/api/tags
   ```

4. In the Bridge wizard enable **Use my private AI**, then enter:

   - **Private AI type:** `Ollama`
   - **Local AI endpoint:** `http://host.docker.internal:11434/v1/`
   - **Allowed model:** the exact Ollama model name, for example `qwen3:8b`

5. Choose **Save and check**. The AI readiness card must become healthy before Alven sends work.

On Linux, a host service bound only to `127.0.0.1` may not be reachable from Docker. Configure Ollama to
listen on the Docker host interface, keep port 11434 protected by the host firewall, and never expose an
unauthenticated Ollama endpoint to the public internet.

## LM Studio

1. Install [LM Studio](https://lmstudio.ai/), download a model, and load it.
2. Start its local OpenAI-compatible server.
3. Allow connections from the Docker host only; do not publish the server on the internet.
4. In the Bridge wizard enable **Use my private AI**, then enter:

   - **Private AI type:** `LM Studio / OpenAI-compatible`
   - **Local AI endpoint:** `http://host.docker.internal:1234/v1/`
   - **Allowed model:** the exact model identifier returned by LM Studio's `/v1/models` endpoint

5. Choose **Save and check** and wait for the AI readiness card.

Bridge currently expects the local OpenAI-compatible endpoint to be reachable without an API key from
the container. Keep it on a trusted host/network. A remote authenticated inference service needs an
explicitly supported credential boundary rather than putting its key into an unrelated field.

## Model selection

Use a recent instruction-tuned model with reliable multilingual understanding and structured JSON
output. The model identifier is an allowlist, not a friendly label: it must exactly match the model the
Alven job requests. Start with one model and validate real family examples before adding another.

Model files, model choice, and compute stay on the Bridge host. Safe diagnostics deliberately omit model
names and endpoints, so record the chosen model in your own private operations notes if needed.

## Verify the full path

1. Confirm the wizard shows **Ready** for private AI.
2. Run `./alven-bridge doctor` or `.\alven-bridge.ps1 doctor`.
3. In Alven, open **More → Files & Smart Actions → Alven Bridge** and verify that private processing is
   enabled and Bridge was seen recently.
4. Submit a small supported Alven request and review the proposed result before saving it.
5. Confirm the Smart Action balance did not decrease.

If Bridge is offline or the model fails, Alven does not silently spend a managed Smart Action as a
fallback.

## Troubleshooting

- **AI needs attention:** check that the model server is running and reachable from Docker.
- **Connection refused:** replace `localhost` with `host.docker.internal`; on Linux, review the server's
  bind address and firewall.
- **Model not allowed:** copy the exact identifier from Ollama or `/v1/models` into **Allowed model**.
- **Invalid structured result:** try a stronger instruction-tuned model with better JSON/schema support.
- **Slow or timed out:** choose a smaller model, verify available RAM/VRAM, and check host load.

Use `./alven-bridge logs 100` for recent operational errors. Logs and diagnostics must never contain
family prompts or responses; do not work around a failure by adding content logging.
