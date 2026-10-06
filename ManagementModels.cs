using System.Text.RegularExpressions;

namespace RunnerRoom;

public sealed class ManagementOptions
{
    public bool Enabled { get; set; }
    public string? RootDirectory { get; set; }
    public string? TokenFile { get; set; }
    public string[] AllowedScopes { get; set; } = [];
    public int MaximumRunners { get; set; } = 50;
    public int MaximumBatchSize { get; set; } = 10;
    public int DrainTimeoutMinutes { get; set; } = 60;
}

public sealed record RunnerSettings
{
    public string Name { get; set; } = "runner";
    public string Scope { get; set; } = "";
    public string[] Labels { get; set; } = [];
    public string Mode { get; set; } = "process";
    public string WorkFolder { get; set; } = "_work";
    public string? GitHubGroup { get; set; }
    public string Pool { get; set; } = "default";
    public bool Ephemeral { get; set; }
    public bool DisableUpdate { get; set; }
    public bool RestoreOnRestart { get; set; } = true;
    public int RetryLimit { get; set; } = 3;
    public int RetryDelaySeconds { get; set; } = 30;
}

public sealed record ManagedRunner
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Path { get; set; } = "";
    public RunnerSettings Settings { get; set; } = new();
    public bool Imported { get; set; }
    public string? Service { get; set; }
    public long? AgentId { get; set; }
    public string? Version { get; set; }
    public bool DesiredRunning { get; set; }
    public string State { get; set; } = "stopped";
    public string? Error { get; set; }
    public int Retries { get; set; }
    public DateTimeOffset? NextRetry { get; set; }
    public DateTimeOffset? StartedAt { get; set; }
}
public sealed record ManagementOperation
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Action { get; set; } = "";
    public string Target { get; set; } = "";
    public string Actor { get; set; } = "";
    public string State { get; set; } = "queued";
    public string? Message { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? FinishedAt { get; set; }
}
public sealed record ManagedPool(string Name, RunnerSettings Template);
public sealed record WorkflowAttempt(string Repository, long RunId, int Attempt, string Status, string? Conclusion, string Url, DateTimeOffset CheckedAt);
public sealed class ManagementState
{
    public List<ManagedRunner> Runners { get; set; } = [];
    public List<ManagedPool> Pools { get; set; } = [];
    public List<ManagementOperation> Operations { get; set; } = [];
    public List<WorkflowAttempt> Workflows { get; set; } = [];
}
public sealed record CreateRunners(RunnerSettings Settings, int Count = 1, bool Start = true);
public sealed record ImportRunner(string RunnerId, string Mode = "process", bool RestoreOnRestart = true);
public sealed record RunnerAction(string[] Ids, string Action);
public sealed record ConfigureRunner(string Id, RunnerSettings Settings);
public sealed record ScalePool(string Name, int Count);
public sealed record WorkflowAction(string Repository, long RunId, string Action);
public sealed record GitHubGroupAction(string Organization, string Action, long? Id = null, string? Name = null,
    string Visibility = "private", bool AllowsPublicRepositories = false, long[]? RepositoryIds = null, long[]? RunnerIds = null);
public sealed class ManagementException(string message) : Exception(message);

internal static class ManagementValidation
{
    private static bool Part(string value) => Regex.IsMatch(value, @"^[A-Za-z0-9_][A-Za-z0-9_.-]{0,99}$") && value is not "." and not "..";
    internal static string Scope(string value)
    {
        var parts = value.Split(':', 2);
        if (parts.Length != 2 || parts[0] is not ("repo" or "org")) throw new ManagementException("Use repo:owner/repository or org:organization.");
        var segments = parts[1].Split('/');
        if (segments.Length != (parts[0] == "repo" ? 2 : 1) || segments.Any(s => !Part(s))) throw new ManagementException("Invalid GitHub scope.");
        return parts[0] + ":" + parts[1].ToLowerInvariant();
    }
    internal static string ApiScope(string scope) { scope = Scope(scope); return (scope.StartsWith("repo:") ? "repos/" : "orgs/") + scope.Split(':')[1]; }
    internal static string Url(string scope) => "https://github.com/" + Scope(scope).Split(':')[1];
    internal static void Settings(RunnerSettings s)
    {
        s.Scope = Scope(s.Scope);
        if (!Part(s.Name) || !Part(s.Pool) || !Part(s.WorkFolder)) throw new ManagementException("Name, pool and work folder must be simple names (letters, numbers, dot, underscore or hyphen), up to 100 characters.");
        if (s.WorkFolder.StartsWith('.') || s.WorkFolder is "bin" or "externals" or "_diag" || s.WorkFolder.StartsWith("bin.") || s.WorkFolder.StartsWith("externals.")) throw new ManagementException("Choose a workspace folder separate from runner binaries, diagnostics and configuration.");
        if (s.Mode is not ("process" or "user-service" or "system-service")) throw new ManagementException("Choose process, user-service or an imported system-service.");
        if (s.RetryLimit is < 0 or > 20 || s.RetryDelaySeconds is < 5 or > 3600) throw new ManagementException("Retry limit must be 0–20; delay 5–3600 seconds.");
        if (s.Labels is null || s.Labels.Length > 50 || s.Labels.Any(l => string.IsNullOrWhiteSpace(l) || l.Length > 100 || l.Any(c => char.IsControl(c) || c == ','))) throw new ManagementException("Use up to 50 nonempty labels, without commas or control characters.");
        s.Labels = s.Labels.Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        if (s.GitHubGroup is not null && (s.GitHubGroup.Length > 100 || s.GitHubGroup.Any(char.IsControl) || !s.Scope.StartsWith("org:"))) throw new ManagementException("GitHub runner groups apply to organization runners only.");
    }
    // Refuse symlinked ancestors as well as symlinked runner roots. Binary symlinks inside a runner are supported separately.
    internal static string SafeDirectory(string path)
    {
        if (!Path.IsPathFullyQualified(path)) throw new ManagementException("Runner paths must be absolute.");
        var full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        for (var current = new DirectoryInfo(full); current is not null; current = current.Parent)
            if (current.LinkTarget is not null) throw new ManagementException("Managed paths cannot contain symbolic links.");
        return full;
    }
    internal static bool Within(string child, string parent) => child.StartsWith(Path.TrimEndingDirectorySeparator(parent) + Path.DirectorySeparatorChar, StringComparison.Ordinal);
}
