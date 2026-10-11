using System.Net;
using System.Text;
using System.Text.Json;
using RunnerRoom;

internal static class UpdateChecks
{
    internal static async Task Run(Action<bool, string> check)
    {
        foreach (var (installed, latest, status, available) in new (string, string, string, bool?)[]
        {
            ("0.9.0", "0.10.0", "update_available", true), ("0.10.0", "0.9.0", "ahead", false),
            ("1.0.0", "1.0.0", "up_to_date", false), ("1.0.0+abc123", "1.0.0", "up_to_date", false),
            ("1.0.0-rc.1", "1.0.0", "update_available", true), ("1.0.0", "1.0.0-rc.1", "ahead", false),
            ("1.0.0-rc.2", "1.0.0-rc.10", "update_available", true),
            ("1.0.0-alpha", "1.0.0-beta", "update_available", true),
            ("1.0.0-1", "1.0.0-alpha", "update_available", true),
            ("1.0.0-alpha", "1.0.0-alpha.1", "update_available", true),
            ("999999999999999999999999.0.0", "1000000000000000000000000.0.0", "update_available", true),
            ("0.0.0-dev", "0.9.0", "development", null), ("0.0.0", "0.9.0", "development", null),
            ("1.0.0-dev.5", "0.9.0", "development", null), ("unknown", "0.9.0", "development", null),
            ("1.01.0", "1.1.0", "development", null), ("1.0.0-01", "1.0.0", "development", null)
        })
        {
            var result = AppUpdateService.Evaluate(installed, latest);
            check(result.Status == status && result.Available == available,
                $"Application version precedence must compare {installed} with {latest} without lexical, prerelease or unknown-version mistakes.");
        }

        var clock = new TestClock();
        HttpResponseMessage response = Success();
        var requests = 0;
        using var http = new HttpClient(new Handler((request, _) =>
        {
            requests++;
            check(request.RequestUri?.AbsoluteUri == AppUpdateService.LatestReleaseEndpoint && request.Method == HttpMethod.Get,
                "Application checks use only the fixed public release endpoint.");
            check(request.Headers.Authorization is null && !request.Headers.Contains("Cookie"),
                "Application update checks never use runner, user or GitHub integration credentials.");
            check(request.Headers.UserAgent.Count > 0 && request.Headers.Contains("X-GitHub-Api-Version"),
                "Public GitHub checks declare their client and API version.");
            return Task.FromResult(response);
        }));
        using var updates = new AppUpdateService(new(), false, "0.9.0", http, clock);
        check(updates.Current is { Status: "checking", UpdateAvailable: null, CheckedAt: null },
            "Before the first release check, availability is unknown rather than up to date.");
        await updates.CheckAsync();
        check(updates.Current is { Status: "update_available", UpdateAvailable: true, LatestVersion: "0.10.0", Stale: false } &&
            updates.Current.CheckedAt == clock.GetUtcNow(), "A successful check identifies a newer complete release.");
        check(updates.Current.ReleaseUrl == "https://github.com/igor-couto/github-self-hosted-runner-manager/releases/tag/v0.10.0",
            "Release links are derived from the validated tag; untrusted response URLs cannot redirect users elsewhere.");
        for (var i = 0; i < 100; i++) _ = updates.Current;
        check(requests == 1, "Polling dashboard snapshots must not fan out to GitHub API requests.");
        var goodTime = updates.Current.CheckedAt;
        clock.Now = clock.Now.AddHours(6);
        response = new(HttpStatusCode.TooManyRequests) { Content = new StringContent("private-infrastructure-error") };
        await updates.CheckAsync();
        check(updates.Current is { Status: "unavailable", Stale: true, LatestVersion: "0.10.0", UpdateAvailable: true } &&
            updates.Current.CheckedAt == goodTime && updates.Current.LastAttemptAt == clock.GetUtcNow(),
            "Rate limits retain previous release information but mark it unavailable and stale with the failed attempt time.");
        check(!JsonSerializer.Serialize(updates.Current).Contains("private-infrastructure-error"),
            "Remote error response bodies are not exposed in update status.");
        response = Success("v0.9.0");
        await updates.CheckAsync();
        check(updates.Current is { Status: "up_to_date", UpdateAvailable: false, Stale: false } && updates.Current.CheckedAt == clock.GetUtcNow(),
            "A successful retry clears stale state and can report an equal version.");
        response = new(HttpStatusCode.Redirect) { Headers = { Location = new("https://untrusted.invalid/releases") } };
        await updates.CheckAsync();
        check(updates.Current is { Status: "unavailable", Stale: true }, "Redirect responses are never treated as successful update checks.");

        foreach (var invalid in new[]
        {
            "{}", "[]", "not json", Release(draft: true), Release(prerelease: true), Release("v1.0.0-rc.1"),
            Release("v1.0.0+meta"), Release("../evil"), Release("v1.02.0"), Release(missingAsset: true), Release(uploading: true), Release(emptyAsset: true)
        })
        {
            response = new(HttpStatusCode.OK) { Content = new StringContent(invalid) };
            await updates.CheckAsync();
            check(updates.Current is { Status: "unavailable", Stale: true, LatestVersion: "0.9.0" },
                "Malformed, prerelease, draft or incomplete release data cannot replace the last successful stable release.");
        }

        using var initialHttp = new HttpClient(new Handler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound))));
        using var firstFailure = new AppUpdateService(new(), false, "1.0.0", initialHttp);
        await firstFailure.CheckAsync();
        check(firstFailure.Current is { Status: "unavailable", UpdateAvailable: null, LatestVersion: null, CheckedAt: null },
            "An initial check failure must not claim there are no updates.");

        response = Success();
        using var development = new AppUpdateService(new(), false, "0.0.0-dev", http, clock);
        await development.CheckAsync();
        check(development.Current is { Status: "development", UpdateAvailable: null, LatestVersion: "0.10.0", CheckedAt: not null },
            "Development builds may show the latest stable release but cannot claim an installed-version comparison.");
        var beforeDisabled = requests;
        using var disabled = new AppUpdateService(new() { Enabled = false }, false, "0.9.0", http);
        using var demo = new AppUpdateService(new(), true, "0.0.0-dev", http);
        await disabled.StartAsync(default); await disabled.CheckAsync();
        await demo.StartAsync(default); await demo.CheckAsync();
        check(requests == beforeDisabled && disabled.Current is { Enabled: false, Status: "disabled", UpdateAvailable: null },
            "Disabling update checks makes no background or direct network requests.");
        check(demo.Current is { Demo: true, Status: "demo", CurrentVersion: "0.0.0-dev", LatestVersion: null, UpdateAvailable: null } && requests == beforeDisabled,
            "Demo mode keeps the real installed version and explicitly pauses release checks without fake availability.");
        using var lowInterval = new AppUpdateService(new() { CheckIntervalHours = -10 }, false, "1.0.0", http);
        using var highInterval = new AppUpdateService(new() { CheckIntervalHours = int.MaxValue }, false, "1.0.0", http);
        check(lowInterval.Interval == TimeSpan.FromHours(1) && highInterval.Interval == TimeSpan.FromHours(168),
            "Invalid configured check intervals stay within one hour and one week.");

        var oversized = new byte[AppUpdateService.MaximumResponseBytes + 1];
        response = new(HttpStatusCode.OK) { Content = new ByteArrayContent(oversized) };
        await updates.CheckAsync();
        check(updates.Current.Status == "unavailable", "Oversized declared response bodies are rejected.");
        response = new(HttpStatusCode.OK) { Content = new StreamContent(new NonSeekableStream(oversized)) };
        await updates.CheckAsync();
        check(updates.Current.Status == "unavailable", "Response size is also bounded when Content-Length is missing.");

        using var timeoutHttp = new HttpClient(new Handler(async (_, token) =>
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, token);
            return Success();
        }));
        using var timed = new AppUpdateService(new(), false, "0.9.0", timeoutHttp, requestTimeout: TimeSpan.FromMilliseconds(20));
        await timed.CheckAsync();
        check(timed.Current is { Status: "unavailable", CheckedAt: null, UpdateAvailable: null } && timed.Current.Message!.Contains("timed out"),
            "Slow response headers are cancelled within the request deadline.");
        using var bodyHttp = new HttpClient(new Handler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            { Content = new StreamContent(new BlockingStream()) })));
        using var timedBody = new AppUpdateService(new(), false, "0.9.0", bodyHttp, requestTimeout: TimeSpan.FromMilliseconds(20));
        await timedBody.CheckAsync();
        check(timedBody.Current is { Status: "unavailable", CheckedAt: null }, "The deadline includes streaming the release response body.");

        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var finish = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var concurrentRequests = 0;
        using var slowHttp = new HttpClient(new Handler(async (_, token) =>
        {
            concurrentRequests++; started.TrySetResult(); await finish.Task.WaitAsync(token); return Success();
        }));
        using var serialized = new AppUpdateService(new(), false, "0.9.0", slowHttp);
        var pending = serialized.CheckAsync();
        await started.Task;
        await serialized.CheckAsync();
        finish.TrySetResult(); await pending;
        check(concurrentRequests == 1, "Overlapping application update checks are coalesced into one request.");
    }

    private static HttpResponseMessage Success(string tag = "v0.10.0") => new(HttpStatusCode.OK) { Content = new StringContent(Release(tag)) };
    private static string Release(string tag = "v0.10.0", bool draft = false, bool prerelease = false,
        bool missingAsset = false, bool uploading = false, bool emptyAsset = false)
    {
        var names = new[] { "install.sh", "runner-room-linux-x64.tar.gz", "runner-room-linux-x64.tar.gz.sha256", "runner-room-linux-arm.tar.gz",
            "runner-room-linux-arm.tar.gz.sha256", "runner-room-linux-arm64.tar.gz", "runner-room-linux-arm64.tar.gz.sha256" };
        return JsonSerializer.Serialize(new
        {
            tag_name = tag, draft, prerelease, html_url = "https://untrusted.invalid/evil",
            assets = names.Take(missingAsset ? 6 : 7).Select(name => new { name, state = uploading ? "new" : "uploaded", size = emptyAsset ? 0 : 100 })
        });
    }

    private sealed class Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token) => send(request, token);
    }
    private sealed class TestClock : TimeProvider
    {
        internal DateTimeOffset Now = new(2026, 10, 10, 12, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => Now;
    }
    private sealed class NonSeekableStream(byte[] bytes) : MemoryStream(bytes)
    {
        public override bool CanSeek => false;
    }
    private sealed class BlockingStream : Stream
    {
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        { await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken); return 0; }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override void Flush() => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
