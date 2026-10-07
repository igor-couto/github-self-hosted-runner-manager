namespace RunnerRoom;

public sealed class RunnerOptions
{
    public string? RunnersRoot { get; set; }
    public string[] RunnersRoots { get; set; } = [];
    public bool Demo { get; set; }
    public DiscoveryOptions Discovery { get; set; } = new();
    public RunnerOverride[] RunnerOverrides { get; set; } = [];
    public GitHubOptions GitHub { get; set; } = new();
    public LogOptions Logs { get; set; } = new();
    public MonitoringOptions Monitoring { get; set; } = new();
    public AnalyticsOptions Analytics { get; set; } = new();
    public AlertOptions Alerts { get; set; } = new();
    public AccessOptions Access { get; set; } = new();
    public ManagementOptions Management { get; set; } = new();
    public QuotaOptions Quotas { get; set; } = new();
}

public sealed class AnalyticsOptions
{
    public int RetentionDays { get; set; } = 30;
}

public sealed class MonitoringOptions
{
    public string? StateDirectory { get; set; }
    public string[] FileSystems { get; set; } = [];
    public int HistoryDays { get; set; } = 7;
    public int WorkspaceScanMinutes { get; set; } = 5;
}

public sealed class LogOptions
{
    public bool Enabled { get; set; } = true;
    public string[] RedactValues { get; set; } = [];
}

public sealed class DiscoveryOptions
{
    public bool Recursive { get; set; }
    public int MaxDepth { get; set; } = 4;
}

public sealed class RunnerOverride
{
    public string Path { get; set; } = "";
    public string? DisplayName { get; set; }
    public string? Group { get; set; }
}

public sealed class GitHubOptions
{
    public string? Token { get; set; }
    public string? TokenFile { get; set; }
}

public sealed record RunnerInfo(string Name, string Folder, string Status, int? Pid)
{
    public string Id => Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(Path))).ToLowerInvariant();
    public string Path { get; init; } = "";
    public string DisplayName { get; init; } = Name;
    public string Host { get; init; } = Environment.MachineName;
    public string? Group { get; init; }
    public long? AgentId { get; init; }
    public string? Repository { get; init; }
    public string? Organization { get; init; }
    public string? GitHubUrl { get; init; }
    public string? OperatingSystem { get; init; }
    public string? Architecture { get; init; }
    public string? Version { get; init; }
    public string? RunnerGroup { get; init; }
    public string ProcessStatus { get; init; } = "unknown";
    public double? UptimeSeconds { get; init; }
    public int? WorkerPid { get; init; }
    public DateTimeOffset? WorkerStartedAt { get; init; }
    public CurrentJob? CurrentJob { get; init; }
    public string? ServiceName { get; init; }
    public string ServiceState { get; init; } = "not_configured";
    public string? ServiceSubState { get; init; }
    public JobSummary? LastJob { get; init; }
    public DateTimeOffset? LastActivityAt { get; init; }
    public GitHubRunnerInfo GitHub { get; init; } = new("not_configured");
}

public sealed record JobSummary(string Name, string? Result, DateTimeOffset At);
public sealed record GitHubRunnerInfo(string Status, bool? Busy = null, string[]? Labels = null,
    DateTimeOffset? CheckedAt = null, string? Message = null, string? OperatingSystem = null, string? Version = null);
public sealed record RunnerSnapshot(string Host, string? Root, DateTimeOffset CheckedAt,
    bool Demo, string? Error, string? Warning, IReadOnlyList<RunnerInfo> Runners, SystemSnapshot? System = null)
{
    public string[] Roots { get; init; } = [];
}
