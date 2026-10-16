# Update Runner Room from Preferences

For a native Linux installation, open **Preferences → Application updates**. The section shows the installed version, latest release, and release notes. **Check for updates** refreshes GitHub release information. When a newer complete stable release is available, select **Update now**.

Progress stays available while you use the dashboard or close Preferences. The dashboard briefly disconnects during its restart, then reconnects and displays the result. A failed download leaves the current application running. If the new application fails its startup check, the updater restores the previous release and reports the failure. UI updates preserve the service configuration, runner directories, GitHub credentials, access settings, and monitoring history.

Stop or drain managed runners first. Updates are blocked while Runner Room has active runners or queued lifecycle operations, and new management operations are held while an application update is in progress. Existing independent runner services are not stopped by the updater.

## Enable updates on an older installation

The first release with this feature needs one final run of your existing installer command. Use the same runner path, account, and port flags as before:

```bash
curl -fsSL https://github.com/igor-couto/github-self-hosted-runner-manager/releases/latest/download/install.sh \
  | sudo bash -s -- --runners "/home/github-runner/actions-runner"
```

The installer adds the background updater and its request directory. From then on, use Preferences. A running application cannot add this operating-system service by itself because the dashboard runs as your ordinary runner account.

Docker Compose and Kubernetes deployments show an explanation instead of a native update button. Upgrade their image or release checkout using the [deployment guide](deployment.md). Demo mode offers a simulated update without downloading files or restarting anything.

## How the updater works

The installer provisions a root-owned systemd path/service pair. The dashboard writes a request containing only a generated identifier to `/var/lib/runner-room-updater/inbox/request`. It cannot select a command, download host, installation directory, or script. The fixed updater installs releases from this project's GitHub repository, checks the architecture and checksum, validates archive entries, checks the bundled runtime, switches releases, and checks startup.

The updater runs separately from the dashboard, so replacing and restarting the application does not interrupt the update process. Progress is stored in `/var/lib/runner-room-updater/status.json` and remains available after the restart. Release files and updater scripts stay root-owned; the dashboard only needs write access to the inbox. Its existing `NoNewPrivileges` setting remains enabled.

If sign-in is enabled, starting or checking updates requires an administrator. With sign-in disabled, users who can reach the dashboard can use these controls. Requests still require JSON and the dashboard's origin; existing authenticated installations also retain their CSRF checks.

## Troubleshooting

- **Updater not installed:** run the installer once with the original settings after this feature is released.
- **Managed runners are active:** drain or stop them in Runner management, then retry the update.
- **Release check failed:** retry when GitHub is reachable and its public API limit has reset. The dashboard does not treat an unavailable check as "up to date."
- **Update failed or did not start:** read the message in Preferences. Inspect the updater service journal if it requests server-side recovery. Settings and recorded history remain in their original locations.

Previous application releases remain under `/opt/runner-room/releases`. The native uninstaller also removes its updater services while preserving dashboard settings, history, and runner installations.
