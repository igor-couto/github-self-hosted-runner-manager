using System.Globalization;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

[assembly: InternalsVisibleTo("RunnerRoom.Tests")]

namespace RunnerRoom;

public sealed record ResourceUsage(long TotalBytes, long UsedBytes, long AvailableBytes)
{
    public double UsedPercent => UsedBytes * 100d / TotalBytes;
}

public sealed record SystemSnapshot(string Architecture, int? LogicalProcessors, double? CpuUsagePercent,
    double? UptimeSeconds, ResourceUsage? Memory, ResourceUsage? Disk);

// Called under RunnerMonitor's snapshot lock. No shell commands or elevated access.
internal sealed class SystemMonitor(string procRoot = "/proc")
{
    internal sealed record CpuSample(ulong Total, ulong Idle, int LogicalProcessors);
    private CpuSample? previousCpu;

    public SystemSnapshot Read(string? runnerRoot, bool demo)
    {
        if (demo)
        {
            const long gib = 1024L * 1024 * 1024;
            return new("arm64", 4, 18.4, 9 * 86400 + 6 * 3600 + 23 * 60,
                new(8 * gib, 3 * gib, 5 * gib), new(256 * gib, 92 * gib, 164 * gib));
        }

        var cpu = ParseCpu(ReadProc("stat"));
        var usage = CpuUsage(previousCpu, cpu);
        previousCpu = cpu;
        return new(RuntimeInformation.OSArchitecture.ToString().ToLowerInvariant(), cpu?.LogicalProcessors, usage,
            ParseUptime(ReadProc("uptime")), ParseMemory(ReadProc("meminfo")), ReadDisk(runnerRoot));
    }

    private string? ReadProc(string name)
    {
        try { return File.ReadAllText(Path.Combine(procRoot, name)); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return null; }
    }

    internal static ResourceUsage? ParseMemory(string? text)
    {
        if (text is null) return null;
        long? total = null, available = null;
        foreach (var line in text.Split('\n'))
        {
            var fields = line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            if (fields.Length != 3 || fields[2] != "kB" ||
                !long.TryParse(fields[1], NumberStyles.None, CultureInfo.InvariantCulture, out var kb) ||
                kb > long.MaxValue / 1024) continue;
            if (fields[0] == "MemTotal:") total = kb * 1024;
            if (fields[0] == "MemAvailable:") available = kb * 1024;
        }
        // MemAvailable includes reclaimable caches, unlike MemFree.
        if (total is not > 0 || available is null || available > total) return null;
        return new(total.Value, total.Value - available.Value, available.Value);
    }

    internal static CpuSample? ParseCpu(string? text)
    {
        if (text is null) return null;
        var lines = text.Split('\n');
        var fields = lines.FirstOrDefault(line => line.StartsWith("cpu ", StringComparison.Ordinal))?
            .Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        if (fields is null || fields.Length < 5) return null;
        ulong total = 0, idle = 0;
        // guest and guest_nice are already included in user and nice. Sum only
        // user, nice, system, idle, iowait, irq, softirq, and steal.
        for (var index = 1; index < Math.Min(fields.Length, 9); index++)
        {
            if (!ulong.TryParse(fields[index], NumberStyles.None, CultureInfo.InvariantCulture, out var value) ||
                value > ulong.MaxValue - total) return null;
            total += value;
            if (index is 4 or 5) idle += value;
        }
        var processors = lines.Count(line => line.StartsWith("cpu", StringComparison.Ordinal) &&
            line.Length > 3 && char.IsAsciiDigit(line[3]));
        return processors > 0 ? new(total, idle, processors) : null;
    }

    internal static double? CpuUsage(CpuSample? previous, CpuSample? current)
    {
        // Wait for a second sample; do not block the request with a sleep.
        if (previous is null || current is null || current.Total <= previous.Total ||
            current.Idle < previous.Idle || current.LogicalProcessors != previous.LogicalProcessors) return null;
        var total = current.Total - previous.Total;
        var idle = current.Idle - previous.Idle;
        return idle <= total ? (total - idle) * 100d / total : null;
    }

    internal static double? ParseUptime(string? text)
    {
        var value = text?.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
        return double.TryParse(value, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var seconds) &&
            double.IsFinite(seconds) && seconds >= 0 ? seconds : null;
    }

    internal static ResourceUsage? ReadDisk(string? runnerRoot)
    {
        if (string.IsNullOrWhiteSpace(runnerRoot)) return null;
        try
        {
            // On Linux, DriveInfo queries the filesystem containing this path,
            // including nested mounts and paths traversing symbolic links.
            var path = Path.GetFullPath(runnerRoot);
            if (!Directory.Exists(path)) return null;
            var drive = new DriveInfo(path);
            var total = drive.TotalSize;
            if (total <= 0) return null;
            var free = Math.Clamp(drive.TotalFreeSpace, 0, total);
            var available = Math.Clamp(drive.AvailableFreeSpace, 0, free);
            return new(total, total - free, available);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or
            OverflowException or System.Security.SecurityException) { return null; }
    }
}
