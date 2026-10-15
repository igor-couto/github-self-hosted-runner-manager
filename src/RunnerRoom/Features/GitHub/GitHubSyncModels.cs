namespace RunnerRoom.Features.GitHub;

public sealed record GitHubSyncRepository(string FullName, string Url, bool IsPrivate = false, bool Selected = false,
    DateTimeOffset? LastSyncedAt = null, string? Error = null, bool Partial = false);
public sealed record GitHubRepositoryDiscovery(GitHubSyncRepository[] Repositories, bool Partial);
public sealed record GitHubSyncStep(int Number, string Name, string Status, string? Conclusion,
    DateTimeOffset? StartedAt, DateTimeOffset? CompletedAt);
public sealed record GitHubSyncJob(long Id, string Name, string Status, string? Conclusion, long? RunnerId,
    string? RunnerName, string[] Labels, DateTimeOffset? StartedAt, DateTimeOffset? CompletedAt,
    string Url, GitHubSyncStep[] Steps);
public sealed record GitHubSyncRun(long Id, int Attempt, string Repository, string Name, string Status,
    string? Conclusion, string? Branch, string? Commit, string? Actor, string? Event, string Url,
    DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt, DateTimeOffset? StartedAt, GitHubSyncJob[] Jobs,
    bool Partial = false);
public sealed record GitHubSyncProgress(bool Running = false, string? Repository = null,
    int CompletedRepositories = 0, int TotalRepositories = 0, DateTimeOffset? StartedAt = null);
public sealed record GitHubSyncHistory(DateTimeOffset At, string? Repository, string Outcome, int RunsImported, string? Message);
public sealed record GitHubApiBudget(int? Remaining = null, int? Limit = null, DateTimeOffset? ResetsAt = null, DateTimeOffset? RetryAt = null);
public sealed record GitHubSyncSnapshot(bool Enabled, bool Configured, bool Demo, GitHubSyncRepository[] Repositories,
    GitHubSyncRun[] Runs, GitHubSyncProgress Progress, GitHubSyncHistory[] History, GitHubApiBudget Api,
    string? Warning, int HistoryDays, int MaxRunsPerRepository, int IntervalMinutes, DateTimeOffset? NextSyncAt);

// Messages are deliberately fixed, actionable text; never expose GitHub response bodies or credential exceptions.
public sealed class GitHubSyncException(string message, int? statusCode = null) : Exception(message) { public int? StatusCode { get; } = statusCode; }

