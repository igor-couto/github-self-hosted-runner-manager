using System.Globalization;
using RunnerRoom;

var checks = 0;
void Check(bool condition, string description)
{
    if (!condition) throw new InvalidOperationException(description);
    checks++;
}

var memory = SystemMonitor.ParseMemory("MemTotal: 8192 kB\nMemFree: 512 kB\nMemAvailable: 6144 kB\n");
Check(memory is { TotalBytes: 8388608, UsedBytes: 2097152, AvailableBytes: 6291456, UsedPercent: 25 },
    "RAM must use available memory, including reclaimable caches, rather than just free memory.");
Check(SystemMonitor.ParseMemory("MemTotal: 8192 kB\nMemFree: 512 kB") is null,
    "Missing MemAvailable must not become a misleading usage value.");
foreach (var invalid in new[] { null, "MemTotal: 0 kB\nMemAvailable: 0 kB",
    "MemTotal: 1 kB\nMemAvailable: 2 kB", "MemTotal: 9223372036854775807 kB\nMemAvailable: 1 kB",
    "MemTotal: 8192 kB\nMemAvailable: -1 kB" })
    Check(SystemMonitor.ParseMemory(invalid) is null, "Invalid memory readings must be unavailable.");

const string cores = "\ncpu0 1 2 3 4\ncpu1 1 2 3 4\nintr 123";
var before = SystemMonitor.ParseCpu("cpu 80 10 10 780 40 0 0 0 30 5" + cores);
var after = SystemMonitor.ParseCpu("cpu 100 20 30 800 50 10 5 5 40 10" + cores);
Check(after is { Total: 1020, Idle: 850, LogicalProcessors: 2 }, "Do not double-count guest CPU time.");
Check(SystemMonitor.CpuUsage(before, after) == 70, "CPU usage must be calculated from elapsed counters.");
Check(SystemMonitor.CpuUsage(null, after) is null, "The first sample must not report zero usage.");
Check(SystemMonitor.CpuUsage(after, after) is null, "Unchanged counters have no sampling interval.");
Check(SystemMonitor.CpuUsage(after, before) is null, "Counter resets must discard the interval.");
Check(SystemMonitor.CpuUsage(before, new(1100, 810, 2)) is null, "Decreasing idle time must discard the interval.");
Check(SystemMonitor.CpuUsage(before, new(1100, 1001, 2)) is null, "An invalid idle delta must not produce a negative percentage.");
Check(SystemMonitor.CpuUsage(before, new(1100, 900, 4)) is null, "CPU topology changes need a new baseline.");
Check(SystemMonitor.ParseCpu("cpu malformed" + cores) is null, "Malformed counters must be unavailable.");
Check(SystemMonitor.ParseCpu("cpu 18446744073709551615 1 1 1" + cores) is null,
    "Counter overflow must not break the dashboard.");
Check(SystemMonitor.ParseCpu(null) is null, "Missing CPU counters must be unavailable.");

var culture = CultureInfo.CurrentCulture;
try
{
    CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("de-DE");
    Check(SystemMonitor.ParseUptime("86400.50 100000.25\n") == 86400.5, "Linux uptime uses invariant decimal notation.");
}
finally { CultureInfo.CurrentCulture = culture; }
Check(SystemMonitor.ParseUptime("0.0 0.0") == 0, "Zero uptime is a valid reading.");
foreach (var invalid in new[] { null, "", "NaN 0", "Infinity 0", "-1 0", "unavailable" })
    Check(SystemMonitor.ParseUptime(invalid) is null, "Invalid uptime must not enter the JSON response.");

var missingPath = Path.Combine(Path.GetTempPath(), "runner-room-missing-" + Guid.NewGuid().ToString("N"));
var missing = new SystemMonitor(missingPath).Read(missingPath, false);
Check(missing is { LogicalProcessors: null, CpuUsagePercent: null, UptimeSeconds: null, Memory: null, Disk: null },
    "Unavailable sources must return null fields without failing the snapshot.");
Check(SystemMonitor.ReadDisk(null) is null, "No configured folder must not silently use a different disk.");
Check(SystemMonitor.ReadDisk(Path.GetTempPath()) is { TotalBytes: > 0, UsedBytes: >= 0, AvailableBytes: >= 0 },
    "An existing directory must resolve to its filesystem.");
var demo = new SystemMonitor(missingPath).Read(missingPath, true);
Check(demo is { LogicalProcessors: 4, CpuUsagePercent: 18.4, Memory: not null, Disk: not null },
    "Demo mode must use explicit sample metrics even without system access.");
await InventoryChecks.Run(Check);
await JobLogChecks.Run(Check);
await MonitoringChecks.Run(Check);
AnalyticsChecks.Run(Check);
await AlertChecks.Run(Check);
Console.WriteLine($"PASS: {checks} system, inventory, job, log, monitoring, analytics and alert checks.");
