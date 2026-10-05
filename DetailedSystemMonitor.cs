namespace RunnerRoom;

// A single background collector owns counter baselines and history. HTTP reads immutable snapshots.
public sealed class DetailedSystemMonitor(RunnerOptions options) : BackgroundService
{
    private DetailedSystemSnapshot? current;
    public DetailedSystemSnapshot? Current => Volatile.Read(ref current);
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await Task.Yield();
        var directory = options.Monitoring.StateDirectory ?? Environment.GetEnvironmentVariable("STATE_DIRECTORY") ??
            (OperatingSystem.IsLinux() ? "/var/lib/runner-room" : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "RunnerRoom"));
        var collector = new SystemMetricsCollector(options, directory);
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(15));
        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                try { Volatile.Write(ref current, options.Demo ? SystemMetricsCollector.Demo(DateTimeOffset.UtcNow) : await collector.CollectAsync(stoppingToken)); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
                {
                    if (Current is { } previous) Volatile.Write(ref current, previous with { Warnings = ["The latest system scan failed; these readings are out of date."] });
                }
                await timer.WaitForNextTickAsync(stoppingToken);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
        finally { if (!options.Demo) collector.Save(); }
    }
}

internal sealed class SystemMetricsCollector
{
    private readonly RunnerOptions options;
    private readonly string proc, sys;
    private readonly MonitoringHistory history;
    private readonly ProcessMetrics processes = new();
    private readonly WorkspaceMonitor workspaces = new();
    private Dictionary<string, SystemMonitor.CpuSample> cores = [];
    private Dictionary<string, (LinuxMetrics.NetCounter Counter, string? Identity)> network = [];
    private Dictionary<string, LinuxMetrics.DiskCounter> disks = [];
    private DateTimeOffset? previousAt;
    internal SystemMetricsCollector(RunnerOptions options, string stateDirectory, string proc = "/proc", string sys = "/sys")
    { this.options = options; this.proc = proc; this.sys = sys; history = new(stateDirectory, options.Monitoring.HistoryDays); }
    internal async Task<DetailedSystemSnapshot> CollectAsync(CancellationToken cancellation, DateTimeOffset? at = null)
    {
        var now = at ?? DateTimeOffset.UtcNow;
        var seconds = previousAt is { } previous ? (now - previous).TotalSeconds : 0;
        var warnings = new List<string>();
        var stat = LinuxMetrics.Read(Path.Combine(proc, "stat"));
        var newCores = LinuxMetrics.Cores(stat);
        var coreUsage = newCores.Select(c => new CoreUsage(c.Key, SystemMonitor.CpuUsage(cores.GetValueOrDefault(c.Key), c.Value))).ToArray();
        cores = newCores;
        if (stat is null) warnings.Add("Linux CPU readings are unavailable.");
        var boot = LinuxMetrics.Read(Path.Combine(proc, "sys/kernel/random/boot_id"), 100);
        var nets = LinuxMetrics.Network(LinuxMetrics.Read(Path.Combine(proc, "net/dev")));
        var nextNetwork = new Dictionary<string, (LinuxMetrics.NetCounter, string?)>();
        var traffic = nets.Select(n =>
        {
            var identity = boot + ":" + LinuxMetrics.Read(Path.Combine(sys, "class/net", n.Name, "ifindex"), 100);
            var valid = network.TryGetValue(n.Name, out var old) && old.Identity == identity && seconds is > 0 and <= 60;
            var rx = valid ? LinuxMetrics.Rate(old.Counter.Rx, n.Rx, seconds) : null;
            var tx = valid ? LinuxMetrics.Rate(old.Counter.Tx, n.Tx, seconds) : null;
            var today = history.Traffic(n.Name, now, rx is not null ? n.Rx - old.Counter.Rx : null, tx is not null ? n.Tx - old.Counter.Tx : null, seconds, previousAt);
            nextNetwork[n.Name] = (n, identity);
            return new NetworkUsage(n.Name, LinuxMetrics.Read(Path.Combine(sys, "class/net", n.Name, "operstate"), 100) ?? "unknown", n.Rx, n.Tx, rx, tx, today);
        }).ToArray();
        network = nextNetwork;
        var diskCounters = LinuxMetrics.Disks(LinuxMetrics.Read(Path.Combine(proc, "diskstats")))
            .Where(d => Directory.Exists(Path.Combine(sys, "block", d.Name)) && !d.Name.StartsWith("loop") && !d.Name.StartsWith("ram")).Take(64).ToArray();
        var activities = diskCounters.Select(d =>
        {
            if (!disks.TryGetValue(d.Name, out var old)) return new DiskActivity(d.Name, null, null, null);
            return new DiskActivity(d.Name, LinuxMetrics.Rate(old.ReadSectors, d.ReadSectors, seconds, 512), LinuxMetrics.Rate(old.WrittenSectors, d.WrittenSectors, seconds, 512),
                LinuxMetrics.Rate(old.BusyMs, d.BusyMs, seconds, .1) is { } busy ? Math.Min(100, busy) : null);
        }).ToArray();
        disks = diskCounters.ToDictionary(d => d.Name);
        var discovery = LocalRunnerReader.Discover(options);
        var mounts = LinuxMetrics.Mounts(LinuxMetrics.Read(Path.Combine(proc, "self/mountinfo")));
        var allMounts = mounts.Select(m => m.Path).ToHashSet(StringComparer.Ordinal);
        var selected = mounts.Where(m => m.Path == "/" || m.Type is "ext2" or "ext3" or "ext4" or "xfs" or "btrfs" or "f2fs" or "zfs" or "vfat" or "exfat" or "ntfs" or "ntfs3" ||
            discovery.Roots.Concat(options.Monitoring.FileSystems).Any(p => p == m.Path || p.StartsWith(m.Path.TrimEnd('/') + "/", StringComparison.Ordinal)))
            .Where(m => !File.Exists(m.Path)) // Exclude file bind mounts, such as a container's /etc/hosts.
            .OrderBy(m => m.Path.Length).GroupBy(m => m.DeviceId + ":" + m.Root).Select(g => g.First()).Take(64).ToArray();
        var fs = selected.Select(m =>
        {
            var usage = SystemMonitor.ReadDisk(m.Path);
            var points = usage is not null ? history.Storage(m.DeviceId + ":" + m.Device + ":" + m.Root, usage.UsedBytes, now) : [];
            return new FileSystemUsage(m.Path, m.Device, m.Type, usage, points);
        }).ToArray();
        var processRead = ProcessMetrics.Read(proc);
        var usageData = processes.Calculate(processRead.Samples, SystemMonitor.ParseCpu(stat), discovery.Folders, processRead.Partial);
        if (processRead.Partial) warnings.Add("Some processes cannot be inspected. Process and runner totals may be incomplete.");
        var workspaceBudget = System.Diagnostics.Stopwatch.StartNew();
        var runnerUsage = usageData.Runners.Select(r =>
        {
            if (workspaceBudget.Elapsed.TotalSeconds > 3) return r with { Partial = true };
            var folder = discovery.Folders.First(f => new RunnerInfo("", "", "", null) { Path = f }.Id == r.Id);
            return r with { Workspace = workspaces.Read(folder, options.Monitoring.WorkspaceScanMinutes, allMounts) };
        }).ToArray();
        workspaces.Prune(discovery.Folders);
        var hardware = await HardwareMonitor.ReadAsync(sys, proc == "/proc" && OperatingSystem.IsLinux(), cancellation);
        history.Save(now);
        if (history.Warning is { } warning) warnings.Add(warning);
        previousAt = now;
        return new(now, false, coreUsage, LinuxMetrics.Load(LinuxMetrics.Read(Path.Combine(proc, "loadavg"))), LinuxMetrics.Swap(LinuxMetrics.Read(Path.Combine(proc, "meminfo"))),
            traffic, history.TrafficHistory, activities, fs, hardware,
            usageData.Processes.OrderByDescending(p => p.CpuPercent ?? -1).ThenByDescending(p => p.MemoryBytes).Take(20).ToArray(),
            usageData.Processes.OrderByDescending(p => p.MemoryBytes).Take(20).ToArray(), usageData.Applications, runnerUsage, warnings.ToArray());
    }
    internal void Save() => history.Save(DateTimeOffset.UtcNow, true);
    internal static DetailedSystemSnapshot Demo(DateTimeOffset now)
    {
        const long gib = 1024L * 1024 * 1024;
        var today = new TrafficDay(now.ToString("yyyy-MM-dd"), "eth0", 4 * gib, gib, 6 * 3600);
        var runnerPath = "/srv/actions-runners/activities-api-arm64-2.322.0";
        return new(now, true, [new("cpu0", 42), new("cpu1", 21), new("cpu2", 64), new("cpu3", 18)], new(1.2, .8, .6), new(2 * gib, gib / 8, 2 * gib - gib / 8),
            [new("eth0", "up", 24 * gib, 9 * gib, 524288, 131072, today), new("wlan0", "down", 0, 0, 0, 0, today with { Interface = "wlan0", ReceivedBytes = 0, SentBytes = 0 })], [today],
            [new("nvme0n1", 12 * 1024 * 1024, 3 * 1024 * 1024, 14), new("mmcblk0", 1024, 0, .2)],
            [new("/", "/dev/nvme0n1p2", "ext4", new(256 * gib, 92 * gib, 164 * gib), Enumerable.Range(0, 48).Select(i => new StoragePoint(now.AddHours(i - 48), (long)((84 + i / 6d) * gib))).ToArray()),
             new("/mnt/builds", "/dev/sda1", "ext4", new(512 * gib, 140 * gib, 372 * gib), [new(now.AddDays(-1), 138 * gib), new(now, 140 * gib)])],
            new([new("cpu_thermal", 54.2)], [new("Raspberry Pi firmware", null, false, false)], new(false, false, false, false, false, false, "Sample Pi firmware"), []),
            [new(2412, "dotnet", "dotnet", 84, 720 * 1024 * 1024), new(2415, "node", "node", 22, 180 * 1024 * 1024)],
            [new(2412, "dotnet", "dotnet", 84, 720 * 1024 * 1024), new(2415, "node", "node", 22, 180 * 1024 * 1024)],
            [new("dotnet", 4, 112, gib, false), new("node", 2, 28, 300 * 1024 * 1024, false)],
            [new(new RunnerInfo("", "", "", null) { Path = runnerPath }.Id, Path.GetFileName(runnerPath), 8, 142, 1300 * 1024 * 1024, false,
                new(runnerPath + "/_work", 18 * gib, 14250, false, now.AddMinutes(-2), null))], []);
    }
}
