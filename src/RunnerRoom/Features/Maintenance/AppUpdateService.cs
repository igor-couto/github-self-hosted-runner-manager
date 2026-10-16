using System.Net;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

using RunnerRoom.Configuration;

namespace RunnerRoom.Features.Maintenance;

public sealed class ApplicationUpdateOptions
{
    public bool Enabled { get; set; } = true;
    public int CheckIntervalHours { get; set; } = 6;
}

public sealed record AppUpdateSnapshot(bool Enabled, bool Demo, string CurrentVersion,
    string Status, bool? UpdateAvailable = null, string? LatestVersion = null, string? ReleaseUrl = null,
    DateTimeOffset? CheckedAt = null, DateTimeOffset? LastAttemptAt = null, bool Stale = false, string? Message = null);

/// <summary>Checks public release metadata on a schedule; dashboard reads only the cached snapshot.</summary>
public sealed partial class AppUpdateService : BackgroundService
{
    internal const string LatestReleaseEndpoint = "https://api.github.com/repos/igor-couto/github-self-hosted-runner-manager/releases/latest";
    internal const int MaximumResponseBytes = 512 * 1024;
    private const string ReleasesUrl = "https://github.com/igor-couto/github-self-hosted-runner-manager/releases/tag/";
    private readonly ApplicationUpdateOptions options;
    private readonly bool demo;
    private readonly HttpClient http;
    private readonly bool ownsClient;
    private readonly TimeProvider clock;
    private readonly TimeSpan requestTimeout;
    private readonly SemaphoreSlim gate = new(1, 1);
    private AppUpdateSnapshot current;

    public static string InstalledVersion { get; } = typeof(AppUpdateService).Assembly
        .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion.Split('+')[0] ?? "unknown";

    public AppUpdateService(RunnerOptions options) : this(options.ApplicationUpdates, options.Demo, InstalledVersion,
        new HttpClient(new HttpClientHandler { AllowAutoRedirect = false, UseCookies = false })
            { Timeout = Timeout.InfiniteTimeSpan }, ownsClient: true) { }

    internal AppUpdateService(ApplicationUpdateOptions options, bool demo, string installedVersion,
        HttpClient http, TimeProvider? clock = null, TimeSpan? requestTimeout = null, bool ownsClient = false)
    {
        this.options = options;
        this.demo = demo;
        this.http = http;
        this.ownsClient = ownsClient;
        this.clock = clock ?? TimeProvider.System;
        this.requestTimeout = requestTimeout ?? TimeSpan.FromSeconds(10);
        current = new(options.Enabled, demo, installedVersion, !options.Enabled ? "disabled" : demo ? "demo" : "checking",
            Message: !options.Enabled ? "Automatic application update checks are disabled." : demo
                ? "Demo mode: release checks are paused. The installed application version is shown."
                : "Checking for a published application release.");
    }

