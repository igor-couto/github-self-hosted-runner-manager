# Dashboard and alternative interfaces

[Back to README](../README.md)

Open **Preferences** in the top bar to change the theme, language, date/time format, time zone, or display density. Dark remains the default. **System** follows your device, and **Night** uses a dimmer palette. English and Portuguese are included for the main interface; diagnostics, provider messages and untranslated labels fall back to English. Runner names, paths and log contents are never translated. History date-range inputs still use UTC boundaries, independently of how timestamps are displayed.

**Edit layout** reveals keyboard-accessible controls to move, collapse, pin and hide widgets. Pinning moves a widget above unpinned widgets; movement stays within its pinned/unpinned group. Restore hidden widgets in Preferences. Themes, layouts, runner filters/grouping/sort/page size/page, history filters/page, alert filters and process display choices are saved in this browser. Reset preferences restores defaults. Private browsing or blocked storage may prevent persistence. Preferences do not store passwords, cookies, API responses or logs.

**TV / wall display** enlarges monitoring rows and requests a screen wake lock where supported. Use **Full screen** for a wall display and **Exit TV mode** to return. **Compact** reduces spacing. Live refresh remains every 15 seconds in each mode, with stale/unavailable warnings when requests fail.

Runner expansions link to dedicated runner and current-job pages. Repository names and the server hostname are also links; recorded jobs in History open stable detail links. These hash routes can be bookmarked and shared. Historical jobs remain available only for the configured retention window. A current-job page follows the selected runner's current job, so it changes when that runner starts another job.

### Install the web app

Use **Preferences → Install app**, or your browser's installation menu. On iPhone/iPad, use Safari's Share → Add to Home Screen. The dashboard must be served over **HTTPS** (or localhost during development); a plain `http://192.168...` address is insufficient for service-worker installation. Browser support varies. See [MDN's installation requirements](https://developer.mozilla.org/en-US/docs/Web/Progressive_web_apps/Guides/Making_PWAs_installable).

The installed app uses the same server and authentication. Only the generic offline page and icons are cached. Runner data, diagnostic logs, credentials and dashboard HTML are never cached by the service worker. Offline mode offers reconnection; it does not show old statuses or queue management commands.

### Command line and terminal dashboard

The existing Linux release binary includes both interfaces; no extra runtime or packages are needed:

```bash
/opt/runner-room/current/RunnerRoom cli runners
/opt/runner-room/current/RunnerRoom cli server --json
/opt/runner-room/current/RunnerRoom cli history --json
/opt/runner-room/current/RunnerRoom tui
```

Commands: `runners`, `server`, `history`, `alerts`, `management`, `quotas`. Add `--json` for machine-readable output; errors go to stderr with a nonzero exit status. `history` returns the API's default seven-day range and first page of up to 50 jobs. Use the web interface/API for arbitrary ranges or exports. `cli runners --id DISCOVERY_ID` shows one runner's fields. See `cli --help` for options.

For an authenticated server, use a **local dashboard account**:

```bash
RunnerRoom cli runners --url https://runners.example.com --user reader
RunnerRoom cli quotas --json --url https://runners.example.com --user admin --password-file /secure/path/password
```

Without a password file, the client prompts without echoing the password. Passwords cannot be supplied as command-line arguments. Sessions stay in memory. Remote sign-in requires HTTPS; an SSH tunnel to localhost can also be used. A GitHub OAuth-only installation needs a local account added for CLI/tray access. Viewer/admin restrictions and request-forgery protection apply exactly as in the browser. Quotas and management require admin access (or the existing trusted-LAN mode with access control disabled).

Management actions use IDs from `cli management --json`, which differ from discovery IDs:

```bash
RunnerRoom cli drain --id MANAGED_ID --yes --url https://runners.example.com --user admin
```

`start`, `stop`, `restart`, `drain` and `update` enqueue actions; inspect `cli management` or the web management page for completion/errors. TUI keys: arrows/Page Up/Page Down select a runner, Enter toggles its details, F filters to active runners, S switches sort order, R refreshes and Q/Esc exits. TUI requires an interactive terminal and refreshes every 15 seconds; redirected output should use the CLI.

### Desktop exploration: Windows tray prototype

`src/RunnerRoom.Tray` is an optional .NET Windows Forms companion. It connects to your existing server, polls every 15 seconds, shows fleet counts/status, opens the browser dashboard, and exposes start/drain/restart requests for administrators with confirmation. Requests are queued on the server and tracked in the management page. Select **Connect…** from its tray menu; the prototype does not persist credentials, install itself or run at startup.

```powershell
dotnet run --project src/RunnerRoom.Tray
# Optional standalone Windows build:
dotnet publish src/RunnerRoom.Tray -c Release -r win-x64 --self-contained true
```

This is a Windows-only prototype, separate from the Linux release. The PWA is the portable desktop/phone interface. Native Linux tray/macOS menu-bar packaging is not included. [Windows NotifyIcon documentation](https://learn.microsoft.com/en-us/dotnet/desktop/winforms/controls/notifyicon-component-overview-windows-forms).

### Optional AI-provider quotas

Disabled by default. Add `Quotas` to the existing server settings file and restart the service. Up to ten providers are polled independently of browser activity; `RefreshSeconds` is clamped to 60–3600 seconds. These integrations read quota data without making inference requests:

```json
{
  "Quotas": {
    "Enabled": true,
    "RefreshSeconds": 300,
    "Providers": [
      { "Name": "OpenRouter build key", "Type": "openrouter", "TokenFile": "/etc/runner-room/openrouter-token" },
      { "Name": "Other provider", "Type": "json-file", "DataFile": "/var/lib/runner-room/provider-quota.json", "MaxAgeSeconds": 900 }
    ]
  }
}
```

`openrouter` calls only [OpenRouter's current-key endpoint](https://openrouter.ai/docs/api/api-reference/api-keys/get-current-key), using the token file. It reports the key's spending limit/remaining amount in USD, not account credit or a ChatGPT/Claude subscription quota. A key without a cap shows unknown limit/remaining; lifetime usage is not mixed with the current cap. Key labels and error bodies are not exposed. The service account must be able to read the token file; restrict its permissions accordingly.

`json-file` is an explicit adapter contract for a separate collector using another provider's supported API. Runner Room does not execute that collector or scrape credentials/browser sessions. Write the file atomically, using this format (maximum 16 KiB):

```json
{
  "used": 40,
  "limit": 100,
  "remaining": 60,
  "unit": "requests",
  "observedAt": "2026-10-07T12:00:00Z",
  "resetAt": "2026-10-08T00:00:00Z"
}
```

Amounts may be null when unknown; at least one is required. When all three are supplied they must be consistent. `unit` and `observedAt` are required, `resetAt` is optional. Old readings show **stale** using `MaxAgeSeconds` (60–86400). Invalid/unreadable/denied integrations show **unavailable** and retry without stopping monitoring. Demo mode uses clearly marked sample quota values and makes no provider requests.
