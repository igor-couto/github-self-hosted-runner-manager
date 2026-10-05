using System.Diagnostics;
using System.Text.Json;

namespace RunnerRoom;

internal sealed class WorkspaceMonitor
{
    private readonly Dictionary<string, WorkspaceUsage> cache = [];
    internal WorkspaceUsage Read(string folder, int minutes, IReadOnlySet<string> mounts)
    {
        var now = DateTimeOffset.UtcNow;
        if (cache.TryGetValue(folder, out var cached) && now - cached.CheckedAt < TimeSpan.FromMinutes(Math.Clamp(minutes, 1, 60))) return cached;
        string work = "_work";
        try
        {
            using var json = JsonDocument.Parse(LocalRunnerReader.ReadSmall(Path.Combine(folder, ".runner")) ?? "{}");
            if (json.RootElement.ValueKind == JsonValueKind.Object && json.RootElement.TryGetProperty("workFolder", out var field) && field.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(field.GetString())) work = field.GetString()!;
            var path = Path.GetFullPath(Path.Combine(folder, work));
            return cache[folder] = Scan(path, now, mounts);
        }
        catch (Exception ex) when (ex is JsonException or ArgumentException or IOException or UnauthorizedAccessException)
        { return cache[folder] = new(work, 0, 0, true, now, "Workspace path is unavailable."); }
    }
    internal static WorkspaceUsage Scan(string path, DateTimeOffset now, IReadOnlySet<string> mounts)
    {
        long bytes = 0, files = 0; var visited = 0; var partial = false;
        var timer = Stopwatch.StartNew(); var stack = new Stack<(string Path, int Depth)>(); stack.Push((path, 0));
        while (stack.Count > 0 && visited < 50000 && timer.ElapsedMilliseconds < 250)
        {
            var directory = stack.Pop();
            try
            {
                var info = new DirectoryInfo(directory.Path);
                if (!info.Exists) { partial = true; continue; }
                if (info.LinkTarget is not null || directory.Depth > 32 || (directory.Depth > 0 && mounts.Contains(info.FullName))) { partial = true; continue; }
                foreach (var entry in info.EnumerateFileSystemInfos())
                {
                    if (++visited > 50000 || timer.ElapsedMilliseconds >= 250) { partial = true; break; }
                    if (entry.LinkTarget is not null) { partial = true; continue; }
                    if (entry is DirectoryInfo dir) stack.Push((dir.FullName, directory.Depth + 1));
                    else if (entry is FileInfo file) { bytes += file.Length; files++; }
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { partial = true; }
        }
        partial |= stack.Count > 0;
        return new(path, bytes, files, partial, now, partial ? "Partial scan: access restrictions, skipped links/mounts, or scan budget reached. Size is a lower bound." : null);
    }
    internal void Prune(string[] folders) { foreach (var key in cache.Keys.Except(folders).ToArray()) cache.Remove(key); }
}
