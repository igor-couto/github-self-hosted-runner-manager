using System.Globalization;

namespace RunnerRoom;

internal static class LinuxMetrics
{
    internal sealed record NetCounter(string Name, long Rx, long Tx);
    internal sealed record DiskCounter(string Name, long ReadSectors, long WrittenSectors, long BusyMs);
    internal sealed record Mount(string DeviceId, string Root, string Path, string Type, string Device);
    internal static string? Read(string path, int limit = 1024 * 1024)
    {
        try
        {
            using var reader = new StreamReader(path);
            var buffer = new char[limit + 1];
            var n = reader.ReadBlock(buffer, 0, buffer.Length);
            return n > limit ? null : new string(buffer, 0, n).Trim();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return null; }
    }
    internal static string[] Directories(string path, string pattern = "*")
    {
        try { return Directory.EnumerateDirectories(path, pattern).Take(8192).ToArray(); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return []; }
    }
    internal static string[] Files(string path, string pattern)
    {
        try { return Directory.EnumerateFiles(path, pattern).Take(256).ToArray(); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return []; }
    }
    internal static long? Number(string? value) => long.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var n) && n >= 0 ? n : null;
    internal static double? Decimal(string? value) => double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var n) && double.IsFinite(n) && n >= 0 ? n : null;
    internal static Dictionary<string, SystemMonitor.CpuSample> Cores(string? text)
    {
        var result = new Dictionary<string, SystemMonitor.CpuSample>();
        foreach (var line in (text ?? "").Split('\n'))
        {
            var parts = line.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length == 2 && parts[0].StartsWith("cpu") && Number(parts[0][3..]) is not null &&
                SystemMonitor.ParseCpu("cpu " + parts[1] + "\ncpu0 0 0 0 0") is { } cpu) result[parts[0]] = cpu;
        }
        return result;
    }
    internal static LoadAverage? Load(string? text)
    {
        var p = (text ?? "").Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        return p.Length >= 3 && Decimal(p[0]) is { } one && Decimal(p[1]) is { } five && Decimal(p[2]) is { } fifteen ? new(one, five, fifteen) : null;
    }
    internal static ResourceUsage? Swap(string? text)
    {
        long? total = null, free = null;
        foreach (var line in (text ?? "").Split('\n'))
        {
            var p = line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            if (p.Length != 3 || p[2] != "kB" || Number(p[1]) is not { } n || n > long.MaxValue / 1024) continue;
            if (p[0] == "SwapTotal:") total = n * 1024;
            if (p[0] == "SwapFree:") free = n * 1024;
        }
        return total is { } t && free is { } f && f <= t ? new(t, t - f, f) : null;
    }
    internal static NetCounter[] Network(string? text)
    {
        var result = new List<NetCounter>();
        foreach (var line in (text ?? "").Split('\n'))
        {
            var p = line.Split(':', 2); if (p.Length != 2) continue;
            var fields = p[1].Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            if (fields.Length >= 16 && Number(fields[0]) is { } rx && Number(fields[8]) is { } tx)
                result.Add(new(p[0].Trim(), rx, tx));
        }
        return result.Take(256).ToArray();
    }
    internal static DiskCounter[] Disks(string? text)
    {
        var result = new List<DiskCounter>();
        foreach (var line in (text ?? "").Split('\n'))
        {
            var p = line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            if (p.Length >= 14 && Number(p[5]) is { } read && Number(p[9]) is { } write && Number(p[12]) is { } busy)
                result.Add(new(p[2], read, write, busy));
        }
        return result.ToArray();
    }
    internal static double? Rate(long previous, long current, double seconds, double multiplier = 1) =>
        seconds > 0 && seconds <= 60 && current >= previous ? (current - previous) / seconds * multiplier : null;
    internal static Mount[] Mounts(string? text)
    {
        var result = new List<Mount>();
        string Decode(string s) => s.Replace("\\040", " ").Replace("\\011", "\t").Replace("\\012", "\n").Replace("\\134", "\\");
        foreach (var line in (text ?? "").Split('\n'))
        {
            var halves = line.Split(" - ", 2); if (halves.Length != 2) continue;
            var left = halves[0].Split(' '); var right = halves[1].Split(' ');
            if (left.Length >= 6 && right.Length >= 2) result.Add(new(left[2], Decode(left[3]), Decode(left[4]), right[0], Decode(right[1])));
        }
        return result.ToArray();
    }
}
