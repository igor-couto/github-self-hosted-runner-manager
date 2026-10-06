using System.Formats.Tar;
using System.Net;
using System.Security.Claims;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using RunnerRoom;

internal static class ManagementChecks
{
    internal static async Task Run(Action<bool, string> check)
    {
        void Reject(Action action, string label) { try { action(); check(false, label); } catch (ManagementException) { check(true, label); } }
        check(ManagementValidation.Scope("repo:Owner/Repo") == "repo:owner/repo", "Normalize allowed scopes consistently.");
        foreach (var scope in new[] { "https://evil.test", "repo:owner/../secret", "org:org/repo", "repo:owner", "repo:owner/repo?x=1", "repo:owner/%2f", "enterprise:org" })
            Reject(() => ManagementValidation.Scope(scope), "Reject arbitrary management API paths: " + scope);
        foreach (var work in new[] { "../outside", "/tmp", "bin", "externals", "_diag", ".credentials" })
            Reject(() => ManagementValidation.Settings(new() { Scope = "repo:owner/repo", WorkFolder = work }), "Workspace cannot overlap runner state or leave installation.");
        Reject(() => ManagementValidation.Settings(new() { Scope = "repo:owner/repo", RetryLimit = 21 }), "Restart retries must be bounded.");
        Reject(() => ManagementValidation.Settings(new() { Scope = "repo:owner/repo", Labels = ["a,b"] }), "Labels cannot inject command arguments.");
        var command = ManagedRunnerRuntime.Command("runner", "/tmp", "--name", "one; touch /tmp/never");
        check(command.ArgumentList.Count == 2 && !command.UseShellExecute, "Runner commands use argument lists, never a shell.");
        Environment.SetEnvironmentVariable("RunnerRoomTestSecret", "secret");
        check(!ManagedRunnerRuntime.Command("runner", "/tmp").Environment.ContainsKey("RunnerRoomTestSecret"), "Do not pass dashboard secrets into runner jobs.");
        foreach (var url in new[] { "http://github.com/", "https://github.com.evil.test/", "https://github.com:8443/", "https://secret@github.com/", "https://127.0.0.1/" })
            check(!RunnerPackages.AllowedDownload(new Uri(url)), "Download redirects cannot leave the release host allowlist.");
        var root = Path.Combine(Path.GetTempPath(), "runner-room-management-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
        try
        {
            foreach (var entry in new[] { "../escape", "/etc/passwd", "a/../../escape", "a\\evil" }) Reject(() => RunnerPackages.EntryPath(root, entry), "Reject unsafe archive paths.");
            using var package = new MemoryStream();
            using (var writer = new TarWriter(package, leaveOpen: true))
            {
                writer.WriteEntry(new PaxTarEntry(TarEntryType.SymbolicLink, "bin") { LinkName = "../../outside" });
            }
            package.Position = 0;
            try { await RunnerPackages.Extract(package, Path.Combine(root, "extract"), default); check(false, "Reject archive links escaping extraction directory."); } catch (ManagementException) { check(true, "Reject archive links escaping extraction directory."); }
            var source = Path.Combine(root, "install-source"); var dest = Path.Combine(root, "install-destination");
            Directory.CreateDirectory(Path.Combine(source, "bin")); Directory.CreateDirectory(Path.Combine(dest, "bin"));
            File.WriteAllText(Path.Combine(source, "bin", "Runner.Listener"), "new binary"); File.WriteAllText(Path.Combine(dest, "bin", "Runner.Listener"), "old binary");
            File.WriteAllText(Path.Combine(dest, ".runner"), "keep registration");
            RunnerPackages.Install(source, dest, Path.Combine(root, "backup"));
            check(File.ReadAllText(Path.Combine(dest, "bin", "Runner.Listener")) == "new binary" && File.ReadAllText(Path.Combine(root, "backup", "bin", "Runner.Listener")) == "old binary" && File.ReadAllText(Path.Combine(dest, ".runner")) == "keep registration", "Binary install preserves registration and keeps previous files for recovery.");
            if (OperatingSystem.IsLinux())
            {
                var alias = Path.Combine(root, "linked-parent"); Directory.CreateSymbolicLink(alias, dest);
                Reject(() => ManagementValidation.SafeDirectory(Path.Combine(alias, "child")), "Reject symlinks in ancestors, not only at runner root.");
                Directory.Delete(alias);
            }
            var digest = new string('a', 64);
            var release = JsonSerializer.SerializeToElement(new { tag_name = "v2.337.0", assets = new[] { new { name = "actions-runner-linux-arm64-2.337.0.tar.gz", browser_download_url = "https://github.com/actions/runner/releases/download/v2.337.0/actions-runner-linux-arm64-2.337.0.tar.gz", digest = "sha256:" + digest } } });
            check(ManagementGitHub.ParseRelease(release, "arm64").Sha256 == digest, "Release selection binds architecture, exact URL and checksum.");
            Reject(() => ManagementGitHub.ParseRelease(release, "arm"), "Missing architecture must fail, not download another CPU's binary.");
            var secret = Path.Combine(root, "token"); File.WriteAllText(secret, "test-only-management-token");
            var options = new RunnerOptions { Management = new() { TokenFile = secret, AllowedScopes = ["repo:owner/repo", "org:team"] } };
            var handler = new RecordingHandler(); var github = new ManagementGitHub(new HttpClient(handler), options);
            Reject(() => github.Allow("repo:other/private"), "Management allowlist gates resources independently of token permissions.");
            await github.RegistrationToken("repo:owner/repo", default);
            check(handler.Requests.Last() is { Method: "POST", Path: "/repos/owner/repo/actions/runners/registration-token", Authorization: "Bearer test-only-management-token" }, "Registration uses dedicated management credentials at fixed API origin.");
            await github.Workflow(new("owner/repo", 55, "rerun-failed"), default);
            check(handler.Requests.Any(r => r.Path.EndsWith("/55/rerun-failed-jobs") && r.Method == "POST"), "Failed-job reruns use the workflow rerun endpoint.");
            await github.Workflow(new("owner/repo", 55, "cancel"), default);
            check(handler.Requests.Any(r => r.Path.EndsWith("/55/cancel")), "Workflow cancellation is explicit and separately authorized by scope.");
            await github.Groups(new("team", "update", 4, "Build", "selected", RepositoryIds: [8, 9]), default);
            check(handler.Requests.Any(r => r.Path.EndsWith("/4/repositories") && r.Method == "PUT" && r.Body.Contains("selected_repository_ids")), "Group repository restrictions use the separate membership endpoint.");
            handler.Status = HttpStatusCode.Forbidden;
            try { await github.RegistrationToken("repo:owner/repo", default); check(false, "GitHub denial must fail."); } catch (ManagementException ex) { check(!ex.Message.Contains("SECRET RESPONSE"), "Remote error bodies must not leak secrets."); }
            check(new LogRedactor(options).Redact("test-only-management-token") == "[REDACTED]", "Management token is masked in diagnostic excerpts.");

            var demoOptions = new RunnerOptions { Demo = true, Monitoring = new() { StateDirectory = root }, Management = new() { MaximumBatchSize = 10 } };
            var services = new ServiceCollection().AddSingleton(new AccessAudit(demoOptions)).BuildServiceProvider();
            var context = new DefaultHttpContext { RequestServices = services, User = new ClaimsPrincipal(new ClaimsIdentity([new(ClaimTypes.NameIdentifier, "local:test")], "test")) };
            using var manager = new RunnerManagement(demoOptions, github, new RunnerMonitor(demoOptions, new GitHubRunnerClient(new HttpClient(), new())));
            await manager.StartAsync(default);
            async Task<ManagementOperation> Finish(ManagementOperation op)
            {
                for (var i = 0; i < 100; i++) { var current = manager.CopyState().Operations.Single(o => o.Id == op.Id); if (current.State is not ("queued" or "running")) return current; await Task.Delay(50); }
                throw new Exception("Management test operation timed out.");
            }
            check((await Finish(manager.Create(new(new() { Name = "batch", Scope = "repo:owner/repo" }, 3), context))).State == "succeeded", "Batch creation completes as a queued operation.");
            var created = manager.CopyState().Runners.Where(r => r.Settings.Name.StartsWith("batch-")).ToArray();
            check(created.Length == 3 && created.All(r => r.DesiredRunning), "Batch creation makes distinct persistent managed entries.");
            check((await Finish(manager.Act(new(created.Select(r => r.Id).ToArray(), "drain"), context))).State == "succeeded" && manager.CopyState().Runners.Where(r => r.Settings.Name.StartsWith("batch-")).All(r => r.State == "stopped" && !r.DesiredRunning), "Drain clears restore intent and leaves runners stopped.");
            await Finish(manager.Pool(new("test-pool", new() { Name = "pool", Scope = "org:team" }), context));
            await Finish(manager.Scale(new("test-pool", 2), context));
            check(manager.CopyState().Runners.Count(r => r.Settings.Pool == "test-pool") == 2, "Manual pool scale-up creates requested members.");
            await Finish(manager.Scale(new("test-pool", 0), context));
            check(!manager.CopyState().Runners.Any(r => r.Settings.Pool == "test-pool"), "Manual pool scale-down removes selected members.");
            var invalid = await Finish(manager.Create(new(new() { Name = "batch-1", Scope = "repo:owner/repo" }), context));
            check(invalid.State == "failed" && manager.CopyState().Runners.Count(r => r.Settings.Name == "batch-1") == 1, "Duplicate creation fails without replacing an existing registration.");
            await Finish(manager.Workflow(new("owner/repo", 55, "rerun"), context));
            check(manager.CopyState().Workflows.Single().Attempt == 2, "Workflow tracking retains the subsequent attempt.");
            var cancel = manager.Create(new(new() { Name = "cancelled", Scope = "repo:owner/repo" }), context); manager.Cancel(cancel.Id);
            await Task.Delay(2500);
            check(!manager.CopyState().Runners.Any(r => r.Settings.Name == "cancelled"), "Cancelling queued creation has no runner side effects.");
            await manager.StopAsync(default);
            check(!File.Exists(Path.Combine(root, "management.json")), "Demo management never persists into live management state.");
            using var disabled = new RunnerManagement(new(), github, new RunnerMonitor(new(), new GitHubRunnerClient(new HttpClient(), new())));
            Reject(() => disabled.Create(new(new() { Scope = "repo:owner/repo" }), context), "Disabled management cannot queue mutations.");
            if (OperatingSystem.IsLinux() && Environment.GetEnvironmentVariable("RUNNER_ROOM_STUB") is { Length: > 0 } stub)
            {
                handler.Status = HttpStatusCode.OK;
                await LinuxManagementChecks.Run(check, root, stub, github);
            }
            if (OperatingSystem.IsLinux() && Environment.GetEnvironmentVariable("RUNNER_ROOM_PACKAGE_CHECK") == "1")
            {
                var real = new ManagementGitHub(new HttpClient(new HttpClientHandler { AllowAutoRedirect = false }), new());
                var downloaded = await real.Latest(default);
                var stage = Path.Combine(root, "public-package");
                await RunnerPackages.Download(downloaded, stage, default);
                await RunnerPackages.VerifyRuntime(stage, downloaded, default);
                var installed = Path.Combine(root, "real-installed"); Directory.CreateDirectory(installed);
                File.WriteAllText(Path.Combine(installed, ".runner"), "preserved registration");
                RunnerPackages.Install(Path.Combine(stage, "files"), installed, Path.Combine(stage, "previous"));
                check(File.Exists(Path.Combine(installed, "bin", "Runner.Listener")) && File.ReadAllText(Path.Combine(installed, ".runner")) == "preserved registration", "A real GitHub release passes checksum, extraction, preflight and installation without overwriting registration.");
            }
        }
        finally { Directory.Delete(root, true); }
    }
    private sealed record Request(string Method, string Path, string? Authorization, string Body);
    private sealed class RecordingHandler : HttpMessageHandler
    {
        internal List<Request> Requests { get; } = [];
        internal HttpStatusCode Status = HttpStatusCode.OK;
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(new(request.Method.Method, request.RequestUri!.AbsolutePath, request.Headers.Authorization?.ToString(), request.Content is null ? "" : await request.Content.ReadAsStringAsync(cancellationToken)));
            if (request.RequestUri.Host != "api.github.com") throw new Exception("Credential forwarded to unexpected host.");
            var body = Status != HttpStatusCode.OK ? "SECRET RESPONSE" : request.RequestUri.AbsolutePath.Contains("registration-token") ? "{\"token\":\"registration-only-token\"}" :
                request.RequestUri.AbsolutePath.Contains("/runs/") ? "{\"run_attempt\":2,\"status\":\"queued\",\"conclusion\":null}" : "{\"id\":4,\"busy\":false}";
            return new(Status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
        }
    }
}
