# Install Alven Bridge

This guide takes a new machine from Docker to a paired and verified Alven Bridge. The local setup page is
available only on the machine running Bridge unless you intentionally reach it through an SSH tunnel.
Do not expose it through a router or public reverse proxy.

## Before you begin

You need:

- Docker Desktop 4.30+ or Docker Engine 26+ with Compose v2, installed and running;
- a current macOS or Windows computer, or a 64-bit `amd64`/`arm64` Linux host;
- outbound HTTPS access to the Alven service and GitHub Container Registry;
- an Alven Family Owner account that can create a one-time pairing code;
- optional private AI and/or storage prepared using the linked capability guides.

Bridge itself needs about 100 MB plus a small state volume. Model files and family originals require
their own capacity. The current preview relay accepts files up to 5 MB each.

## macOS or Linux

Open Terminal in the parent folder where the Bridge installation should live, then run:

```bash
curl -fsSL https://raw.githubusercontent.com/IlyaBaikou/Alven-Bridge/main/scripts/install.sh | sh
```

The installer creates `./alven-bridge`, downloads the Compose files, pulls the image, starts Bridge, and
opens [http://127.0.0.1:7433](http://127.0.0.1:7433).

To use another installation folder:

```bash
ALVEN_BRIDGE_INSTALL_DIR=/srv/alven-bridge \
  sh -c 'curl -fsSL https://raw.githubusercontent.com/IlyaBaikou/Alven-Bridge/main/scripts/install.sh | sh'
```

The target folder must either be empty or already contain a Bridge installation.

## Windows

Start Docker Desktop, open PowerShell in the parent folder where Bridge should live, and run:

```powershell
irm https://raw.githubusercontent.com/IlyaBaikou/Alven-Bridge/main/scripts/install.ps1 | iex
```

The installer creates `alven-bridge`, starts the container, and opens the local setup page. To use a
different folder:

```powershell
$env:ALVEN_BRIDGE_INSTALL_DIR = "D:\Alven\Bridge"
irm https://raw.githubusercontent.com/IlyaBaikou/Alven-Bridge/main/scripts/install.ps1 | iex
```

## Complete the local wizard

Open [http://127.0.0.1:7433](http://127.0.0.1:7433) and complete all four steps:

1. **Choose capabilities.** Enable private AI, family storage, or both.
2. **Connect services.** Enter only the settings for the enabled capabilities and choose **Save and
   check**. Use the [private AI guide](AI_SETUP.md) or [storage guide](STORAGE_SETUP.md) for exact values.
3. **Pair this machine.** In the Alven app, open **More → Files & Smart Actions → Alven Bridge**, create
   a one-time code, and paste it into the wizard. The code expires after ten minutes and works once.
4. **Verify readiness.** Wait until pairing, Alven contact, and every enabled capability show healthy.

Cancelling or failing step 3 does not select Bridge as the family's storage. One Bridge installation is
paired with one Alven Family Workspace. Revoke or unpair it before moving the state to another family.

## Verify from the command line

From the installation folder, run:

```bash
./alven-bridge doctor
```

On Windows:

```powershell
.\alven-bridge.ps1 doctor
```

A ready installation confirms:

1. Docker and Compose configuration;
2. a healthy Bridge process;
3. pairing and recent Alven contact;
4. every enabled AI or storage capability.

You can also open `http://127.0.0.1:7433/health/live` for process health and
`http://127.0.0.1:7433/health/ready` for complete readiness.

## Finish setup in Alven

After Bridge is ready:

- private AI becomes available to the family and consumes zero Smart Actions;
- Bridge storage appears as **Personal storage** under **More → Files & Smart Actions**;
- selecting Personal storage changes the destination for new originals only;
- existing originals move only after an Owner starts and confirms an explicit migration.

## Remote Linux server

Run the macOS/Linux installer over SSH on the server. The wizard remains bound to the server's loopback
interface. On your own computer, create a temporary tunnel:

```bash
ssh -L 7433:127.0.0.1:7433 user@your-server
```

Keep that terminal open, then visit [http://127.0.0.1:7433](http://127.0.0.1:7433) locally and complete
the wizard. Close the tunnel when setup is finished. Normal Bridge work uses outbound HTTPS and does not
need the tunnel.

## Pin a released image

The one-line installer follows the current public preview. For a long-running home server, open the
[latest release](https://github.com/IlyaBaikou/Alven-Bridge/releases/latest), note the container tag, and
set it in the installation `.env`, for example:

```dotenv
ALVEN_BRIDGE_IMAGE=ghcr.io/ilyabaikou/alven-bridge:0.1.0
```

Then recreate the container:

```bash
docker compose pull
docker compose up -d
./alven-bridge doctor
```

Review release notes before changing major versions. Release archives include a SHA-256 checksum; the
published container supports Linux `amd64` and `arm64`.

## Update, back up, or remove

The installed helper keeps routine operations in one place:

```bash
./alven-bridge update
./alven-bridge backup
./alven-bridge logs
./alven-bridge uninstall
```

Use the `.ps1` helper on Windows. Uninstall keeps state and family files by default. Before purging local
state, disconnect Bridge in **More → Files & Smart Actions → Alven Bridge** or choose **Unpair this
machine** in the wizard. The destructive `--purge-local-state` option requires typing `PURGE` and still
does not remove the mounted family folder.

See [Operations and recovery](OPERATIONS.md) for backup, restore, rollback, and incident handling.

## If setup does not become ready

1. Run `./alven-bridge doctor` or `.\alven-bridge.ps1 doctor`.
2. Open the local wizard and read the capability card that needs attention.
3. Check recent logs with `./alven-bridge logs 100`.
4. Download the safe diagnostic JSON from the wizard if you need support.

Diagnostics intentionally omit prompts, responses, model names, endpoints, paths, file names,
credentials, and tokens. Never send the `.env`, state backup, NAS contents, or credentials to support.
