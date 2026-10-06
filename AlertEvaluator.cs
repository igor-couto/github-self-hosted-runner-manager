using System.Globalization;
using System.Text.RegularExpressions;

namespace RunnerRoom;

internal static class AlertEvaluator
{
    internal static AlertRule[] Defaults() => [
        new() { Id = "offline", Kind = "runner-offline" },
        new() { Id = "long-job", Kind = "job-duration", Threshold = 60 },
        new() { Id = "failed-jobs", Kind = "job-failures", Threshold = 3, HoldSeconds = 0 },
        new() { Id = "cpu", Kind = "cpu", Threshold = 90 },
        new() { Id = "memory", Kind = "memory", Threshold = 90 },
        new() { Id = "disk", Kind = "disk", Threshold = 90, Severity = "critical" },
        new() { Id = "temperature", Kind = "temperature", Threshold = 80 },
        new() { Id = "monitoring", Kind = "monitor-unavailable", Severity = "critical" }
    ];
    internal static bool Valid(AlertRule rule) =>
        rule.Id is not null && Regex.IsMatch(rule.Id, "^[a-z0-9-]{1,50}$", RegexOptions.CultureInvariant) &&
        rule.Kind is "runner-offline" or "job-duration" or "job-failures" or "cpu" or "memory" or "disk" or "temperature" or "monitor-unavailable" &&
        rule.Severity is "warning" or "critical" && rule.HoldSeconds is >= 0 and <= 86400 &&
        double.IsFinite(rule.Threshold) && rule.Threshold >= 0 &&
        (rule.Kind is not ("cpu" or "memory" or "disk") || rule.Threshold <= 100) &&
        (rule.Kind != "job-duration" || rule.Threshold > 0) &&
        (rule.Kind != "job-failures" || rule.Threshold >= 1 && rule.Threshold == Math.Floor(rule.Threshold));

    internal static string Prefix(AlertRule rule) => rule.Id + ":" + Convert.ToHexString(
        System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(System.Text.Json.JsonSerializer.Serialize(rule))))[..12] + ":";

    internal static AlertObservation[] Evaluate(AlertRule[] rules, RunnerSnapshot snapshot, DetailedSystemSnapshot? system,
        HistoricalJob[] jobs, DateTimeOffset now, LogRedactor redactor, bool jobsFresh = true)
    {
        var observations = new List<AlertObservation>();
        var runnersFresh = snapshot.Error is null && now - snapshot.CheckedAt <= TimeSpan.FromSeconds(45) && snapshot.CheckedAt <= now;
        var systemFresh = system is not null && now - system.CheckedAt <= TimeSpan.FromSeconds(45) && system.CheckedAt <= now;
        foreach (var rule in rules)
        {
            string Mask(string text, int limit) { var masked = redactor.Redact(text); return masked.Length <= limit ? masked : masked[..limit] + "…"; }
            void Add(string key, string title, bool? breached, string detail) =>
                observations.Add(new(Prefix(rule) + Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(key))),
                    rule.Id, Mask(title, 512), rule.Severity, breached, Mask(detail, 1024), rule.HoldSeconds));
            void Metric(string key, string title, double? value, string unit)
            {
                if (value is { } number && !double.IsFinite(number)) value = null;
                Add(key, title, value is { } v ? v >= rule.Threshold : null, value is { } n ?
                    n.ToString("0.0", CultureInfo.InvariantCulture) + unit + " (threshold " + rule.Threshold.ToString(CultureInfo.InvariantCulture) + unit + ")" : "Reading unavailable.");
            }
            switch (rule.Kind)
            {
                case "runner-offline":
                case "job-duration":
                case "job-failures":
                    foreach (var runner in snapshot.Runners.Where(r => rule.Target is null || rule.Target == r.Path || rule.Target == r.Folder))
                    {
                        if (rule.Kind == "runner-offline")
                            Add(runner.Id, runner.DisplayName + " — runner offline", !runnersFresh || runner.Status == "unknown" ? null : runner.Status == "offline", "Local process status: " + runner.Status);
                        else if (rule.Kind == "job-duration")
                            Metric(runner.Id, runner.DisplayName + " — long-running job",
                                !runnersFresh || runner.Status == "unknown" ? null : runner.Status != "busy" ? 0 :
                                    runner.WorkerStartedAt is { } start && start <= now ? (now - start).TotalMinutes : null, " min");
                        else
                            Metric(runner.Id, runner.DisplayName + " — repeated job failures", !runnersFresh || !jobsFresh ? null :
                                jobs.Count(j => j.RunnerId == runner.Id && j.CompletedAt >= now.AddMinutes(-15) && j.CompletedAt <= now && j.Result is "Failed" or "Abandoned"), " failures / 15 min");
                    }
                    break;
                case "cpu": Metric("host", "High CPU usage", runnersFresh ? snapshot.System?.CpuUsagePercent : null, "%"); break;
                case "memory": Metric("host", "High memory usage", runnersFresh && snapshot.System?.Memory is { TotalBytes: > 0 } memory ? memory.UsedPercent : null, "%"); break;
                case "disk":
                    foreach (var disk in system?.FileSystems.Where(d => rule.Target is null || rule.Target == d.Mount) ?? [])
                        Metric(disk.Mount, "Disk space — " + disk.Mount, systemFresh && disk.Usage is { TotalBytes: > 0 } usage ? usage.UsedPercent : null, "% used");
                    break;
                case "temperature":
                    var values = system?.Hardware.Temperatures.Select(t => t.Celsius).Where(double.IsFinite).ToArray() ?? [];
                    Metric("host", "High CPU / system temperature", systemFresh && values.Length > 0 ? values.Max() : null, " °C"); break;
                case "monitor-unavailable":
                    Add("host", "Monitoring unavailable", !runnersFresh || !systemFresh, !runnersFresh ? "Runner scan unavailable or out of date." : !systemFresh ? "System scan unavailable or out of date." : "Monitoring is up to date."); break;
            }
        }
        return observations.ToArray();
    }
}
