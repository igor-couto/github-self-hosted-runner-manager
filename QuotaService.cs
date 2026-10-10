using System.Net.Http.Headers;
using System.Text.Json;

namespace RunnerRoom;

public sealed class QuotaOptions
{
    public bool Enabled { get; set; }
    public int RefreshSeconds { get; set; } = 300;
    public QuotaProvider[] Providers { get; set; } = [];
}
public sealed class QuotaProvider
{
    public string Name { get; set; } = "Provider";
    public string Type { get; set; } = "json-file";
    public string? TokenFile { get; set; }
    public string? DataFile { get; set; }
    public int MaxAgeSeconds { get; set; } = 900;
}
public sealed record QuotaReading(string Name, string Status, decimal? Used = null, decimal? Limit = null,
    decimal? Remaining = null, string? Unit = null, DateTimeOffset? ObservedAt = null, DateTimeOffset? ResetAt = null, string? Message = null);
public sealed record QuotaSnapshot(bool Enabled, bool Demo, QuotaReading[] Providers);

public sealed class QuotaService(RunnerOptions options) : BackgroundService
{
    private QuotaSnapshot current = new(options.Quotas.Enabled, options.Demo, []);
    public QuotaSnapshot Current => Volatile.Read(ref current);
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await Task.Yield();
        if (options.Demo) { Volatile.Write(ref current, new(true, true, [new("Example provider", "ok", 12, 50, 38, "USD", DateTimeOffset.UtcNow)])); return; }
        if (!options.Quotas.Enabled) return;
        using var client = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false }) { Timeout = TimeSpan.FromSeconds(8) };
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(Math.Clamp(options.Quotas.RefreshSeconds, 60, 3600)));
        try
        {
            do
            {
                var readings = new List<QuotaReading>();
                foreach (var provider in options.Quotas.Providers.Take(10))
                {
                    try { readings.Add(await Read(provider, client, stoppingToken)); }
                    catch (Exception ex) when (ex is HttpRequestException or IOException or UnauthorizedAccessException or JsonException or InvalidOperationException or ArgumentException or OperationCanceledException or KeyNotFoundException or FormatException)
                    {
                        if (stoppingToken.IsCancellationRequested) return;
                        // Never return provider error bodies, file paths or tokens to the browser.
                        readings.Add(new(provider.Name, "unavailable", Message: "No current reading. Check the configured integration and its credentials."));
                    }
                }
                Volatile.Write(ref current, new(true, false, readings.ToArray()));
            } while (await timer.WaitForNextTickAsync(stoppingToken));
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
    }
    internal static async Task<QuotaReading> Read(QuotaProvider provider, HttpClient client, CancellationToken cancellation)
    {
        string json;
        if (provider.Type == "openrouter")
        {
            var token = LocalRunnerReader.ReadSmall(provider.TokenFile ?? "", 8192)?.Trim();
            if (string.IsNullOrWhiteSpace(token)) throw new InvalidOperationException();
            using var request = new HttpRequestMessage(HttpMethod.Get, "https://openrouter.ai/api/v1/key");
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellation);
            response.EnsureSuccessStatusCode();
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellation); timeout.CancelAfter(TimeSpan.FromSeconds(8));
            await using var stream = await response.Content.ReadAsStreamAsync(timeout.Token);
            using var buffer = new MemoryStream(); var bytes = new byte[4096]; int count;
            while ((count = await stream.ReadAsync(bytes, timeout.Token)) > 0)
            { if (buffer.Length + count > 16384) throw new JsonException(); buffer.Write(bytes, 0, count); }
            json = System.Text.Encoding.UTF8.GetString(buffer.ToArray());
        }
        else if (provider.Type == "json-file") json = LocalRunnerReader.ReadSmall(provider.DataFile ?? "", 16384) ?? throw new IOException();
        else throw new InvalidOperationException();
        return Parse(provider, json, DateTimeOffset.UtcNow);
    }
    internal static QuotaReading Parse(QuotaProvider provider, string json, DateTimeOffset now)
    {
        using var document = JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = 8 });
        var root = document.RootElement;
        decimal? Number(string key) => !root.TryGetProperty(key, out var node) || node.ValueKind == JsonValueKind.Null ? null :
            node.TryGetDecimal(out var value) && value >= 0 ? value : throw new JsonException();
        if (provider.Type == "openrouter")
        {
            root = root.GetProperty("data"); var limit = Number("limit"); var remaining = Number("limit_remaining");
            if (limit is not null && remaining > limit) throw new JsonException();
            return new(provider.Name, "ok", limit is not null && remaining is not null ? limit - remaining : null, limit, remaining, "USD", now,
                Message: limit is null ? "No key spending cap is configured. This is not an account balance." : "Current key spending cap. Usage across other keys is not included.");
        }
        if (provider.Type != "json-file") throw new JsonException();
        var used = Number("used"); var cap = Number("limit"); var left = Number("remaining");
        if (used is null && cap is null && left is null || cap is not null && (left > cap || used > cap)) throw new JsonException();
        if (used is not null && cap is not null && left is not null && left != cap - used) throw new JsonException();
        var unit = root.GetProperty("unit").GetString();
        if (string.IsNullOrWhiteSpace(unit) || unit.Length > 24 || unit.Any(char.IsControl)) throw new JsonException();
        var at = root.GetProperty("observedAt").GetDateTimeOffset();
        DateTimeOffset? reset = root.TryGetProperty("resetAt", out var r) && r.ValueKind != JsonValueKind.Null ? r.GetDateTimeOffset() : null;
        if (at > now.AddMinutes(1)) throw new JsonException();
        var stale = now - at > TimeSpan.FromSeconds(Math.Clamp(provider.MaxAgeSeconds, 60, 86400));
        return new(provider.Name, stale ? "stale" : "ok", used, cap, left, unit, at, reset, stale ? "Adapter data is out of date; these are the last supplied values." : null);
    }
}
