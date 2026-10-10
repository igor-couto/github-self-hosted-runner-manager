using System.Globalization;
using System.Text.Json;

namespace RunnerRoom;

public sealed record HistoricalRunner(string Id, string Name, string? Repository);
public sealed record HistoricalJob(string RunnerId, string Name, DateTimeOffset? StartedAt, DateTimeOffset? CompletedAt, string? Result)
{
    // Completion identity remains stable when a matching start is discovered later.
    public string Id => Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
        System.Text.Encoding.UTF8.GetBytes(System.Text.Json.JsonSerializer.Serialize(new { RunnerId, Name, At, Result })))).ToLowerInvariant();
    public double? DurationSeconds => StartedAt is { } start && CompletedAt is { } end && end >= start ? (end - start).TotalSeconds : null;
    public DateTimeOffset At => CompletedAt ?? StartedAt!.Value;
}
public sealed record HistoryEvent(string RunnerId, string Name, DateTimeOffset At, string? Result, DateTimeOffset? MatchedStart = null);
public sealed record ActivityHour(string RunnerId, DateTimeOffset At, double Busy = 0, double Idle = 0, double Offline = 0, double Unknown = 0)
{
    public double Covered => Busy + Idle + Offline + Unknown;
    public double Known => Busy + Idle + Offline;
}
public sealed record RunnerAnalytics(string Id, string Name, double BusySeconds, double IdleSeconds, double OfflineSeconds, double UnknownSeconds, int CompletedJobs)
{
    public double ObservedSeconds => BusySeconds + IdleSeconds + OfflineSeconds + UnknownSeconds;
    public double? Utilization => BusySeconds + IdleSeconds + OfflineSeconds > 0 ? BusySeconds * 100 / (BusySeconds + IdleSeconds + OfflineSeconds) : null;
    public double? Availability => BusySeconds + IdleSeconds + OfflineSeconds > 0 ? (BusySeconds + IdleSeconds) * 100 / (BusySeconds + IdleSeconds + OfflineSeconds) : null;
}
public sealed record DailyAnalytics(string Date, int Completed, int Succeeded, int Failed, int Canceled, double BusySeconds, double KnownSeconds);
public sealed record AnalyticsSummary(int Completed, int Succeeded, int Failed, int Canceled, int Skipped, int Incomplete,
    double? SuccessRate, double? AverageDurationSeconds, double? MedianDurationSeconds, double? P95DurationSeconds);
public sealed record AnalyticsReport(DateTimeOffset From, DateTimeOffset To, DateTimeOffset? CheckedAt, bool Demo,
    HistoricalRunner[] RunnerOptions, RunnerAnalytics[] Runners, DailyAnalytics[] Days, ActivityHour[] Hours,
    AnalyticsSummary Summary, HistoricalJob[] Jobs, int TotalJobs, int Page, int PageSize, string[] Warnings);

