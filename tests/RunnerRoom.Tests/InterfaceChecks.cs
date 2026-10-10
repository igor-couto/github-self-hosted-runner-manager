using System.Net;
using System.Text.Json;
using RunnerRoom;

internal static class InterfaceChecks
{
    internal static async Task Run(Action<bool, string> check)
    {
        var now = DateTimeOffset.UtcNow;
        var provider = new QuotaProvider { Name = "Test", Type = "openrouter" };
        var open = QuotaService.Parse(provider, """{"data":{"limit":50,"limit_remaining":35,"usage":200,"label":"DO-NOT-EXPOSE"}}""", now);
        check(open is { Used: 15, Limit: 50, Remaining: 35, Unit: "USD", Status: "ok" }, "OpenRouter quota uses the current key cap rather than lifetime usage.");
        check(!JsonSerializer.Serialize(open).Contains("DO-NOT-EXPOSE"), "Provider key labels are never exposed.");
        check(QuotaService.Parse(provider, """{"data":{"limit":null,"limit_remaining":null}}""", now) is { Limit: null, Remaining: null }, "Unlimited/missing quotas stay unknown rather than zero.");
        provider = new() { Type = "json-file", MaxAgeSeconds = 120 };
        var adapter = JsonSerializer.Serialize(new { used = 4, limit = 10, remaining = 6, unit = "requests", observedAt = now.AddSeconds(-30), resetAt = now.AddDays(1) });
        check(QuotaService.Parse(provider, adapter, now) is { Status: "ok", Remaining: 6 }, "Adapter readings retain their unit and timestamp.");
        check(QuotaService.Parse(provider, adapter, now.AddMinutes(5)).Status == "stale", "Old adapter readings are explicitly stale.");
        foreach (var json in new[] { "{}", "[]", """{"used":-1,"unit":"USD"}""", JsonSerializer.Serialize(new {used=4,limit=10,remaining=9,unit="requests",observedAt=now}), JsonSerializer.Serialize(new {used=4,unit="requests",observedAt=now.AddDays(1)}) })
        {
            bool failed = false; try { QuotaService.Parse(provider, json, now); } catch (Exception ex) when (ex is JsonException or InvalidOperationException or KeyNotFoundException or FormatException) { failed = true; }
            check(failed, "Malformed, inconsistent or future-dated quota data must be rejected.");
        }
        var path = Path.Combine(Path.GetTempPath(), "rr-quota-" + Guid.NewGuid().ToString("N"));
        try
        {
            await File.WriteAllTextAsync(path, adapter); provider.DataFile = path;
            using var http = new HttpClient(new FakeQuota());
            check((await QuotaService.Read(provider, http, default)).Unit == "requests", "The local adapter reads bounded JSON without executing commands.");
            await File.WriteAllTextAsync(path, "test-private-key");
            var p = new QuotaProvider { Type = "openrouter", TokenFile = path };
            check((await QuotaService.Read(p, http, default)).Remaining == 8, "Native integration sends credentials only to its fixed HTTPS origin.");
            var redactor = new LogRedactor(new() { Quotas = new() { Providers = [p] } });
            check(!redactor.Redact("message test-private-key").Contains("test-private-key"), "Provider credentials are included in diagnostic masking.");
        }
        finally { File.Delete(path); }
        var job = new HistoricalJob("runner", "Build", now.AddMinutes(-1), now, "Succeeded");
        check(job.Id == (job with { StartedAt = null }).Id && job.Id != (job with { RunnerId = "other" }).Id, "Job links survive late start matching and distinguish runners.");
        check(RoomCommand.Safe("name\u001b[31m\n\u202epayload") == "name[31mpayload", "Terminal data cannot inject control or bidi sequences.");
        foreach (var url in new[] { "file:///etc/passwd", "https://user:pass@example.com", "https://example.com/other", "https://example.com/?token=secret" })
        { bool denied = false; try { using var c = new DashboardClient(url); } catch (ArgumentException) { denied = true; } check(denied, "Clients accept only a credential-free HTTP(S) origin."); }
        using var remote = new DashboardClient("http://192.0.2.1:8080");
        bool refused = false; try { await remote.Login("admin", "secret"); } catch (InvalidOperationException) { refused = true; }
        check(refused, "Remote passwords must be refused before making a plaintext HTTP request.");
    }
    private sealed class FakeQuota : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (request.RequestUri?.ToString() != "https://openrouter.ai/api/v1/key" || request.Headers.Authorization?.ToString() != "Bearer test-private-key") throw new InvalidOperationException("Unexpected credential destination.");
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("""{"data":{"limit":10,"limit_remaining":8}}""") });
        }
    }
}
