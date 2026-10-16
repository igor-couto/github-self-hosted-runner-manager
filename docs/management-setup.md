# Set up runner management in the dashboard

On your Linux server, open **Runner management → Set up management**. The wizard creates configuration and secret files for you. It does not register, stop, import, or modify existing runners during setup.

1. **Connect GitHub.** Create a fine-grained personal access token using the wizard's GitHub link and paste it into the token field. Grant repository **Administration: read and write** for runner registration/removal and **Actions: read and write** for workflow controls. Organization runners need **Self-hosted runners: read and write** at the organization level. Find and select repositories, or add `repo:owner/repository` and `org:organization` scopes manually. Repository discovery is bounded to 500 entries; you can add omitted scopes manually. Setup verifies runner API read access for every selected scope; write access is checked by GitHub when you perform the relevant operation. This credential is separate from the read-only synchronization connection and OAuth sign-in.

2. **Review and save.** Review the permitted scopes and directory for newly created runners. Settings are saved only after the server verifies the selected scopes. Failed validation leaves your current setup unchanged. The wizard clears the token when closed or saved and never puts it in browser storage.

3. **Start managing runners.** Successful setup enables management immediately. No ownership code, new dashboard account, or service restart is required. If settings were saved but activation failed, the wizard reports that separately and shows recovery instructions.

Setup preserves your existing sign-in settings. With sign-in disabled, users who can reach the dashboard on your LAN can configure and control management. If sign-in is already enabled, only administrators can configure management; operator and viewer permissions still apply. Setting up management does not change existing independent runner services.

## Existing runners and Linux permissions

After setup, use **Import installation** to choose an existing runner, or **Create runners** for a new installation. Importing preserves its GitHub registration. A runner with an existing system service must be imported using its existing service mode.

Linux permissions remain a separate administrator step. The dashboard account needs write access to imported runner folders and permission to control the specific existing system services. Before handing a service over, its automatic restart policy must be `Restart=no` and its independent boot enablement must be disabled; enable **Restore after restart** in Runner Room instead. Arrange the required systemd authorization before doing this handover. Setup deliberately does not disable service supervision or grant root permissions automatically. Follow the [Linux service permissions instructions](runner-management.md#linux-service-permissions) for the folder override and service details.

## Where settings are saved

The application state directory (normally `/var/lib/runner-room`) contains:

- `ui-settings.json`: management settings, including allowed scopes and the managed runner directory.
- `management-setup.token`: the GitHub management token, readable only by the file owner on Linux.

The settings and token files use owner-only permissions on Linux. Existing `/etc/runner-room/settings.json`, access settings, and GitHub credential files are preserved. Back up the state directory privately. The saved setup replaces the complete Management section, so stale scopes are not merged back in from older settings. Previously saved Access sections remain supported for compatibility with earlier installations. Later changes to management settings can be made in `ui-settings.json`, followed by a restart. This wizard currently handles initial setup, not subsequent credential rotation or account administration.

Environment variables and command-line arguments remain authoritative. If they control settings the wizard would change, it explains the conflict and blocks saving. In particular, the supplied production Compose and Helm deployments explicitly disable management and mount runner folders read-only: changing the browser settings cannot override those deployment restrictions. Configure the deployment first or use the native installer for host runner management.

Demo mode provides **Preview setup**, using sample repositories without a real token or any file/GitHub changes. No restart is needed for a preview.
