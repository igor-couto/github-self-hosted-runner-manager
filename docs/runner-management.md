# Runner management

[Back to README](../README.md)

The **Runner management** tab provides repository and organization registration, imports, batch creation and lifecycle actions, editable labels/settings, process or systemd execution, bounded crash recovery, local pool scaling, GitHub organization groups, verified runner updates, and workflow rerun/cancellation with attempt tracking. The preview simulates these operations and never changes GitHub or runner files.

Live management is **disabled by default** and requires Linux and a non-root runner account. Open **Runner management → Set up management**, connect a GitHub token, select repositories, and review before saving. Management becomes available immediately after successful setup, with no server verification code, mandatory login, or restart. Existing dashboard sign-in settings are preserved. See [setup, storage, and existing-service permissions](management-setup.md).

When sign-in is enabled, administrators can configure management and operators can perform routine runner and workflow controls. For manual configuration instead, merge this into `/etc/runner-room/settings.json` and restart Runner Room:

```json
"Management": {
  "Enabled": true,
  "RootDirectory": "/var/lib/runner-room/runners",
  "TokenFile": "/etc/runner-room/management.token",
  "AllowedScopes": ["repo:YOUR_OWNER/YOUR_REPO", "org:YOUR_ORG"],
  "MaximumRunners": 50,
  "MaximumBatchSize": 10,
  "DrainTimeoutMinutes": 60
}
```

Use your actual scopes; repository and organization entries are distinct. A workflow action always needs an explicit `repo:owner/repository` entry, even when its organization is allowed. The dedicated management token is separate from the optional read-only monitoring token and the OAuth sign-in secret. Put it in the configured file with owner set to the runner service account and permissions `600`. Tokens and command output are not included in operation responses. Registration uses short-lived registration tokens passed to the runner through its environment, never a shell command or persisted management state.

GitHub fine-grained tokens need **repository Administration: write** for repository runner management, **organization Self-hosted runners: write** for organization runners/groups, and **repository Actions: write** for reruns/cancellation. Read-only tracking still needs access to the repository. Organization policies, approval requirements and runner-group availability also apply. See GitHub's [runner API](https://docs.github.com/en/rest/actions/self-hosted-runners), [runner-group API](https://docs.github.com/en/rest/actions/self-hosted-runner-groups), and [workflow-run API](https://docs.github.com/en/rest/actions/workflow-runs). This version targets GitHub.com. Its single management credential must cover all configured scopes; use separate dashboard installations for owners that require separate credentials.

### Linux service permissions

The default managed directory above is inside the installer's writable `StateDirectory`. Created runners run as the dashboard service account. Native libraries required by GitHub's runner and job tools must already be installed; Runner Room does not run dependency installers as root.

To import installations under your home directory, give the dashboard service write access to that specific runner parent. For example, use `sudo systemctl edit runner-room.service`:

```ini
[Service]
ReadWritePaths=/home/github-runner/actions-runner
```

Then reload systemd and restart Runner Room. Keep `ProtectHome=read-only` and add only the paths you manage. Imported folders must already be readable/writable by the same Linux account and remain within configured discovery roots. Paths with symbolic-link ancestors are rejected; normal runner `bin` auto-update links inside an installation are supported.

**Managed process** mode needs no per-runner service. The dashboard restores desired-running processes after restart and owns their retry budget. Processes share the dashboard service's lifetime and sandbox: restarting that system service can interrupt their jobs. Drain before restarting it. **User systemd service** mode creates a `runner-room-ID.service` in the account's `~/.config/systemd/user` and controls it through the user bus; runners survive a dashboard restart. Enable lingering for that account, make its user bus available, and permit writing the user-unit directory. For UID 1001, a typical additional dashboard override is:

```ini
[Service]
Environment=XDG_RUNTIME_DIR=/run/user/1001
Environment=DBUS_SESSION_BUS_ADDRESS=unix:path=/run/user/1001/bus
ReadWritePaths=/home/github-runner/.config/systemd/user
```

Create that directory as the runner user and run `sudo loginctl enable-linger github-runner` once. Substitute the real username, UID and paths. Units created by Runner Room use `Restart=no` and are not independently enabled: the dashboard controls restoration/retries. The dashboard itself remains enabled at boot. Existing **system** services can be imported, but the account needs narrowly scoped systemd authorization for those specific services. The application never uses sudo or installs privileged policy. Permission failures appear in the operation history.

