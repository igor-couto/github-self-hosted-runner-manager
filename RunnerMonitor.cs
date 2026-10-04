using System.Text.Json;

namespace RunnerRoom;

public sealed record RunnerInfo(string Name, string Folder, string Status, int? Pid);
public sealed record RunnerSnapshot(string Host, string? Root, DateTimeOffset CheckedAt,
    bool Demo, string? Error, string? Warning, IReadOnlyList<RunnerInfo> Runners, SystemSnapshot? System = null);

// Reads local metadata and process identities. Never executes runner commands.
public sealed class RunnerMonitor(string? configuredRoot, bool demo)
{
    private readonly object gate = new();
    private readonly SystemMonitor systemMonitor = new();
    private RunnerSnapshot? cached;

    public RunnerSnapshot GetSnapshot()
    {
        lock (gate)
        {
            if (cached is not null && DateTimeOffset.UtcNow - cached.CheckedAt < TimeSpan.FromSeconds(2)) return cached;
            var snapshot = Scan();
            return cached = snapshot with { System = systemMonitor.Read(snapshot.Root, demo) };
        }
    }

    private RunnerSnapshot Scan()
    {
        var now = DateTimeOffset.UtcNow;
        if (demo) return new("home-server", "/srv/actions-runners", now, true, null, null,
            [new("build-linux-01", "build-linux-01", "on", 2148), new("build-linux-02", "build-linux-02", "on", 2351),
             new("deploy-01", "deploy-01", "off", null), new("test-linux-01", "test-linux-01", "on", 3102)]);
        RunnerSnapshot Error(string message) => new(Environment.MachineName, configuredRoot, now, false, message, null, []);
        if (string.IsNullOrWhiteSpace(configuredRoot)) return Error("Set RunnersRoot to the folder containing your runners, then restart the app.");
        try
        {
            var rootInfo = new DirectoryInfo(Path.GetFullPath(configuredRoot));
            var root = rootInfo.ResolveLinkTarget(true)?.FullName ?? rootInfo.FullName;
            if (!Directory.Exists(root)) return Error("The runner folder does not exist or cannot be read. Check RunnersRoot and permissions.");
            var folders = Directory.GetDirectories(root)
                .Where(path => !File.GetAttributes(path).HasFlag(FileAttributes.ReparsePoint))
                .Where(IsRunner).OrderBy(Path.GetFileName, StringComparer.OrdinalIgnoreCase).ToArray();
            // A parent can retain an old registration. Prefer its child runners so
            // that metadata does not hide them; otherwise support a single runner.
            if (folders.Length == 0 && IsRunner(root)) folders = [root];
            var processes = ReadProcesses(out var reliable);
            var runners = new List<RunnerInfo>();
            foreach (var folder in folders)
            {
                int? pid = null;
                foreach (var binary in new[] { "Runner.Listener", "Runner.Worker" })
                {
                    if (processes.TryGetValue(ResolveExecutable(Path.Combine(folder, "bin", binary)), out var found)) { pid = found; break; }
                }
                runners.Add(new(ReadName(folder), Path.GetFileName(folder), pid.HasValue ? "on" : reliable ? "off" : "unknown", pid));
            }
            return new(Environment.MachineName, root, now, false, null,
                reliable ? null : "Process visibility is limited. Runners without a visible process are marked Unknown. Run on Linux as the runner user.", runners);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return Error("The runner folder could not be inspected. Check the path and read permissions.");
        }
    }

    private static bool IsRunner(string path) => File.Exists(Path.Combine(path, ".runner")) || File.Exists(Path.Combine(path, "bin", "Runner.Listener"));

    private static string ReadName(string folder)
    {
        try
        {
            var file = new FileInfo(Path.Combine(folder, ".runner"));
            if (file.Exists && file.LinkTarget is null && file.Length <= 65536)
            {
                using var json = JsonDocument.Parse(File.ReadAllText(file.FullName));
                if (json.RootElement.ValueKind == JsonValueKind.Object && json.RootElement.TryGetProperty("agentName", out var name) &&
                    name.ValueKind == JsonValueKind.String && name.GetString() is { Length: > 0 and <= 200 } value) return value;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException) { }
        return Path.GetFileName(folder);
    }

    private static string ResolveExecutable(string executable)
    {
        try
        {
            // Auto-updates can symlink bin/ to a versioned directory.
            var bin = new DirectoryInfo(Path.GetDirectoryName(executable)!);
            var directory = bin.ResolveLinkTarget(true)?.FullName ?? bin.FullName;
            var file = new FileInfo(Path.Combine(directory, Path.GetFileName(executable)));
            return file.ResolveLinkTarget(true)?.FullName ?? file.FullName;
        }
        catch (IOException) { return executable; }
    }

    private static Dictionary<string, int> ReadProcesses(out bool reliable)
    {
        var result = new Dictionary<string, int>(StringComparer.Ordinal);
        reliable = OperatingSystem.IsLinux();
        if (!reliable) return result;
        try
        {
            // hidepid can omit processes entirely, without an access-denied error.
            foreach (var line in File.ReadLines("/proc/mounts"))
            {
                var fields = line.Split(' ');
                if (fields.Length > 3 && fields[1] == "/proc" && fields[3].Split(',').Any(option =>
                    option.StartsWith("hidepid=") && option is not "hidepid=0" and not "hidepid=off")) reliable = false;
            }
            foreach (var path in Directory.EnumerateDirectories("/proc"))
            {
                if (!int.TryParse(Path.GetFileName(path), out var pid)) continue;
                try
                {
                    var name = File.ReadAllText(Path.Combine(path, "comm")).Trim();
                    if (name is not "Runner.Listener" and not "Runner.Worker") continue;
                    var target = new FileInfo(Path.Combine(path, "exe")).LinkTarget;
                    if (target is not null)
                    {
                        const string deleted = " (deleted)";
                        if (target.EndsWith(deleted)) target = target[..^deleted.Length];
                        result[target] = pid;
                    }
                    else if (Directory.Exists(path)) reliable = false;
                }
                catch (FileNotFoundException) { }
                catch (DirectoryNotFoundException) { }
                catch (Exception ex) when (ex is UnauthorizedAccessException or IOException) { reliable = false; }
            }
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException) { reliable = false; }
        return result;
    }
}
