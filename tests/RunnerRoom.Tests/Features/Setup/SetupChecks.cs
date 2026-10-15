using System.Net;
using System.Security.Claims;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using RunnerRoom.Configuration;
using RunnerRoom.Features.Access;
using RunnerRoom.Features.Setup;

namespace RunnerRoom.Tests.Features.Setup;

internal static class SetupChecks
{
    internal static async Task Run(Action<bool, string> check)
    {
        var directory = Path.Combine(Path.GetTempPath(), "runner-room-setup-" + Guid.NewGuid().ToString("N"));
        var clock = new Clock(new DateTimeOffset(2026, 10, 15, 12, 0, 0, TimeSpan.Zero));
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
        SetupService Service(RunnerOptions options) => new(options, http, clock, prerequisites: () => []);
        DefaultHttpContext Context(string? code = null, string? role = null)
        {
            var context = new DefaultHttpContext();
            if (code is not null) context.Request.Headers["X-RR-Setup-Code"] = code;
            if (role is not null) context.User = new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.Role, role)], "test"));
            return context;
        }
        var input = new SetupSaveRequest("igor", "a-long-test-password", "setup-test-token", ["repo:Example/Project"], "http://server:8080", true);
        async Task Reject(Func<Task> action, string message)
        {
            try { await action(); check(false, message); }
            catch (SetupException error) { check(!error.Message.Contains("setup-test-token") && !error.Message.Contains(input.Password!), message); }
        }
        try
        {
            var options = Options("initial");
            options.Access.TrustedProxies = ["127.0.0.1"];
            var setup = Service(options);
            check(!setup.Authorized(Context()), "An unconfigured dashboard does not authorize setup without a server-generated code.");
            var code = SetupService.CreateCode(options, clock);
            check(code.Length == 48 && setup.Authorized(Context(code)), "A fresh random setup code authorizes initial setup.");
            check(!File.ReadAllText(Path.Combine(options.Monitoring.StateDirectory!, "setup-code.json")).Contains(code), "The setup code is stored only as a hash.");
            check(!setup.Authorized(Context(new string('x', 48))), "Incorrect setup codes cannot configure the server.");
            var status = JsonSerializer.Serialize(setup.Status());
            check(!status.Contains(code) && !status.Contains(options.Monitoring.StateDirectory!), "Public setup status omits secrets and server paths.");
            clock.Now += TimeSpan.FromMinutes(15);
            check(!setup.Authorized(Context(code)), "Setup codes expire at fifteen minutes.");
            var fresh = SetupService.CreateCode(options, clock);
            check(!setup.Authorized(Context(code)) && setup.Authorized(Context(fresh)), "Generating a new code revokes the previous one.");
            var discovered = await setup.DiscoverAsync(input.Token, default);
            check(discovered.Repositories is [{ FullName: "example/project", IsPrivate: true }] && !discovered.Partial, "Repository discovery normalizes names and reports visibility.");
            var saved = await setup.SaveAsync(input, "http://server:8080", default);
            check(saved.Saved && saved.RestartRequired && setup.PendingRestart && !setup.Available, "Saving setup requires a restart and prevents repeat configuration.");
            check(!setup.Authorized(Context(fresh)), "A successful save consumes the bootstrap code.");
            check(!File.Exists(Path.Combine(options.Monitoring.StateDirectory!, "setup-code.json")), "Consumed setup code files are removed.");
            using var settings = JsonDocument.Parse(File.ReadAllText(SetupService.ConfigurationPath(options)));
            var access = settings.RootElement.GetProperty("Access").Deserialize<AccessOptions>()!;
            check(access.Enabled && !access.RequireHttps && access.PublicOrigin == "http://server:8080", "Bootstrap saves the explicitly confirmed HTTP origin.");
            check(access.TrustedProxies.SequenceEqual(options.Access.TrustedProxies), "Bootstrap preserves the server's existing trusted proxy configuration.");
            check(new AccessAccounts(access).Login("igor", input.Password)?.IsInRole("admin") == true, "Bootstrap creates a working hashed administrator account.");
            var management = settings.RootElement.GetProperty("Management");
            check(management.GetProperty("Enabled").GetBoolean() && management.GetProperty("AllowedScopes")[0].GetString() == "repo:example/project", "Saved configuration enables only explicitly selected scopes.");
            var tokenPath = management.GetProperty("TokenFile").GetString()!;
            check(File.ReadAllText(tokenPath) == input.Token && !File.ReadAllText(SetupService.ConfigurationPath(options)).Contains(input.Token!), "Credentials are stored separately and never embedded in configuration.");
            check(!File.ReadAllText(SetupService.ConfigurationPath(options)).Contains(input.Password!), "Plaintext dashboard passwords are never persisted.");
            if (OperatingSystem.IsLinux())
                check(File.GetUnixFileMode(tokenPath) == (UnixFileMode.UserRead | UnixFileMode.UserWrite) &&
                    File.GetUnixFileMode(SetupService.ConfigurationPath(options)) == (UnixFileMode.UserRead | UnixFileMode.UserWrite), "Saved credentials and settings are readable only by their owner.");
            check(Service(options).PendingRestart, "A new process detects saved configuration awaiting activation.");
            await Reject(() => setup.SaveAsync(input, "http://server:8080", default), "Pending setup cannot overwrite credentials or accounts.");

            var existing = Options("existing-access");
            existing.Access = access;
            var protectedSetup = Service(existing);
            check(!protectedSetup.Authorized(Context()) && !protectedSetup.Authorized(Context(role: "viewer")) && !protectedSetup.Authorized(Context(role: "operator")), "Existing sign-in restricts setup to authenticated administrators.");
            check(protectedSetup.Authorized(Context(role: "admin")), "An existing administrator can configure management without a bootstrap code.");
            await protectedSetup.SaveAsync(input with { Username = "invalid!", Password = null, Origin = "https://attacker.example" }, "http://server:8080", default);
            using var preserved = JsonDocument.Parse(File.ReadAllText(SetupService.ConfigurationPath(existing)));
            check(!preserved.RootElement.TryGetProperty("Access", out _), "Management setup preserves existing authentication configuration without writing an Access override.");

            var demoOptions = Options("demo"); demoOptions.Demo = true;
            var demo = Service(demoOptions);
            var before = requested.Count;
            check(demo.Authorized(Context()), "Demo setup can be explored without credentials.");
            await demo.DiscoverAsync(null, default);
            var demoResult = await demo.SaveAsync(input with { Token = null, Username = null, Password = null }, "http://server:8080", default);
            check(!demoResult.RestartRequired && requested.Count == before && !Directory.Exists(demoOptions.Monitoring.StateDirectory), "Demo discovery and saving perform no network or disk writes.");
            check(!demo.PendingRestart && demo.Available, "Completing a demo does not leave a misleading restart prompt and can be previewed again.");

            var restricted = new SetupService(Options("restricted"), http, clock, "Deployment configuration overrides setup.", () => []);
            check(!restricted.Available && restricted.Configuration("http://server").Issues.Length == 1, "Deployment overrides are visible and disable unsupported setup.");
            await Reject(() => restricted.SaveAsync(input, "http://server:8080", default), "Setup refuses to save settings that deployment overrides would ignore.");

            var rateOptions = Options("rate"); var rateCode = SetupService.CreateCode(rateOptions, clock); var rate = Service(rateOptions);
            for (var i = 0; i < 30; i++) rate.Authorized(Context("wrong"));
            check(!rate.Authorized(Context(rateCode)), "Repeated incorrect codes temporarily throttle bootstrap access.");
            clock.Now += TimeSpan.FromMinutes(1);
            check(rate.Authorized(Context(rateCode)), "Bootstrap throttling recovers after the bounded retry window.");

            foreach (var invalid in new[] { input with { Password = "short" }, input with { Username = "../user" }, input with { AllowHttp = false },
                input with { Origin = "http://other-server:8080" }, input with { Origin = "http://server:8080/path" }, input with { Origin = "http://user:pass@server:8080" } })
                await Reject(() => { SetupService.NewAccess(invalid, "http://server:8080"); return Task.CompletedTask; }, "Invalid accounts, unconfirmed HTTP and unrelated origins are rejected.");
            foreach (var invalid in new string[]?[] { null, [], ["repo:../private"], ["org:owner/path"], ["repo:owner/project?token=secret"], [null!] })
                await Reject(() => { SetupService.ValidateScopes(invalid); return Task.CompletedTask; }, "Invalid or empty allowed scopes cannot reach API requests.");

            using var deniedHttp = new HttpClient(new Handler(_ => new(HttpStatusCode.Unauthorized) { Content = new StringContent("setup-test-token a-long-test-password") }));
            var denied = new SetupService(Options("denied"), deniedHttp, clock, prerequisites: () => []);
            await Reject(() => denied.SaveAsync(input, "http://server:8080", default), "GitHub rejection does not leak API response bodies.");
            check(!Directory.Exists(Path.Combine(directory, "denied")), "GitHub verification failure leaves configuration and token files untouched.");
            using var invalidHttp = new HttpClient(new Handler(_ => new(HttpStatusCode.OK) { Content = new StringContent("{}") }));
            await Reject(() => new SetupService(Options("invalid-response"), invalidHttp, clock, prerequisites: () => []).SaveAsync(input, "http://server:8080", default), "An invalid runner-list response cannot authorize saving setup.");
            using var redirectHttp = new HttpClient(new Handler(_ => new(HttpStatusCode.Redirect) { Headers = { Location = new Uri("https://attacker.example/") } }));
            await Reject(() => new SetupService(Options("redirect"), redirectHttp, clock, prerequisites: () => []).DiscoverAsync(input.Token, default), "Unexpected redirect responses are rejected.");
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
    }
    private sealed class Clock(DateTimeOffset now) : TimeProvider
    {
        internal DateTimeOffset Now { get; set; } = now;
        public override DateTimeOffset GetUtcNow() => Now;
    }
    private sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> callback) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => Task.FromResult(callback(request));
    }
}
