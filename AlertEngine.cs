using System.Text.Json;

namespace RunnerRoom;

internal sealed class AlertEngine
{
    private sealed class SavedState
    {
        public int Version { get; set; } = 1;
        public List<AlertIncident> Incidents { get; set; } = [];
    }
    private readonly object gate = new();
    private readonly string file;
    private readonly AlertOptions options;
    private readonly Dictionary<string, (DateTimeOffset Since, DateTimeOffset Last)> pending = [];
    private List<AlertIncident> records = [];
    private bool corrupt;
    private bool capacityReached;
    internal string? Warning { get; private set; }
    internal AlertEngine(string directory, AlertOptions options, bool load = true)
    {
        this.options = options; file = Path.Combine(directory, "alerts.json");
        if (!load) return;
        try
        {
            if (!File.Exists(file)) return;
            var text = LocalRunnerReader.ReadSmall(file, 8 * 1024 * 1024);
            var saved = text is null ? null : JsonSerializer.Deserialize<SavedState>(text);
            if (saved is null || saved.Version != 1 || saved.Incidents is null || saved.Incidents.Count > 2000 ||
                saved.Incidents.Any(a => a is null || string.IsNullOrEmpty(a.Id) || string.IsNullOrEmpty(a.Key) || string.IsNullOrEmpty(a.Title) ||
                    a.State is not ("firing" or "unknown" or "resolved" or "retired") || a.Severity is not ("warning" or "critical") ||
                    a.ResolvedAt < a.FiredAt || a.LastObservedAt < a.FiredAt) ||
                saved.Incidents.DistinctBy(a => a.Id).Count() != saved.Incidents.Count ||
                saved.Incidents.Where(a => a.ResolvedAt is null).DistinctBy(a => a.Key).Count() != saved.Incidents.Count(a => a.ResolvedAt is null))
                throw new JsonException();
            // Revalidate persisted incidents before treating them as currently firing or sending anything.
            records = saved.Incidents.Select(a => a.ResolvedAt is null ? a with { State = "unknown" } : a).ToList();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or ArgumentException)
        { corrupt = true; Warning = "Saved alerts could not be read. Using memory only; back up and remove alerts.json, then restart to restore persistence."; }
    }
    internal void Check(DateTimeOffset now, AlertObservation[] observations, string[] rulePrefixes)
    {
        lock (gate)
        {
            capacityReached = false;
            var present = observations.Select(o => o.Key).ToHashSet();
            for (var i = 0; i < records.Count; i++)
            {
                var alert = records[i];
                if (alert.ResolvedAt is not null) continue;
                if (!rulePrefixes.Any(p => alert.Key.StartsWith(p, StringComparison.Ordinal)))
                    records[i] = alert with { ResolvedAt = now, State = "retired", Detail = "Rule was removed, disabled or changed.", Pending = false,
                        Delivery = alert.Pending ? "canceled by rule change" : alert.Delivery };
                else if (!present.Contains(alert.Key))
                    records[i] = alert with { State = "unknown", Detail = "The monitored target is no longer readable or discoverable." };
            }
            foreach (var key in pending.Keys.Where(k => !present.Contains(k)).ToArray()) pending.Remove(key);
            foreach (var item in observations)
            {
                var index = records.FindIndex(a => a.Key == item.Key && a.ResolvedAt is null);
                if (item.Breached is null)
                {
                    pending.Remove(item.Key);
                    if (index >= 0) records[index] = records[index] with { State = "unknown", Detail = item.Detail };
                    continue;
                }
                if (!item.Breached.Value)
                {
                    pending.Remove(item.Key);
                    if (index >= 0)
                    {
                        var alert = records[index];
                        records[index] = alert with { State = "resolved", ResolvedAt = now, LastObservedAt = now, Detail = item.Detail,
                            Pending = alert.Notified, Delivery = alert.Notified ? "pending" : "not sent", NextAttemptAt = null };
                    }
                    continue;
                }
                if (index >= 0)
                {
                    var alert = records[index];
                    var repeat = options.RepeatMinutes > 0 && alert.LastNotificationAt is { } sent && now - sent >= TimeSpan.FromMinutes(Math.Clamp(options.RepeatMinutes, 1, 10080));
                    records[index] = alert with { State = "firing", LastObservedAt = now, Detail = item.Detail, Pending = alert.Pending || repeat };
                    continue;
                }
                if (!pending.TryGetValue(item.Key, out var candidate) || now < candidate.Last ||
                    now - candidate.Last > TimeSpan.FromSeconds(Math.Clamp(options.CheckIntervalSeconds, 15, 3600) * 2 + 10))
                    candidate = (now, now);
                pending[item.Key] = (candidate.Since, now);
                if (now - candidate.Since < TimeSpan.FromSeconds(item.HoldSeconds)) continue;
                if (records.Count(a => a.ResolvedAt is null) >= 500) { capacityReached = true; Warning = "Active alert capacity reached (500 targets). Narrow the configured rules."; continue; }
                records.Add(new(Guid.NewGuid().ToString("N"), item.Key, item.RuleId, item.Title, item.Severity, now) { Detail = item.Detail });
                pending.Remove(item.Key);
            }
            var cutoff = now.AddDays(-Math.Clamp(options.RetentionDays, 1, 90));
            records = records.Where(a => a.ResolvedAt is null || a.ResolvedAt >= cutoff)
                .OrderByDescending(a => a.ResolvedAt is null).ThenByDescending(a => a.FiredAt).Take(2000).ToList();
        }
    }
    internal void ScanFailed()
    {
        lock (gate)
        {
            pending.Clear();
            records = records.Select(a => a.ResolvedAt is null ? a with { State = "unknown", Detail = "Latest check failed; waiting for fresh data." } : a).ToList();
        }
    }
    internal AlertIncident[] Due(DateTimeOffset now)
    {
        lock (gate) return records.Where(a => a.Pending && a.State is "firing" or "resolved" && (a.NextAttemptAt is null || a.NextAttemptAt <= now)).OrderBy(a => a.FiredAt).Take(10).ToArray();
    }
    internal void Delivered(string id, DateTimeOffset now, bool success, bool dashboardOnly = false)
    {
        lock (gate)
        {
            var index = records.FindIndex(a => a.Id == id); if (index < 0) return;
            var alert = records[index];
            records[index] = alert with { Pending = !success, Delivery = dashboardOnly ? "dashboard only" : success ? "sent" : "failed; retry scheduled",
                Notified = alert.Notified || success && !dashboardOnly, LastNotificationAt = now, NextAttemptAt = success ? null : now.AddMinutes(5) };
        }
    }
    internal AlertIncident[] Snapshot() { lock (gate) return records.OrderByDescending(a => a.FiredAt).ToArray(); }
    internal void Save()
    {
        lock (gate)
        {
            if (corrupt) return;
            var temp = file + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(file)!);
                using (var stream = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                    JsonSerializer.Serialize(stream, new SavedState { Incidents = records });
                File.Move(temp, file, true); Warning = capacityReached ? "Active alert capacity reached (500 targets). Narrow the configured rules." : null;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { Warning = "Alerts are in memory only; the state directory is not writable."; }
            finally { try { if (File.Exists(temp)) File.Delete(temp); } catch (IOException) { } catch (UnauthorizedAccessException) { } }
        }
    }
}
