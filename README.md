# Runner Room

A small dashboard for GitHub Actions runners on your Linux server. It shows which runners are **On**, **Off**, or **Unknown**, and refreshes every five seconds.

C# / ASP.NET Core backend. Plain HTML, CSS, and JavaScript frontend.

## Install

Run this on the server where your runners live. Change the folder at the end:

```bash
curl -fsSL https://github.com/igor-couto/github-self-hosted-runner-manager/releases/latest/download/install.sh \
  | sudo bash -s -- --runners /srv/actions-runners
```

The folder should contain your runner installations:

```text
/srv/actions-runners/
  runner-one/    # .runner, bin/Runner.Listener, ...
  runner-two/
```

The installer downloads the correct Linux release, checks its SHA-256 checksum, configures the folder, creates a systemd service, enables startup after reboot, and prints the dashboard URL. **Users do not need to install .NET, Git, or Docker.** The .NET runtime is included.

Open `http://YOUR_SERVER_IP:8080` on your local network.

Supported: glibc-based Linux with systemd, on **x64, ARM32 (ARMv7+), or ARM64** (for example Debian 12 or Ubuntu 22.04/24.04 where that architecture is available). Standard utilities including Bash, curl, tar, sha256sum, and util-linux must be present. The installer detects both CPU and userspace bitness, including Raspberry Pis with a 64-bit kernel and 32-bit OS. ARMv6 devices, Alpine/musl, and normal isolated Docker containers are not supported installation targets.

> Maintainers: this command becomes available after the first release containing these installer changes is published. See **Publishing releases** below.

### Optional settings

```bash
curl -fsSL https://github.com/igor-couto/github-self-hosted-runner-manager/releases/latest/download/install.sh \
  | sudo bash -s -- --runners /home/igor/runners --user igor --port 8090
```

| Option | Default | Meaning |
| --- | --- | --- |
| `--runners` | Required | Parent runner folder, or one runner installation |
| `--user` | Runner folder owner | Existing Linux account that runs your runners |
| `--port` | `8080` | Dashboard port, between 1024 and 65535 |
| `--version` | `latest` | Specific release, for example `v0.1.0` |

If the parent folder is owned by root, supply `--user` explicitly. Using the runner account lets the dashboard inspect its processes. Unknown means permissions or process visibility prevent a reliable answer.

To inspect the script before running it instead:

```bash
curl -fsSL https://github.com/igor-couto/github-self-hosted-runner-manager/releases/latest/download/install.sh -o install.sh
less install.sh
sudo bash install.sh --runners /srv/actions-runners
```

## Updating and configuration

Run the **same install command** again to update. It reapplies the folder, user, and port from those arguments, so keep any custom flags. If startup fails, the installer restores the previous binary, configuration, and service.

Settings are stored in `/etc/runner-room/runner-room.env`. You can edit them and restart:

```bash
sudoedit /etc/runner-room/runner-room.env
sudo systemctl restart runner-room
```

Check status or application logs:

```bash
sudo systemctl status runner-room
sudo journalctl -u runner-room -n 50 --no-pager
```

The service runs as the selected account. Application files live under `/opt/runner-room/releases`, with `/opt/runner-room/current` pointing to the active installation. Previous versions are retained for rollback. The installer only manages Runner Room; it does not change your GitHub runner services.

This prototype has no login and is intended for a trusted LAN. If your firewall blocks the chosen port, allow it from your home subnet. The installer does not change firewall rules or expose the dashboard through your router.

## What the status means

- **On:** a local `Runner.Listener` or `Runner.Worker` executable from that folder is running.
- **Off:** no matching process was found.
- **Unknown:** a reliable process check was not possible.

On describes a local process, not a confirmed connection to GitHub. Discovery uses runner installations in the configured folder's immediate children; symbolic-link child folders are skipped. If it finds no child runners, it checks the configured folder itself as a single installation. Leftover `.runner` metadata in a parent folder therefore cannot hide its child runners. It reads runner names from `.runner` and process identities from `/proc`, never credentials, job workspaces, or diagnostic logs. It cannot start or stop runners.

## Publishing releases (maintainers only)

The release workflow builds self-contained packages for all three targets and uploads them, their checksums, and `install.sh` to a GitHub Release:

| Target | Release asset |
| --- | --- |
| x64 | `runner-room-linux-x64.tar.gz` |
| ARM32 (ARMv7+) | `runner-room-linux-arm.tar.gz` |
| ARM64 | `runner-room-linux-arm64.tar.gz` |

All three can be cross-published on GitHub's `ubuntu-latest` x64 runner; ARM hardware is not required to build them. The installer always downloads the latest published release unless `--version` is supplied.

For the first release, commit and push the installer changes, then push a version tag:

```bash
git tag v0.1.0
git push origin v0.1.0
```

Wait for the **Release** workflow to succeed. After that, the one-command installer URL works. For later releases, push a new tag such as `v0.1.1`. The public installer requires a public repository and release; it does not request GitHub credentials.

If a release failed on an older commit, push the fix and a **new version tag**. Re-running the old workflow uses the original tagged code and will repeat the same error.

### Optional Raspberry Pi build runner

Use a Raspberry Pi with a **64-bit Linux OS** and a registered GitHub self-hosted runner. Give it the custom label `runner-room-arm-builder`. Then create this repository **Actions variable** under Settings → Secrets and variables → Actions → Variables:

```text
Name:  ARM_RUNNER_LABELS
Value: ["self-hosted","linux","ARM64","runner-room-arm-builder"]
```

Both ARM32 and ARM64 package jobs will run on that Pi; one registered runner processes them sequentially. The workflow installs the .NET 9 SDK through `setup-dotnet`. The Pi needs the usual GitHub runner prerequisites plus Git, Bash, tar, and sha256sum. It does not need Docker or ShellCheck: validation, x64 installer tests, and release publication stay on GitHub-hosted runners.

Leave the variable unset to build everything on GitHub-hosted runners. Set it only once the Pi runner is online and has all four labels; otherwise the ARM jobs wait in the queue.

## Development

Requires the .NET 9 SDK:

```bash
dotnet run -- --Demo true --urls http://127.0.0.1:8080
```

Open `http://localhost:8080`. Demo data is explicitly labeled. To inspect real Linux runners, replace `--Demo true` with `--RunnersRoot /path/to/runners`.

Build with `dotnet build`. For a Docker demo:

```bash
docker build -t runner-room .
docker run --rm -p 127.0.0.1:8080:8080 runner-room --Demo true
```

For installer tests:

```bash
dotnet publish -c Release -r linux-x64 --self-contained true -p:InvariantGlobalization=true -o artifacts/package-linux-x64
docker build -f tests/installer.Dockerfile -t runner-room-installer-test .
docker run --rm runner-room-installer-test
```

These tests use the actual bundled x64 application and real Linux runner stand-in processes. Release downloads and service supervision are stubbed inside the disposable container; `systemd-analyze verify` checks the generated unit. Tests cover missing arguments, x64/ARM32/ARM64 download selection (including mixed kernel/userspace bitness), checksum failure, first installation, seven child runners beneath a parent with leftover registration metadata, on/off detection through a versioned `bin` symlink, a single-runner root, failed-update rollback, and a successful update. Architecture selection tests stub system identity; they do not execute an ARM binary on the x64 test host.
