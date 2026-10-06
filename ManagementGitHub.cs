using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace RunnerRoom;

public sealed record RunnerRelease(string Version, string Architecture, string Url, string Sha256);
public sealed class ManagementGitHub(HttpClient http, RunnerOptions options)
{
    internal void Allow(string scope)
    {
        scope = ManagementValidation.Scope(scope);
        if (!options.Management.AllowedScopes.Select(ManagementValidation.Scope).Contains(scope))
            throw new ManagementException("This scope is not in Management.AllowedScopes on the server.");
    }
    internal async Task<JsonElement> Send(HttpMethod method, string endpoint, object? body, CancellationToken token, bool anonymous = false)
    {
        if (endpoint.StartsWith('/') || endpoint.Contains("..") || endpoint.Contains("://")) throw new ManagementException("Invalid GitHub API path.");
        using var request = new HttpRequestMessage(method, "https://api.github.com/" + endpoint);
        request.Headers.UserAgent.ParseAdd("RunnerRoom/1.0");
        request.Headers.Accept.ParseAdd("application/vnd.github+json");
        request.Headers.Add("X-GitHub-Api-Version", "2022-11-28");
        if (!anonymous)
        {
            var secret = string.IsNullOrWhiteSpace(options.Management.TokenFile) ? null : LocalRunnerReader.ReadSmall(options.Management.TokenFile, 8192)?.Trim();
            if (string.IsNullOrWhiteSpace(secret)) throw new ManagementException("Set Management.TokenFile to a readable GitHub token with the required write permissions.");
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", secret);
        }
        if (body is not null) request.Content = JsonContent.Create(body);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(TimeSpan.FromSeconds(30));
        using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, deadline.Token);
        if (!response.IsSuccessStatusCode) throw new ManagementException(response.StatusCode switch
        {
            HttpStatusCode.Unauthorized => "GitHub rejected the management token. Check its expiry and permissions.",
            HttpStatusCode.Forbidden => "GitHub denied this operation. Check token permissions, organization policy and API rate limits.",
            HttpStatusCode.NotFound => "GitHub could not find this resource, or the token cannot access it.",
            HttpStatusCode.Conflict => "GitHub reports a conflict. Refresh the runner or workflow state and retry.",
            HttpStatusCode.UnprocessableEntity => "GitHub rejected these settings. Check names, group membership, labels and workflow status.",
            HttpStatusCode.TooManyRequests => "GitHub API rate limit reached. Wait before retrying.",
            _ => $"GitHub returned HTTP {(int)response.StatusCode}. Refresh state before retrying; an operation may have reached GitHub."
        });
        if (response.StatusCode == HttpStatusCode.NoContent) return default;
        await using var stream = await response.Content.ReadAsStreamAsync(deadline.Token);
        var bytes = new byte[2 * 1024 * 1024 + 1];
        var count = await stream.ReadAtLeastAsync(bytes, bytes.Length, false, deadline.Token);
        if (count == bytes.Length) throw new ManagementException("GitHub response exceeded the size limit.");
        if (count == 0) return default;
        using var json = JsonDocument.Parse(bytes.AsMemory(0, count));
        return json.RootElement.Clone();
    }
    internal async Task<string> RegistrationToken(string scope, CancellationToken token)
    {
        Allow(scope);
        var json = await Send(HttpMethod.Post, ManagementValidation.ApiScope(scope) + "/actions/runners/registration-token", new { }, token);
        return json.GetProperty("token").GetString() ?? throw new ManagementException("GitHub did not return a registration token.");
    }
    internal async Task<RunnerRelease> Latest(CancellationToken token)
    {
        var json = await Send(HttpMethod.Get, "repos/actions/runner/releases/latest", null, token, true);
        return ParseRelease(json, System.Runtime.InteropServices.RuntimeInformation.OSArchitecture.ToString().ToLowerInvariant());
    }
    internal static RunnerRelease ParseRelease(JsonElement json, string arch)
    {
        if (arch is not ("x64" or "arm" or "arm64")) throw new ManagementException("Runner management supports Linux x64, ARM and ARM64.");
        var tag = json.GetProperty("tag_name").GetString() ?? "";
        if (!Regex.IsMatch(tag, @"^v[0-9]+\.[0-9]+\.[0-9]+$")) throw new ManagementException("GitHub returned an unexpected runner version.");
        var name = $"actions-runner-linux-{arch}-{tag[1..]}.tar.gz";
        var asset = json.GetProperty("assets").EnumerateArray().FirstOrDefault(a => a.GetProperty("name").GetString() == name);
        if (asset.ValueKind == JsonValueKind.Undefined) throw new ManagementException("The latest release does not contain this architecture.");
        var url = asset.GetProperty("browser_download_url").GetString();
        if (url != $"https://github.com/actions/runner/releases/download/{tag}/{name}") throw new ManagementException("Unexpected runner download URL.");
        var digest = asset.TryGetProperty("digest", out var d) ? d.GetString() : null;
        if (digest is null || !Regex.IsMatch(digest, "^sha256:[a-fA-F0-9]{64}$")) throw new ManagementException("The release has no SHA-256 digest. Refusing an unverified download.");
        return new(tag[1..], arch, url, digest[7..].ToLowerInvariant());
    }
    internal async Task<bool> Busy(ManagedRunner runner, CancellationToken token)
    {
        Allow(runner.Settings.Scope);
        if (runner.AgentId is not > 0) throw new ManagementException("Runner registration ID is unavailable. Re-import the installation.");
        var json = await Send(HttpMethod.Get, ManagementValidation.ApiScope(runner.Settings.Scope) + $"/actions/runners/{runner.AgentId}", null, token);
        return json.GetProperty("busy").GetBoolean();
    }
    internal async Task Unregister(ManagedRunner runner, CancellationToken token)
    {
        Allow(runner.Settings.Scope);
        if (runner.AgentId is not > 0) return;
        await Send(HttpMethod.Delete, ManagementValidation.ApiScope(runner.Settings.Scope) + $"/actions/runners/{runner.AgentId}", null, token);
    }
    internal async Task Labels(ManagedRunner runner, string[] labels, CancellationToken token)
    {
        Allow(runner.Settings.Scope);
        if (runner.AgentId is not > 0) throw new ManagementException("Runner is not registered.");
        await Send(HttpMethod.Put, ManagementValidation.ApiScope(runner.Settings.Scope) + $"/actions/runners/{runner.AgentId}/labels", new { labels }, token);
    }
    internal async Task<WorkflowAttempt> Workflow(WorkflowAction input, CancellationToken token)
    {
        var scope = ManagementValidation.Scope("repo:" + input.Repository); Allow(scope);
        if (input.RunId <= 0 || input.Action is not ("track" or "rerun" or "rerun-failed" or "cancel")) throw new ManagementException("Choose track, rerun, rerun-failed or cancel, with a positive run ID.");
        var path = ManagementValidation.ApiScope(scope) + $"/actions/runs/{input.RunId}";
        if (input.Action != "track") await Send(HttpMethod.Post, path + "/" + (input.Action == "rerun-failed" ? "rerun-failed-jobs" : input.Action), new { }, token);
        return await ReadWorkflow(input.Repository, input.RunId, token);
    }
    internal async Task<WorkflowAttempt> ReadWorkflow(string repository, long id, CancellationToken token)
    {
        var scope = ManagementValidation.Scope("repo:" + repository); Allow(scope);
        var json = await Send(HttpMethod.Get, ManagementValidation.ApiScope(scope) + $"/actions/runs/{id}", null, token);
        return new(scope[5..], id, json.GetProperty("run_attempt").GetInt32(), json.GetProperty("status").GetString() ?? "unknown",
            json.GetProperty("conclusion").GetString(), $"https://github.com/{scope[5..]}/actions/runs/{id}", DateTimeOffset.UtcNow);
    }
    internal async Task<JsonElement> Groups(GitHubGroupAction input, CancellationToken token)
    {
        var scope = ManagementValidation.Scope("org:" + input.Organization); Allow(scope);
        var path = ManagementValidation.ApiScope(scope) + "/actions/runner-groups";
        if (input.Action == "list") return await Send(HttpMethod.Get, path + "?per_page=100", null, token);
        if (input.Action is not ("create" or "update" or "delete" or "members")) throw new ManagementException("Unknown GitHub group action.");
        if (input.Action != "create" && input.Id is not > 0) throw new ManagementException("A positive group ID is required.");
        if (input.Action != "create") path += "/" + input.Id;
        if (input.Action == "delete") return await Send(HttpMethod.Delete, path, null, token);
        if (input.Action == "members")
        {
            if (input.RunnerIds is null || input.RunnerIds.Length > 100 || input.RunnerIds.Any(i => i <= 0)) throw new ManagementException("Provide up to 100 positive runner IDs.");
            return await Send(HttpMethod.Put, path + "/runners", new { runners = input.RunnerIds }, token);
        }
        if (string.IsNullOrWhiteSpace(input.Name) || input.Name.Length > 100 || input.Name.Any(char.IsControl) || input.Visibility is not ("private" or "all" or "selected") || input.RepositoryIds?.Any(i => i <= 0) == true)
            throw new ManagementException("Provide a group name, visibility and valid repository IDs.");
        var result = await Send(input.Action == "create" ? HttpMethod.Post : HttpMethod.Patch, path,
            new { name = input.Name, visibility = input.Visibility, allows_public_repositories = input.AllowsPublicRepositories, selected_repository_ids = input.RepositoryIds ?? [] }, token);
        if (input.Action == "update" && input.Visibility == "selected")
            await Send(HttpMethod.Put, path + "/repositories", new { selected_repository_ids = input.RepositoryIds ?? [] }, token);
        return result;
    }
}
