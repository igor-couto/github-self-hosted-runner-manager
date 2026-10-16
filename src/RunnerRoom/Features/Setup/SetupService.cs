using System.Net;
using System.Net.Http.Headers;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using RunnerRoom.Configuration;
using RunnerRoom.Features.Access;
using RunnerRoom.Features.Management;
using RunnerRoom.Features.Runners;

namespace RunnerRoom.Features.Setup;

// Open dashboards can configure management directly. Existing sign-in remains authoritative.
internal sealed class SetupService(RunnerOptions options, HttpClient http, string? restriction = null, Func<string[]>? prerequisites = null,
    Action<ManagementOptions>? activate = null)
{
    private readonly SemaphoreSlim gate = new(1, 1);
    private bool saved;
    [DllImport("libc")] private static extern uint geteuid();
    internal static string ConfigurationPath(RunnerOptions options) => Path.Combine(AccessSetup.StateDirectory(options), "ui-settings.json");
    internal bool PendingRestart => !options.Demo && !options.Management.Enabled && (saved || File.Exists(ConfigurationPath(options)));
    internal bool Available => !options.Management.Enabled && !PendingRestart && Issues().Length == 0;

    internal bool Authorized(HttpContext context) => !options.Access.Enabled ||
        context.User.Identity?.IsAuthenticated == true && context.User.IsInRole("admin");

    internal object Status() => new { available = Available, demo = options.Demo, accessEnabled = options.Access.Enabled,
        managementEnabled = options.Management.Enabled, pendingRestart = PendingRestart,
        unavailableReason = Issues().FirstOrDefault() };

    internal SetupConfiguration Configuration(string origin) => new(options.Demo, options.Access.Enabled, options.Management.Enabled,
        PendingRestart, options.Access.Enabled ? options.Access.PublicOrigin! : origin,
        Path.Combine(AccessSetup.StateDirectory(options), "runners"), options.Management.AllowedScopes, Issues());

    private string[] Issues() => options.Demo ? [] : restriction is not null ? [restriction] : prerequisites?.Invoke() ?? (!OperatingSystem.IsLinux()
        ? ["Runner management requires a Linux server."] : geteuid() == 0
        ? ["Runner Room must run as a non-root runner account before management can be enabled."] : []);

    internal async Task<SetupDiscovery> DiscoverAsync(string? token, CancellationToken cancellation)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        deadline.CancelAfter(TimeSpan.FromMinutes(2));
        RequireAvailable();
        if (options.Demo) return new([new("example/activities-api", true), new("example/newsletter", false)], false, "Demo repositories; no GitHub connection was made.");
        token = ValidateToken(token);
        var repositories = new List<SetupRepository>();
        var partial = false;
        for (var page = 1; page <= 5; page++)
        {
            using var json = await GetAsync($"user/repos?per_page=100&page={page}&sort=full_name", token, deadline.Token);
            if (json.RootElement.ValueKind != JsonValueKind.Array) throw new SetupException("GitHub returned an unexpected repository response.");
            foreach (var repository in json.RootElement.EnumerateArray())
            {
                if (repository.ValueKind != JsonValueKind.Object) continue;
                if (!repository.TryGetProperty("full_name", out var name) || name.ValueKind != JsonValueKind.String) continue;
                string scope;
                try { scope = ManagementValidation.Scope("repo:" + name.GetString()); } catch (ManagementException) { continue; }
                repositories.Add(new(scope[5..], repository.TryGetProperty("private", out var privacy) && privacy.ValueKind == JsonValueKind.True));
            }
            if (json.RootElement.GetArrayLength() < 100) break;
            partial = page == 5;
        }
        return new(repositories.DistinctBy(r => r.FullName).ToArray(), partial,
            "Repositories visible to this token. Runner registration also requires Administration: read and write; workflow controls require Actions: read and write.");
    }

    internal async Task<SetupResult> SaveAsync(SetupSaveRequest input, string currentOrigin, CancellationToken cancellation)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        deadline.CancelAfter(TimeSpan.FromMinutes(2));
        await gate.WaitAsync(deadline.Token);
        try
        {
            RequireAvailable();
            var issues = Issues();
            if (issues.Length > 0) throw new SetupException(issues[0]);
            var scopes = ValidateScopes(input.Scopes);
            if (options.Demo) { saved = true; return new(true, false, "Demo setup completed. No server settings or GitHub registrations were changed."); }
            var token = ValidateToken(input.Token);
            foreach (var scope in scopes)
            {
                using var response = await GetAsync(ManagementValidation.ApiScope(scope) + "/actions/runners?per_page=1", token, deadline.Token);
                if (response.RootElement.ValueKind != JsonValueKind.Object || !response.RootElement.TryGetProperty("total_count", out var count) ||
                    count.ValueKind != JsonValueKind.Number || !count.TryGetInt32(out var total) || total < 0 || !response.RootElement.TryGetProperty("runners", out var runners) || runners.ValueKind != JsonValueKind.Array)
                    throw new SetupException("GitHub returned an unexpected runner response. No settings were saved.");
            }
            deadline.Token.ThrowIfCancellationRequested();
            var directory = ManagementValidation.SafeDirectory(AccessSetup.StateDirectory(options));
            CreatePrivateDirectory(directory);
            var root = ManagementValidation.SafeDirectory(Path.Combine(directory, "runners"));
            Directory.CreateDirectory(root);
            var tokenFile = Path.Combine(directory, "management-setup.token");
            if (File.Exists(tokenFile) || new FileInfo(tokenFile).LinkTarget is not null)
                throw new SetupException("A previous setup token file exists. Ask the server administrator to inspect it before retrying setup.");
            var management = new ManagementOptions { Enabled = true, RootDirectory = root, TokenFile = tokenFile, AllowedScopes = scopes,
                MaximumRunners = options.Management.MaximumRunners, MaximumBatchSize = options.Management.MaximumBatchSize,
                DrainTimeoutMinutes = options.Management.DrainTimeoutMinutes };
            if (management.MaximumRunners is < 1 or > 500 || management.MaximumBatchSize is < 1 or > 50 || management.DrainTimeoutMinutes is < 1 or > 1440)
                throw new SetupException("Existing management capacity settings are invalid. Correct them before setup.");
            // Credentials are durable before publishing the configuration. Never overwrite an existing overlay.
            WritePrivate(tokenFile, token, overwrite: false);
            try
            {
                var json = JsonSerializer.Serialize(new { Management = management });
                WritePrivate(ConfigurationPath(options), json, overwrite: false);
            }
            catch { File.Delete(tokenFile); throw; }
            saved = true;
            if (activate is not null)
            {
                try
                {
                    activate(management);
                    return new(true, false, "Management is ready. You can now create runners or import existing installations.");
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or ManagementException)
                {
                    return new(true, true, "Settings were saved, but management could not start. Check state-directory permissions and whether another Runner Room instance owns its management state, then restart Runner Room. Existing runner services have not been changed.");
                }
            }
            return new(true, true, "Settings saved. Restart Runner Room to activate management. Existing runner services have not been changed.");
        }
        finally { gate.Release(); }
    }

    internal static string[] ValidateScopes(string[]? scopes)
    {
        if (scopes is null || scopes.Length is < 1 or > 25 || scopes.Any(s => s is null || s.Length > 205))
            throw new SetupException("Choose between 1 and 25 repository or organization scopes.");
        try { return scopes.Select(ManagementValidation.Scope).Distinct(StringComparer.OrdinalIgnoreCase).ToArray(); }
        catch (ManagementException) { throw new SetupException("Use repo:owner/repository or org:organization for allowed scopes."); }
    }

    private void RequireAvailable()
    {
        if (Issues().FirstOrDefault() is { } issue) throw new SetupException(issue);
        if (!Available) throw new SetupException(PendingRestart ? "Setup is already saved. Restart Runner Room to apply it." : "Runner management is already configured.");
    }
    private static string ValidateToken(string? value)
    {
        var token = value?.Trim();
        if (string.IsNullOrEmpty(token) || token.Length > 4096 || token.Any(c => c <= 32 || c >= 127)) throw new SetupException("Enter a valid GitHub personal access token.");
        return token;
    }
    private async Task<JsonDocument> GetAsync(string path, string token, CancellationToken cancellation)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, "https://api.github.com/" + path);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        request.Headers.UserAgent.ParseAdd("RunnerRoom/1.0");
        request.Headers.Accept.ParseAdd("application/vnd.github+json");
        request.Headers.Add("X-GitHub-Api-Version", "2022-11-28");
        try
        {
            using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellation);
            if (!response.IsSuccessStatusCode) throw new SetupException(response.StatusCode switch
            {
                HttpStatusCode.Unauthorized => "GitHub rejected the token. Check its value and expiration.",
                HttpStatusCode.Forbidden or HttpStatusCode.TooManyRequests => "GitHub denied access or reached an API limit. Check token permissions, organization approval and the GitHub API limit before retrying.",
                HttpStatusCode.NotFound => "The token cannot access runners for one of the selected scopes. Check repository names and token permissions.",
                _ => "GitHub could not verify these settings. Please try again later."
            });
            await using var stream = await response.Content.ReadAsStreamAsync(cancellation);
            var bytes = new byte[2 * 1024 * 1024 + 1];
            var count = await stream.ReadAtLeastAsync(bytes, bytes.Length, false, cancellation);
            if (count == bytes.Length) throw new SetupException("GitHub returned too much data. Try a token limited to the repositories you need.");
            return JsonDocument.Parse(bytes.AsMemory(0, count));
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException || ex is TaskCanceledException && !cancellation.IsCancellationRequested)
        { throw new SetupException("GitHub could not be reached or returned an invalid response. Check the connection and try again."); }
    }
    private static void CreatePrivateDirectory(string path)
    {
        if (OperatingSystem.IsLinux()) Directory.CreateDirectory(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        else Directory.CreateDirectory(path);
    }
    private static void WritePrivate(string path, string contents, bool overwrite = true)
    {
        if (new FileInfo(path).LinkTarget is not null) throw new SetupException("Setup files cannot be symbolic links.");
        var temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            var streamOptions = new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write, Share = FileShare.None };
            if (OperatingSystem.IsLinux()) streamOptions.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
            using (var stream = new FileStream(temp, streamOptions))
            {
                var bytes = Encoding.UTF8.GetBytes(contents); stream.Write(bytes); stream.Flush(true);
            }
            File.Move(temp, path, overwrite);
        }
        finally { if (File.Exists(temp)) File.Delete(temp); }
    }
}
