# Set up runner management in the dashboard

On your Linux server, open **Runner management → Set up management**. The wizard creates configuration and secret files for you. It does not register, stop, import, or modify existing runners during setup.

1. **Confirm ownership.** If dashboard sign-in is disabled, run the command shown in the wizard on the server and paste its setup code. For a standard installer deployment:

   ```bash
   sudo -u "$(systemctl show runner-room.service --property=User --value)" /opt/runner-room/current/RunnerRoom --setup-code
   ```

   The code expires after 15 minutes and is consumed when settings are saved. Generating a new code invalidates the old one. Signed-in administrators skip this step. Operators and viewers cannot configure management. For custom deployments, run the command as the dashboard account with the same `SettingsFile` and state-directory settings as the running application.

2. **Create your login.** Choose a username and a password of at least 12 characters. The server hashes the password; you do not need to generate a hash yourself. Existing enabled authentication is preserved and this step is skipped. The wizard uses the address you opened, so use your intended dashboard URL. HTTPS is recommended. HTTP requires explicit acknowledgement because passwords and tokens are transmitted without encryption. An HTTPS reverse proxy must already be listed in `Access.TrustedProxies`; arbitrary forwarded headers are never trusted.

3. **Connect GitHub.** Create a fine-grained personal access token using the wizard's GitHub link and paste it into the password field. Grant repository **Administration: read and write** for runner registration/removal and **Actions: read and write** for workflow controls. Organization runners need **Self-hosted runners: read and write** at the organization level. Find and select repositories, or add `repo:owner/repository` and `org:organization` scopes manually. Repository discovery is bounded to 500 entries; you can add omitted scopes manually. Setup verifies runner API read access for every selected scope; write access is checked by GitHub when you perform the relevant operation. This credential is separate from the read-only synchronization connection and OAuth sign-in.

4. **Review and save.** Review the administrator, dashboard address, permitted scopes, and directory for newly created runners. Settings are saved only after the server verifies the selected scopes. Failed validation leaves your current setup unchanged. The wizard clears password, token, and setup-code fields when closed or saved and never puts them in browser storage.

5. **Restart once.** For an installer deployment, run the command shown on completion:

   ```bash
   sudo systemctl restart runner-room.service
   ```

   Reload the dashboard and sign in. The setup callout continues to show **Finish setup** until the saved settings take effect. The application does not execute privileged restart commands. Your existing independent runner services are not changed by this setup or dashboard restart.

## Existing runners and Linux permissions

After setup, use **Import installation** to choose an existing runner, or **Create runners** for a new installation. Importing preserves its GitHub registration. A runner with an existing system service must be imported using its existing service mode.

Linux permissions remain a separate administrator step. The dashboard account needs write access to imported runner folders and permission to control the specific existing system services. Before handing a service over, its automatic restart policy must be `Restart=no` and its independent boot enablement must be disabled; enable **Restore after restart** in Runner Room instead. Arrange the required systemd authorization before doing this handover. Setup deliberately does not disable service supervision or grant root permissions automatically. Follow the [Linux service permissions instructions](../README.md#linux-service-permissions) for the folder override and service details.

## Where settings are saved

The application state directory (normally `/var/lib/runner-room`) contains:

- `ui-settings.json`: management settings and, for first-time login setup, the administrator password hash and access settings.
- `management-setup.token`: the GitHub management token, readable only by the file owner on Linux.
- `setup-code.json`: a hash of the expiring ownership code; removed after successful setup.

The settings and token files use owner-only permissions on Linux. Existing `/etc/runner-room/settings.json` and GitHub credential files are preserved. Back up the state directory privately. The saved setup replaces complete Access/Management sections that it owns, so stale accounts and scopes are not merged back in from older settings. Unrelated settings remain unchanged. Later changes to these owned sections must be made in `ui-settings.json`, followed by a restart. This wizard currently handles initial setup, not subsequent credential rotation or account administration.

Environment variables and command-line arguments remain authoritative. If they control settings the wizard would change, it explains the conflict and blocks saving. In particular, the supplied production Compose and Helm deployments explicitly disable management and mount runner folders read-only: changing the browser settings cannot override those deployment restrictions. Configure the deployment first or use the native installer for host runner management.

Demo mode provides **Preview setup**, using sample repositories without a real token or any file/GitHub changes. No restart is needed for a preview.
