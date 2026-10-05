using RunnerRoom;

internal static class AnalyticsChecks
{
    internal static void Run(Action<bool, string> check)
    {
        var root = Path.Combine(Path.GetTempPath(), "runner-room-analytics-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var now = new DateTimeOffset(DateTime.UtcNow.Date.AddDays(-1).AddHours(10), TimeSpan.Zero);
            var runner = new RunnerInfo("runner", "runner", "busy", 123) { Path = "/srv/runner", DisplayName = "API", Repository = "example/api" };
            RunnerSnapshot Snapshot(DateTimeOffset at, string status = "busy", string? error = null) =>
                new("server", "/srv", at, false, error, null, [runner with { Status = status }]);
            var store = new AnalyticsStore(root, 30);
            AnalyticsReport Report(string? outcome = null, int page = 1) => store.Query(now.AddDays(-1), now.AddDays(1), null, outcome, page);
            store.Record(Snapshot(now.AddSeconds(-10)), []);
            store.Record(Snapshot(now.AddSeconds(10), "idle"), []);
            var report = Report();
            check(report.Hours.Length == 2 && report.Hours.All(h => h.Busy == 10), "Activity is split at UTC hour boundaries.");
            check(report.Runners.Single() is { Utilization: 100, ObservedSeconds: 20 }, "Utilization uses observed known states.");
            store.Record(Snapshot(now.AddSeconds(25)), []);
            store.Record(Snapshot(now.AddMinutes(5), "idle"), []);
            report = Report();
            check(report.Runners.Single() is { BusySeconds: 20, IdleSeconds: 15, OfflineSeconds: 0 }, "Collection gaps are not counted as downtime.");
            store.Record(Snapshot(now.AddMinutes(5).AddSeconds(15), "unknown"), []);
            store.Record(Snapshot(now.AddMinutes(5).AddSeconds(30), "idle"), []);
            check(Report().Runners.Single() is { UnknownSeconds: 15, Availability: 100 }, "Unknown states count as coverage but do not lower known availability.");
            var observed = Report().Runners.Single().ObservedSeconds;
            store.Record(Snapshot(now.AddMinutes(1)), []);
            store.Record(Snapshot(now.AddMinutes(6), error: "Scan failed"), []);
            store.Record(Snapshot(now.AddMinutes(6).AddSeconds(15)), []);
            check(Report().Runners.Single().ObservedSeconds == observed, "Older and failed scans do not create observations.");
            var start = now.AddHours(-1);
            HistoryEvent[] events = [
                new(runner.Id, "Build", start, null),
                new(runner.Id, "Build", start.AddSeconds(120), "Succeeded", start),
                new(runner.Id, "Build", start.AddMinutes(5), null),
                new(runner.Id, "Build", start.AddMinutes(8), "Failed", start.AddMinutes(5)),
                new(runner.Id, "Skipped job", start.AddMinutes(9), "Skipped"),
                new(runner.Id, "Missing end", start.AddMinutes(10), null)
            ];
            store.Record(Snapshot(now.AddMinutes(7)), events);
            store.Record(Snapshot(now.AddMinutes(7)), events);
            report = Report();
            check(report.TotalJobs == 4 && report.Summary is { Completed: 3, Incomplete: 1, SuccessRate: 50 }, "Replayed logs are deduplicated and skipped jobs are excluded from success rate.");
            check(report.Summary is { AverageDurationSeconds: 150, MedianDurationSeconds: 150, P95DurationSeconds: 180 }, "Duration aggregates use paired starts and ends.");
            check(Report("Failed") is { TotalJobs: 1, Summary.Completed: 3 }, "Outcome filters affect jobs without silently changing summary statistics.");
            check(store.Query(now.AddDays(-1), now.AddDays(1), "missing", null, 1).TotalJobs == 0, "Runner filtering excludes other runners.");
            check(AnalyticsStore.Jobs([events[0], events[1] with { MatchedStart = null }]).Single().DurationSeconds is null, "Missing log windows cannot fabricate same-name job durations.");
            store.Save(now, true);
            var restored = new AnalyticsStore(root, 30);
            restored.Record(Snapshot(now.AddMinutes(8)), events);
            var restoredReport = restored.Query(now.AddDays(-1), now.AddDays(1), null, null, 1);
            check(restoredReport.TotalJobs == 4 && restoredReport.Runners.Single().ObservedSeconds == report.Runners.Single().ObservedSeconds, "Restart restores records without counting the shutdown interval or replaying jobs.");
            store.Record(Snapshot(now.AddMinutes(9)), Enumerable.Range(0, 70).Select(i => new HistoryEvent(runner.Id, "Job " + i, now.AddSeconds(i), "Succeeded")));
            check(Report(null, 2) is { TotalJobs: 74, Jobs.Length: 24, Page: 2 }, "Job history is paginated.");
            check(Report(null, 999).Page == 2, "Out-of-range pages are clamped.");
            observed = Report().Runners.Single().ObservedSeconds;
            store.CollectionFailed();
            check(Report().Warnings.Length > 0, "Collection failures are visible without losing retained history.");
            store.Record(Snapshot(now.AddMinutes(9).AddSeconds(15)), []);
            check(Report().Warnings.Length == 0 && Report().Runners.Single().ObservedSeconds == observed, "Collection recovery clears the warning without bridging a failed scan.");
            var redactor = new LogRedactor(new RunnerOptions { Logs = new() { RedactValues = ["secret-job"] } });
            var text = $"[{start:yyyy-MM-ddTHH:mm:ss.fffZ} INFO Terminal] Running job: secret-job\n" +
                $"[{start.AddMinutes(2):yyyy-MM-ddTHH:mm:ss.fffZ} INFO Terminal] Job secret-job completed with result: Succeeded\n";
            var parsed = AnalyticsService.ReadEvents(runner.Id, text, redactor);
            check(parsed.Length == 2 && parsed[1].MatchedStart == start && parsed.All(e => !e.Name.Contains("secret-job")), "Listener summaries are paired and redacted before persistence.");
            check(AnalyticsService.TryRange(null, null, now, out var from, out var to) && from == new DateTimeOffset(now.UtcDateTime.Date.AddDays(-6), TimeSpan.Zero) && to == now, "Default range uses seven UTC calendar dates.");
            check(!AnalyticsService.TryRange("bad", null, now, out _, out _) &&
                !AnalyticsService.TryRange(now.AddDays(-90).ToString("yyyy-MM-dd"), null, now, out _, out _) &&
                !AnalyticsService.TryRange(null, now.AddDays(1).ToString("yyyy-MM-dd"), now, out _, out _), "Invalid, excessive and future ranges are rejected.");
            var csv = AnalyticsService.Csv(report with { Jobs = [new(runner.Id, " =HYPERLINK(\"evil\")\nnext", start, start.AddMinutes(1), "Succeeded")] });
            check(csv.Contains("\"' =HYPERLINK(\"\"evil\"\")\nnext\"") && csv.Contains("example/api"), "CSV escapes quotes/newlines and neutralizes spreadsheet formulas.");
            var demo = new AnalyticsStore(root, 30, false);
            AnalyticsService.SeedDemo(demo, now);
            check(demo.Query(now.AddDays(-7), now, null, null, 1).RunnerOptions.All(r => r.Id != runner.Id), "Demo history does not load production records.");
            store.Record(Snapshot(now.AddDays(31)), []);
            check(store.Query(now.AddDays(-1), now.AddDays(32), null, null, 1).TotalJobs == 0, "Expired jobs are pruned.");
            File.WriteAllText(Path.Combine(root, "analytics.json"), "{bad");
            var corrupt = new AnalyticsStore(root, 30);
            corrupt.Record(Snapshot(now), []);
            corrupt.Save(now, true);
            check(File.ReadAllText(Path.Combine(root, "analytics.json")) == "{bad" && corrupt.Query(now.AddDays(-1), now, null, null, 1).Warnings.Length > 0, "Corrupt history is preserved with a visible warning.");
            var blocked = Path.Combine(root, "blocked");
            File.WriteAllText(blocked, "file");
            var memory = new AnalyticsStore(blocked, 30);
            memory.Save(now, true);
            check(memory.Query(now.AddDays(-1), now, null, null, 1).Warnings.Length > 0, "Unwritable persistence degrades to in-memory collection.");
        }
        finally { Directory.Delete(root, true); }
    }
}
