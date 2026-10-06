namespace RunnerRoom;

public sealed class AlertService : BackgroundService
{
    private readonly RunnerOptions options;
    private readonly RunnerMonitor runners;
    private readonly DetailedSystemMonitor system;
    private readonly AnalyticsService analytics;
    private readonly AlertEngine engine;
    private readonly AlertRule[] rules;
    private readonly AlertNotifier notifier;
    private readonly HttpClient client = new(new HttpClientHandler { AllowAutoRedirect = false }) { Timeout = TimeSpan.FromSeconds(5) };
    private readonly string[] configurationWarnings;
    private readonly SemaphoreSlim wake = new(0, 1);
    private readonly object scheduleGate = new();
    private DateTimeOffset? lastChecked, nextCheck, lastRequested;
    private AlertObservation[] observations = [];
    private string? scanWarning;
    private bool checking;
    private int Interval => Math.Clamp(options.Alerts.CheckIntervalSeconds, 15, 3600);
    public AlertService(RunnerOptions options, RunnerMonitor runners, DetailedSystemMonitor system, AnalyticsService analytics)
    {
        this.options = options; this.runners = runners; this.system = system; this.analytics = analytics;
        var warnings = new List<string>();
        var requested = options.Alerts.Rules is { Length: > 0 } ? options.Alerts.Rules : AlertEvaluator.Defaults();
        rules = requested.Where(r => r is not null && AlertEvaluator.Valid(r)).GroupBy(r => r.Id).Where(g => g.Count() == 1).Select(g => g.Single()).Take(32).ToArray();
        if (rules.Length != requested.Length) warnings.Add("Some alert rules are invalid, duplicated or exceed the 32-rule limit. Only valid, unique rules are enabled.");
        if (!AlertNotifier.ValidQuietHours(options.Alerts)) warnings.Add("Invalid quiet hours. Use two different UTC times in HH:mm format. External notifications are paused.");
        if (options.Alerts.CheckIntervalSeconds != Interval) warnings.Add("Check interval is limited to 15–3600 seconds.");
        var directory = options.Monitoring.StateDirectory ?? Environment.GetEnvironmentVariable("STATE_DIRECTORY") ??
            (OperatingSystem.IsLinux() ? "/var/lib/runner-room" : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "RunnerRoom"));
        engine = new(directory, options.Alerts, !options.Demo);
        notifier = new(options.Alerts, client);
        if (notifier.Warning is { } warning) warnings.Add(warning);
        configurationWarnings = warnings.ToArray();
    }
    public AlertReport Current()
    {
        lock (scheduleGate)
        {
            var records = engine.Snapshot();
            var warnings = configurationWarnings.Concat(new[] { engine.Warning, scanWarning }.OfType<string>()).ToArray();
            return new(options.Alerts.Enabled, options.Demo, Interval, Math.Clamp(options.Alerts.RepeatMinutes, 0, 10080),
                lastChecked, options.Alerts.Enabled ? nextCheck : null, AlertNotifier.IsQuiet(options.Alerts, DateTimeOffset.UtcNow),
                options.Alerts.QuietHoursStartUtc is null ? null : options.Alerts.QuietHoursStartUtc + "–" + options.Alerts.QuietHoursEndUtc + " UTC",
                options.Demo ? "Demo — no notifications sent" : notifier.Configured ? "HTTPS webhook" : "Dashboard only",
                rules.Select(r => new AlertRuleStatus(r.Id, r.Kind, r.Threshold, r.HoldSeconds, r.Severity, r.Target)
                { Targets = observations.Count(o => o.RuleId == r.Id), Unavailable = observations.Count(o => o.RuleId == r.Id && o.Breached is null) }).ToArray(),
                records.Where(a => a.ResolvedAt is null).ToArray(), records.Where(a => a.ResolvedAt is not null).Take(200).ToArray(), warnings);
        }
    }
    public bool RequestCheck()
    {
        lock (scheduleGate)
        {
            if (!options.Alerts.Enabled || checking || wake.CurrentCount > 0 || lastRequested is { } at && DateTimeOffset.UtcNow - at < TimeSpan.FromSeconds(10)) return false;
            lastRequested = DateTimeOffset.UtcNow; nextCheck = lastRequested; wake.Release(); return true;
        }
    }
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await Task.Yield();
        if (!options.Alerts.Enabled) return;
        try
        {
            do
            {
                lock (scheduleGate) checking = true;
                try
                {
                    var now = DateTimeOffset.UtcNow;
                    string? checkWarning = null;
                    if (options.Demo) SeedDemo(now);
                    else
                    {
                        RunnerSnapshot snapshot;
                        try { snapshot = await runners.GetSnapshotAsync(); }
                        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
                        {
                            snapshot = new(Environment.MachineName, null, DateTimeOffset.UtcNow, false, "Runner scan failed.", null, []);
                        }
                        if (snapshot.Error is not null) checkWarning = "Runner readings are unavailable; runner conditions are unconfirmed.";
                        now = DateTimeOffset.UtcNow;
                        var jobs = analytics.RecentJobs(now);
                        var found = AlertEvaluator.Evaluate(rules, snapshot, system.Current, jobs.Jobs, now, new LogRedactor(options),
                            jobs.CheckedAt is { } checkedAt && now - checkedAt <= TimeSpan.FromSeconds(45) && checkedAt <= now);
                        engine.Check(now, found, rules.Select(AlertEvaluator.Prefix).ToArray());
                        lock (scheduleGate) observations = found;
                        // Persist the transition before delivery. A crash during delivery may cause a retry;
                        // receivers can deduplicate using incidentId + state.
                        engine.Save();
                        if (!notifier.Configured || !AlertNotifier.IsQuiet(options.Alerts, now))
                            foreach (var alert in engine.Due(now))
                            {
                                if (stoppingToken.IsCancellationRequested) break;
                                var success = await notifier.SendAsync(alert, stoppingToken);
                                engine.Delivered(alert.Id, DateTimeOffset.UtcNow, success, !notifier.Configured);
                            }
                        engine.Save();
                    }
                    lock (scheduleGate) { lastChecked = now; scanWarning = checkWarning; }
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
                {
                    engine.ScanFailed();
                    lock (scheduleGate) scanWarning = "The latest alert check failed. Existing incidents remain unconfirmed; the scheduler will retry.";
                }
                finally { lock (scheduleGate) { checking = false; nextCheck = DateTimeOffset.UtcNow.AddSeconds(Interval); } }
                await wake.WaitAsync(TimeSpan.FromSeconds(Interval), stoppingToken);
            } while (!stoppingToken.IsCancellationRequested);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
        finally { if (!options.Demo) engine.Save(); }
    }
    private void SeedDemo(DateTimeOffset now)
    {
        if (engine.Snapshot().Length > 0) return;
        var sample = new AlertObservation("sample:disk", "disk", "Disk space — /mnt/builds", "critical", true, "92.4% used (threshold 90%)", 0);
        engine.Check(now.AddMinutes(-30), [sample], ["sample:"]);
        engine.Delivered(engine.Snapshot().Single().Id, now.AddMinutes(-30), true, true);
        engine.Check(now.AddMinutes(-10), [sample with { Breached = false, Detail = "Disk usage returned to 68.2%." }], ["sample:"]);
        engine.Check(now.AddMinutes(-3), [sample with { Key = "sample:offline", RuleId = "offline", Title = "Newsletter — runner offline", Severity = "warning", Detail = "No local runner process found." }], ["sample:"]);
        engine.Delivered(engine.Snapshot().First(a => a.ResolvedAt is null).Id, now.AddMinutes(-3), true, true);
    }
    public override void Dispose() { base.Dispose(); client.Dispose(); wake.Dispose(); }
}
