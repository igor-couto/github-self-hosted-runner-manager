namespace RunnerRoom;

internal sealed class ProcessMetrics
{
    internal sealed record Sample(int Pid, int Parent, string Name, string? Executable, long StartedTicks, long CpuTicks, long MemoryBytes);
    private Dictionary<int, Sample> previous = [];
    private SystemMonitor.CpuSample? previousCpu;
    internal static Sample? Parse(string? stat, string? executable, int pageSize)
    {
        if (stat is null) return null;
        var open = stat.IndexOf('('); var close = stat.LastIndexOf(')');
        if (open < 1 || close <= open || !int.TryParse(stat[..open].Trim(), out var pid) || pid < 1) return null;
        var p = stat[(close + 1)..].Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        if (p.Length < 22 || !int.TryParse(p[1], out var parent) || parent < 0 || LinuxMetrics.Number(p[11]) is not { } user ||
            LinuxMetrics.Number(p[12]) is not { } system || user > long.MaxValue - system || LinuxMetrics.Number(p[19]) is not { } start ||
            LinuxMetrics.Number(p[21]) is not { } rss || rss > long.MaxValue / pageSize) return null;
        return new(pid, parent, LocalRunnerReader.Text(stat[(open + 1)..close], 100) ?? "Unknown", executable, start, user + system, rss * pageSize);
    }
    internal static (Sample[] Samples, bool Partial) Read(string proc)
    {
        var samples = new List<Sample>(); var partial = false;
        var folders = LinuxMetrics.Directories(proc);
        if (folders.Length >= 8192 || folders.Length == 0) partial = true;
        foreach (var folder in folders)
        {
            if (!int.TryParse(Path.GetFileName(folder), out _)) continue;
            string? exe = null;
            try { exe = new FileInfo(Path.Combine(folder, "exe")).LinkTarget?.Replace(" (deleted)", ""); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
            var sample = Parse(LinuxMetrics.Read(Path.Combine(folder, "stat"), 8192), exe, Environment.SystemPageSize);
            if (sample is null) { if (Directory.Exists(folder)) partial = true; continue; }
            if (exe is null && sample.Name is "Runner.Listener" or "Runner.Worker") partial = true;
            samples.Add(sample);
        }
        if ((LinuxMetrics.Read(Path.Combine(proc, "mounts")) ?? "").Split('\n').Any(l => l.Contains("hidepid=") && !l.Contains("hidepid=0") && !l.Contains("hidepid=off"))) partial = true;
        return (samples.ToArray(), partial);
    }
    internal (ProcessUsage[] Processes, ApplicationUsage[] Applications, RunnerUsage[] Runners) Calculate(Sample[] samples, SystemMonitor.CpuSample? cpu, string[] runnerFolders, bool partial)
    {
        var delta = previousCpu is not null && cpu is not null && cpu.Total > previousCpu.Total && cpu.LogicalProcessors == previousCpu.LogicalProcessors ? cpu.Total - previousCpu.Total : (ulong?)null;
        var usages = samples.Select(p => new ProcessUsage(p.Pid, p.Name, p.Executable is { } exe ? Path.GetFileName(exe) : p.Name,
            delta is { } d && previous.TryGetValue(p.Pid, out var old) && old.StartedTicks == p.StartedTicks && p.CpuTicks >= old.CpuTicks
                ? Math.Min(cpu!.LogicalProcessors * 100, (p.CpuTicks - old.CpuTicks) / (double)d * cpu.LogicalProcessors * 100) : null, p.MemoryBytes)).ToArray();
        var byPid = samples.ToDictionary(p => p.Pid);
        var seeds = new Dictionary<int, string>();
        foreach (var p in samples)
        {
            if (p.Executable is not { } exe || Path.GetFileName(exe) is not ("Runner.Listener" or "Runner.Worker")) continue;
            var bin = Path.GetDirectoryName(exe);
            if (bin is null) continue;
            foreach (var folder in runnerFolders)
                if ((Path.GetDirectoryName(bin) == folder && (Path.GetFileName(bin) == "bin" || Path.GetFileName(bin).StartsWith("bin."))) ||
                    exe == LocalRunnerReader.ResolveExecutable(Path.Combine(folder, "bin", Path.GetFileName(exe))))
                { seeds[p.Pid] = folder; break; }
        }
        var owner = new Dictionary<int, string>();
        foreach (var p in samples)
        {
            var visited = new HashSet<int>(); var cursor = p;
            while (visited.Add(cursor.Pid))
            {
                if (seeds.TryGetValue(cursor.Pid, out var folder)) { owner[p.Pid] = folder; break; }
                if (!byPid.TryGetValue(cursor.Parent, out var parent) || parent.StartedTicks > cursor.StartedTicks) break;
                cursor = parent;
            }
        }
        double? Sum(IEnumerable<ProcessUsage> group) => group.Any(p => p.CpuPercent is not null) ? group.Sum(p => p.CpuPercent ?? 0) : null;
        var groups = usages.GroupBy(p => p.Application).Select(g => new ApplicationUsage(g.Key, g.Count(), Sum(g), g.Sum(p => p.MemoryBytes), partial || g.Any(p => p.CpuPercent is null))).ToArray();
        var applications = groups.OrderByDescending(g => g.CpuPercent ?? -1).ThenByDescending(g => g.MemoryBytes).Take(50)
            .Concat(groups.OrderByDescending(g => g.MemoryBytes).Take(50)).DistinctBy(g => g.Name).ToArray();
        var runners = runnerFolders.Select(folder =>
        {
            var members = usages.Where(p => owner.GetValueOrDefault(p.Pid) == folder).ToArray();
            return new RunnerUsage(new RunnerInfo("", "", "", null) { Path = folder }.Id, Path.GetFileName(folder), members.Length,
                members.Length == 0 && !partial ? 0 : Sum(members), members.Length > 0 || !partial ? members.Sum(p => p.MemoryBytes) : null,
                partial || members.Any(p => p.CpuPercent is null), null);
        }).ToArray();
        previous = byPid; previousCpu = cpu;
        return (usages, applications, runners);
    }
}