Before importing an existing service, set its `Restart=no` in a service override and disable its independent boot start **without stopping it**. Reload systemd, then import using its existing system/user service mode. Otherwise import refuses so two supervisors cannot fight over stopped state and retry limits. Imported service units are preserved on removal; uninstall them locally if no longer needed.

### Operations, recovery and maintenance

- **Create/import:** choose scope, custom labels, workspace folder, pool, execution mode, optional organization runner group, ephemeral mode and auto-update preference. Batches get numbered names. Import preserves the existing GitHub registration and directory. Creation never replaces a same-name registration automatically.
- **Start/stop/restart:** queued operations continue with the browser closed. Start resets the per-runner retry budget. Stop, restart, update, unregister and remove wait for idle first. Cancel operation interrupts pending work; it does not cancel a GitHub workflow or undo already completed changes.
- **Drain:** waits for no local worker and two consecutive fresh GitHub idle readings, then rechecks the local worker before signalling the listener. Unknown visibility or API errors do not count as idle. Timeout leaves jobs running and the runner unmanaged by automatic restart until an explicit action. GitHub has no atomic pause/drain API; a new assignment can race the final stop. Pause workflow producers for strict maintenance isolation. The [runner's shutdown implementation](https://github.com/actions/runner/blob/main/src/Runner.Listener/JobDispatcher.cs) can cancel dispatched jobs, so sending a signal to a known busy runner is deliberately avoided.
- **Settings:** labels and retry/restore settings change in place. Changes to workspace, ephemeral/auto-update settings or GitHub registration group drain and re-register the runner, changing its GitHub ID and leaving it stopped. Created installations can switch process/user-service mode while stopped. Imported service registration/mode changes must be performed locally; label/retry settings remain editable.
- **Crash recovery:** each runner has 0–20 retries and a 5–3600 second delay. The persisted budget is not reset by restarting the dashboard. Exhaustion produces an error and stops retrying. Ephemeral runners are never automatically restarted after exit. Previously running persistent runners with restoration enabled restart after application/server restart; deliberately stopped runners remain stopped. An interrupted maintenance operation is marked interrupted and is never replayed automatically.
- **Remove:** unregisters at GitHub and clears local registration credentials. App-created installation folders are moved to `Management.RootDirectory/.removed`; imported installation folders are retained in place. Workspaces are not recursively deleted. Review and purge archives locally when no longer needed. Unregister alone keeps the managed record so it can be registered again.
- **Pools/groups:** local pools are templates plus a target installation count on this server. Scaling up creates named members; scaling down prefers stopped members, drains and removes them. Limits apply per operation. GitHub organization groups separately support list/create/update/delete, visibility/public-repository settings, selected repository IDs and replacement runner membership. Listing shows up to 100 GitHub groups.
- **Updates:** fetch the latest stable `actions/runner` release for the host's Linux x64/ARM/ARM64 architecture. A GitHub-provided SHA-256 digest is required. Downloads do not carry the management token, redirects are restricted to GitHub asset hosts, and archive paths/links/types are checked. The new runtime is checked before installation. Existing binaries are retained in `.packages/ID/previous`; file-installation failure rolls back completed moves. A later runner-start failure leaves the runner stopped with an error and the previous binaries available for manual recovery. Packages/backups are retained for inspection and should be purged locally when no longer needed. Auto-update remains GitHub's responsibility unless disabled during registration.
- **Workflows:** provide repository and run ID to track, rerun all jobs, rerun failed jobs or cancel a run. GitHub is polled every 30 seconds for its current attempt/result, including completed runs that may be rerun. Cancellation is a request, not proof of completion. Failed polling retains the last checked timestamp. The latest 30 tracked runs are retained.

Management records, desired state, pools, tracked workflows and the latest 390 completed plus pending operations are stored in `management.json` under the monitoring state directory. The UI shows the latest 100 operations; entries identify the requesting account and outcome. Enqueue/cancel actions also enter the access audit. A process lock prevents two dashboards managing the same state. Corrupt/unwritable state fails closed instead of recreating an empty fleet; preserve and repair the original file before restarting. Batches are not atomic: successful earlier items remain, and errors describe partial failure. Operations are serialized; a long drain holds the queue until it completes or is cancelled.

This is a single trusted-host manager, not an isolation boundary between mutually untrusted runner jobs. Jobs running as the dashboard account can read that account's files, including its configured tokens. Use appropriately isolated hosts/accounts for workloads you do not trust. This release does not provision VMs/containers or install system-wide services/policies on your behalf.
