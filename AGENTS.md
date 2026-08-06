# Repository instructions

Alven Bridge is a security-sensitive, user-operated capability worker.

- Keep all network connections outbound from Bridge.
- Never add a production endpoint, secret, token, household payload, or private fixture.
- Keep AI and storage capabilities independently switchable.
- Treat every control-plane job, local model result, and mounted path as untrusted input.
- Preserve bounded leases, idempotency, cancellation, and content-redacted diagnostics.
- Use `apply_patch` for edits and run formatting, tests, and a container build before completion.
