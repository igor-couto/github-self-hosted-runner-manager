using System.Net;
using System.Security.Claims;
using System.Runtime.InteropServices;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using RunnerRoom.Configuration;
using RunnerRoom.Features.Access;
using RunnerRoom.Features.Management;
using RunnerRoom.Features.GitHub;
using RunnerRoom.Features.Runners;
using RunnerRoom.Features.Setup;

namespace RunnerRoom.Tests.Features.Setup;

internal static class SetupChecks
{
    [DllImport("libc")] private static extern uint geteuid();
    internal static async Task Run(Action<bool, string> check)
    {
        var directory = Path.Combine(Path.GetTempPath(), "runner-room-setup-" + Guid.NewGuid().ToString("N"));
        RunnerOptions Options(string name) => new() { Monitoring = new() { StateDirectory = Path.Combine(directory, name) } };
        var requested = new List<string>();
        using var handler = new Handler(request =>
        {
            requested.Add(request.RequestUri!.AbsoluteUri);
            check(request.RequestUri.Host == "api.github.com" && request.Headers.Authorization?.Scheme == "Bearer", "Setup verifies credentials only against GitHub's fixed API host.");
            return new(HttpStatusCode.OK) { Content = new StringContent(request.RequestUri.AbsolutePath == "/user/repos" ?
                "[{\"full_name\":\"Example/Project\",\"private\":true}]" : "{\"total_count\":0,\"runners\":[]}") };
        });
        using var http = new HttpClient(handler);
        SetupService Service(RunnerOptions options, Action<ManagementOptions>? activate = null) => new(options, http, prerequisites: () => [], activate: activate);
        DefaultHttpContext Context(string? role = null)
        {
            var context = new DefaultHttpContext();
            if (role is not null) context.User = new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.Role, role)], "test"));
            return context;
        }
        var input = new SetupSaveRequest("setup-test-token", ["repo:Example/Project"]);
        async Task Reject(Func<Task> action, string message)
        {
            try { await action(); check(false, message); }
            catch (SetupException error) { check(!error.Message.Contains("setup-test-token"), message); }
        }
        try
        {
            var options = Options("initial");
            options.Access.TrustedProxies = ["127.0.0.1"];
            var originalAccess = JsonSerializer.Serialize(options.Access);
            var activated = false;
            var setup = Service(options, management =>
            {
                check(File.Exists(SetupService.ConfigurationPath(options)) && File.ReadAllText(management.TokenFile!) == input.Token,
                    "Configuration and credentials are durable before live management activation.");
                options.Management = management;
                activated = true;
            });
            check(setup.Available && setup.Authorized(Context()), "An open LAN dashboard permits setup directly without a server code or login.");
            var status = JsonSerializer.Serialize(setup.Status());
            check(!status.Contains("requiresCode") && !status.Contains(options.Monitoring.StateDirectory!), "Setup status has no ownership-verification step or private server paths.");
            var discovered = await setup.DiscoverAsync(input.Token, default);
            check(discovered.Repositories is [{ FullName: "example/project", IsPrivate: true }] && !discovered.Partial, "Repository discovery normalizes names and reports visibility.");
            var saved = await setup.SaveAsync(input, "http://server:8080", default);
            check(saved.Saved && !saved.RestartRequired && activated && !setup.PendingRestart && !setup.Available,
                "Successful live activation enables management immediately without a restart or repeat setup.");
            using var settings = JsonDocument.Parse(File.ReadAllText(SetupService.ConfigurationPath(options)));
            check(!settings.RootElement.TryGetProperty("Access", out _) && JsonSerializer.Serialize(options.Access) == originalAccess,
                "Open LAN setup never enables login or changes access, proxy or account settings.");
            var management = settings.RootElement.GetProperty("Management");
            check(management.GetProperty("Enabled").GetBoolean() && management.GetProperty("AllowedScopes")[0].GetString() == "repo:example/project", "Saved configuration enables only explicitly selected scopes.");
            var tokenPath = management.GetProperty("TokenFile").GetString()!;
            check(File.ReadAllText(tokenPath) == input.Token && !File.ReadAllText(SetupService.ConfigurationPath(options)).Contains(input.Token!), "Credentials are stored separately and never embedded in configuration.");
            if (OperatingSystem.IsLinux())
                check(File.GetUnixFileMode(tokenPath) == (UnixFileMode.UserRead | UnixFileMode.UserWrite) &&
                    File.GetUnixFileMode(SetupService.ConfigurationPath(options)) == (UnixFileMode.UserRead | UnixFileMode.UserWrite), "Saved credentials and settings are readable only by their owner.");
            await Reject(() => setup.SaveAsync(input, "http://server:8080", default), "Active management cannot overwrite setup credentials or accounts.");

            var existing = Options("existing-access");
            existing.Access = new() { Enabled = true, PublicOrigin = "https://runner.example", LocalUsers = [new("existing", "test-hash", "admin")],
                GitHubUsers = [new("1234", "operator")], TrustedProxies = ["127.0.0.1"] };
            var existingAccess = JsonSerializer.Serialize(existing.Access);
            var protectedSetup = Service(existing, configuration => existing.Management = configuration);
            check(!protectedSetup.Authorized(Context()) && !protectedSetup.Authorized(Context("viewer")) && !protectedSetup.Authorized(Context("operator")), "Existing sign-in restricts setup to authenticated administrators.");
            check(protectedSetup.Authorized(Context("admin")), "An existing administrator can configure management directly.");
            await protectedSetup.SaveAsync(input, "https://runner.example", default);
            using var preserved = JsonDocument.Parse(File.ReadAllText(SetupService.ConfigurationPath(existing)));
            check(!preserved.RootElement.TryGetProperty("Access", out _) && JsonSerializer.Serialize(existing.Access) == existingAccess,
                "Management setup preserves existing authenticated accounts, roles, origin and proxies exactly.");

            var failedOptions = Options("activation-failure");
            var failed = Service(failedOptions, _ => throw new IOException("private filesystem detail"));
            var failure = await failed.SaveAsync(input, "http://server:8080", default);
            check(failure.Saved && failure.RestartRequired && failed.PendingRestart && !failure.Message.Contains("private filesystem detail"),
                "Activation failure reports a successful durable save and a safe actionable restart message.");
            check(File.Exists(SetupService.ConfigurationPath(failedOptions)) && Service(failedOptions).PendingRestart,
                "Saved configuration survives activation failure and is detected by another process.");
            await Reject(() => failed.SaveAsync(input, "http://server:8080", default), "Pending activation cannot overwrite already saved configuration.");
            var fallback = await Service(Options("without-activator")).SaveAsync(input, "http://server:8080", default);
            check(fallback.Saved && fallback.RestartRequired, "A host without live activation accurately reports a required restart.");

            if (OperatingSystem.IsLinux() && geteuid() != 0)
            {
                var liveOptions = Options("live-management");
                liveOptions.RunnersRoot = Path.Combine(directory, "existing-runners");
                var client = new ManagementGitHub(http, liveOptions);
                var monitor = new RunnerMonitor(liveOptions, new GitHubRunnerClient(http, new()));
                using var manager = new RunnerManagement(liveOptions, client, monitor);
                await manager.StartAsync(default);
                check(manager.ExecuteTask is { IsCompleted: false }, "Disabled management keeps its background worker ready for UI activation.");
                var liveSetup = Service(liveOptions, manager.EnableFromSetup);
                var enabled = await liveSetup.SaveAsync(input, "http://server:8080", default);
                check(enabled.Saved && !enabled.RestartRequired && liveOptions.Management.Enabled && !liveOptions.Access.Enabled,
                    "Real Linux management starts immediately from UI setup with access left disabled.");
                check(liveOptions.RunnersRoots.Contains(liveOptions.RunnersRoot) && liveOptions.RunnersRoots.Contains(liveOptions.Management.RootDirectory!),
                    "Live activation adds managed runners to discovery while retaining the original monitored directory.");
                using var services = new ServiceCollection().AddSingleton(new AccessAudit(liveOptions)).BuildServiceProvider();
                var context = new DefaultHttpContext { RequestServices = services };
                var operation = manager.Pool(new("setup-test", new() { Name = "setup-runner", Scope = "repo:example/project" }), context);
                for (var i = 0; i < 100 && manager.CopyState().Operations.Single(o => o.Id == operation.Id).State is "queued" or "running"; i++)
                    await Task.Delay(50);
                check(manager.CopyState().Operations.Single(o => o.Id == operation.Id) is { State: "succeeded", Actor: "lan" },
                    "The worker executes management requests after live setup and records their LAN origin.");
                var competingOptions = Options("live-management");
                using var competing = new RunnerManagement(competingOptions, new ManagementGitHub(http, competingOptions),
                    new RunnerMonitor(competingOptions, new GitHubRunnerClient(http, new())));
                try { competing.EnableFromSetup(liveOptions.Management); check(false, "Competing managers cannot share state ownership."); }
                catch (ManagementException) { check(!competingOptions.Management.Enabled, "Failed live activation preserves disabled management when state is already owned."); }
                await manager.StopAsync(default);
                manager.Dispose();
                using var restarted = new RunnerManagement(liveOptions, client, monitor);
                check(JsonSerializer.SerializeToElement(restarted.Snapshot()).GetProperty("enabled").GetBoolean() && !liveOptions.Access.Enabled,
                    "Enabled management restarts successfully without introducing a login requirement.");
            }

            var demoOptions = Options("demo"); demoOptions.Demo = true;
            var demo = Service(demoOptions, _ => throw new Exception("Demo must not activate real management."));
            var before = requested.Count;
            check(demo.Authorized(Context()), "Demo setup can be explored without credentials.");
            await demo.DiscoverAsync(null, default);
            var demoResult = await demo.SaveAsync(input with { Token = null }, "http://server:8080", default);
            check(!demoResult.RestartRequired && requested.Count == before && !Directory.Exists(demoOptions.Monitoring.StateDirectory), "Demo discovery and saving perform no network or disk writes.");
            check(!demo.PendingRestart && demo.Available, "Completing a demo does not leave a misleading restart prompt and can be previewed again.");

            var restricted = new SetupService(Options("restricted"), http, "Deployment configuration overrides setup.", () => []);
            check(!restricted.Available && restricted.Configuration("http://server").Issues.Length == 1, "Deployment overrides are visible and disable unsupported setup.");
            await Reject(() => restricted.SaveAsync(input, "http://server:8080", default), "Setup refuses to save settings that deployment overrides would ignore.");
            foreach (var invalid in new string[]?[] { null, [], ["repo:../private"], ["org:owner/path"], ["repo:owner/project?token=secret"], [null!] })
                await Reject(() => { SetupService.ValidateScopes(invalid); return Task.CompletedTask; }, "Invalid or empty allowed scopes cannot reach API requests.");
            foreach (var invalid in new[] { "", "token with spaces", "token\nheader", new string('x', 4097) })
                await Reject(() => Service(Options("invalid-token")).DiscoverAsync(invalid, default), "Invalid credentials are rejected before GitHub requests.");

            using var deniedHttp = new HttpClient(new Handler(_ => new(HttpStatusCode.Unauthorized) { Content = new StringContent("setup-test-token") }));
            var denied = new SetupService(Options("denied"), deniedHttp, prerequisites: () => []);
            await Reject(() => denied.SaveAsync(input, "http://server:8080", default), "GitHub rejection does not leak API response bodies.");
            check(!Directory.Exists(Path.Combine(directory, "denied")), "GitHub verification failure leaves configuration and token files untouched.");
            using var invalidHttp = new HttpClient(new Handler(_ => new(HttpStatusCode.OK) { Content = new StringContent("{}") }));
            await Reject(() => new SetupService(Options("invalid-response"), invalidHttp, prerequisites: () => []).SaveAsync(input, "http://server:8080", default), "An invalid runner-list response cannot authorize saving setup.");
            using var redirectHttp = new HttpClient(new Handler(_ => new(HttpStatusCode.Redirect) { Headers = { Location = new Uri("https://attacker.example/") } }));
            await Reject(() => new SetupService(Options("redirect"), redirectHttp, prerequisites: () => []).DiscoverAsync(input.Token, default), "Unexpected redirect responses are rejected.");
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
    }
    private sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> callback) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => Task.FromResult(callback(request));
    }
}
