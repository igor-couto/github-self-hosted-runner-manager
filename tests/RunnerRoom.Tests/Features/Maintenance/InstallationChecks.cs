using System.Net;
using System.Text.Json;
using RunnerRoom.Features.Maintenance;

namespace RunnerRoom.Tests.Features.Maintenance;

internal static class InstallationChecks
{
    internal static async Task Run(Action<bool, string> check)
    {
        var directory = Path.Combine(Path.GetTempPath(), "runner-room-update-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(directory, "inbox"));
        var clock = new Clock();
        var calls = 0;
        var responseCode = HttpStatusCode.OK;
        using var http = new HttpClient(new Handler(() =>
        {
            calls++;
            var names = new[] { "install.sh", "runner-room-linux-x64.tar.gz", "runner-room-linux-x64.tar.gz.sha256", "runner-room-linux-arm.tar.gz",
                "runner-room-linux-arm.tar.gz.sha256", "runner-room-linux-arm64.tar.gz", "runner-room-linux-arm64.tar.gz.sha256" };
            return new(responseCode) { Content = new StringContent(JsonSerializer.Serialize(new { tag_name = "v1.1.0", draft = false, prerelease = false,
                assets = names.Select(name => new { name, state = "uploaded", size = 100 }) })) };
        }));
        using var updates = new AppUpdateService(new(), false, "1.0.0", http, clock);
        var guards = 0;
        var installation = new AppInstallationService(false, updates, directory, clock, () => null, (_, write) => { guards++; write(); });
        async Task Reject(Func<Task> action, string description)
        {
            try { await action(); check(false, description); }
            catch (AppInstallationException) { check(true, description); }
        }
        var request = Path.Combine(directory, "inbox", "request");
        var status = Path.Combine(directory, "status.json");
        void Status(string id, string state, DateTimeOffset? at = null) => File.WriteAllText(status,
            JsonSerializer.Serialize(new { requestId = id, state, message = "Local helper update status.", updatedAt = at ?? clock.Now, targetVersion = "1.1.0" }));
        try
        {
            check(installation.Snapshot() is { Supported: false, State: "unsupported" }, "Old installations explain the one-time installer upgrade.");
            File.WriteAllText(Path.Combine(directory, "capability.json"), "{\"protocol\":1}");
            check(installation.Snapshot() is { Supported: true, State: "idle" } && calls == 0, "Reading installation availability never checks GitHub or writes files.");
            var queued = await installation.InstallAsync();
            var bytes = await File.ReadAllBytesAsync(request);
            check(queued is { State: "queued", RequestId.Length: 32 } && bytes.Length == 33 && bytes[^1] == '\n' &&
                System.Text.Encoding.ASCII.GetString(bytes)[..32] == queued.RequestId && guards == 1,
                "A verified update writes only an atomic random request identifier, guarded against active runners.");
            check(calls == 1 && updates.Current is { UpdateAvailable: true, Stale: false }, "Requesting installation first verifies a complete newer stable release.");
            await Reject(async () => await installation.InstallAsync(), "A queued request cannot be submitted twice.");
            check(calls == 1 && guards == 1, "Duplicate requests neither download nor invoke runner management.");
            clock.Now = clock.Now.AddMinutes(3);
            check(installation.Snapshot().State == "failed", "An unclaimed request becomes an actionable stalled result.");
            await Reject(async () => await installation.InstallAsync(), "A stale queued request remains reserved instead of being executed twice.");
            Status(queued.RequestId!, "checking"); File.Delete(request);
            check(installation.Snapshot() is { State: "checking", TargetVersion: "1.1.0" }, "The helper's acknowledged status survives consumption of the request file.");
            await Reject(async () => await installation.InstallAsync(), "Helper activity prevents a duplicate after request consumption.");
            clock.Now = clock.Now.AddMinutes(31);
            check(installation.Snapshot().State == "failed", "Stale active helper state reports recovery instructions.");
            await Reject(async () => await installation.InstallAsync(), "Stale active status does not release the installer reservation.");
            Status(queued.RequestId!, "rolled_back");
            check(installation.Snapshot().State == "rolled_back", "Rollback is presented as its own terminal result.");
            File.WriteAllText(Path.Combine(directory, "processing"), "");
            await Reject(async () => await installation.InstallAsync(), "A helper claim marker blocks new requests before status publication.");
            File.Delete(Path.Combine(directory, "processing"));
            clock.Now = DateTimeOffset.UtcNow;
            Status(queued.RequestId!, "succeeded");
            var restarted = new AppInstallationService(false, updates, directory, clock, () => null);
            check(restarted.Snapshot() is { State: "succeeded", RequestId: not null }, "A new application process reads the helper's persisted completion.");
            foreach (var invalid in new[] { "{}", "{\"requestId\":2}", "{\"requestId\":\"" + queued.RequestId + "\",\"state\":\"restarting\",\"message\":\"m\",\"updatedAt\":\"invalid\"}", new string('x', 8193) })
            {
                File.WriteAllText(status, invalid);
                check(installation.Snapshot() is { Supported: false }, "Malformed or oversized helper state is unavailable instead of throwing or claiming success.");
                await Reject(async () => await installation.InstallAsync(), "Invalid helper state cannot permit another update.");
            }
            File.Delete(status);
            responseCode = HttpStatusCode.ServiceUnavailable;
            clock.Now = clock.Now.AddMinutes(2);
            await Reject(async () => await installation.InstallAsync(), "A stale previously available release cannot authorize installation after a failed refresh.");
            check(!File.Exists(request) && guards == 1, "Failed release verification leaves the installer and runner state untouched.");
            responseCode = HttpStatusCode.OK;
            clock.Now = clock.Now.AddMinutes(1);
            var results = await Task.WhenAll(Enumerable.Range(0, 3).Select(async _ =>
            {
                try { await installation.InstallAsync(); return true; } catch (AppInstallationException) { return false; }
            }));
            check(results.Count(result => result) == 1 && File.Exists(request), "Concurrent installation clicks produce exactly one request.");
            File.Delete(request);
            if (OperatingSystem.IsLinux())
            {
                var outside = Path.Combine(directory, "outside"); File.WriteAllText(outside, "never alter");
                File.CreateSymbolicLink(request, outside);
                check(!installation.Snapshot().Supported, "Symbolic-link request files fail closed.");
                await Reject(async () => await installation.InstallAsync(), "An update cannot overwrite a symbolic-link destination.");
                check(File.ReadAllText(outside) == "never alter", "Rejected symlinks leave their target untouched.");
                File.Delete(request);
            }
            var before = Directory.GetFiles(directory, "*", SearchOption.AllDirectories).Length;
            var previousCalls = calls;
            var demo = new AppInstallationService(true, updates, directory, clock);
            check(demo.Snapshot() is { Demo: true, State: "idle", Supported: true }, "Demo exposes the same install controls.");
            check((await demo.InstallAsync()).State == "queued", "Demo installation begins with an explicit preview state.");
            await Reject(async () => await demo.InstallAsync(), "Demo also prevents double submissions.");
            clock.Now = clock.Now.AddSeconds(4);
            check(demo.Snapshot() is { State: "succeeded", Demo: true } && calls == previousCalls &&
                Directory.GetFiles(directory, "*", SearchOption.AllDirectories).Length == before, "Demo completes without network calls, installer requests or state writes.");
            using var disabled = new AppUpdateService(new() { Enabled = false }, false, "1.0.0", http, clock);
            await disabled.CheckNowAsync(); var count = calls; await disabled.CheckNowAsync();
            check(disabled.Current.UpdateAvailable == true && calls == count, "Manual checks work when scheduled checks are disabled and are rate limited.");
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    private sealed class Clock : TimeProvider
    {
        internal DateTimeOffset Now = DateTimeOffset.UtcNow;
        public override DateTimeOffset GetUtcNow() => Now;
    }
    private sealed class Handler(Func<HttpResponseMessage> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => Task.FromResult(send());
    }
}
