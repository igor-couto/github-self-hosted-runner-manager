using System.Net;
using System.Text.Json;
using RunnerRoom;

internal static class AlertChecks
{
    internal static async Task Run(Action<bool, string> check)
    {
        var directory = Path.Combine(Path.GetTempPath(), "runner-room-alerts-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var now = DateTimeOffset.UtcNow.AddHours(-3);
            var options = new AlertOptions { CheckIntervalSeconds = 15, RepeatMinutes = 60 };
            var engine = new AlertEngine(directory, options);
            var bad = new AlertObservation("cpu:test", "cpu", "CPU high", "warning", true, "95%", 30);
            engine.Check(now, [bad], ["cpu:"]);
            engine.Check(now.AddSeconds(15), [bad], ["cpu:"]);
            check(engine.Snapshot().Length == 0, "Thresholds wait for sustained observations.");
            engine.Check(now.AddSeconds(30), [bad], ["cpu:"]);
            var incident = engine.Snapshot().Single();
            check(engine.Due(now.AddSeconds(30)).Length == 1, "Sustained breach creates one pending incident.");
            engine.Delivered(incident.Id, now.AddSeconds(30), true);
            engine.Check(now.AddSeconds(45), [bad], ["cpu:"]);
            check(engine.Snapshot().Length == 1 && engine.Due(now.AddSeconds(45)).Length == 0, "Repeated checks do not duplicate or spam an active incident.");
            engine.Check(now.AddMinutes(61), [bad], ["cpu:"]);
            check(engine.Due(now.AddMinutes(61)).Length == 1, "Reminder cooldown permits a later notification.");
            engine.Delivered(incident.Id, now.AddMinutes(61), false);
            check(engine.Due(now.AddMinutes(62)).Length == 0 && engine.Due(now.AddMinutes(66)).Length == 1, "Failed deliveries retry after five minutes, not every scan.");
            engine.Check(now.AddMinutes(67), [bad with { Breached = null }], ["cpu:"]);
            check(engine.Snapshot().Single().State == "unknown" && engine.Due(now.AddMinutes(67)).Length == 0, "Unavailable readings neither resolve nor notify a firing incident.");
            engine.Check(now.AddMinutes(68), [bad with { Breached = false }], ["cpu:"]);
            check(engine.Snapshot().Single() is { State: "resolved", Pending: true } && engine.Due(now.AddMinutes(68)).Length == 1, "Observed recovery schedules a recovery notification for delivered alerts.");
            engine.Delivered(incident.Id, now.AddMinutes(68), true);
            engine.Save();
            var restored = new AlertEngine(directory, options);
            check(restored.Snapshot().Single().State == "resolved" && restored.Due(now.AddMinutes(69)).Length == 0, "Resolved history and delivery state survive restart.");
            engine.Check(now.AddMinutes(70), [bad], ["cpu:"]);
            engine.Check(now.AddMinutes(70).AddSeconds(15), [bad with { Breached = null }], ["cpu:"]);
            engine.Check(now.AddMinutes(70).AddSeconds(30), [bad], ["cpu:"]);
            check(engine.Snapshot().Length == 1, "Unknown observations reset the sustained-condition timer.");
            engine.Check(now.AddMinutes(72), [bad], ["cpu:"]);
            check(engine.Snapshot().Length == 1, "Long scheduler gaps reset pending thresholds.");
            engine.Check(now.AddMinutes(72).AddSeconds(30), [bad], ["cpu:"]);
            check(engine.Snapshot().Length == 2, "A new sustained breach creates a separate incident after recovery.");
            engine.Save();
            restored = new AlertEngine(directory, options);
            check(restored.Snapshot().Any(a => a.State == "unknown") && restored.Due(now.AddMinutes(73)).Length == 0, "Restart revalidates active incidents before sending.");
            restored.Check(now.AddMinutes(73), [], ["cpu:"]);
            check(restored.Snapshot().Any(a => a.State == "unknown" && a.ResolvedAt is null), "Disappearing targets remain unconfirmed rather than recovered.");
            restored.Check(now.AddMinutes(74), [], ["changed-rule:"]);
            check(restored.Snapshot().Any(a => a.State == "retired") && restored.Due(now.AddMinutes(74)).Length == 0, "Changed rules retire incidents without a false recovery notification.");
            var unsent = new AlertEngine(Path.Combine(directory, "unsent"), options);
            unsent.Check(now, [bad with { HoldSeconds = 0 }], ["cpu:"]);
            unsent.Check(now.AddSeconds(15), [bad with { Breached = false }], ["cpu:"]);
            check(unsent.Due(now.AddSeconds(15)).Length == 0, "A breach that recovers during quiet hours does not send a recovery without an initial notification.");
            unsent.Check(now.AddDays(31), [], []);
            check(unsent.Snapshot().Length == 0, "Alert history retention removes expired resolved incidents.");

            var quiet = new AlertOptions { QuietHoursStartUtc = "22:00", QuietHoursEndUtc = "07:00" };
            var day = new DateTimeOffset(2026, 10, 1, 0, 0, 0, TimeSpan.Zero);
            check(AlertNotifier.IsQuiet(quiet, day.AddHours(22)) && AlertNotifier.IsQuiet(quiet, day.AddHours(6)) &&
                !AlertNotifier.IsQuiet(quiet, day.AddHours(7)) && !AlertNotifier.IsQuiet(quiet, day.AddHours(21)), "UTC quiet windows include their start, exclude their end and cross midnight.");
            quiet.QuietHoursEndUtc = "bad";
            check(!AlertNotifier.ValidQuietHours(quiet) && AlertNotifier.IsQuiet(quiet, day), "Invalid quiet hours pause external delivery.");
            check(!AlertNotifier.IsQuiet(new(), day), "Quiet hours are optional.");
            check(AlertEvaluator.Defaults().All(AlertEvaluator.Valid), "Default alert rules are valid.");
            check(!AlertEvaluator.Valid(new() { Id = "bad", Kind = "cpu", Threshold = double.NaN }) &&
                !AlertEvaluator.Valid(new() { Id = "bad", Kind = "job-duration", Threshold = 0 }) &&
                !AlertEvaluator.Valid(new() { Id = "bad", Kind = "cpu", Threshold = 101 }), "Invalid thresholds cannot enable misleading rules.");
            var runner = new RunnerInfo("runner", "api", "offline", null) { Path = "/srv/api", DisplayName = "secret-name", WorkerStartedAt = now.AddHours(-2) };
            var snapshot = new RunnerSnapshot("host", "/srv", now, false, null, null, [runner], new("arm64", 4, 95, 100, new(100, 95, 5), null));
            var detailed = SystemMetricsCollector.Demo(now) with { FileSystems = [new("/", "sda", "ext4", new(100, 95, 5), [])] };
            var redactor = new LogRedactor(new() { Logs = new() { RedactValues = ["secret-name"] } });
            var evaluated = AlertEvaluator.Evaluate(AlertEvaluator.Defaults(), snapshot, detailed, [], now, redactor);
            check(evaluated.Single(o => o.RuleId == "offline").Breached == true && evaluated.All(o => !o.Title.Contains("secret-name")), "Runner alerts use local process status and mask configured secrets.");
            check(evaluated.Single(o => o.RuleId == "cpu").Breached == true && evaluated.Single(o => o.RuleId == "memory").Breached == true &&
                evaluated.Single(o => o.RuleId == "disk").Breached == true, "CPU, RAM and filesystem thresholds are evaluated.");
            evaluated = AlertEvaluator.Evaluate(AlertEvaluator.Defaults(), snapshot, detailed with { CheckedAt = now.AddMinutes(-2) }, [], now, redactor, false);
            check(evaluated.Single(o => o.RuleId == "disk").Breached is null && evaluated.Single(o => o.RuleId == "failed-jobs").Breached is null &&
                evaluated.Single(o => o.RuleId == "monitoring").Breached == true, "Stale metrics and job history cannot clear alerts.");
            evaluated = AlertEvaluator.Evaluate([new() { Id = "long", Kind = "job-duration", Threshold = 60 }],
                snapshot with { Runners = [runner with { Status = "busy" }] }, detailed, [], now, redactor);
            check(evaluated.Single().Breached == true, "Long-running jobs use worker process age.");
            var failures = Enumerable.Range(0, 3).Select(i => new HistoricalJob(runner.Id, "job", now.AddMinutes(-5), now.AddMinutes(-i), "Failed")).ToArray();
            evaluated = AlertEvaluator.Evaluate([new() { Id = "failures", Kind = "job-failures", Threshold = 3 }], snapshot, detailed, failures, now, redactor);
            check(evaluated.Single().Breached == true, "Failure alerts count recent completed failed jobs.");
            evaluated = AlertEvaluator.Evaluate([new() { Id = "filtered", Kind = "runner-offline", Target = "other" }], snapshot, detailed, [], now, redactor);
            check(evaluated.Length == 0, "Rule target filters do not match unrelated runners.");
            evaluated = AlertEvaluator.Evaluate(AlertEvaluator.Defaults(), snapshot with { Error = "scan failed" }, detailed, [], now, redactor);
            check(evaluated.Single(o => o.RuleId == "monitoring").Breached == true && evaluated.Single(o => o.RuleId == "offline").Breached is null,
                "Failed runner scans can trigger monitoring alerts without asserting runners are offline.");
            var capped = new AlertEngine(Path.Combine(directory, "capped"), options);
            capped.Check(now, Enumerable.Range(0, 501).Select(i => bad with { Key = "cpu:" + i, HoldSeconds = 0 }).ToArray(), ["cpu:"]);
            capped.Save();
            check(capped.Snapshot().Length == 500 && capped.Warning is not null, "Active incident capacity stays bounded with a warning after saving.");

            var handler = new WebhookHandler();
            using var client = new HttpClient(handler);
            var notifier = new AlertNotifier(new() { WebhookUrl = "https://alerts.example.test/private-token" }, client);
            check(await notifier.SendAsync(incident, default) && handler.Body?.Contains(incident.Id) == true && !handler.Body.Contains("private-token"), "Webhook payload includes incident identity but never its secret URL.");
            handler.Status = HttpStatusCode.ServiceUnavailable;
            check(!await notifier.SendAsync(incident, default), "Webhook failure is reported for scheduled retry.");
            var invalid = new AlertNotifier(new() { WebhookUrl = "http://invalid.example" }, client);
            check(!invalid.Configured && invalid.Warning is not null, "Webhook delivery requires HTTPS.");
            var secretFile = Path.Combine(directory, "webhook");
            File.WriteAllText(secretFile, "https://alerts.example.test/from-file\n");
            check(new AlertNotifier(new() { WebhookUrlFile = secretFile }, client).Configured, "Webhook secrets can be read from a local file.");
            File.WriteAllText(Path.Combine(directory, "alerts.json"), "{broken");
            var broken = new AlertEngine(directory, options);
            broken.Save();
            check(broken.Warning is not null && File.ReadAllText(Path.Combine(directory, "alerts.json")) == "{broken", "Corrupt alert files are preserved.");
            var blocked = Path.Combine(directory, "blocked");
            File.WriteAllText(blocked, "file");
            var memory = new AlertEngine(blocked, options);
            memory.Save();
            check(memory.Warning is not null, "Persistence failure leaves a visible warning.");
        }
        finally { Directory.Delete(directory, true); }
    }
    private sealed class WebhookHandler : HttpMessageHandler
    {
        public HttpStatusCode Status { get; set; } = HttpStatusCode.OK;
        public string? Body { get; private set; }
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Body = await request.Content!.ReadAsStringAsync(cancellationToken);
            using var parsed = JsonDocument.Parse(Body);
            return new(Status);
        }
    }
}