internal sealed class AnalyticsStore
{
    internal sealed class State
    {
        public int Version { get; set; } = 1;
        public Dictionary<string, HistoricalRunner> Runners { get; set; } = [];
        public List<ActivityHour> Hours { get; set; } = [];
        public List<HistoryEvent> Events { get; set; } = [];
    }
    private readonly object gate = new();
    private readonly string file;
    private readonly int retention;
    private State state = new();
    private RunnerSnapshot? previous;
    private DateTimeOffset lastSave;
    private DateTimeOffset? checkedAt;
    private bool corrupt;
    private string? warning;
    private string? collectionWarning;
    private bool capped;
    internal AnalyticsStore(string directory, int retention, bool load = true)
    {
        file = Path.Combine(directory, "analytics.json"); this.retention = Math.Clamp(retention, 1, 90);
        if (!load) return;
        try
        {
            if (!File.Exists(file)) return;
            var text = LocalRunnerReader.ReadSmall(file, 24 * 1024 * 1024);
            var loaded = text is null ? null : JsonSerializer.Deserialize<State>(text);
            if (loaded is null || loaded.Version != 1 || loaded.Runners is null || loaded.Hours is null || loaded.Events is null ||
                loaded.Runners.Count > 4096 || loaded.Hours.Count > 50000 || loaded.Events.Count > 20000 ||
                loaded.Runners.Any(p => p.Value is null || p.Key != p.Value.Id || string.IsNullOrEmpty(p.Value.Name)) ||
                loaded.Hours.Any(h => h is null || !loaded.Runners.ContainsKey(h.RunnerId) || new[] { h.Busy, h.Idle, h.Offline, h.Unknown }.Any(n => !double.IsFinite(n) || n < 0) || h.Covered > 3600.01 || h.At.Minute != 0 || h.At.Second != 0) ||
                loaded.Events.Any(e => e is null || !loaded.Runners.ContainsKey(e.RunnerId) || string.IsNullOrEmpty(e.Name) || !ValidResult(e.Result) || e.MatchedStart > e.At) ||
                loaded.Events.DistinctBy(e => (e.RunnerId, e.Name, e.At, e.Result)).Count() != loaded.Events.Count ||
                loaded.Hours.DistinctBy(h => (h.RunnerId, h.At)).Count() != loaded.Hours.Count) throw new JsonException();
            state = loaded;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or ArgumentException)
        { corrupt = true; warning = "Saved analytics could not be read. Collecting in memory; the original file is preserved. Back up and remove it to restore persistence."; }
    }
    internal static bool ValidResult(string? result) => result is null or "Succeeded" or "SucceededWithIssues" or "Failed" or "Canceled" or "Skipped" or "Abandoned";
    internal void CollectionFailed()
    {
        lock (gate)
        {
            previous = null;
            collectionWarning = "The latest history scan failed. Retained records are still available; collection will retry automatically.";
        }
    }
    internal void Record(RunnerSnapshot snapshot, IEnumerable<HistoryEvent> events)
    {
        lock (gate)
        {
            if (previous is not null && snapshot.CheckedAt < previous.CheckedAt) return;
            collectionWarning = snapshot.Error;
            checkedAt = snapshot.CheckedAt;
            foreach (var r in snapshot.Runners) state.Runners[r.Id] = new(r.Id, r.DisplayName, r.Repository);
            if (previous is { Error: null } old && snapshot.Error is null && snapshot.CheckedAt > old.CheckedAt && snapshot.CheckedAt - old.CheckedAt <= TimeSpan.FromSeconds(45))
            {
                var present = snapshot.Runners.Select(r => r.Id).ToHashSet();
                foreach (var runner in old.Runners.Where(r => present.Contains(r.Id)))
                {
                    var cursor = old.CheckedAt;
                    while (cursor < snapshot.CheckedAt)
                    {
                        var hour = new DateTimeOffset(cursor.UtcDateTime.Date.AddHours(cursor.UtcDateTime.Hour), TimeSpan.Zero);
                        var end = snapshot.CheckedAt < hour.AddHours(1) ? snapshot.CheckedAt : hour.AddHours(1);
                        var seconds = (end - cursor).TotalSeconds;
                        var index = state.Hours.FindIndex(h => h.RunnerId == runner.Id && h.At == hour);
                        var value = index >= 0 ? state.Hours[index] : new ActivityHour(runner.Id, hour);
                        // A restart has no baseline. An overlapping clock interval cannot exceed a bucket's capacity.
                        seconds = Math.Min(seconds, Math.Max(0, 3600 - value.Covered));
                        value = runner.Status switch {
                            "busy" => value with { Busy = value.Busy + seconds }, "idle" => value with { Idle = value.Idle + seconds },
                            "offline" => value with { Offline = value.Offline + seconds }, _ => value with { Unknown = value.Unknown + seconds }
                        };
                        if (index >= 0) state.Hours[index] = value; else state.Hours.Add(value);
                        cursor = end;
                    }
                }
            }
            // Replayed cached/older snapshots never move the sampling baseline backwards.
            if (previous is null || snapshot.CheckedAt >= previous.CheckedAt) previous = snapshot;
            var keys = state.Events.Select((e, i) => (Key: (e.RunnerId, e.Name, e.At, e.Result), Index: i)).ToDictionary(p => p.Key, p => p.Index);
            foreach (var ev in events)
                if (state.Runners.ContainsKey(ev.RunnerId) && ValidResult(ev.Result) && ev.At <= snapshot.CheckedAt &&
                    ev.At >= snapshot.CheckedAt.AddDays(-retention))
                {
                    var key = (ev.RunnerId, ev.Name, ev.At, ev.Result);
                    if (!keys.TryGetValue(key, out var index)) { keys[key] = state.Events.Count; state.Events.Add(ev); }
                    else if (state.Events[index].MatchedStart is null && ev.MatchedStart is not null) state.Events[index] = ev;
                }
            Prune(snapshot.CheckedAt);
        }
    }
    private void Prune(DateTimeOffset now)
    {
        var cutoff = new DateTimeOffset(now.UtcDateTime.Date.AddDays(1 - retention), TimeSpan.Zero);
        state.Hours.RemoveAll(h => h.At < cutoff || h.At > now);
        state.Events.RemoveAll(e => e.At < cutoff || e.At > now);
        if (state.Events.Count > 20000 || state.Hours.Count > 50000) capped = true;
        state.Events = state.Events.OrderByDescending(e => e.At).Take(20000).ToList();
        state.Hours = state.Hours.OrderByDescending(h => h.At).Take(50000).ToList();
        var ids = state.Events.Select(e => e.RunnerId).Concat(state.Hours.Select(h => h.RunnerId)).Concat(previous?.Runners.Select(r => r.Id) ?? []).ToHashSet();
        foreach (var id in state.Runners.Keys.Except(ids).ToArray()) state.Runners.Remove(id);
    }
    internal void Save(DateTimeOffset now, bool force = false)
    {
        lock (gate)
        {
            if (corrupt || (!force && now - lastSave < TimeSpan.FromMinutes(1))) return;
            lastSave = now;
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(file)!);
                var temp = file + "." + Guid.NewGuid().ToString("N") + ".tmp";
                try
                {
                    using (var stream = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None)) JsonSerializer.Serialize(stream, state);
                    File.Move(temp, file, true);
                }
                finally { if (File.Exists(temp)) File.Delete(temp); }
                warning = null;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { warning = "Analytics are in memory only; the state directory is not writable."; }
        }
    }
    internal static HistoricalJob[] Jobs(IEnumerable<HistoryEvent> events)
    {
        var jobs = new List<HistoricalJob>();
        foreach (var group in events.Distinct().GroupBy(e => e.RunnerId))
        {
            HistoryEvent? pending = null;
            foreach (var ev in group.OrderBy(e => e.At).ThenBy(e => e.Result is null ? 0 : 1))
            {
                if (ev.Result is null)
                {
                    if (pending is not null) jobs.Add(new(group.Key, pending.Name, pending.At, null, null));
                    pending = ev;
                }
                else
                {
                    // Pair only the adjacent start for this runner. Same-name jobs never share a start.
                    var sameName = pending?.Name == ev.Name;
                    var matched = sameName && pending!.At == ev.MatchedStart && pending.At <= ev.At;
                    if (!sameName && pending is not null) jobs.Add(new(group.Key, pending.Name, pending.At, null, null));
                    jobs.Add(new(group.Key, ev.Name, matched ? pending!.At : null, ev.At, ev.Result)); pending = null;
                }
            }
            if (pending is not null) jobs.Add(new(group.Key, pending.Name, pending.At, null, null));
        }
        return jobs.OrderByDescending(j => j.At).ThenBy(j => j.RunnerId).ToArray();
    }
    internal AnalyticsReport Query(DateTimeOffset from, DateTimeOffset to, string? runner, string? result, int page, bool demo = false, bool allJobs = false)
    {
        lock (gate)
        {
            var jobs = Jobs(state.Events).Where(j => j.At >= from && j.At < to && (runner is null || j.RunnerId == runner)).ToArray();
            var hours = state.Hours.Where(h => h.At >= from && h.At < to && (runner is null || h.RunnerId == runner)).ToArray();
            var durations = jobs.Select(j => j.DurationSeconds).OfType<double>().Order().ToArray();
            var completed = jobs.Count(j => j.Result is not null);
            var success = jobs.Count(j => j.Result is "Succeeded" or "SucceededWithIssues");
            var failed = jobs.Count(j => j.Result is "Failed" or "Abandoned");
            var canceled = jobs.Count(j => j.Result == "Canceled"); var skipped = jobs.Count(j => j.Result == "Skipped");
            var denominator = completed - skipped;
            var summary = new AnalyticsSummary(completed, success, failed, canceled, skipped, jobs.Length - completed,
                denominator > 0 ? success * 100d / denominator : null, durations.Length > 0 ? durations.Average() : null,
                durations.Length > 0 ? (durations[(durations.Length - 1) / 2] + durations[durations.Length / 2]) / 2 : null,
                durations.Length > 0 ? durations[(int)Math.Ceiling(durations.Length * .95) - 1] : null);
            var runners = state.Runners.Values.Where(r => runner is null || r.Id == runner).Select(r =>
            {
                var samples = hours.Where(h => h.RunnerId == r.Id).ToArray();
                return new RunnerAnalytics(r.Id, r.Name, samples.Sum(h => h.Busy), samples.Sum(h => h.Idle), samples.Sum(h => h.Offline), samples.Sum(h => h.Unknown), jobs.Count(j => j.RunnerId == r.Id && j.CompletedAt is not null));
            }).OrderByDescending(r => r.BusySeconds).ToArray();
            var daily = new List<DailyAnalytics>();
            for (var day = from; day < to; day = day.AddDays(1))
            {
                var dayJobs = jobs.Where(j => j.CompletedAt >= day && j.CompletedAt < day.AddDays(1)).ToArray();
                var samples = hours.Where(h => h.At >= day && h.At < day.AddDays(1)).ToArray();
                daily.Add(new(day.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), dayJobs.Length, dayJobs.Count(j => j.Result is "Succeeded" or "SucceededWithIssues"),
                    dayJobs.Count(j => j.Result is "Failed" or "Abandoned"), dayJobs.Count(j => j.Result == "Canceled"), samples.Sum(h => h.Busy), samples.Sum(h => h.Known)));
            }
            var filtered = jobs.Where(j => result is null || (result == "Incomplete" ? j.Result is null : j.Result == result)).ToArray();
            page = Math.Clamp(page, 1, Math.Max(1, (filtered.Length + 49) / 50));
            var warnings = new List<string>();
            if (warning is not null) warnings.Add(warning);
            if (collectionWarning is not null) warnings.Add(collectionWarning);
            if (capped) warnings.Add("Retention capacity was reached. Older records were removed.");
            return new(from, to, checkedAt, demo, state.Runners.Values.OrderBy(r => r.Name).ToArray(), runners, daily.ToArray(), hours, summary,
                allJobs ? filtered : filtered.Skip((page - 1) * 50).Take(50).ToArray(), filtered.Length, page, 50, warnings.ToArray());
        }
    }
}
