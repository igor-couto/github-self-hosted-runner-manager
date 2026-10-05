# Runner Room

A small dashboard for GitHub Actions runners on your Linux server. It shows which runners are **Busy**, **Idle**, **Offline**, or **Unknown**, and refreshes every 15 seconds.

Dark theme is the default. Use the theme button in the header to switch to light; your choice is saved in your browser.

Server details includes CPU usage, logical core count, architecture, RAM usage, disk space, and system uptime. These refresh with the runner status.

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

- **Busy:** a local `Runner.Worker` from that installation is running.
- **Idle:** its `Runner.Listener` is running with no visible worker, and process visibility is reliable.
- **Offline:** no matching process was found and process visibility is reliable.
- **Unknown:** permissions or process visibility prevent determining activity reliably.

**Local activity and GitHub connectivity are independent.** A listener can run while disconnected. The GitHub column reports Online/Offline only after an optional API check; otherwise it shows Not checked or Unknown. A failed API request never becomes Offline. API results are cached for up to 60 seconds, with their check time in the expanded details; local readings refresh every 15 seconds. Local activity is not overwritten by GitHub's potentially older busy flag.

Select a runner to expand its registered name, repository or organization scope, labels, machine, installed platform/version, process/PID/uptime, service name/state, last observed job and job activity time. Group by repository, organization/owner, machine, custom group, or registered runner group. Search includes display names, registered names, directories, projects, custom groups, machines, and GitHub labels.

Repository metadata and runner group come from `.runner`; the group is the locally recorded registration group, not a fresh GitHub group lookup. Platform comes from the runner's ELF executable; version comes from its assembly metadata or resolved `bin.VERSION`, never the installation folder's potentially outdated name. Uptime is the listener process age (or worker age if no listener is visible). Service state comes from a read-only `systemctl show`; unavailable systemd access shows Unknown, and runners without `.service` show Not configured. Missing metadata is explicitly unavailable.

For last-job information, the app reads only recognized job start/completion summary lines from the tails of up to five recent `_diag/Runner_*.log` files (256 KiB per file). Last activity means the most recent **recorded job event**, not a heartbeat or filesystem modification time. Old/rotated logs may leave this unavailable; an observed start without a completion does not prove the job is still running. No raw logs, runner credentials, or job workspaces are exposed. The app cannot start or stop runners.

## Richer inventory configuration

The original installer command still works. Advanced settings live in **`/etc/runner-room/settings.json`**, which the installer leaves untouched during updates. Create this file only if needed; start with `settings.example.json` in this repository and adjust every path. The service account must be able to read it. Restart `runner-room` after changes.

```json
{
  "RunnersRoots": ["/home/github-runner/actions-runner", "/srv/other-runners"],
  "Discovery": { "Recursive": true, "MaxDepth": 4 },
  "RunnerOverrides": [
    {
      "Path": "/home/github-runner/actions-runner/activities-api-arm64-2.322.0",
      "DisplayName": "Activities API",
      "Group": "APIs"
    }
  ]
}
```

- `RunnersRoots` replaces the legacy single `RunnersRoot` when nonempty. Overlapping roots are deduplicated. Every root needs read/traverse access for the service account; inaccessible roots generate a warning while readable roots still work.
- Default discovery checks immediate children, then the configured root itself when no child runners are found. This preserves the fix for stale parent registrations. Enable `Discovery.Recursive` to find installations at deeper levels; `MaxDepth` defaults to 4 and is capped at 16. Discovery stops inside recognized installations and excludes `_work`, `_diag`, hidden folders, binary directories, and child directory symlinks. A configured root may itself be a symlink. A 4096-directory limit bounds scans and produces a warning when reached.
- `RunnerOverrides.Path` is the full installation path. `DisplayName` and `Group` affect this dashboard only; they never change GitHub registration, labels, or runner files. Repeated registered names such as `pifive2` remain visible in the details.
- System disk readings refer to the **first readable root's filesystem**; they do not combine disks from all roots. Machine grouping currently groups installations on this one host, ready for a future multi-server feature.
- For development, pass `--SettingsFile /absolute/path/settings.json`. Explicitly selected missing/invalid files fail startup. Environment variables and command-line settings take precedence over JSON. Arrays can also be set using `RunnersRoots__0`, `RunnersRoots__1`, etc.

### Optional GitHub connectivity and labels

Local monitoring works without a token. To enable GitHub checks, create a fine-grained personal access token for the relevant repository owner with **repository Administration: Read** for repository runners, or **organization Self-hosted runners: Read** for organization runners. The account must have the access GitHub requires for these endpoints. See [GitHub's runner API permissions](https://docs.github.com/en/rest/actions/self-hosted-runners).

Store the token alone in `/etc/runner-room/github.token`, readable only by root and the dashboard's service account (for example root-owned, the service account's private group, mode `640`). Add this property to `settings.json` and restart the service:

```json
"GitHub": { "TokenFile": "/etc/runner-room/github.token" }
```

Alternatively use the `GitHub__Token` environment variable. Never put real tokens in source control. The backend sends the token only to `https://api.github.com`, rejects redirects, and matches registrations by scope and runner ID rather than name. GitHub Enterprise hosts are not supported by this optional integration yet. Requests have bounded concurrency/timeouts; inaccessible registrations, permission errors, and rate limits show Unknown with an explanation. The token is never sent to the browser. One token must cover all configured scopes; scopes it cannot access continue to show local information.

The installer rewrites its environment file on update, so the separate JSON/token files are the recommended persistent configuration.

## System information

The **System resources** section inside Server details shows:

- **CPU:** utilization across logical cores between scans, plus core count and architecture. The first reading says Sampling until another scan is available.
- **RAM:** total usable memory minus available memory, so reclaimable caches are not counted as used RAM.
- **Disk:** used, total, and available space on the filesystem containing the watched runner directory, including a separate drive or a symlinked path. This is filesystem usage, not the size of the runner folders. Available space excludes blocks reserved by the filesystem.
- **System uptime:** time since the Linux system booted.

Memory, CPU, and uptime come from Linux's [system information files](https://docs.kernel.org/filesystems/proc.html); disk capacity comes from .NET's filesystem APIs. Readings use binary units (GiB, MiB). Missing or inaccessible measurements show Unavailable and do not prevent runner monitoring. No extra permissions or tools are required by the installer.

Install directly on the Linux runner host for host readings. In a development container, readings reflect the Linux environment visible to that container, not container resource limits or the Windows/macOS host. Demo mode uses sample system metrics as well as sample runners.

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

Build with `dotnet build`. Check system metric parsing and failure cases with `dotnet run --project tests/RunnerRoom.Tests`. For a Docker demo:

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

These tests use the actual bundled x64 application and real Linux runner stand-in processes. Release downloads and service supervision are stubbed inside the disposable container; `systemd-analyze verify` checks the generated unit. Tests cover missing arguments, x64/ARM32/ARM64 download selection (including mixed kernel/userspace bitness), checksum failure, first installation, stale parent registration metadata, busy/idle/offline detection through versioned binaries, runner metadata and log summaries, service state parsing, recursive/multiple roots, aliases, failed-update rollback, and settings preservation during updates. The console checks also exercise GitHub responses using a fake HTTP handler, including denied access, invalid data, registration matching, and token isolation. Architecture selection tests stub system identity; they do not execute an ARM binary on the x64 test host.
