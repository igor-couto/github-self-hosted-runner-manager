using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;

namespace RunnerRoom;

// Only GitHub.com's fixed API origin receives the optional token, never a URL from runner metadata.
public sealed class GitHubRunnerClient(HttpClient http, GitHubOptions options, TimeProvider? timeProvider = null)
{
    private readonly TimeProvider clock = timeProvider ?? TimeProvider.System;
    private readonly Dictionary<string, (DateTimeOffset Expires, GitHubRunnerInfo Info)> cache = new();
    private string? cachedToken;

    internal static string? Endpoint(RunnerInfo runner)
    {
        if (runner.AgentId is not > 0 || !Uri.TryCreate(runner.GitHubUrl, UriKind.Absolute, out var uri) ||
            uri.Host != "github.com" || uri.Scheme != "https" || !uri.IsDefaultPort || uri.UserInfo.Length > 0) return null;
        var scope = LocalRunnerReader.ParseScope(runner.GitHubUrl);
        return scope.Repository is not null ? $"repos/{scope.Repository}/actions/runners/{runner.AgentId}" :
            scope.Organization is not null ? $"orgs/{scope.Organization}/actions/runners/{runner.AgentId}" : null;
    }

    internal async Task<RunnerInfo[]> EnrichAsync(RunnerInfo[] runners)
    {
        var token = options.Token;
        if (string.IsNullOrWhiteSpace(token) && !string.IsNullOrWhiteSpace(options.TokenFile))
            token = LocalRunnerReader.ReadSmall(options.TokenFile, 8192)?.Trim();
        if (string.IsNullOrWhiteSpace(token))
            return runners.Select(r => r with { GitHub = new(string.IsNullOrWhiteSpace(options.TokenFile) ? "not_configured" : "unknown",
                Message: string.IsNullOrWhiteSpace(options.TokenFile) ? "Add a read-only GitHub token to check connectivity and labels." : "The configured GitHub token file could not be read.") }).ToArray();
        token = token.Trim();
        if (cachedToken != token) { cache.Clear(); cachedToken = token; }
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        using var concurrency = new SemaphoreSlim(4);
        var pending = new Dictionary<string, Task<GitHubRunnerInfo>>();
        var fetched = new HashSet<string>();
        foreach (var runner in runners)
        {
            var endpoint = Endpoint(runner);
            if (endpoint is null || pending.ContainsKey(endpoint)) continue;
            if (cache.TryGetValue(endpoint, out var cached) && cached.Expires > clock.GetUtcNow())
                pending[endpoint] = Task.FromResult(cached.Info);
            else
            {
                fetched.Add(endpoint);
                pending[endpoint] = FetchAsync(endpoint, token, concurrency, deadline.Token);
            }
        }
        await Task.WhenAll(pending.Values);
        foreach (var endpoint in fetched) cache[endpoint] = (clock.GetUtcNow().AddSeconds(60), await pending[endpoint]);
        foreach (var key in cache.Keys.Except(pending.Keys).ToArray()) cache.Remove(key);
        return runners.Select(r =>
        {
            var info = Endpoint(r) is { } endpoint ? pending[endpoint].Result :
                new GitHubRunnerInfo("unknown", Message: "A GitHub.com registration URL and runner ID are required.");
            return r with { GitHub = info, OperatingSystem = r.OperatingSystem ?? info.OperatingSystem, Version = r.Version ?? info.Version,
                Architecture = r.Architecture ?? info.Labels?.FirstOrDefault(l => l is "X64" or "ARM64" or "ARM" or "X86") };
        }).ToArray();
    }

    private async Task<GitHubRunnerInfo> FetchAsync(string endpoint, string token, SemaphoreSlim concurrency, CancellationToken cancellation)
    {
        var entered = false;
        try
        {
            await concurrency.WaitAsync(cancellation);
            entered = true;
            using var request = new HttpRequestMessage(HttpMethod.Get, "https://api.github.com/" + endpoint);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            request.Headers.UserAgent.ParseAdd("RunnerRoom/1.0");
            request.Headers.Accept.ParseAdd("application/vnd.github+json");
            request.Headers.Add("X-GitHub-Api-Version", "2022-11-28");
            using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellation);
            var at = clock.GetUtcNow();
            if (!response.IsSuccessStatusCode) return new("unknown", CheckedAt: at, Message: response.StatusCode switch
            {
                HttpStatusCode.Unauthorized => "GitHub rejected the token.",
                HttpStatusCode.Forbidden or HttpStatusCode.TooManyRequests => "GitHub access was denied or its API limit was reached.",
                HttpStatusCode.NotFound => "Registration not found or the token cannot access it.",
                _ => "GitHub status is temporarily unavailable."
            });
            await using var stream = await response.Content.ReadAsStreamAsync(cancellation);
            var bytes = new byte[65537];
            var count = await stream.ReadAtLeastAsync(bytes, bytes.Length, false, cancellation);
            if (count == bytes.Length) return new("unknown", CheckedAt: at, Message: "GitHub returned an unexpected response.");
            using var json = JsonDocument.Parse(bytes.AsMemory(0, count));
            var root = json.RootElement;
            if (root.ValueKind != JsonValueKind.Object) throw new JsonException();
            if (!root.TryGetProperty("id", out var id) || id.ValueKind != JsonValueKind.Number || !id.TryGetInt64(out var number) ||
                number.ToString(System.Globalization.CultureInfo.InvariantCulture) != endpoint.Split('/')[^1]) throw new JsonException();
            string? Text(string key) => root.TryGetProperty(key, out var item) && item.ValueKind == JsonValueKind.String ? LocalRunnerReader.Text(item.GetString()) : null;
            var status = Text("status") is "online" ? "online" : Text("status") is "offline" ? "offline" : "unknown";
            bool? busy = root.TryGetProperty("busy", out var activity) && activity.ValueKind is JsonValueKind.True or JsonValueKind.False ? activity.GetBoolean() : null;
            var labels = root.TryGetProperty("labels", out var list) && list.ValueKind == JsonValueKind.Array ? list.EnumerateArray()
                .Where(v => v.ValueKind == JsonValueKind.Object && v.TryGetProperty("name", out var n) && n.ValueKind == JsonValueKind.String)
                .Select(v => LocalRunnerReader.Text(v.GetProperty("name").GetString())).OfType<string>().Take(100).ToArray() : null;
            return new(status, busy, labels, at, OperatingSystem: Text("os"), Version: Text("version"));
        }
        catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException or JsonException or IOException or FormatException)
        { return new("unknown", CheckedAt: clock.GetUtcNow(), Message: "Could not verify GitHub status. Will retry automatically."); }
        finally { if (entered) concurrency.Release(); }
    }
}
