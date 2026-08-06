# Contributing

Keep changes narrow and reviewable. Every protocol, credential, filesystem, networking, logging, update,
or AI-output change requires tests and a threat-model review.

Before opening a change:

```bash
dotnet format Alven.Bridge.slnx --verify-no-changes
dotnet test Alven.Bridge.slnx
docker build .
```

Use synthetic fixtures only. Never commit a real control-plane URL, pairing code, installation secret,
token, prompt, model output, mount path, or household file.
