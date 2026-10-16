# Installation and maintenance

[Back to README](../README.md)

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

## Updates

Use **Preferences → Application updates** for native installations. See the [application update guide](application-updates.md) for progress, rollback, and enabling the updater on older installations. Docker Compose and Helm upgrades use the [deployment guide](deployment.md).

## Configuration and troubleshooting

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

Access is open to your trusted LAN by default. Enable the optional [sign-in and permissions](access-control.md) to restrict access. If your firewall blocks the chosen port, allow it from your home subnet. The installer does not change firewall rules or expose the dashboard through your router.

Advanced discovery and monitoring settings are in the [monitoring guide](monitoring.md).

## Uninstall

For an installation created by the native installer:

```bash
sudo bash /opt/runner-room/current/uninstall.sh
```

The command asks for confirmation before removing the dashboard service, updater, and recorded application files. It preserves settings, history, runner installations, and independent runner services; no runners are unregistered from GitHub. Older release folders without an ownership manifest are retained.

Stop or drain runners managed in process mode first: stopping the dashboard can stop those processes. Container removal is covered in the [deployment guide](deployment.md).
