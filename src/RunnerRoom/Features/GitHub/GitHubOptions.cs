namespace RunnerRoom.Features.GitHub;

public sealed class GitHubOptions
{
    public string? Token { get; set; }
    public string? TokenFile { get; set; }
    public GitHubAppOptions App { get; set; } = new();
    public GitHubSyncOptions Sync { get; set; } = new();
    public string? WebhookSecretFile { get; set; }
}

public sealed class GitHubAppOptions
{
    public long AppId { get; set; }
    public long InstallationId { get; set; }
    public string? PrivateKeyFile { get; set; }
}

public sealed class GitHubSyncOptions
{
    public bool Enabled { get; set; } = true;
    public int IntervalMinutes { get; set; } = 5;
    public int HistoryDays { get; set; } = 30;
    public int MaxRepositories { get; set; } = 25;
    public int MaxRunsPerRepository { get; set; } = 100;
    public int MaxJobsPerRun { get; set; } = 100;
}
