using System.Net;
using System.Text;
using System.Text.Json;
using RunnerRoom.Configuration;
using RunnerRoom.Features.GitHub;

namespace RunnerRoom.Tests.Features.GitHub;

internal static class SyncChecks
{
    internal static async Task Run(Action<bool, string> check)
    {
        foreach (var input in new[] { "../repo", "owner/../repo", "https://github.com/owner/repo", "owner/repo?x=1", "owner/%2e", "owner/..", "owner/repo/extra" })
        {
            try { GitHubSyncClient.Repository(input); check(false, "Reject invalid repository identifiers."); }
            catch (GitHubSyncException) { check(true, "Reject invalid repository identifiers."); }
        }
        check(GitHubSyncClient.Repository("Owner/Repo.Name") == "owner/repo.name", "Normalize repository names.");
        var directory = Path.Combine(Path.GetTempPath(), "runner-room-sync-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var now = DateTimeOffset.UtcNow;
            string RunJson(int attempt = 1, string repo = "owner/repo", long id = 10) => JsonSerializer.Serialize(new
            {
                id, run_attempt = attempt, name = "CI", status = "completed", conclusion = "success", head_branch = "main", head_sha = "abc",
                created_at = now.AddHours(-1), updated_at = now, run_started_at = now.AddHours(-1), actor = new { login = "owner" }, @event = "push", repository = new { full_name = repo }
            });
            var jobJson = JsonSerializer.Serialize(new { total_count = 1, jobs = new[] { new { id = 50, run_id = 10, name = "Test", status = "completed", conclusion = "success", runner_id = 7, runner_name = "pi", labels = new[] { "self-hosted", "ARM64" }, started_at = now.AddHours(-1), completed_at = now, steps = new[] { new { number = 1, name = "Build", status = "completed", conclusion = "success", started_at = now.AddHours(-1), completed_at = now } } } } });
            var attempt = 1; var failJobs = false; var targetedMismatch = false; var emptyRuns = false; var requested = new List<string>();
            using var http = new HttpClient(new Handler(request =>
            {
                requested.Add(request.RequestUri!.PathAndQuery);
                check(request.RequestUri.Scheme == "https" && request.RequestUri.Host == "api.github.com" && request.Headers.Authorization?.Parameter == "fixture-token", "Only fixed GitHub API receives the credential.");
                var path = request.RequestUri.AbsolutePath;
                if (path == "/user/repos") return Json("[{\"full_name\":\"owner/repo\",\"private\":true}]");
                if (path.EndsWith("/jobs")) return failJobs ? new(HttpStatusCode.InternalServerError) { Content = new StringContent("sensitive-body-fixture-token") } : Json(jobJson);
                if (path.EndsWith("/runs/10")) return Json(RunJson(attempt, targetedMismatch ? "different/repo" : "owner/repo"));
                return Json(emptyRuns ? "{\"total_count\":0,\"workflow_runs\":[]}" : "{\"total_count\":1,\"workflow_runs\":[" + RunJson(attempt) + "]}");
            }));
            var client = new GitHubSyncClient(http, _ => Task.FromResult<string?>("fixture-token"), () => true);
            var options = new RunnerOptions { Monitoring = new() { StateDirectory = directory } };
            using var service = new GitHubSyncService(options, client);
            var discovery = await service.DiscoverAsync();
            check(discovery.Repositories.Length == 1 && discovery.Repositories[0].IsPrivate && !discovery.Partial, "Discover accessible repositories without selecting everything.");
            await service.SelectAsync(["OWNER/REPO"], historyDays: 7, maxRunsPerRepository: 25);
            check(service.Snapshot() is { HistoryDays: 7, MaxRunsPerRepository: 25 }, "Save simple import settings.");
            await service.SynchronizeAsync(null, null, default);
            var snapshot = service.Snapshot();
            check(snapshot.Runs is [{ Id: 10, Attempt: 1 }] && snapshot.Runs[0].Jobs[0].Steps[0].Name == "Build" && snapshot.Runs[0].Jobs[0].Labels.Contains("ARM64"), "Import workflow, attempt, job, step and runner labels.");
            check(snapshot.Repositories[0].LastSyncedAt is not null && snapshot.Repositories[0].Error is null, "Successful synchronization updates freshness.");
            var jobsBefore = requested.Count(p => p.Contains("/jobs"));
            await service.SynchronizeAsync(null, null, default);
            check(requested.Count(p => p.Contains("/jobs")) == jobsBefore && service.Snapshot().Runs.Length == 1, "Deduplicate repeated attempts and reuse unchanged completed jobs.");
            attempt = 2;
            await service.SynchronizeAsync("owner/repo", 10, default);
            check(service.Snapshot().Runs.Select(r => r.Attempt).Order().SequenceEqual([1, 2]), "Retain separately observed workflow attempts.");
            failJobs = true;
            await service.SynchronizeAsync("owner/repo", 10, default);
            snapshot = service.Snapshot();
            check(snapshot.Runs.Length == 2 && snapshot.Runs.All(r => r.Jobs.Length == 1) && snapshot.Repositories[0].Error is not null, "Failed job fetch preserves prior complete results and marks repository stale.");
            check(!JsonSerializer.Serialize(snapshot).Contains("fixture-token") && !JsonSerializer.Serialize(snapshot).Contains("sensitive-body"), "Remote failure content and token never leak into state.");
            failJobs = false; targetedMismatch = true;
            await service.SynchronizeAsync("owner/repo", 10, default);
            check(service.Snapshot().Repositories[0].Error!.Contains("unexpected repository"), "Targeted run refresh validates its repository.");
            using var restored = new GitHubSyncService(options, client);
            check(restored.Snapshot().Runs.Length == 2 && restored.Snapshot().Repositories[0].Selected && restored.Snapshot().HistoryDays == 7, "Restore selected repositories, import settings and history after restart.");
            try { service.Queue("other/repo"); check(false, "Reject sync outside selected repositories."); } catch (GitHubSyncException) { check(true, "Reject sync outside selected repositories."); }
            check(service.Queue("owner/repo") && !service.Queue(), "Allow only one queued synchronization.");
            check(service.Cancel() && !service.Snapshot().Progress.Running, "Cancel a queued synchronization.");
            emptyRuns = true;
            await service.SynchronizeAsync(null, null, default);
            check(service.Snapshot().Runs.Length == 0, "Complete resynchronization removes stale/deleted workflow records.");
            await service.SelectAsync([]);
            check(!service.Queue(), "Removing followed repositories stops refresh.");

            using var limitedHttp = new HttpClient(new Handler(_ =>
            {
                var response = new HttpResponseMessage(HttpStatusCode.TooManyRequests);
                response.Headers.TryAddWithoutValidation("X-RateLimit-Remaining", "0");
                response.Headers.TryAddWithoutValidation("X-RateLimit-Limit", "5000");
                response.Headers.TryAddWithoutValidation("X-RateLimit-Reset", now.AddMinutes(10).ToUnixTimeSeconds().ToString());
                return response;
            }));
            var limited = new GitHubSyncClient(limitedHttp, _ => Task.FromResult<string?>("token"), () => true);
            try { await limited.DiscoverAsync(default); check(false, "Handle GitHub API limits."); } catch (GitHubSyncException) { check(limited.Budget is { Remaining: 0, Limit: 5000, RetryAt: not null }, "Record API limits and cooldown."); }
            try { await limited.DiscoverAsync(default); check(false, "Back off after rate limiting."); } catch (GitHubSyncException ex) { check(ex.Message.Contains("pause"), "Back off before issuing another request."); }
            using var appHttp = new HttpClient(new Handler(request =>
            {
                check(request.RequestUri!.AbsolutePath == "/installation/repositories", "App credentials discover installation repositories.");
                return Json("{\"repositories\":[]}");
            }));
            await new GitHubSyncClient(appHttp, _ => Task.FromResult<string?>("token"), () => true, true).DiscoverAsync(default);
            using var malformedHttp = new HttpClient(new Handler(_ => Json("{\"workflow_runs\":[{\"id\":\"not-number\"}]}")));
            try { await new GitHubSyncClient(malformedHttp, _ => Task.FromResult<string?>("token"), () => true).RunsAsync("owner/repo", now, 10, default); check(false, "Reject malformed runs safely."); }
            catch (GitHubSyncException) { check(true, "Reject malformed runs safely."); }
            using var noHttp = new HttpClient(new Handler(_ => throw new Exception("Demo must not call GitHub.")));
            using var demo = new GitHubSyncService(new() { Demo = true, Monitoring = new() { StateDirectory = directory } }, new GitHubSyncClient(noHttp, _ => throw new Exception("Demo must not read credentials."), () => false));
            await demo.DiscoverAsync(); await demo.SynchronizeAsync(null, null, default);
            check(demo.Snapshot() is { Demo: true, Runs.Length: 3 }, "Demo uses illustrative data without external requests or credentials.");
            File.WriteAllText(Path.Combine(directory, "github-sync.json"), "invalid-json");
            using var corrupt = new GitHubSyncService(options, client);
            await corrupt.SelectAsync(["owner/repo"]);
            check(corrupt.Snapshot().Warning is not null && File.ReadAllText(Path.Combine(directory, "github-sync.json")) == "invalid-json", "Preserve corrupt history for recovery instead of silently overwriting it.");
        }
        finally { Directory.Delete(directory, true); }
    }
    private static HttpResponseMessage Json(string value) => new(HttpStatusCode.OK) { Content = new StringContent(value, Encoding.UTF8, "application/json") };
    private sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> response) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => Task.FromResult(response(request));
    }
}
