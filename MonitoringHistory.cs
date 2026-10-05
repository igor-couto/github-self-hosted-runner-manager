using System.Globalization;
using System.Text.Json;

namespace RunnerRoom;

internal sealed class MonitoringHistory
{
    internal sealed class State
    {
        public int Version { get; set; } = 1;
        public List<TrafficDay> Traffic { get; set; } = [];
        public Dictionary<string, List<StoragePoint>> Storage { get; set; } = [];
    }
    private State state = new();
    private readonly string file;
    private readonly int days;
    private DateTimeOffset lastSave;
    internal string? Warning { get; private set; }
    internal MonitoringHistory(string directory, int days)
    {
        file = Path.Combine(directory, "monitoring.json"); this.days = Math.Clamp(days, 1, 30);
        try
        {
            if (!File.Exists(file)) return;
            var text = LocalRunnerReader.ReadSmall(file, 4 * 1024 * 1024);
            var loaded = text is null ? null : JsonSerializer.Deserialize<State>(text);
            if (loaded is null || loaded.Version != 1 || loaded.Traffic is null || loaded.Storage is null || loaded.Traffic.Count > 8000 || loaded.Storage.Count > 256 ||
                loaded.Traffic.Any(d => d is null || d.Interface is null || d.Interface.Length > 256 || d.ReceivedBytes < 0 || d.SentBytes < 0 || !double.IsFinite(d.ObservedSeconds) || d.ObservedSeconds < 0 || d.ObservedSeconds > 86400 || !DateOnly.TryParseExact(d.Date, "yyyy-MM-dd", out _)) ||
                loaded.Storage.Values.Any(v => v is null || v.Count > 750 || v.Any(p => p is null || p.UsedBytes < 0)))
                throw new JsonException();
            state = loaded;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        { Warning = "Saved monitoring history could not be read; collecting a new history."; }
    }
    internal TrafficDay Traffic(string name, DateTimeOffset now, long? rxDelta, long? txDelta, double seconds, DateTimeOffset? previousAt)
    {
        var date = now.UtcDateTime.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        var index = state.Traffic.FindIndex(d => d.Date == date && d.Interface == name);
        var value = index >= 0 ? state.Traffic[index] : new TrafficDay(date, name, 0, 0, 0);
        // No estimate for downtime, counter resets or the interval straddling UTC midnight.
        if (previousAt?.UtcDateTime.Date == now.UtcDateTime.Date && rxDelta is >= 0 && txDelta is >= 0 && seconds is > 0 and <= 60)
            value = value with { ReceivedBytes = Add(value.ReceivedBytes, rxDelta.Value), SentBytes = Add(value.SentBytes, txDelta.Value), ObservedSeconds = Math.Min(86400, value.ObservedSeconds + seconds) };
        if (index >= 0) state.Traffic[index] = value; else state.Traffic.Add(value);
        return value;
    }
    private static long Add(long a, long b) => b <= long.MaxValue - a ? a + b : long.MaxValue;
    internal StoragePoint[] Storage(string identity, long used, DateTimeOffset now)
    {
        if (!state.Storage.TryGetValue(identity, out var points)) state.Storage[identity] = points = [];
        if (points.Count == 0 || now - points[^1].At >= TimeSpan.FromHours(1)) points.Add(new(now, used));
        return points.ToArray();
    }
    internal TrafficDay[] TrafficHistory => state.Traffic.OrderByDescending(t => t.Date).ThenBy(t => t.Interface).ToArray();
    internal void Save(DateTimeOffset now, bool force = false)
    {
        var cutoff = now.AddDays(-days);
        var firstDate = now.UtcDateTime.Date.AddDays(1 - days).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        state.Traffic.RemoveAll(t => string.CompareOrdinal(t.Date, firstDate) < 0 || string.CompareOrdinal(t.Date, now.UtcDateTime.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)) > 0);
        foreach (var key in state.Storage.Keys.ToArray())
        {
            state.Storage[key].RemoveAll(p => p.At < cutoff || p.At > now.AddMinutes(1));
            if (state.Storage[key].Count == 0) state.Storage.Remove(key);
        }
        state.Traffic = state.Traffic.OrderByDescending(t => t.Date).Take(8000).ToList();
        foreach (var key in state.Storage.OrderByDescending(p => p.Value[^1].At).Skip(64).Select(p => p.Key).ToArray()) state.Storage.Remove(key);
        if (!force && now - lastSave < TimeSpan.FromMinutes(1)) return;
        lastSave = now;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(file)!);
            if (File.Exists(file) && new FileInfo(file).LinkTarget is not null) throw new IOException();
            // A private state directory and an exclusive new temporary file avoid following a stale link.
            var temporary = file + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None)) JsonSerializer.Serialize(stream, state);
                File.Move(temporary, file, overwrite: true);
            }
            finally { if (File.Exists(temporary)) File.Delete(temporary); }
            Warning = null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        { Warning = "History is in memory only: the monitoring state directory is not writable."; }
    }
}
