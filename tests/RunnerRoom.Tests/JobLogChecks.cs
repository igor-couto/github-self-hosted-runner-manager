using System.Text.Json;
using RunnerRoom;

internal static class JobLogChecks
{
    internal static async Task Run(Action<bool, string> check)
    {
        var root = Path.Combine(Path.GetTempPath(), "runner-room-jobs-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(root, "_diag"));
        try
        {
            var started = DateTimeOffset.Parse("2026-10-05T12:00:00Z");
            var now = started.AddMinutes(3);
            const string file = "Worker_20261005-120000-utc.log";
            const string header = "[2026-10-05 12:00:00Z INFO Worker] Job message:\n";
            const string message = """
            {
              "jobDisplayName": "Build API",
              "variables": {"secret": {"value": "private-payload-value", "isSecret": true}},
              "contextData": {"github": {"t": 2, "d": [
                {"k": "repository", "v": "example/api"},
                {"k": "workflow", "v": {"s": "Build and test"}},
                {"k": "ref", "v": "refs/heads/main"},
                {"k": "sha", "v": "1234567890abcdef"},
                {"k": "actor", "v": "octocat"},
                {"k": "event_name", "v": "push"},
                {"k": "run_id", "v": "42"},
                {"k": "server_url", "v": "https://github.com"}
              ]}}
            }
            """;
            const string events = "\n[2026-10-05 12:00:02Z INFO StepsRunner] Processing step: DisplayName='Checkout'\n" +
                "[2026-10-05 12:00:06Z INFO StepsRunner] Step result: Succeeded\n" +
                "[2026-10-05 12:00:07Z INFO StepsRunner] Processing step: DisplayName='Build'\n" +
                "[2026-10-05 12:00:10Z WARN ActionRunner] Cache unavailable\n";
            var options = new RunnerOptions { RunnersRoot = root, GitHub = new() { Token = "configured-token-value" }, Logs = new() { RedactValues = ["custom-private-value"] } };
            var redactor = new LogRedactor(options);
            var content = header + message + events;
            File.WriteAllText(Path.Combine(root, "_diag", file), content);
            File.WriteAllText(Path.Combine(root, ".runner"), "{}");
            var runner = new RunnerInfo("pi", "runner", "busy", 12) { Path = root, WorkerPid = 13, WorkerStartedAt = started };
            var current = CurrentJobReader.Read(runner, options, now)!;
            check(current is { Name: "Build API", Workflow: "Build and test", Repository: "example/api", Ref: "refs/heads/main", Actor: "octocat", Event: "push", ElapsedSeconds: 180 }, "Read current job metadata from the actual serialized worker message format.");
            check(current.RunUrl == "https://github.com/example/api/actions/runs/42" && current.LogFile == file, "Build only validated GitHub run links and associate the matching worker log.");
            check(current.Steps is [{ Status: "succeeded" }, { Name: "Build", Status: "running" }], "Observed step progress retains start, completion and active state.");
            check(current.Steps[0].CompletedAt == started.AddSeconds(6), "Record step durations from timestamped events.");
            check(!JsonSerializer.Serialize(current).Contains("private-payload-value"), "Job DTO must never serialize the complete job payload or variables.");
            check(CurrentJobReader.Read(runner with { Status = "idle" }, options, now) is null, "An old log does not mean a job is running.");
            check(CurrentJobReader.Read(runner with { WorkerStartedAt = started.AddMinutes(1) }, options, now)?.Name == "Job details unavailable", "Never match an old log to a new worker.");
            check(CurrentJobReader.Read(runner with { WorkerStartedAt = null }, options, now) is null, "Missing worker start time must not guess a job identity.");
            var failed = CurrentJobReader.Parse(content, events + "[2026-10-05 12:02:00Z INFO StepsRunner] Step result: Failed\n", started, now, redactor, false);
            check(failed.Steps[^1].Status == "failed", "Parse failed steps.");
            var skipped = CurrentJobReader.Parse(content, events + "[2026-10-05 12:02:00Z INFO StepsRunner] Skipping step due to condition evaluation.\n", started, now, redactor, false);
            check(skipped.Steps[^1].Status == "skipped", "Parse skipped steps without inventing success.");
            var finishing = CurrentJobReader.Parse(content, events + "[2026-10-05 12:02:00Z INFO Worker] Job completed.\n", started, now, redactor, false);
            check(finishing.Status == "finishing" && finishing.Steps[^1].Status == "unknown", "A finishing worker must not leave a step falsely running.");
            var partial = CurrentJobReader.Parse("{malformed", events, started, now, redactor, true);
            check(partial.Partial && partial.Name == "Job details unavailable" && partial.Steps.Length == 2, "Truncated metadata does not prevent bounded recent step readings.");
            check(CurrentJobReader.Parse(header + "{", events, started, now, redactor, false).Name == "Job details unavailable", "Incomplete job JSON is tolerated.");
            check(CurrentJobReader.Parse(content.Replace("https://github.com", "https://evil.example"), events, started, now, redactor, false).RunUrl is null, "Job metadata cannot create links to an arbitrary origin.");
            check(CurrentJobReader.Parse(content.Replace("example/api", "../evil"), events, started, now, redactor, false).RunUrl is null, "Reject malformed repository paths.");
            check(CurrentJobReader.Parse(content, events, now, now, redactor, false).Steps.Length == 0, "Ignore step events from before this worker.");

            File.WriteAllText(Path.Combine(root, "_diag", "credentials.log"), "private-file");
            check(RunnerLogs.List(root) is [{ Name: file }], "Only correctly named listener and worker diagnostics are listed.");
            var excerpt = RunnerLogs.Excerpt(root, file, redactor);
            check(excerpt.Lines.Length == 5 && !JsonSerializer.Serialize(excerpt).Contains("private-payload-value"), "Omit job payload and multiline continuations from excerpts.");
            check(excerpt.Lines.Any(l => l.Level == "WARN"), "Keep severity for warning and error filters.");
            foreach (var path in new[] { "../.credentials", "/etc/passwd", "Worker_20261005-120000-utc.log/../../.credentials", "credentials.log" })
                check(RunnerLogs.Read(root, path, true) is null, "Reject traversal and files outside the diagnostic allowlist.");
            File.AppendAllText(Path.Combine(root, "_diag", file), "[2026-10-05 12:02:10Z INFO Worker] unfinished-private");
            check(!JsonSerializer.Serialize(RunnerLogs.Excerpt(root, file, redactor)).Contains("unfinished-private"), "Never show a partially written final line.");
            foreach (var secretLine in new[] { "Authorization: Bearer abc", "password=abc", "\"access_token\":\"abc\"", "Cookie: session=abc", "-----BEGIN PRIVATE KEY-----" })
                check(redactor.Redact(secretLine) == "[Sensitive diagnostic line omitted]", "Remove credential-bearing diagnostic lines.");
            foreach (var secret in new[] { "configured-token-value", "custom-private-value", "ghp_abcdefghijklmnopqrstuvwxyz", "github_pat_abcdefghijklmnopqrstuvwxyz", "eyJhbGciOiJIUzI1NiJ9.eyJzdWIiOiIxIn0.signature" })
                check(!redactor.Redact("value " + secret).Contains(secret), "Mask configured values and common token formats.");
            check(!redactor.Redact("https://user:pass@example.com/path https://example.com?sig=secret").Contains("pass@"), "Mask URL credentials and signed query strings.");
            check(redactor.Redact("<script>alert(1)</script>").Contains("<script>"), "Redaction retains text; the browser must render it as text, never HTML.");
            File.WriteAllText(Path.Combine(root, "_diag", file), new string('x', RunnerLogs.TailBytes + 100) + "\n" + events);
            var bounded = RunnerLogs.Excerpt(root, file, redactor);
            check(bounded.Truncated && bounded.Lines.Length == 4, "Large logs are bounded and incomplete first lines are discarded.");
            File.WriteAllText(Path.Combine(root, "_diag", file), string.Concat(Enumerable.Repeat(events, 300)));
            check(RunnerLogs.Excerpt(root, file, redactor) is { Truncated: true, Lines.Length: 1000 }, "Cap log responses at 1000 lines.");
            if (OperatingSystem.IsLinux())
            {
                var fifo = Path.Combine(root, "_diag", "Worker_20261005-120200-utc.log");
                using (var mkfifo = new System.Diagnostics.Process { StartInfo = new("mkfifo") { UseShellExecute = false } })
                {
                    mkfifo.StartInfo.ArgumentList.Add(fifo); mkfifo.Start(); await mkfifo.WaitForExitAsync();
                    check(mkfifo.ExitCode == 0 && RunnerLogs.Read(root, Path.GetFileName(fifo), true) is null, "Named pipes cannot block a log request.");
                }
                File.Delete(fifo);
                var linked = Path.Combine(root, "_diag", "Worker_20261005-120100-utc.log");
                File.CreateSymbolicLink(linked, Path.Combine(root, ".runner"));
                check(RunnerLogs.List(root).All(f => f.Name != Path.GetFileName(linked)) && RunnerLogs.Read(root, Path.GetFileName(linked), true) is null, "Reject symlinked log files.");
                File.Delete(linked);
                var other = Path.Combine(root, "other"); Directory.CreateDirectory(other);
                Directory.CreateSymbolicLink(Path.Combine(other, "_diag"), Path.Combine(root, "_diag"));
                check(RunnerLogs.List(other).Length == 0 && RunnerLogs.Read(other, file, true) is null, "Reject symlinked diagnostic directories.");
                Directory.Delete(Path.Combine(other, "_diag"));
            }
            using var http = new HttpClient();
            var monitor = new RunnerMonitor(new() { Demo = true }, new GitHubRunnerClient(http, new()));
            var demo = await monitor.GetSnapshotAsync();
            check(demo.Runners[0].CurrentJob?.Steps.Length == 2, "Demo supplies explicit job and step examples.");
            check(await monitor.GetLogsAsync("not-a-runner", "../../etc/passwd") is null, "API resolves only discovered runner IDs.");
            var logs = await monitor.GetLogsAsync(demo.Runners[0].Id, null);
            check(logs is { Enabled: true, Files.Length: 2 } && logs.Excerpt.Lines.Length > 0, "Demo log endpoint contains sample diagnostics.");
            var disabled = new RunnerMonitor(new() { Demo = true, Logs = new() { Enabled = false } }, new GitHubRunnerClient(http, new()));
            check(await disabled.GetLogsAsync(demo.Runners[0].Id, null) is { Enabled: false, Files.Length: 0, Excerpt.Lines.Length: 0 }, "Disable log access server-side.");
            File.Delete(Path.Combine(root, "_diag", file));
            check(RunnerLogs.Excerpt(root, file, redactor).Lines.Length == 0, "Rotation or deletion returns an unavailable excerpt rather than failing.");
        }
        finally { Directory.Delete(root, true); }
    }
}
