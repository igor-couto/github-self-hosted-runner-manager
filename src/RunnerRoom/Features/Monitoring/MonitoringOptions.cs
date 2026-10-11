namespace RunnerRoom.Features.Monitoring;

public sealed class MonitoringOptions
{
    public string? StateDirectory { get; set; }
    public string[] FileSystems { get; set; } = [];
    public int HistoryDays { get; set; } = 7;
    public int WorkspaceScanMinutes { get; set; } = 5;
}
