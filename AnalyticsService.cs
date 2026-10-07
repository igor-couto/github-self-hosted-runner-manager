using System.Globalization;
using System.Text;

namespace RunnerRoom;

public sealed class AnalyticsService(RunnerOptions options, RunnerMonitor monitor) : BackgroundService
{
    private readonly AnalyticsStore store = new(options.Monitoring.StateDirectory ?? Environment.GetEnvironmentVariable("STATE_DIRECTORY") ??
        (OperatingSystem.IsLinux() ? "/var/lib/runner-room" : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "RunnerRoom")), options.Analytics.RetentionDays, !options.Demo);
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await Task.Yield();
        if (options.Demo) { SeedDemo(store, DateTimeOffset.UtcNow); return; }
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(15));
        try
        {
            do
            {
                try
                {
                    var snapshot = await monitor.GetSnapshotAsync();
                    var redactor = new LogRedactor(options);
                    var events = new List<HistoryEvent>();
                    foreach (var runner in snapshot.Runners)
                    {
                        foreach (var file in RunnerLogs.List(runner.Path).Where(f => f.Kind == "Listener").Take(5))
                        {
                            if (RunnerLogs.Read(runner.Path, file.Name, true) is { } read)
                                events.AddRange(ReadEvents(runner.Id, read.Text, redactor));
                        }
                    }
                    // Persist only selected, masked metadata; never store raw diagnostic content.
                    snapshot = snapshot with { Runners = snapshot.Runners.Select(r => r with { DisplayName = redactor.Redact(r.DisplayName), Repository = r.Repository is null ? null : redactor.Redact(r.Repository) }).ToArray() };
                    store.Record(snapshot, events); store.Save(snapshot.CheckedAt);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
                { store.CollectionFailed(); }
            } while (await timer.WaitForNextTickAsync(stoppingToken));
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
        finally { store.Save(DateTimeOffset.UtcNow, true); }
    }
    internal static HistoryEvent[] ReadEvents(string id, string content, LogRedactor redactor)
    {
        var result = new List<HistoryEvent>(); JobSummary? pending = null;
        foreach (var line in content.Split('\n'))
        {
            var job = LocalRunnerReader.ParseJobLog(line);
            if (job is null) continue;
            var start = job.Result is not null && pending?.Name == job.Name && pending.At <= job.At ? pending.At : (DateTimeOffset?)null;
            result.Add(new(id, redactor.Redact(job.Name), job.At, job.Result == "Cancelled" ? "Canceled" : job.Result, start));
            pending = job.Result is null ? job : null;
        }
        return result.ToArray();
    }
    public IResult Get(string? from, string? to, string? runner, string? result, int? page, bool export = false)
    {
        var now = DateTimeOffset.UtcNow;
        if (!TryRange(from, to, now, out var start, out var end) || (result is not null && result != "Incomplete" && !AnalyticsStore.ValidResult(result)) || page is < 1)
            return Results.BadRequest(new { message = "Choose a valid UTC date range of up to 90 days, a supported outcome and a positive page number." });
        var report = store.Query(start, end, string.IsNullOrEmpty(runner) ? null : runner, result, page ?? 1, options.Demo, export);
        if (!export) return Results.Ok(report);
        return Results.File(Encoding.UTF8.GetBytes(Csv(report)), "text/csv; charset=utf-8", "runner-room-job-history.csv");
    }
    internal AnalyticsReport RecentJobs(DateTimeOffset now) => store.Query(now.AddMinutes(-15), now.AddTicks(1), null, null, 1, allJobs: true);
    public IResult Job(string id)
    {
        var now = DateTimeOffset.UtcNow;
        var job = store.Query(now.AddDays(-90), now.AddTicks(1), null, null, 1, options.Demo, allJobs: true).Jobs.FirstOrDefault(j => j.Id == id);
        return job is null ? Results.NotFound() : Results.Ok(job);
    }
    internal static bool TryRange(string? from, string? to, DateTimeOffset now, out DateTimeOffset start, out DateTimeOffset end)
    {
        var today = DateOnly.FromDateTime(now.UtcDateTime);
        start = end = default;
        var first = today.AddDays(-6); var last = today;
        if ((from is not null && !DateOnly.TryParseExact(from, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out first)) ||
            (to is not null && !DateOnly.TryParseExact(to, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out last)) ||
            first > last || last > today || last.DayNumber - first.DayNumber >= 90) return false;
        start = new(first.ToDateTime(TimeOnly.MinValue), TimeSpan.Zero);
        end = last == today ? now : new(last.AddDays(1).ToDateTime(TimeOnly.MinValue), TimeSpan.Zero);
        return true;
    }
    internal static string Csv(AnalyticsReport report)
    {
        string Cell(string? value)
        {
            value ??= "";
            // Spreadsheet formula injection protection, including formulas preceded by whitespace.
            if (value.TrimStart().FirstOrDefault() is '=' or '+' or '-' or '@' || value.StartsWith('\t') || value.StartsWith('\r')) value = "'" + value;
            return "\"" + value.Replace("\"", "\"\"") + "\"";
        }
        var text = new StringBuilder("Runner,Repository,Job,Started (UTC),Completed (UTC),Outcome,Duration seconds\r\n");
        foreach (var job in report.Jobs)
        {
            var runner = report.RunnerOptions.FirstOrDefault(r => r.Id == job.RunnerId);
            text.AppendLine(string.Join(',', new[] { runner?.Name, runner?.Repository, job.Name, job.StartedAt?.ToString("O"), job.CompletedAt?.ToString("O"), job.Result ?? "Incomplete", job.DurationSeconds?.ToString(CultureInfo.InvariantCulture) }.Select(Cell)));
        }
        return text.ToString();
    }
    internal static void SeedDemo(AnalyticsStore target, DateTimeOffset now)
    {
        var definitions = RunnerMonitor.Demo(now).Runners.Take(3).ToArray();
        var first = new DateTimeOffset(now.UtcDateTime.Date.AddDays(-6), TimeSpan.Zero);
        for (var time = first; time < now.AddSeconds(-15); time = time.AddMinutes(15))
        {
            var runners = definitions.Select((r, i) => r with { Status = (time.Hour + i) % 6 == 0 ? "offline" : (time.Hour + i) % 3 == 0 ? "busy" : "idle" }).ToArray();
            // Demo has explicitly sampled slices, not fabricated 100% coverage.
            target.Record(new("home-server", null, time, true, null, null, runners), []);
            target.Record(new("home-server", null, time.AddSeconds(15), true, null, null, runners), []);
            if (time.Minute == 0 && time.Hour % 2 == 0)
            {
                var r = definitions[time.Hour % definitions.Length]; var start = time.AddMinutes(-4 - time.Hour % 4);
                target.Record(new("home-server", null, time.AddSeconds(15), true, null, null, runners),
                    [new(r.Id, "Build and test", start, null), new(r.Id, "Build and test", time, time.Hour % 8 == 0 ? "Failed" : "Succeeded", start)]);
            }
        }
        target.Record(new("home-server", null, now, true, null, null, definitions), []);
    }
}
