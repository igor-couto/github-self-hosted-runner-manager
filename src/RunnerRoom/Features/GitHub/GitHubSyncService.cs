using System.Threading.Channels;
using RunnerRoom.Configuration;

namespace RunnerRoom.Features.GitHub;

public sealed class GitHubSyncService : BackgroundService
{
    private readonly RunnerOptions options;
    private readonly GitHubSyncClient client;
    private readonly TimeProvider clock;
    private readonly object gate = new();
    private readonly GitHubSyncStore store;
    private readonly Channel<(string? Repository, long? RunId)> requests = Channel.CreateBounded<(string?, long?)>(1);
    private GitHubSyncStore.State state;
    private GitHubSyncProgress progress = new();
    private CancellationTokenSource? active;
    private DateTimeOffset? nextSyncAt;
    private bool queued;

    public GitHubSyncService(RunnerOptions options, GitHubSyncClient client, TimeProvider? timeProvider = null)
    {
        this.options = options; this.client = client; clock = timeProvider ?? TimeProvider.System;
        store = new(options.Monitoring.StateDirectory ?? Environment.GetEnvironmentVariable("STATE_DIRECTORY") ??
            (OperatingSystem.IsLinux() ? "/var/lib/runner-room" : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "RunnerRoom")), options.Demo);
        state = store.Load(options.GitHub.Sync.HistoryDays, options.GitHub.Sync.MaxRunsPerRepository);
        if (options.Demo) SeedDemo();
    }

    public GitHubSyncSnapshot Snapshot()
    {
        lock (gate) return new(options.GitHub.Sync.Enabled, client.Configured || options.Demo, options.Demo,
            state.Repositories.ToArray(), state.Runs.OrderByDescending(r => r.UpdatedAt).ThenByDescending(r => r.Attempt).ToArray(),
            progress with { Running = progress.Running || queued }, state.History.ToArray(), client.Budget, store.Warning,
            state.HistoryDays, state.MaxRunsPerRepository, Math.Clamp(options.GitHub.Sync.IntervalMinutes, 1, 1440), nextSyncAt);
    }

    public async Task<GitHubRepositoryDiscovery> DiscoverAsync(CancellationToken cancellation = default)
    {
        if (!options.GitHub.Sync.Enabled) throw new GitHubSyncException("GitHub synchronization is disabled in the server settings.");
        var discovered = options.Demo ? new GitHubRepositoryDiscovery(DemoRepositories(), false) : await client.DiscoverAsync(cancellation);
        lock (gate)
        {
            var old = state.Repositories.ToDictionary(r => r.FullName);
            // Retain selected repositories even if permissions change or discovery is capped.
            var merged = discovered.Repositories.Select(r => old.TryGetValue(r.FullName, out var previous) ? r with
                { Selected = previous.Selected, LastSyncedAt = previous.LastSyncedAt, Error = previous.Error, Partial = previous.Partial } : r)
                .Concat(state.Repositories.Where(r => r.Selected && !discovered.Repositories.Any(d => d.FullName == r.FullName))).OrderBy(r => r.FullName).ToArray();
            state.Repositories = merged.OrderByDescending(r => r.Selected).Take(500).ToArray(); store.Save(state);
            return new(state.Repositories.ToArray(), discovered.Partial);
        }
    }

    public Task SelectAsync(string[] repositories, CancellationToken cancellation = default, int? historyDays = null, int? maxRunsPerRepository = null)
    {
        cancellation.ThrowIfCancellationRequested();
        if (repositories is null || repositories.Length > Math.Clamp(options.GitHub.Sync.MaxRepositories, 1, 25))
            throw new GitHubSyncException($"Choose up to {Math.Clamp(options.GitHub.Sync.MaxRepositories, 1, 25)} repositories.");
        if (historyDays is < 1 or > 90 || maxRunsPerRepository is < 1 or > 100)
            throw new GitHubSyncException("Choose 1–90 days and 1–100 workflow runs per repository.");
        var selected = repositories.Select(GitHubSyncClient.Repository).Distinct().ToHashSet();
        lock (gate)
        {
            if (progress.Running || queued) throw new GitHubSyncException("Wait for the current synchronization to finish or cancel it before changing repositories.");
            var updated = state.Repositories.Select(r => r with { Selected = selected.Contains(r.FullName) }).ToList();
            foreach (var name in selected.Where(name => !updated.Any(r => r.FullName == name))) updated.Add(new(name, $"https://github.com/{name}", Selected: true));
            state.Repositories = updated.OrderByDescending(r => r.Selected).ThenBy(r => r.FullName).Take(500).ToArray();
            state.HistoryDays = historyDays ?? state.HistoryDays;
            state.MaxRunsPerRepository = maxRunsPerRepository ?? state.MaxRunsPerRepository;
            Trim(); store.Save(state); nextSyncAt = clock.GetUtcNow();
        }
        return Task.CompletedTask;
    }

    public bool Queue(string? repository = null, long? runId = null)
    {
        if (repository is not null) repository = GitHubSyncClient.Repository(repository);
        if (runId is <= 0 || runId.HasValue && repository is null) throw new GitHubSyncException("Choose the repository and a valid workflow run to refresh.");
        lock (gate)
        {
            if (repository is not null && !state.Repositories.Any(r => r.Selected && r.FullName == repository)) throw new GitHubSyncException("Follow this repository before synchronizing it.");
            if (!options.GitHub.Sync.Enabled || !client.Configured && !options.Demo || progress.Running || queued || !state.Repositories.Any(r => r.Selected) || client.Budget.RetryAt > clock.GetUtcNow()) return false;
            queued = requests.Writer.TryWrite((repository, runId));
            return queued;
        }
    }
    public bool Cancel()
    {
        lock (gate)
        {
            if (active is not null) { active.Cancel(); return true; }
            if (queued && requests.Reader.TryRead(out _)) { queued = false; return true; }
            return false;
        }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await Task.Yield();
        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                if (requests.Reader.TryRead(out var request)) await SynchronizeAsync(request.Repository, request.RunId, stoppingToken);
                else
                {
                    lock (gate)
                    {
                        if (!options.Demo && options.GitHub.Sync.Enabled && client.Configured && state.Repositories.Any(r => r.Selected) && (nextSyncAt is null || nextSyncAt <= clock.GetUtcNow()))
                        {
                            if (!Queue()) nextSyncAt = client.Budget.RetryAt ?? clock.GetUtcNow().AddMinutes(1);
                        }
                    }
                    await Task.Delay(TimeSpan.FromSeconds(1), clock, stoppingToken);
                }
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
    }

    internal async Task SynchronizeAsync(string? repository, long? runId, CancellationToken cancellation)
    {
        GitHubSyncRepository[] selected; int days; int maxRuns;
        using var scope = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        // Bound each complete import as well as each HTTP request.
        scope.CancelAfter(TimeSpan.FromMinutes(10));
        lock (gate)
        {
            queued = false; active = scope;
            selected = state.Repositories.Where(r => r.Selected && (repository is null || r.FullName == repository)).ToArray();
            days = state.HistoryDays; maxRuns = state.MaxRunsPerRepository;
            progress = new(true, repository, 0, selected.Length, clock.GetUtcNow());
        }
        try
        {
            foreach (var repo in selected)
            {
                lock (gate) progress = progress with { Repository = repo.FullName };
                try
                {
                    scope.Token.ThrowIfCancellationRequested();
                    var fetched = options.Demo ? (Runs: state.Runs.Where(r => r.Repository == repo.FullName).ToArray(), Partial: false) : runId.HasValue
                        ? (Runs: new[] { await client.RunAsync(repo.FullName, runId.Value, scope.Token) }, Partial: false)
                        : await client.RunsAsync(repo.FullName, clock.GetUtcNow().AddDays(-days), maxRuns, scope.Token);
                    var complete = new List<GitHubSyncRun>();
                    foreach (var run in fetched.Runs)
                    {
                        GitHubSyncRun? previous;
                        lock (gate) previous = state.Runs.FirstOrDefault(r => r.Repository == run.Repository && r.Id == run.Id && r.Attempt == run.Attempt);
                        // Completed unchanged attempts keep their jobs; targeted resync always repairs them.
                        if (options.Demo || !runId.HasValue && previous is { Status: "completed", Partial: false } && run.Status == "completed" && previous.UpdatedAt == run.UpdatedAt)
                            complete.Add(previous ?? run);
                        else complete.Add(await client.JobsAsync(run, Math.Clamp(options.GitHub.Sync.MaxJobsPerRun, 1, 100), scope.Token));
                    }
                    lock (gate)
                    {
                        var keys = complete.Select(r => (r.Repository, r.Id, r.Attempt)).ToHashSet();
                        var ids = complete.Select(r => r.Id).ToHashSet();
                        // Only a complete listing can establish that a run disappeared. Preserve older observed attempts of retained runs.
                        state.Runs = state.Runs.Where(r => !keys.Contains((r.Repository, r.Id, r.Attempt)) &&
                            (r.Repository != repo.FullName || runId.HasValue || fetched.Partial || ids.Contains(r.Id))).Concat(complete).ToArray();
                        var partial = fetched.Partial || complete.Any(r => r.Partial);
                        state.Repositories = state.Repositories.Select(r => r.FullName == repo.FullName ? r with { LastSyncedAt = clock.GetUtcNow(), Error = null, Partial = runId.HasValue ? r.Partial || partial : partial } : r).ToArray();
                        AddHistory(new(clock.GetUtcNow(), repo.FullName, partial ? "partial" : "success", complete.Count,
                            partial ? "Import limit reached. Showing the latest available records within your limits." : null));
                        Trim(); store.Save(state);
                    }
                }
                catch (GitHubSyncException ex)
                {
                    lock (gate)
                    {
                        state.Repositories = state.Repositories.Select(r => r.FullName == repo.FullName ? r with { Error = ex.Message } : r).ToArray();
                        AddHistory(new(clock.GetUtcNow(), repo.FullName, "error", 0, ex.Message)); store.Save(state);
                    }
                    if (client.Budget.RetryAt > clock.GetUtcNow()) break;
                }
                lock (gate) progress = progress with { CompletedRepositories = progress.CompletedRepositories + 1 };
            }
        }
        catch (OperationCanceledException)
        {
            lock (gate) { AddHistory(new(clock.GetUtcNow(), progress.Repository, "canceled", 0, "Synchronization stopped. Previously saved results were preserved.")); store.Save(state); }
        }
        finally
        {
            lock (gate)
            {
                active = null; progress = progress with { Running = false };
                nextSyncAt = clock.GetUtcNow().AddMinutes(Math.Clamp(options.GitHub.Sync.IntervalMinutes, 1, 1440));
                if (client.Budget.RetryAt > nextSyncAt) nextSyncAt = client.Budget.RetryAt;
            }
        }
    }
    private void AddHistory(GitHubSyncHistory entry) => state.History = new[] { entry }.Concat(state.History).Take(50).ToArray();
    private void Trim()
    {
        var selected = state.Repositories.Where(r => r.Selected).Select(r => r.FullName).ToHashSet();
        var retained = state.Runs.Where(r => selected.Contains(r.Repository) && r.CreatedAt >= clock.GetUtcNow().AddDays(-state.HistoryDays))
            .GroupBy(r => r.Repository).SelectMany(g => g.OrderByDescending(r => r.UpdatedAt).ThenByDescending(r => r.Attempt).Take(state.MaxRunsPerRepository))
            .OrderByDescending(r => r.UpdatedAt).ToArray();
        if (retained.Length > 500)
        {
            var omitted = retained.Skip(500).Select(r => r.Repository).ToHashSet();
            state.Repositories = state.Repositories.Select(r => omitted.Contains(r.FullName) ? r with { Partial = true } : r).ToArray();
        }
        state.Runs = retained.Take(500).ToArray();
    }
    private static GitHubSyncRepository[] DemoRepositories() => [new("octo-home/activities-api", "https://github.com/octo-home/activities-api", Selected: true), new("octo-home/newsletter", "https://github.com/octo-home/newsletter", Selected: true), new("octo-home/website", "https://github.com/octo-home/website")];
    private void SeedDemo()
    {
        var now = clock.GetUtcNow();
        state.Repositories = DemoRepositories().Select(r => r.Selected ? r with { LastSyncedAt = now.AddMinutes(-2) } : r).ToArray();
        state.Runs = Enumerable.Range(1, 3).Select(i =>
        {
            var repo = i == 3 ? "octo-home/newsletter" : "octo-home/activities-api";
            var status = i == 1 ? "in_progress" : "completed"; var conclusion = i == 1 ? null : i == 2 ? "success" : "failure";
            var start = now.AddMinutes(-i * 15);
            return new GitHubSyncRun(1200 + i, 1, repo, i == 3 ? "Send newsletter" : "Build & test", status, conclusion, "main", "f83a356604ae3c5d03e1b46ef4d1ca77d64a90b0", "octocat", "push",
                $"https://github.com/{repo}/actions/runs/{1200 + i}/attempts/1", start, now.AddMinutes(-2), start,
                [new(4200 + i, "Build on Raspberry Pi", status, conclusion, 7, "pifive", ["self-hosted", "Linux", "ARM64"], start, i == 1 ? null : start.AddMinutes(4), $"https://github.com/{repo}/actions/runs/{1200 + i}/job/{4200 + i}",
                    [new(1, "Check out repository", "completed", "success", start, start.AddSeconds(5)), new(2, "Build and test", status, conclusion, start.AddSeconds(5), i == 1 ? null : start.AddMinutes(4))])]);
        }).ToArray();
        state.History = [new(now.AddMinutes(-2), null, "success", 3, "Illustrative demo data. No GitHub requests are sent.")];
    }
}
