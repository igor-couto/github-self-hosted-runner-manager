using System.Net;
using System.Text.Json;
using RunnerRoom;

internal static class InventoryChecks
{
    internal static async Task Run(Action<bool, string> check)
    {
        var root = Path.Combine(Path.GetTempPath(), "runner-room-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var first = Path.Combine(root, "one");
            var second = Path.Combine(root, "nested", "two");
            foreach (var dir in new[] { root, first, second, Path.Combine(first, "_work", "not-a-runner") })
            {
                Directory.CreateDirectory(dir);
                File.WriteAllText(Path.Combine(dir, ".runner"), "{\"agentId\":42,\"agentName\":\"pifive2\",\"gitHubUrl\":\"https://github.com/example/api\",\"poolName\":\"Home\"}");
            }
            var shallow = LocalRunnerReader.Discover(new() { RunnersRoot = root });
            check(shallow.Folders.SequenceEqual([first]), "Stale parent metadata must not hide child runners; recursion is opt-in.");
            var recursive = LocalRunnerReader.Discover(new() { RunnersRoots = [root, first, Path.Combine(root, "missing")], Discovery = new() { Recursive = true } });
            check(recursive.Folders.Length == 2 && recursive.Folders.Contains(second) && recursive.Warnings.Length == 1,
                "Multiple roots deduplicate installations, skip workspaces, and retain readable roots when one fails.");
            check(LocalRunnerReader.Discover(new() { RunnersRoot = root, Discovery = new() { Recursive = true, MaxDepth = 1 } }).Folders.Length == 1,
                "Recursive depth limits must be enforced.");
            check(LocalRunnerReader.Discover(new() { RunnersRoot = second }).Folders.SequenceEqual([second]), "A single installation can be a root.");
            File.WriteAllText(Path.Combine(first, ".service"), "actions.runner.example.api.service\n");
            var runner = LocalRunnerReader.Read(first, new() { RunnerOverrides = [new() { Path = first, DisplayName = "API build", Group = "Backend" }] });
            check(runner is { Name: "pifive2", DisplayName: "API build", Group: "Backend", AgentId: 42, Repository: "example/api", Organization: "example", RunnerGroup: "Home", ServiceName: "actions.runner.example.api.service" },
                "Read registration and aliases without changing GitHub's registered name.");
            check(File.ReadAllText(Path.Combine(first, ".runner")).Contains("pifive2"), "Aliases must not modify registration.");
            check(runner.Version is null && runner.OperatingSystem is null, "Missing platform metadata must not be guessed from the host or directory.");
            File.WriteAllText(Path.Combine(first, ".service"), "--help.service\nmalicious");
            check(LocalRunnerReader.Read(first, new()).ServiceName is null, "Reject invalid service names.");
            File.WriteAllText(Path.Combine(second, ".runner"), "malformed");
            check(LocalRunnerReader.Read(second, new()).Name == "two", "Malformed metadata falls back to the directory name.");
            check(LocalRunnerReader.ParseScope("https://github.com/my-org") is { Organization: "my-org", Repository: null }, "Organization registrations retain their scope.");
            foreach (var invalid in new[] { "http://github.com/a/b", "https://token@github.com/a/b", "https://github.com/a/b?token=secret", "https://github.com/a/b/extra", "javascript:alert(1)" })
                check(LocalRunnerReader.ParseScope(invalid).Url is null, "Reject unexpected registration URLs.");

            var now = DateTimeOffset.UtcNow;
            var listener = new RunnerProcess(Path.Combine(first, "bin", "Runner.Listener"), 100, now.AddHours(-2));
            var worker = new RunnerProcess(Path.Combine(first, "bin", "Runner.Worker"), 101, now.AddMinutes(-3));
            check(RunnerRuntime.ApplyProcesses(runner, new([listener], true), now) is { Status: "idle", ProcessStatus: "running", UptimeSeconds: 7200 }, "Listener-only activity is idle with process uptime.");
            check(RunnerRuntime.ApplyProcesses(runner, new([listener, worker], true), now) is { Status: "busy", Pid: 100 }, "Worker presence means busy; uptime belongs to listener.");
            check(RunnerRuntime.ApplyProcesses(runner, new([], true), now).Status == "offline", "Reliable missing processes means offline.");
            check(RunnerRuntime.ApplyProcesses(runner, new([], false), now).Status == "unknown", "Limited visibility must not become offline.");
            check(RunnerRuntime.ApplyProcesses(runner, new([listener], false), now) is { Status: "unknown", ProcessStatus: "running" }, "A visible listener with hidden workers cannot prove idle.");
            check(RunnerRuntime.ApplyProcesses(runner, new([worker], false), now).Status == "busy", "A positive worker observation remains busy despite limited visibility.");
            var oldListener = listener with { Executable = Path.Combine(first, "bin.2.326.0", "Runner.Listener") };
            check(RunnerRuntime.ApplyProcesses(runner, new([oldListener], true), now).Status == "idle", "A running old binary version survives an auto-update symlink change.");
            var other = listener with { Executable = Path.Combine(first + "-other", "bin", "Runner.Listener") };
            check(RunnerRuntime.ApplyProcesses(runner, new([other], true), now).Status == "offline", "Similar path prefixes must not conflate installations.");

            const string log = "[2026-01-01 10:01:00Z INFO Terminal] 2026-01-01 10:01:00Z: Running job: Build API\n" +
                "[2026-01-01 10:03:00Z INFO Terminal] 2026-01-01 10:03:00Z: Job Build API completed with result: Succeeded\n" +
                "[2026-01-01 10:04:00Z INFO Worker] secret-value\n";
            var job = LocalRunnerReader.ParseJobLog(log);
            check(job is { Name: "Build API", Result: "Succeeded" } && job.At == DateTimeOffset.Parse("2026-01-01T10:03:00Z"), "Last activity must come from timestamped job events, not unrelated diagnostics.");
            check(LocalRunnerReader.ParseJobLog("Running job: injected\n" + new string('a', 5000)) is null, "Unrecognized and oversized log lines are ignored.");
            Directory.CreateDirectory(Path.Combine(first, "_diag"));
            File.WriteAllText(Path.Combine(first, "_diag", "Runner_20260101.log"), log);
            check(LocalRunnerReader.ReadLastJob(first)?.Result == "Succeeded", "Read only recognized job summaries from listener logs.");

            var handler = new FakeGitHub();
            using var http = new HttpClient(handler);
            var clock = new TestClock();
            var client = new GitHubRunnerClient(http, new() { Token = "test-only-token" }, clock);
            var enriched = (await client.EnrichAsync([runner]))[0];
            check(enriched.GitHub is { Status: "online", Busy: true } && enriched.GitHub.Labels!.Contains("ARM64"), "Read GitHub connectivity, activity and registered labels.");
            check(enriched.Status == runner.Status && enriched.Architecture == "ARM64", "GitHub activity stays separate; known architecture can enrich missing local data.");
            await client.EnrichAsync([runner]);
            check(handler.Calls == 1, "Repeated dashboard refreshes must use the GitHub cache.");
            check(handler.LastUri?.Host == "api.github.com" && handler.LastUri.AbsolutePath == "/repos/example/api/actions/runners/42", "Use exact scope and registration ID.");
            check(handler.Authorized, "The configured token is sent only as a backend authorization header.");
            check(!JsonSerializer.Serialize(enriched).Contains("test-only-token"), "Never serialize the GitHub token to the browser.");
            var unconfigured = new GitHubRunnerClient(http, new());
            check((await unconfigured.EnrichAsync([runner]))[0].GitHub.Status == "not_configured" && handler.Calls == 1,
                "Local monitoring must make no GitHub requests without configuration.");
            clock.Now = clock.Now.AddSeconds(61);
            handler.Status = HttpStatusCode.Forbidden;
            check((await client.EnrichAsync([runner]))[0].GitHub.Status == "unknown" && handler.Calls == 2,
                "Expired API data must be rechecked and a failure must not preserve a false Online status.");
            foreach (var url in new[] { "https://evil.example/example/api", "https://github.com:444/example/api", "https://github.com@evil.example/example/api" })
                check(GitHubRunnerClient.Endpoint(runner with { GitHubUrl = url }) is null, "Runner metadata cannot redirect the GitHub token to another origin.");
            check(GitHubRunnerClient.Endpoint(runner with { GitHubUrl = "https://github.com/example", Repository = null }) == "orgs/example/actions/runners/42", "Organization runners use the organization endpoint.");
            foreach (var status in new[] { HttpStatusCode.Forbidden, HttpStatusCode.Unauthorized, HttpStatusCode.NotFound, HttpStatusCode.TooManyRequests, HttpStatusCode.Redirect })
            {
                using var failedHttp = new HttpClient(new FakeGitHub { Status = status });
                var failedClient = new GitHubRunnerClient(failedHttp, new() { Token = "test-only-token" });
                check((await failedClient.EnrichAsync([runner]))[0].GitHub.Status == "unknown", "API errors never become a false offline state.");
            }
            foreach (var body in new[] { "bad JSON", "[]", "{\"id\":999,\"status\":\"online\"}", "{\"id\":\"42\"}" })
            {
                using var failedHttp = new HttpClient(new FakeGitHub { Body = body });
                check((await new GitHubRunnerClient(failedHttp, new() { Token = "test-only-token" }).EnrichAsync([runner]))[0].GitHub.Status == "unknown",
                    "Malformed or mismatched registrations must not give false connectivity.");
            }
        }
        finally { Directory.Delete(root, true); }
    }

    private sealed class TestClock : TimeProvider
    {
        internal DateTimeOffset Now = DateTimeOffset.UtcNow;
        public override DateTimeOffset GetUtcNow() => Now;
    }

    private sealed class FakeGitHub : HttpMessageHandler
    {
        internal int Calls;
        internal Uri? LastUri;
        internal bool Authorized;
        internal HttpStatusCode Status = HttpStatusCode.OK;
        internal string Body = "{\"id\":42,\"name\":\"pifive2\",\"status\":\"online\",\"busy\":true,\"os\":\"linux\",\"labels\":[{\"name\":\"ARM64\"}]}";
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Calls++; LastUri = request.RequestUri; Authorized = request.Headers.Authorization?.Parameter == "test-only-token";
            return Task.FromResult(new HttpResponseMessage(Status) { Content = new StringContent(Body) });
        }
    }
}
