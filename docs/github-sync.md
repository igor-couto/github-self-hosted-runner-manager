# GitHub synchronization

Open **GitHub & access**, choose **Choose repositories**, discover repositories, select the ones to follow, and save. **Sync now** imports recent workflow activity. Open a run for jobs and steps; **Runner requirements** inspects workflow YAML on the default branch.

## Connect once on the server

For a fine-grained personal access token, grant access to the repositories you want to follow and **Actions: read**. Add **Contents: read** for workflow inspection. Existing runner connectivity checks additionally need repository **Administration: read** or organization **Self-hosted runners: read**. Write the token into a private file readable by the dashboard service account, then set:

```json
{ "GitHub": { "TokenFile": "/etc/runner-room/github.token" } }
```

Alternatively, install your GitHub App on the desired repositories with those read permissions and configure:

```json
{
  "GitHub": {
    "TokenFile": null,
    "App": {
      "AppId": 12345,
      "InstallationId": 67890,
      "PrivateKeyFile": "/etc/runner-room/github-app.pem"
    }
  }
}
```

Use a token **or** an App installation. App installation tokens are generated and renewed in memory; the private key stays on the server. Restart Runner Room after changing server settings. GitHub.com is supported; GitHub Enterprise hosts are not yet supported. OAuth login and the separate management token are unchanged: the App credential here is for read-only synchronization/runner checks, not runner management.

## Imports and refresh

Synchronization runs every five minutes by default; the dashboard receives change notifications and falls back to polling. Administrators select repositories and the history window; operators can refresh all repositories, one repository, or one run, and cancel an import. Viewers can read saved results for all followed repositories. These are dashboard-wide roles, not repository-specific permissions.

`GitHub.Sync` accepts `Enabled`, `IntervalMinutes` (1–1440), `HistoryDays` (1–90), `MaxRepositories` (1–25), `MaxRunsPerRepository` (1–100), and `MaxJobsPerRun` (1–100). Discovery is capped at 500 repositories. Saved selection/window/run limits override their initial defaults. Storage is capped at 500 run attempts overall and 100 steps per job; partial imports are labeled. Previously imported attempts are retained within these limits, but the importer does not backfill every older attempt of a rerun.

Completed unchanged runs reuse saved job details. Refreshing an individual run fetches its current attempt and jobs again. Errors retain previously saved data. Rate-limit headers control cooldowns. State is saved as `github-sync.json` in the dashboard state directory, including private repository/job metadata; protect and back up that directory. Removing a repository from the selection removes its imported runs.

Workflow inspection reads up to 30 YAML files (128 KiB each), caches results for five minutes, and does not execute workflows. Matrix expressions, reusable workflows, and YAML aliases are not resolved; review those definitions on GitHub. This view shows requested labels/groups, not proof that an eligible runner is currently available.

## Optional webhook refresh

Set `GitHub.WebhookSecretFile` to a private file containing your webhook secret. Configure a GitHub webhook with JSON payloads, the same secret, and **Workflow runs** and **Workflow jobs** events. Point it to `https://YOUR_DASHBOARD/hooks/github`. GitHub must be able to reach that HTTPS endpoint; a LAN-only dashboard can simply use periodic synchronization.

Deliveries are verified using HMAC-SHA256, bounded to 1 MiB, and deduplicated for one hour (up to 1,000 deliveries). Only selected repositories trigger refreshes. Payloads are treated as notifications: authoritative activity is fetched from GitHub. Notifications arriving during an import are repaired by the next periodic sync. The endpoint uses the webhook signature rather than a dashboard login. Enable dashboard authentication before exposing the service outside your trusted LAN.

## Verification

The automated suite uses simulated GitHub responses; no personal credentials are required. Demo mode shows illustrative workflows and never calls GitHub. Real token/App access depends on repository permissions and organization policies.

