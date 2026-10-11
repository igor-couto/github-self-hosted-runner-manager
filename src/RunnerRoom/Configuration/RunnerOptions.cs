using RunnerRoom.Features.Access;
using RunnerRoom.Features.Alerts;
using RunnerRoom.Features.Analytics;
using RunnerRoom.Features.GitHub;
using RunnerRoom.Features.Maintenance;
using RunnerRoom.Features.Management;
using RunnerRoom.Features.Monitoring;
using RunnerRoom.Features.Quotas;
using RunnerRoom.Features.Runners;

namespace RunnerRoom.Configuration;

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
    public ApplicationUpdateOptions ApplicationUpdates { get; set; } = new();
}
