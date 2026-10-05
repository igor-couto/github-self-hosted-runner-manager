using System.Globalization;
using System.Text.Json;
using RunnerRoom;

internal static class MonitoringChecks
{
    internal static async Task Run(Action<bool, string> check)
    {
        check(LinuxMetrics.Load("1.25 2.50 3.75 1/200 99") is { One: 1.25, Five: 2.5, Fifteen: 3.75 }, "Parse Linux load averages invariantly.");
        check(LinuxMetrics.Load("NaN 0 0") is null && LinuxMetrics.Load("1 -1 0") is null, "Invalid loads remain unavailable.");
        check(LinuxMetrics.Swap("SwapTotal: 2048 kB\nSwapFree: 1024 kB") is { UsedBytes: 1048576, UsedPercent: 50 }, "Swap measures used bytes.");
        check(LinuxMetrics.Swap("SwapTotal: 0 kB\nSwapFree: 0 kB") is { TotalBytes: 0, UsedPercent: 0 }, "No swap is valid and JSON-safe.");
        check(LinuxMetrics.Swap("SwapTotal: 10 kB\nSwapFree: 20 kB") is null, "Reject invalid swap counters.");
        var cores = LinuxMetrics.Cores("cpu 1 2 3 4\ncpu0 20 0 0 80\ncpu1 80 0 0 20\nintr 1");
        check(cores.Count == 2 && cores["cpu0"].Total == 100, "Per-core parsing excludes the aggregate row.");
        var next = LinuxMetrics.Cores("cpu0 45 0 0 155\ncpu1 155 0 0 45");
        check(SystemMonitor.CpuUsage(cores["cpu0"], next["cpu0"]) == 25 && SystemMonitor.CpuUsage(cores["cpu1"], next["cpu1"]) == 75, "Each core has its own counter baseline.");
        check(LinuxMetrics.Rate(100, 3100, 15) == 200 && LinuxMetrics.Rate(100, 1, 15) is null && LinuxMetrics.Rate(0, 100, 90) is null, "Rates use elapsed time and discard resets and gaps.");
        var net = LinuxMetrics.Network("Inter-| Receive | Transmit\n eth0: 1024 2 0 0 0 0 0 0 512 2 0 0 0 0 0 0");
        check(net is [{ Name: "eth0", Rx: 1024, Tx: 512 }], "Network byte indices are correct.");
        var disk = LinuxMetrics.Disks("8 0 sda 1 0 20 0 1 0 40 0 0 50 60");
        check(disk is [{ ReadSectors: 20, WrittenSectors: 40, BusyMs: 50 }] && LinuxMetrics.Rate(0, 20, 10, 512) == 1024, "Disk sectors use 512-byte units independent of physical sector size.");
        var mounts = LinuxMetrics.Mounts("1 0 8:1 / /mnt/my\\040disk rw - ext4 /dev/sda1 rw\n2 0 8:1 / /alias rw - ext4 /dev/sda1 rw");
        check(mounts.Length == 2 && mounts[0].Path == "/mnt/my disk" && mounts[0].DeviceId == mounts[1].DeviceId, "Decode mountinfo escapes and preserve bind-mount identity.");
        check(HardwareMonitor.ParsePi("throttled=0x50005\n") is { UnderVoltage: true, UnderVoltageSinceBoot: true, Throttled: true, ThrottledSinceBoot: true }, "Decode current and latched Pi voltage/throttling bits separately.");
        check(HardwareMonitor.ParsePi("throttled=0x50000") is { UnderVoltage: false, UnderVoltageSinceBoot: true, Throttled: false, ThrottledSinceBoot: true }, "Historical throttling must not become active throttling.");
        check(HardwareMonitor.ParsePi("permission denied") is null, "Hardware access errors are unavailable, not healthy.");
        var root = Path.Combine(Path.GetTempPath(), "runner-room-monitoring-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var now = DateTimeOffset.Parse("2026-10-05T12:00:00Z");
            var state = Path.Combine(root, "state");
            var history = new MonitoringHistory(state, 7);
            check(history.Traffic("eth0", now, null, null, 0, null).ReceivedBytes == 0, "Never count pre-monitoring lifetime counters as today's traffic.");
            var day = history.Traffic("eth0", now.AddSeconds(15), 1500, 500, 15, now);
            check(day is { ReceivedBytes: 1500, SentBytes: 500, ObservedSeconds: 15 }, "Observed daily bytes and coverage accumulate together.");
            check(history.Traffic("eth0", now.AddSeconds(30), null, 100, 15, now.AddSeconds(15)).ReceivedBytes == 1500, "An incomplete/reset pair discards the interval.");
            var midnight = DateTimeOffset.Parse("2026-10-06T00:00:00Z");
            check(history.Traffic("eth0", midnight, 100, 100, 15, midnight.AddSeconds(-15)) is { ReceivedBytes: 0, ObservedSeconds: 0 }, "Do not misassign an interval spanning midnight.");
            history.Storage("disk", 100, now); history.Storage("disk", 110, now.AddMinutes(10));
            check(history.Storage("disk", 200, now.AddHours(1)).Length == 2, "Storage growth is sampled hourly.");
            history.Save(now.AddHours(1), true);
            var restored = new MonitoringHistory(state, 7);
            check(restored.TrafficHistory.Single().ReceivedBytes == 1500, "Daily totals survive restart without storing stale counter baselines.");
            check(restored.Storage("disk", 210, now.AddHours(1)).Length == 2, "Restore storage history across restarts.");
            restored.Save(now.AddDays(9), true);
            check(restored.TrafficHistory.Length == 0 && restored.Storage("disk", 300, now.AddDays(9)).Length == 1, "Retention bounds daily and filesystem history.");
            File.WriteAllText(Path.Combine(state, "monitoring.json"), "{broken");
            check(new MonitoringHistory(state, 7).Warning is not null, "Corrupt history cannot stop monitoring.");
            var blocked = Path.Combine(root, "file-not-directory"); File.WriteAllText(blocked, "");
            var memoryOnly = new MonitoringHistory(blocked, 7); memoryOnly.Save(now, true);
            check(memoryOnly.Warning?.Contains("memory only") == true, "Unwritable history reports memory-only mode.");

            var runner = Path.Combine(root, "runner"); Directory.CreateDirectory(Path.Combine(runner, "_work"));
            File.WriteAllText(Path.Combine(runner, ".runner"), "{\"workFolder\":\"_work\"}");
            File.WriteAllBytes(Path.Combine(runner, "_work", "one"), new byte[100]);
            var workspace = new WorkspaceMonitor().Read(runner, 5, new HashSet<string>());
            check(workspace is { Bytes: 100, Files: 1, Partial: false }, "Workspace sizes use logical file lengths.");
            Directory.CreateDirectory(Path.Combine(runner, "_work", "mounted")); File.WriteAllBytes(Path.Combine(runner, "_work", "mounted", "two"), new byte[100]);
            var partial = WorkspaceMonitor.Scan(Path.Combine(runner, "_work"), now, new HashSet<string> { Path.Combine(runner, "_work", "mounted") });
            check(partial is { Bytes: 100, Partial: true }, "Nested mounts are skipped and mark the workspace as a lower bound.");
            if (OperatingSystem.IsLinux())
            {
                Directory.CreateSymbolicLink(Path.Combine(runner, "_work", "cycle"), Path.Combine(runner, "_work"));
                check(WorkspaceMonitor.Scan(Path.Combine(runner, "_work"), now, new HashSet<string>()).Partial, "Do not traverse workspace symlinks or cycles.");
                Directory.Delete(Path.Combine(runner, "_work", "cycle"));
            }
            var rawStat = "42 (worker ) name) S 1 0 0 0 0 0 0 0 0 0 100 50 0 0 0 0 1 0 1234 0 20";
            check(ProcessMetrics.Parse(rawStat, null, 4096) is { Pid: 42, Name: "worker ) name", CpuTicks: 150, StartedTicks: 1234, MemoryBytes: 81920 }, "Parse process stat names containing spaces and parentheses using correct field positions.");
            check(ProcessMetrics.Parse("invalid", null, 4096) is null, "A disappearing/malformed process is skipped.");
            ProcessMetrics.Sample[] first = [new(10, 1, "Runner.Listener", Path.Combine(runner, "bin", "Runner.Listener"), 1, 10, 100),
                new(11, 10, "Runner.Worker", Path.Combine(runner, "bin", "Runner.Worker"), 2, 20, 200), new(12, 11, "dotnet", "/usr/bin/dotnet", 3, 30, 300), new(13, 1, "other", "/usr/bin/other", 4, 40, 400)];
            var processMonitor = new ProcessMetrics();
            var baseline = processMonitor.Calculate(first, new(1000, 500, 2), [runner], false);
            check(baseline.Runners[0] is { Processes: 3, MemoryBytes: 600, CpuPercent: null, Partial: true }, "Runner totals include listener, worker and descendants once, excluding unrelated processes.");
            var second = first.Select(p => p with { CpuTicks = p.CpuTicks + 50 }).ToArray();
            var calculated = processMonitor.Calculate(second, new(1200, 600, 2), [runner], false);
            check(calculated.Runners[0] is { CpuPercent: 150, MemoryBytes: 600, Partial: false }, "Process CPU uses one-core percentages and runner CPU can exceed 100%.");
            var reused = second.Select(p => p with { StartedTicks = p.StartedTicks + 100 }).ToArray();
            check(processMonitor.Calculate(reused, new(1400, 700, 2), [runner], false).Processes.All(p => p.CpuPercent is null), "PID reuse invalidates CPU deltas.");
            check(new ProcessMetrics().Calculate([], new(1000, 500, 2), [runner], true).Runners[0] is { CpuPercent: null, MemoryBytes: null, Partial: true }, "Hidden processes must not look like zero resource use.");

            var sys = Path.Combine(root, "sys");
            void Put(string path, string value) { var target = Path.Combine(sys, path); Directory.CreateDirectory(Path.GetDirectoryName(target)!); File.WriteAllText(target, value); }
            Put("class/thermal/thermal_zone0/type", "cpu_thermal"); Put("class/thermal/thermal_zone0/temp", "54200");
            Put("devices/system/cpu/cpu0/thermal_throttle/core_throttle_count", "3");
            Put("class/hwmon/hwmon0/name", "rpi_volt"); Put("class/hwmon/hwmon0/in0_lcrit_alarm", "1");
            Put("class/power_supply/BAT0/type", "Battery"); Put("class/power_supply/BAT0/capacity", "80"); Put("class/power_supply/BAT0/status", "Discharging");
            Put("class/power_supply/BAT0/energy_now", "40000000"); Put("class/power_supply/BAT0/energy_full", "50000000"); Put("class/power_supply/BAT0/power_now", "10000000");
            var hardware = await HardwareMonitor.ReadAsync(sys, false, CancellationToken.None);
            check(hardware.Temperatures is [{ Celsius: 54.2 }], "Temperature millidegrees become Celsius.");
            check(hardware.Throttling is [{ TotalEvents: 3, Active: null, OccurredSinceBoot: true }], "Core throttle counters are supported without asserting active throttling.");
            check(hardware.RaspberryPi is { UnderVoltage: null, UnderVoltageSinceBoot: null, RecentVoltageAlarm: true }, "Pi hwmon fallback reports a recent sticky alarm, not instantaneous voltage or boot history.");
            check(hardware.Batteries is [{ Percent: 80, EnergyWh: 40, FullEnergyWh: 50, PowerWatts: 10 }], "Battery micro-units become Wh and watts.");

            var proc = Path.Combine(root, "proc");
            void Proc(string path, string value) { var target = Path.Combine(proc, path); Directory.CreateDirectory(Path.GetDirectoryName(target)!); File.WriteAllText(target, value); }
            Proc("stat", "cpu 100 0 0 900\ncpu0 100 0 0 900"); Proc("loadavg", "1 2 3"); Proc("meminfo", "SwapTotal: 0 kB\nSwapFree: 0 kB");
            Proc("net/dev", "eth0: 1000 0 0 0 0 0 0 0 500 0 0 0 0 0 0 0"); Proc("diskstats", "8 0 sda 1 0 20 0 1 0 40 0 0 50 60");
            Proc("self/mountinfo", $"1 0 8:1 / {root} rw - ext4 /dev/sda1 rw\n2 0 8:1 / {runner} rw - ext4 /dev/sda1 rw\n3 0 8:2 / {Path.Combine(runner, "_work")} rw - ext4 /dev/sdb1 rw");
            Directory.CreateDirectory(Path.Combine(sys, "block", "sda")); Put("class/net/eth0/ifindex", "2");
            var collector = new SystemMetricsCollector(new() { RunnersRoot = runner }, Path.Combine(root, "collector-state"), proc, sys);
            var initial = await collector.CollectAsync(CancellationToken.None, now);
            check(initial.Cores[0].Percent is null && initial.Network[0].DownloadBytesPerSecond is null && initial.DiskActivity[0].ReadBytesPerSecond is null, "First sample never reports invented zero rates.");
            check(initial.FileSystems.Length == 2 && initial.FileSystems.All(f => f.Usage is not null && f.History.Length == 1), "Monitor multiple filesystems with real usage while deduplicating bind mounts.");
            Proc("stat", "cpu 125 0 0 975\ncpu0 125 0 0 975"); Proc("net/dev", "eth0: 2500 0 0 0 0 0 0 0 1250 0 0 0 0 0 0 0");
            Proc("diskstats", "8 0 sda 1 0 50 0 1 0 100 0 0 150 160");
            var updated = await collector.CollectAsync(CancellationToken.None, now.AddSeconds(15));
            check(updated.Cores[0].Percent == 25 && updated.Network[0] is { DownloadBytesPerSecond: 100, UploadBytesPerSecond: 50, Today.ReceivedBytes: 1500 }, "Collector calculates actual interval CPU and network rates.");
            check(updated.DiskActivity[0].ReadBytesPerSecond == 1024 && updated.DiskActivity[0].WriteBytesPerSecond == 2048, "Collector calculates read/write activity.");
            Put("class/net/eth0/ifindex", "3");
            check((await collector.CollectAsync(CancellationToken.None, now.AddSeconds(30))).Network[0].DownloadBytesPerSecond is null, "A replaced interface gets a new baseline.");
            check(!JsonSerializer.Serialize(updated).Contains("NaN"), "Monitoring snapshots are JSON-safe even without swap and missing resources.");
            var missing = new SystemMetricsCollector(new(), Path.Combine(root, "missing-state"), Path.Combine(root, "missing-proc"), Path.Combine(root, "missing-sys"));
            var unavailable = await missing.CollectAsync(CancellationToken.None);
            check(unavailable is { Cores.Length: 0, Network.Length: 0, Hardware.Temperatures.Length: 0 } && unavailable.Warnings.Length > 0, "Unsupported platforms degrade to unavailable readings.");
        }
        finally { Directory.Delete(root, true); }
    }
}
