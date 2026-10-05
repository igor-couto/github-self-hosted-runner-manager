using System.Diagnostics;

namespace RunnerRoom;

internal sealed record RunnerProcess(string Executable, int Pid, DateTimeOffset? StartedAt);
internal sealed record ProcessSnapshot(IReadOnlyList<RunnerProcess> Processes, bool Reliable);
internal sealed record ServiceInfo(string State, string? SubState);

internal static class RunnerRuntime
{
    internal static ProcessSnapshot ReadProcesses()
    {
        var result = new List<RunnerProcess>();
        var reliable = OperatingSystem.IsLinux();
        if (!reliable) return new(result, false);
        try
        {
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
                    if (File.ReadAllText(Path.Combine(path, "comm")).Trim() is not ("Runner.Listener" or "Runner.Worker")) continue;
                    var target = new FileInfo(Path.Combine(path, "exe")).LinkTarget;
                    if (target is null) { if (Directory.Exists(path)) reliable = false; continue; }
                    const string deleted = " (deleted)";
                    if (target.EndsWith(deleted)) target = target[..^deleted.Length];
                    DateTimeOffset? started = null;
                    try { using var process = Process.GetProcessById(pid); started = process.StartTime.ToUniversalTime(); }
                    catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception) { }
                    result.Add(new(target, pid, started));
                }
                catch (FileNotFoundException) { }
                catch (DirectoryNotFoundException) { }
                catch (Exception ex) when (ex is UnauthorizedAccessException or IOException) { reliable = false; }
            }
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException) { reliable = false; }
        return new(result, reliable);
    }

    internal static RunnerInfo ApplyProcesses(RunnerInfo runner, ProcessSnapshot snapshot, DateTimeOffset now)
    {
        bool Matches(RunnerProcess process, string name)
        {
            if (Path.GetFileName(process.Executable) != name) return false;
            if (process.Executable == LocalRunnerReader.ResolveExecutable(Path.Combine(runner.Path, "bin", name))) return true;
            var bin = Path.GetDirectoryName(process.Executable);
            return bin is not null && Path.GetDirectoryName(bin) == runner.Path && Path.GetFileName(bin).StartsWith("bin.", StringComparison.Ordinal);
        }
        var listener = snapshot.Processes.FirstOrDefault(p => Matches(p, "Runner.Listener"));
        var worker = snapshot.Processes.FirstOrDefault(p => Matches(p, "Runner.Worker"));
        var process = listener ?? worker;
        return runner with
        {
            Pid = process?.Pid,
            ProcessStatus = process is not null ? "running" : snapshot.Reliable ? "stopped" : "unknown",
            Status = worker is not null ? "busy" : !snapshot.Reliable ? "unknown" : listener is not null ? "idle" : "offline",
            UptimeSeconds = process?.StartedAt is { } start ? Math.Max(0, (now - start).TotalSeconds) : null
        };
    }

    internal static async Task<Dictionary<string, ServiceInfo>> ReadServicesAsync(IEnumerable<string?> names)
    {
        var result = new Dictionary<string, ServiceInfo>(StringComparer.Ordinal);
        var services = names.OfType<string>().Distinct().ToArray();
        if (!OperatingSystem.IsLinux() || services.Length == 0 || !Directory.Exists("/run/systemd/system")) return result;
        using var process = new Process { StartInfo = new("systemctl")
        { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true } };
        foreach (var arg in new[] { "show", "--no-pager", "--property=Id,LoadState,ActiveState,SubState", "--" }.Concat(services)) process.StartInfo.ArgumentList.Add(arg);
        try
        {
            process.Start();
            var output = process.StandardOutput.ReadToEndAsync();
            var error = process.StandardError.ReadToEndAsync();
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            try { await process.WaitForExitAsync(timeout.Token); }
            catch (OperationCanceledException) { process.Kill(true); await process.WaitForExitAsync(); return result; }
            await error;
            foreach (var block in (await output).Split("\n\n", StringSplitOptions.RemoveEmptyEntries))
            {
                var fields = block.Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(l => l.Split('=', 2))
                    .Where(p => p.Length == 2).ToDictionary(p => p[0], p => p[1]);
                if (!fields.TryGetValue("Id", out var id) || !services.Contains(id)) continue;
                result[id] = new(fields.GetValueOrDefault("LoadState") == "not-found" ? "not_found" :
                    fields.GetValueOrDefault("ActiveState") ?? "unknown", fields.GetValueOrDefault("SubState"));
            }
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or IOException) { }
        return result;
    }
}