    public AppUpdateSnapshot Current => Volatile.Read(ref current);
    internal TimeSpan Interval => TimeSpan.FromHours(Math.Clamp(options.CheckIntervalHours, 1, 168));

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!options.Enabled || demo) return;
        await CheckAsync(stoppingToken);
        using var timer = new PeriodicTimer(Interval, clock);
        while (await timer.WaitForNextTickAsync(stoppingToken)) await CheckAsync(stoppingToken);
    }

    internal Task CheckNowAsync(CancellationToken cancellationToken = default) => CheckAsync(cancellationToken, manual: true);

    internal async Task CheckAsync(CancellationToken cancellationToken = default, bool manual = false)
    {
        if ((!options.Enabled && !manual) || demo) return;
        if (manual) await gate.WaitAsync(cancellationToken);
        else if (!await gate.WaitAsync(0, cancellationToken)) return;
        try
        {
            var attempt = clock.GetUtcNow();
            // A button click during a scheduled check waits for that result; repeated clicks
            // reuse the result for thirty seconds instead of consuming GitHub's API quota.
            if (manual && Current.LastAttemptAt is { } lastAttempt && attempt - lastAttempt < TimeSpan.FromSeconds(30)) return;
            Volatile.Write(ref current, Current with { LastAttemptAt = attempt });
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(requestTimeout);
            using var request = new HttpRequestMessage(HttpMethod.Get, LatestReleaseEndpoint);
            request.Headers.UserAgent.ParseAdd("RunnerRoom-update-checker/1.0");
            request.Headers.Accept.ParseAdd("application/vnd.github+json");
            request.Headers.Add("X-GitHub-Api-Version", "2022-11-28");
            using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
            if (!response.IsSuccessStatusCode)
            {
                Fail(response.StatusCode switch
                {
                    HttpStatusCode.NotFound => "No published application release was found.",
                    HttpStatusCode.Forbidden or HttpStatusCode.TooManyRequests => "GitHub declined the release check or its public API rate limit was reached. The next scheduled check will retry.",
                    _ => "GitHub could not return application release information. The next scheduled check will retry."
                });
                return;
            }
            if (response.Content.Headers.ContentLength > MaximumResponseBytes)
                throw new InvalidDataException("The release response exceeded the size limit.");
            await using var source = await response.Content.ReadAsStreamAsync(timeout.Token);
            using var destination = new MemoryStream();
            var buffer = new byte[8192];
            int read;
            while ((read = await source.ReadAsync(buffer, timeout.Token)) != 0)
            {
                if (destination.Length + read > MaximumResponseBytes)
                    throw new InvalidDataException("The release response exceeded the size limit.");
                await destination.WriteAsync(buffer.AsMemory(0, read), timeout.Token);
            }
            var release = ParseRelease(destination.ToArray());
            var result = Evaluate(Current.CurrentVersion, release.Version);
            Volatile.Write(ref current, Current with
            {
                Status = result.Status, UpdateAvailable = result.Available,
                LatestVersion = release.Version, ReleaseUrl = ReleasesUrl + release.Tag,
                CheckedAt = clock.GetUtcNow(), Stale = false, Message = result.Message
            });
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
        catch (OperationCanceledException) { Fail("The application release check timed out. The next scheduled check will retry."); }
        catch (Exception ex) when (ex is HttpRequestException or IOException or InvalidDataException or JsonException or InvalidOperationException or FormatException)
        {
            // Do not surface arbitrary remote response text, headers or infrastructure details to viewers.
            Fail(ex is InvalidDataException ? ex.Message : "Application release information is unavailable or invalid. The next scheduled check will retry.");
        }
        finally { gate.Release(); }
    }

    private void Fail(string message) => Volatile.Write(ref current, Current with
    {
        Status = "unavailable", Stale = Current.CheckedAt is not null, Message = message
    });

    internal static (string Tag, string Version) ParseRelease(ReadOnlyMemory<byte> json)
    {
        using var document = JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = 32 });
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object ||
            !root.TryGetProperty("draft", out var draft) || draft.ValueKind != JsonValueKind.False ||
            !root.TryGetProperty("prerelease", out var prerelease) || prerelease.ValueKind != JsonValueKind.False ||
            !root.TryGetProperty("tag_name", out var tagElement) || tagElement.ValueKind != JsonValueKind.String)
            throw new InvalidDataException("No valid stable application release was returned.");
        var tag = tagElement.GetString()!;
        if (!tag.StartsWith('v') || !SemanticVersion.TryParse(tag[1..], out var version) || version!.PreRelease.Length != 0 || tag.Contains('+'))
            throw new InvalidDataException("The application release tag is not a supported stable version.");
        if (!root.TryGetProperty("assets", out var assets) || assets.ValueKind != JsonValueKind.Array)
            throw new InvalidDataException("The release is missing its Linux installation files.");
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var asset in assets.EnumerateArray())
        {
            if (asset.ValueKind == JsonValueKind.Object && asset.TryGetProperty("name", out var name) && name.ValueKind == JsonValueKind.String &&
                asset.TryGetProperty("state", out var state) && state.ValueKind == JsonValueKind.String && state.GetString() == "uploaded" &&
                asset.TryGetProperty("size", out var size) && size.TryGetInt64(out var bytes) && bytes > 0)
                names.Add(name.GetString()!);
        }
        foreach (var name in new[] { "install.sh", "runner-room-linux-x64.tar.gz", "runner-room-linux-x64.tar.gz.sha256",
            "runner-room-linux-arm.tar.gz", "runner-room-linux-arm.tar.gz.sha256", "runner-room-linux-arm64.tar.gz", "runner-room-linux-arm64.tar.gz.sha256" })
            if (!names.Contains(name)) throw new InvalidDataException("The release is missing its Linux installation files. It may still be publishing.");
        return (tag, tag[1..]);
    }

    internal static (string Status, bool? Available, string Message) Evaluate(string currentVersion, string latestVersion)
    {
        if (!SemanticVersion.TryParse(currentVersion, out var installed) ||
            installed!.Core.All(part => part == "0") || installed.PreRelease.Any(part => part.Equals("dev", StringComparison.OrdinalIgnoreCase)))
            return ("development", null, "This is a development or unversioned build. Compare it with the published release before installing an update.");
        if (!SemanticVersion.TryParse(latestVersion, out var latest))
            return ("unavailable", null, "The published application version could not be compared.");
        var comparison = installed.CompareTo(latest!);
        return comparison < 0 ? ("update_available", true, "A newer application release is available. Open Preferences to update.")
            : comparison == 0 ? ("up_to_date", false, "The application matches the latest published stable release.")
            : ("ahead", false, "The installed application is newer than the latest published stable release.");
    }

    public override void Dispose()
    {
        base.Dispose();
        if (ownsClient) http.Dispose();
    }

    // Compare SemVer components without numeric overflow; build metadata never affects precedence.
    private sealed record SemanticVersion(string[] Core, string[] PreRelease) : IComparable<SemanticVersion>
    {
        public static bool TryParse(string value, out SemanticVersion? result)
        {
            result = null;
            if (value.Length is 0 or > 200) return false;
            var match = SemVerPattern().Match(value);
            if (!match.Success) return false;
            var preRelease = match.Groups[4].Success ? match.Groups[4].Value.Split('.') : [];
            if (preRelease.Any(part => Numeric(part) && part.Length > 1 && part[0] == '0')) return false;
            result = new([match.Groups[1].Value, match.Groups[2].Value, match.Groups[3].Value], preRelease);
            return true;
        }
        public int CompareTo(SemanticVersion? other)
        {
            if (other is null) return 1;
            for (var i = 0; i < 3; i++)
            {
                var comparison = NumberCompare(Core[i], other.Core[i]);
                if (comparison != 0) return comparison;
            }
            if (PreRelease.Length == 0 || other.PreRelease.Length == 0)
                return PreRelease.Length == other.PreRelease.Length ? 0 : PreRelease.Length == 0 ? 1 : -1;
            for (var i = 0; i < Math.Min(PreRelease.Length, other.PreRelease.Length); i++)
            {
                var left = PreRelease[i]; var right = other.PreRelease[i];
                var leftNumeric = Numeric(left); var rightNumeric = Numeric(right);
                var comparison = leftNumeric && rightNumeric ? NumberCompare(left, right)
                    : leftNumeric != rightNumeric ? (leftNumeric ? -1 : 1) : string.CompareOrdinal(left, right);
                if (comparison != 0) return comparison;
            }
            return PreRelease.Length.CompareTo(other.PreRelease.Length);
        }
        private static bool Numeric(string part) => part.All(character => character is >= '0' and <= '9');
        private static int NumberCompare(string left, string right) => left.Length != right.Length ? left.Length.CompareTo(right.Length) : string.CompareOrdinal(left, right);
    }

    [GeneratedRegex(@"\A(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)(?:-([0-9A-Za-z-]+(?:\.[0-9A-Za-z-]+)*))?(?:\+[0-9A-Za-z-]+(?:\.[0-9A-Za-z-]+)*)?\z", RegexOptions.CultureInvariant)]
    private static partial Regex SemVerPattern();
}
