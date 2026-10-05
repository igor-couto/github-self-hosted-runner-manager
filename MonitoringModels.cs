namespace RunnerRoom;

public sealed record CoreUsage(string Name, double? Percent);
public sealed record LoadAverage(double One, double Five, double Fifteen);
public sealed record TrafficDay(string Date, string Interface, long ReceivedBytes, long SentBytes, double ObservedSeconds);
public sealed record NetworkUsage(string Name, string State, long ReceivedBytes, long SentBytes,
    double? DownloadBytesPerSecond, double? UploadBytesPerSecond, TrafficDay? Today);
public sealed record DiskActivity(string Device, double? ReadBytesPerSecond, double? WriteBytesPerSecond, double? BusyPercent);
public sealed record StoragePoint(DateTimeOffset At, long UsedBytes);
public sealed record FileSystemUsage(string Mount, string Device, string Type, ResourceUsage? Usage, StoragePoint[] History);
public sealed record TemperatureReading(string Sensor, double Celsius);
public sealed record ThrottleReading(string Source, long? TotalEvents, bool? Active, bool? OccurredSinceBoot);
public sealed record PiHealth(bool? UnderVoltage, bool? UnderVoltageSinceBoot, bool? Throttled, bool? ThrottledSinceBoot,
    bool? FrequencyCapped, bool? SoftTemperatureLimit, string Source)
{
    public bool? RecentVoltageAlarm { get; init; }
}
public sealed record BatteryReading(string Name, string Status, double? Percent, double? EnergyWh, double? FullEnergyWh, double? PowerWatts);
public sealed record HardwareReadings(TemperatureReading[] Temperatures, ThrottleReading[] Throttling, PiHealth? RaspberryPi, BatteryReading[] Batteries);
public sealed record ProcessUsage(int Pid, string Name, string Application, double? CpuPercent, long MemoryBytes);
public sealed record ApplicationUsage(string Name, int Processes, double? CpuPercent, long MemoryBytes, bool Partial);
public sealed record RunnerUsage(string Id, string Folder, int Processes, double? CpuPercent, long? MemoryBytes, bool Partial, WorkspaceUsage? Workspace);
public sealed record WorkspaceUsage(string Path, long Bytes, long Files, bool Partial, DateTimeOffset CheckedAt, string? Message);
public sealed record DetailedSystemSnapshot(DateTimeOffset CheckedAt, bool Demo, CoreUsage[] Cores, LoadAverage? Load,
    ResourceUsage? Swap, NetworkUsage[] Network, TrafficDay[] TrafficHistory, DiskActivity[] DiskActivity,
    FileSystemUsage[] FileSystems, HardwareReadings Hardware, ProcessUsage[] TopCpu, ProcessUsage[] TopMemory,
    ApplicationUsage[] Applications, RunnerUsage[] Runners, string[] Warnings);
