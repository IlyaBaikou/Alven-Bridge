# Operations and recovery

Alven Bridge makes only outbound connections. Do not expose the wizard, local model, or mounted NAS
service to the public internet as part of an operations workaround.

## Health model

- `/health/live` proves the process can answer locally. It is suitable for container restart policy.
- `/health/ready` additionally requires pairing, a successful control-plane contact, and healthy
  enabled capabilities. It is expected to be unavailable during first-time setup.
- `/api/v1/diagnostics` is loopback-only and content-redacted. Capture it before restart when practical.

## Backup

Back up the family storage mount with the storage platform's snapshot or backup facility. Separately
back up the small Docker state volume while Bridge is stopped:

```bash
docker compose stop bridge
docker run --rm -v alven-bridge_bridge-state:/state:ro -v "$PWD":/backup alpine \
  tar -czf /backup/alven-bridge-state.tgz -C /state .
docker compose start bridge
```

The archive contains a credential. Store it encrypted with access restricted to the family operator.
Do not attach it to support requests.

## Restore

Restore onto a trusted host with Bridge stopped. Use an empty replacement volume, restore the archive,
start Bridge, and verify the local wizard and readiness endpoint. Also restore or mount the exact family
storage volume with its `.alven-bridge-mount-id` marker. A missing or changed marker must fail closed.

If Alven reports that the installation was revoked, discard the restored Bridge state and pair a new
installation. Do not restore an old credential over a newer active installation.

## Common incidents

- `control-plane-unavailable`: verify outbound HTTPS, system time, DNS, and the configured Alven URL.
- AI unavailable: verify the local endpoint from the Docker host and confirm the exact model is in the
  allowlist. Bridge never falls back to paid managed AI.
- Storage unavailable: verify the host mount and identity marker before restarting. Never recreate the
  marker manually to force a mount healthy.
- Pending receipt count grows: keep the state volume, restore control-plane access, and allow Bridge to
  replay completion safely. Receipts older than the configured retention period are removed because the
  corresponding bounded lease is no longer executable.
- Disk full: stop Bridge, free capacity outside the mounted family content, verify the mount, then start
  again. Do not delete credential or receipt files selectively.

## Upgrade and rollback

Use a stable semantic-version tag or immutable digest. Back up state, pull the target image, recreate
the container, then check readiness. Roll back the image without rolling back state unless the release
notes explicitly require it. Never cross a major protocol version without its migration guide.
