# Public release checklist

- CI formatting, tests, and container build are green.
- HomeOS control-plane compatibility tests are green for the advertised protocol.
- The image builds for `linux/amd64` and `linux/arm64`.
- macOS/Linux and Windows installers produce a clean local folder, preserve an existing `.env`, and
  open only the loopback setup page.
- The release contains the operator archive and matching SHA-256 checksum.
- The release tag is semantic, immutable, and created from reviewed `main`.
- Registry provenance and SBOM attestations exist for the published digest.
- A clean machine completes setup, pairing, AI-only, storage-only, and combined smoke tests.
- The four-step wizard hides irrelevant credentials, rejects an empty capability choice, reports an
  actionable failure for every enabled service, and downloads content-redacted diagnostics.
- Revocation, control-plane outage, local-model outage, replaced mount, disk-full, and restart recovery
  have been exercised without content-bearing logs.
- Upgrade from the previous supported minor and rollback to it preserve state.
- README system requirements, 5 MB preview relay limit, support policy, and security-reporting route are
  current.
- The Alven website links to the tagged release or repository, not an unpublished local artifact.
