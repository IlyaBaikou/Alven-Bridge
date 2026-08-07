# Security policy

Do not open a public issue for a suspected vulnerability, leaked credential, authentication bypass, or
household-data exposure. Until a dedicated security address is published, use GitHub private
vulnerability reporting for this repository.

Never include pairing codes, installation credentials, access tokens, prompts, file contents, private
filenames, provider payloads, or production endpoints in a report.

During public preview, the current minor release receives security fixes. A critical fix may also be
backported to the immediately previous minor for 30 days after the new minor ships. `latest` always
points to the newest stable tag; preview and release-candidate tags never move `latest`.

Published images include registry provenance and an SBOM. Installations that require reproducibility
should pin the immutable digest shown by the release rather than relying on a mutable tag.
