using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace RunnerRoom.Features.GitHub;

public sealed class GitHubSyncClient
{
    private readonly HttpClient http;
    private readonly Func<CancellationToken, Task<string?>> token;
    private readonly Func<bool> configured;
    private readonly bool app;
    private readonly TimeProvider clock;
    private readonly SemaphoreSlim requestGate = new(1);
    private GitHubApiBudget budget = new();
    public bool Configured => configured();
    public GitHubApiBudget Budget => budget;
    public GitHubSyncClient(HttpClient http, GitHubCredentialProvider credentials, TimeProvider? timeProvider = null)
        : this(http, credentials.GetTokenAsync, () => credentials.Configured, credentials.UsesApp, timeProvider) { }
    internal GitHubSyncClient(HttpClient http, Func<CancellationToken, Task<string?>> token, Func<bool> configured,
        bool app = false, TimeProvider? timeProvider = null)
    { this.http = http; this.token = token; this.configured = configured; this.app = app; clock = timeProvider ?? TimeProvider.System; }

    public static string Repository(string value)
    {
        if (value is null || value.Length > 140 || !Regex.IsMatch(value, @"\A[a-zA-Z0-9][a-zA-Z0-9-]{0,38}/[a-zA-Z0-9_.-]{1,100}\z", RegexOptions.CultureInvariant) ||
            value.Split('/')[1] is "." or "..")
            throw new GitHubSyncException("Use a repository name such as owner/repository.");
        return value.ToLowerInvariant();
    }

