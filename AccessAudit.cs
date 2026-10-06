using System.Text.Json;

namespace RunnerRoom;

public sealed record AccessAuditEntry(DateTimeOffset At, string Event, string Actor, string? Address);
public sealed class AccessAudit
{
    private readonly object gate = new();
    private readonly string file;
    private List<AccessAuditEntry> records = [];
    private bool preserve;
    private readonly bool demo;
    public string? Warning { get; private set; }
    public AccessAudit(RunnerOptions options)
    {
        demo = options.Demo;
        file = Path.Combine(AccessSetup.StateDirectory(options), "access-audit.json");
        if (demo) return;
        try
        {
            if (!File.Exists(file)) return;
            var text = LocalRunnerReader.ReadSmall(file, 1024 * 1024);
            records = (text is null ? null : JsonSerializer.Deserialize<List<AccessAuditEntry>>(text)) ?? throw new JsonException();
            if (records.Count > 1000 || records.Any(r => r is null || r.Event is null || r.Actor is null)) throw new JsonException();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        { records = []; preserve = true; Warning = "Audit file unreadable; original preserved. New events are in memory only."; }
    }
    public void Add(HttpContext context, string action, string? actor = null)
    {
        lock (gate)
        {
            records.Add(new(DateTimeOffset.UtcNow, action, actor ?? context.User.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value ?? "anonymous", context.Connection.RemoteIpAddress?.ToString()));
            records = records.Where(r => r.At >= DateTimeOffset.UtcNow.AddDays(-30)).TakeLast(1000).ToList();
            if (preserve || demo) return;
            var temp = file + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(file)!);
                File.WriteAllText(temp, JsonSerializer.Serialize(records)); File.Move(temp, file, true); Warning = null;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { Warning = "Access audit is in memory only; state directory is not writable."; }
            finally { try { if (File.Exists(temp)) File.Delete(temp); } catch (IOException) { } catch (UnauthorizedAccessException) { } }
        }
    }
    public AccessAuditEntry[] Recent() { lock (gate) return records.AsEnumerable().Reverse().Take(100).ToArray(); }
}
