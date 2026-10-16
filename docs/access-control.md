# GitHub integration and access control

[Back to README](../README.md)

The **GitHub & access** workspace guides you through connecting GitHub, choosing repositories, and syncing workflow activity. It shows workflow runs, attempts, jobs, steps, runner assignments, and runner requirements from workflow files. Progress, saved history, partial imports, and API cooldowns remain visible. See [GitHub synchronization setup](github-sync.md) for token/App permissions, import limits, and optional webhooks. Account settings and the access audit are under advanced details. Credentials stay on the server; OAuth sign-in only identifies a person.

Authentication is **opt-in** for LAN installations. **Runner management → Set up management** connects GitHub and enables management directly in the UI without requiring a login or server verification code; see [guided setup](management-setup.md). To add dashboard sign-in, set `Access.Enabled` to `true` in server configuration. Invalid enabled configuration fails startup; it never falls back to anonymous access. Enabled authentication requires at least one administrator. Account and role changes use server configuration and apply on restart.

| Access | Viewer | Operator | Admin |
| --- | --- | --- | --- |
| Monitoring and synchronized workflow history | Yes | Yes | Yes |
| Refresh/cancel synchronization, inspect workflow requirements, request alert checks | No | Yes | Yes |
| Start/stop/restart/drain managed runners; track/rerun/cancel workflows | No | Yes | Yes |
| Choose repositories, account list, access audit, diagnostic logs | No | No | Yes |
| Register/remove/update/configure runners, groups and pools | No | No | Yes |

Roles apply to the whole dashboard, not individual repositories. With authentication disabled, anyone who can reach the dashboard has administrator capabilities, including runner management when enabled. `/healthz` remains public for the installer and service health checks.

### Local accounts

Generate a password hash on the server. The interactive helper hides password input, requires at least 12 characters, and prints only an ASP.NET Identity password hash:

```bash
sudo -u github-runner /opt/runner-room/current/RunnerRoom --hash-password
```

Use your actual service account in place of `github-runner`. Add the hash to `/etc/runner-room/settings.json`:

```json
"Access": {
  "Enabled": true,
  "RequireHttps": true,
  "PublicOrigin": "https://runners.example.net",
  "SessionHours": 8,
  "TrustedProxies": ["127.0.0.1"],
  "LocalUsers": [
    { "Username": "admin", "PasswordHash": "PASTE_GENERATED_HASH", "Role": "admin" },
    { "Username": "viewer", "PasswordHash": "PASTE_ANOTHER_HASH", "Role": "viewer" }
  ]
}
```

Serve the application over HTTPS, directly or through your existing reverse proxy. `PublicOrigin` must exactly match the public scheme/host/port and cannot contain a path. If using a proxy, preserve the original Host header and configure only the proxy's exact IP address in `TrustedProxies`; only those peers may supply forwarded protocol/client-address headers. Without a proxy, use an empty list. The installer does not provision certificates or a proxy. For isolated local HTTP testing only, explicitly set `RequireHttps: false` and an `http://` origin; passwords and cookies then travel over HTTP.

### GitHub sign-in

Create a GitHub OAuth app with your dashboard origin as its homepage and **`https://YOUR_DASHBOARD/signin-github`** as its callback. Store its client secret in a file readable only by the service account/administrator, then add these fields alongside the access settings:

```json
"GitHubClientId": "YOUR_OAUTH_CLIENT_ID",
"GitHubClientSecretFile": "/etc/runner-room/github-oauth.secret",
"GitHubUsers": [
  { "Id": "YOUR_NUMERIC_GITHUB_USER_ID", "Role": "admin" },
  { "Id": "ANOTHER_NUMERIC_ID", "Role": "viewer" }
]
```

Use numeric GitHub user IDs (available from `https://api.github.com/users/YOUR_LOGIN`), not usernames. Renaming an account does not transfer its dashboard access to whoever takes the old name. There is no automatic access for repository collaborators or organization members. Local accounts and GitHub accounts can coexist, or either provider can be used alone.

Sign-in uses the [GitHub authorization-code flow with state and PKCE](https://docs.github.com/en/apps/oauth-apps/building-oauth-apps/authorizing-oauth-apps), followed by the fixed GitHub user API to verify identity. The OAuth flow requests no repository permissions. Access is granted only after the returned numeric ID matches the server allowlist. OAuth tokens are discarded after sign-in; they are not saved in cookies or used to manage runners. GitHub App installation credentials are supported separately for synchronization and runner checks; GitHub Enterprise is not yet supported.

Sessions use [ASP.NET Core cookie authentication](https://learn.microsoft.com/en-us/aspnet/core/security/authentication/cookie?view=aspnetcore-9.0), with HttpOnly/SameSite cookies and a fixed 1–24 hour lifetime (default eight hours). Passwords use the framework's salted password hasher with 600,000 iterations. Changing a local password, removing an account or changing its role invalidates its sessions on subsequent requests after restart. Sign-out clears the current browser's cookie. Persistent data-protection keys live in the private `session-keys` directory beneath the state directory, so ordinary updates preserve valid sessions. Keep that directory private and backed up with the server's state; local account and secret files also need restricted filesystem permissions.

State-changing APIs validate antiforgery tokens. Local sign-in is limited to five attempts per client IP per minute and 30 attempts globally per minute. Only explicitly trusted proxies affect the client IP. Login successes/failures, GitHub login failures, denied admin actions, sign-outs and accepted alert-check requests are recorded in `access-audit.json`. The audit retains up to 1,000 events from the last 30 days; the admin page shows the latest 100. It records account IDs and client addresses, never passwords or tokens. Storage failures are visible in the admin page and fall back to memory. Demo mode keeps audit events in memory and does not load real access history.