    internal async Task<JsonDocument> GetAsync(string path, CancellationToken cancellation)
    {
        await requestGate.WaitAsync(cancellation);
        try
        {
            if (budget.RetryAt > clock.GetUtcNow()) throw new GitHubSyncException("GitHub asked us to pause. Synchronization will retry after the API cooldown.");
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
            deadline.CancelAfter(TimeSpan.FromSeconds(20));
            var secret = await token(deadline.Token);
            if (string.IsNullOrWhiteSpace(secret)) throw new GitHubSyncException("Configure a GitHub token or App installation before connecting repositories.");
            using var request = new HttpRequestMessage(HttpMethod.Get, "https://api.github.com/" + path);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", secret.Trim());
            request.Headers.UserAgent.ParseAdd("RunnerRoom/1.0");
            request.Headers.Accept.ParseAdd("application/vnd.github+json");
            request.Headers.Add("X-GitHub-Api-Version", "2022-11-28");
            using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, deadline.Token);
            int? Header(string name) => response.Headers.TryGetValues(name, out var values) && int.TryParse(values.FirstOrDefault(), out var n) && n >= 0 ? n : null;
            DateTimeOffset? reset = null;
            if (response.Headers.TryGetValues("X-RateLimit-Reset", out var resets) && long.TryParse(resets.FirstOrDefault(), out var seconds) && seconds is >= 0 and <= 253402300799)
                reset = DateTimeOffset.FromUnixTimeSeconds(seconds);
            var now = clock.GetUtcNow();
            var remaining = Header("X-RateLimit-Remaining");
            DateTimeOffset? retry = null;
            if (remaining == 0) retry = reset > now ? reset : now.AddMinutes(1);
            if (response.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.TooManyRequests)
            {
                retry ??= now.AddMinutes(1);
                if (response.Headers.RetryAfter?.Delta is { } delta) retry = now.AddSeconds(Math.Clamp(delta.TotalSeconds, 1, 86400));
                else if (response.Headers.RetryAfter?.Date is { } date && date > now) retry = date;
            }
            budget = new(remaining, Header("X-RateLimit-Limit"), reset, retry);
            if (!response.IsSuccessStatusCode) throw new GitHubSyncException(response.StatusCode switch
            {
                HttpStatusCode.Unauthorized => "GitHub rejected the credential. Check its validity and expiration.",
                HttpStatusCode.Forbidden or HttpStatusCode.TooManyRequests => "GitHub access is denied or an API limit was reached. Check Actions read permission; synchronization will retry after the cooldown.",
                HttpStatusCode.NotFound => "Repository or workflow run not found. Check the name and credential access.",
                _ => "GitHub is temporarily unavailable. Saved results are preserved; try syncing again later."
            }, (int)response.StatusCode);
            await using var stream = await response.Content.ReadAsStreamAsync(deadline.Token);
            var bytes = new byte[2 * 1024 * 1024 + 1];
            var count = await stream.ReadAtLeastAsync(bytes, bytes.Length, false, deadline.Token);
            if (count == bytes.Length) throw new GitHubSyncException("GitHub returned more data than can safely be imported. Reduce the import limit.");
            return JsonDocument.Parse(bytes.AsMemory(0, count), new() { MaxDepth = 48 });
        }
        catch (GitHubCredentialException ex) { throw new GitHubSyncException(ex.Message); }
        catch (OperationCanceledException) when (!cancellation.IsCancellationRequested) { throw new GitHubSyncException("GitHub took too long to respond. Saved results are preserved; try again later."); }
        catch (Exception ex) when (ex is HttpRequestException or IOException or JsonException or InvalidOperationException or ArgumentException)
        { throw new GitHubSyncException("GitHub could not be read. Check connectivity and credential settings, then try again."); }
        finally { requestGate.Release(); }
    }

    internal async Task<GitHubRepositoryDiscovery> DiscoverAsync(CancellationToken cancellation)
    {
        var repositories = new Dictionary<string, GitHubSyncRepository>();
        var partial = false;
        for (var page = 1; page <= 5; page++)
        {
            using var json = await GetAsync((app ? "installation/repositories?" : "user/repos?sort=updated&") + $"per_page=100&page={page}", cancellation);
            var list = app ? List(json.RootElement, "repositories") : json.RootElement;
            if (list.ValueKind != JsonValueKind.Array) throw new GitHubSyncException("GitHub returned an unexpected repository list.");
            foreach (var item in list.EnumerateArray().Take(100))
            {
                var name = Repository(Text(item, "full_name") ?? "");
                repositories[name] = new(name, $"https://github.com/{name}", item.TryGetProperty("private", out var privacy) && privacy.ValueKind == JsonValueKind.True);
            }
            if (list.GetArrayLength() < 100) break;
            partial = page == 5;
        }
        return new(repositories.Values.OrderBy(r => r.FullName).ToArray(), partial);
    }

    internal async Task<(GitHubSyncRun[] Runs, bool Partial)> RunsAsync(string repository, DateTimeOffset from, int limit, CancellationToken cancellation)
    {
        repository = Repository(repository);
        var runs = new List<GitHubSyncRun>(); var partial = false;
        var perPage = Math.Min(100, limit);
        for (var page = 1; runs.Count < limit && page <= 5; page++)
        {
            using var json = await GetAsync($"repos/{repository}/actions/runs?per_page={perPage}&page={page}&created={Uri.EscapeDataString(">=" + from.UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ssZ"))}", cancellation);
            var root = json.RootElement; var list = List(root, "workflow_runs");
            var batch = list.EnumerateArray().Take(perPage).Select(item => ParseRun(item, repository)).ToArray();
            runs.AddRange(batch.Take(limit - runs.Count));
            partial = Number(root, "total_count") > runs.Count;
            if (list.GetArrayLength() < perPage) break;
        }
        return (runs.DistinctBy(r => (r.Id, r.Attempt)).ToArray(), partial);
    }

    internal async Task<GitHubSyncRun> RunAsync(string repository, long runId, CancellationToken cancellation)
    {
        repository = Repository(repository);
        if (runId <= 0) throw new GitHubSyncException("Choose a valid workflow run.");
        using var json = await GetAsync($"repos/{repository}/actions/runs/{runId}", cancellation);
        var run = ParseRun(json.RootElement, repository);
        if (run.Id != runId) throw new GitHubSyncException("GitHub returned a different workflow run. Nothing was imported.");
        return run;
    }

    internal async Task<GitHubSyncRun> JobsAsync(GitHubSyncRun run, int limit, CancellationToken cancellation)
    {
        var jobs = new List<GitHubSyncJob>(); var partial = false;
        for (var page = 1; jobs.Count < limit && page <= 5; page++)
        {
            var perPage = Math.Min(100, limit);
            using var json = await GetAsync($"repos/{Repository(run.Repository)}/actions/runs/{run.Id}/attempts/{run.Attempt}/jobs?per_page={perPage}&page={page}", cancellation);
            var root = json.RootElement; var list = List(root, "jobs");
            foreach (var item in list.EnumerateArray().Take(limit - jobs.Count))
            {
                var id = Number(item, "id");
                if (id <= 0 || Number(item, "run_id") != run.Id) throw new GitHubSyncException("GitHub returned an unexpected job. Nothing was imported for this repository.");
                var steps = item.TryGetProperty("steps", out var stepList) && stepList.ValueKind == JsonValueKind.Array ? stepList : default;
                var parsedSteps = steps.ValueKind == JsonValueKind.Array ? steps.EnumerateArray().Take(100).Select(s => new GitHubSyncStep((int)Math.Clamp(Number(s, "number"), 0, int.MaxValue), Text(s, "name") ?? "Step", Text(s, "status") ?? "unknown", Text(s, "conclusion"), Date(s, "started_at"), Date(s, "completed_at"))).ToArray() : [];
                partial |= steps.ValueKind == JsonValueKind.Array && steps.GetArrayLength() > 100;
                var labels = item.TryGetProperty("labels", out var labelList) && labelList.ValueKind == JsonValueKind.Array ? labelList.EnumerateArray().Where(l => l.ValueKind == JsonValueKind.String).Select(l => Clean(l.GetString())!).Take(50).ToArray() : [];
                jobs.Add(new(id, Text(item, "name") ?? "Job", Text(item, "status") ?? "unknown", Text(item, "conclusion"), Number(item, "runner_id") is > 0 and var runner ? runner : null,
                    Text(item, "runner_name"), labels, Date(item, "started_at"), Date(item, "completed_at"), $"https://github.com/{run.Repository}/actions/runs/{run.Id}/job/{id}", parsedSteps));
            }
            partial |= Number(root, "total_count") > jobs.Count;
            if (list.GetArrayLength() < perPage) break;
        }
        return run with { Jobs = jobs.DistinctBy(j => j.Id).ToArray(), Partial = partial };
    }

    private static GitHubSyncRun ParseRun(JsonElement item, string repository)
    {
        if (Number(item, "id") <= 0 || !item.TryGetProperty("repository", out var repo) || !string.Equals(Text(repo, "full_name"), repository, StringComparison.OrdinalIgnoreCase))
            throw new GitHubSyncException("GitHub returned a workflow run from an unexpected repository.");
        var id = Number(item, "id");
        var created = Date(item, "created_at") ?? throw new GitHubSyncException("GitHub returned a workflow run without its creation date.");
        var attempt = Number(item, "run_attempt");
        if (attempt is <= 0 or > 100000) throw new GitHubSyncException("GitHub returned an invalid workflow attempt.");
        return new(id, (int)attempt, repository, Text(item, "name") ?? "Workflow", Text(item, "status") ?? "unknown", Text(item, "conclusion"), Text(item, "head_branch"), Text(item, "head_sha"),
            item.TryGetProperty("actor", out var actor) ? Text(actor, "login") : null, Text(item, "event"), $"https://github.com/{repository}/actions/runs/{id}/attempts/{attempt}", created, Date(item, "updated_at") ?? created, Date(item, "run_started_at"), []);
    }
    internal static JsonElement List(JsonElement root, string key) => root.ValueKind == JsonValueKind.Object && root.TryGetProperty(key, out var list) && list.ValueKind == JsonValueKind.Array ? list : throw new GitHubSyncException("GitHub returned an unexpected list.");
    internal static string? Text(JsonElement root, string key) => root.ValueKind == JsonValueKind.Object && root.TryGetProperty(key, out var value) && value.ValueKind == JsonValueKind.String ? Clean(value.GetString()) : null;
    private static string? Clean(string? value) => value is null ? null : new(value.Where(c => !char.IsControl(c)).Take(256).ToArray());
    internal static long Number(JsonElement root, string key) => root.ValueKind == JsonValueKind.Object && root.TryGetProperty(key, out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out var n) ? n : 0;
    private static DateTimeOffset? Date(JsonElement root, string key) => DateTimeOffset.TryParse(Text(root, key), System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.AssumeUniversal, out var value) ? value : null;
}

