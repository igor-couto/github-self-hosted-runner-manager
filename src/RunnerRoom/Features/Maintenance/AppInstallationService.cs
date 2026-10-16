using System.Text;
using System.Text.Json;
using RunnerRoom.Configuration;
using RunnerRoom.Features.Management;
using RunnerRoom.Features.Runners;

namespace RunnerRoom.Features.Maintenance;

internal sealed record AppInstallationSnapshot(bool Supported, bool Demo, string State, string Message,
    string? RequestId = null, DateTimeOffset? UpdatedAt = null, string? TargetVersion = null);

internal sealed class AppInstallationException(string message) : Exception(message);

/// <summary>Requests a fixed installer operation from the systemd helper; never runs a privileged command.</summary>
internal sealed class AppInstallationService
{
    internal const string UpdaterDirectory = "/var/lib/runner-room-updater";
    private readonly AppUpdateService updates;
    private readonly bool demo;
    private readonly string directory;
    private readonly TimeProvider clock;
    private readonly Func<string?> environmentIssue;
    private readonly Action<Func<bool>, Action>? requestGuard;
    private readonly SemaphoreSlim gate = new(1, 1);
    private AppInstallationSnapshot? demoRequest;

    public AppInstallationService(RunnerOptions options, AppUpdateService updates, RunnerManagement management)
        : this(options.Demo, updates, UpdaterDirectory, requestGuard: management.RequestApplicationUpdate) { }

    internal AppInstallationService(bool demo, AppUpdateService updates, string directory, TimeProvider? clock = null,
        Func<string?>? environmentIssue = null, Action<Func<bool>, Action>? requestGuard = null)
    {
        this.demo = demo; this.updates = updates; this.directory = directory;
        this.clock = clock ?? TimeProvider.System; this.requestGuard = requestGuard;
        this.environmentIssue = environmentIssue ?? (() => !OperatingSystem.IsLinux()
            ? "Updates from the dashboard require an installer-managed Linux server."
            : File.Exists("/.dockerenv") || Environment.GetEnvironmentVariable("DOTNET_RUNNING_IN_CONTAINER") == "true"
                ? "This dashboard runs in a container. Update its Docker Compose or Helm deployment to use the latest image."
                : null);
    }

    internal AppInstallationSnapshot Snapshot()
    {
        if (demo)
        {
            var request = Volatile.Read(ref demoRequest);
            return request is null ? new(true, true, "idle", "Preview an application update. Demo mode does not install or restart anything.")
                : clock.GetUtcNow() - request.UpdatedAt >= TimeSpan.FromSeconds(3)
                    ? request with { State = "succeeded", Message = "Demo complete. No software was downloaded or installed." }
                    : request;
        }
        try
        {
            if (Unsupported() is { } reason) return new(false, false, "unsupported", reason);
            var status = ReadStatus();
            var request = ReadRequest();
            var processing = Processing();
            // A new request takes precedence over the previous operation's terminal status.
            if (request is { } pending && pending.Id != status?.RequestId)
                return clock.GetUtcNow() - pending.At > TimeSpan.FromMinutes(2)
                    ? new(true, false, "failed", "The update service did not pick up the request. Check runner-room-update.path and runner-room-update.service on the server before retrying.", pending.Id, pending.At)
                    : new(true, false, "queued", "Update requested. Waiting for the server's update service.", pending.Id, pending.At);
            if (processing is { } claimedAt && (status is null || !Active(status.State)))
                return clock.GetUtcNow() - claimedAt > TimeSpan.FromMinutes(2)
                    ? new(true, false, "failed", "The update service has an unfinished request. Check runner-room-update.service on the server before retrying.", UpdatedAt: claimedAt)
                    : new(true, false, "queued", "The server's update service is processing the request.", UpdatedAt: claimedAt);
            if (status is not null)
                return Active(status.State) && clock.GetUtcNow() - status.UpdatedAt > TimeSpan.FromMinutes(30)
                    ? status with { State = "failed", Message = "The update service stopped reporting progress. Check runner-room-update.service on the server; the pending operation must finish or be recovered before another update." }
                    : status;
            return new(true, false, "idle", "Application updates are ready. Your settings and runner installations are preserved.");
        }
        catch (Exception error) when (InvalidState(error))
        { return new(false, false, "unsupported", "The server's update service files are unavailable or invalid. Repair the installation by running the latest installer once."); }
    }

    internal async Task<AppInstallationSnapshot> InstallAsync(CancellationToken cancellationToken = default)
    {
        await gate.WaitAsync(cancellationToken);
        try
        {
            if (demo)
            {
                if (Snapshot().State == "queued") throw new AppInstallationException("An update preview is already in progress.");
                Volatile.Write(ref demoRequest, new(true, true, "queued", "Previewing an application update. Nothing on this server will change.", Guid.NewGuid().ToString("N"), clock.GetUtcNow()));
                return Snapshot();
            }
            var availability = Snapshot();
            if (!availability.Supported) throw new AppInstallationException(availability.Message);
            // This lock serializes dashboard requests. The helper's processing marker bridges
            // the interval between claiming the request and publishing progress.
            var lockPath = Path.Combine(directory, "inbox", "request.lock");
            NoLinks(lockPath);
            using var ownership = new FileStream(lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            if (Pending()) throw new AppInstallationException("An update request is already pending. Wait for it to finish, or inspect the update service if progress has stopped.");
            await updates.CheckNowAsync(cancellationToken);
            var release = updates.Current;
            if (release is not { Status: "update_available", UpdateAvailable: true, Stale: false } ||
                release.CheckedAt is not { } checkedAt || clock.GetUtcNow() - checkedAt > TimeSpan.FromMinutes(1))
                throw new AppInstallationException(release.Status is "up_to_date" or "ahead"
                    ? "This application already matches or exceeds the latest stable release."
                    : "A newer complete release could not be verified. Check for updates and try again after GitHub release information is available.");
            cancellationToken.ThrowIfCancellationRequested();
            void Write() => WriteRequest(Guid.NewGuid().ToString("N"));
            if (requestGuard is null) Write(); else requestGuard(Pending, Write);
            return Snapshot();
        }
        finally { gate.Release(); }
    }

    private string? Unsupported()
    {
        if (environmentIssue() is { } issue) return issue;
        NoLinks(Path.Combine(directory, "capability.json"));
        NoLinks(Path.Combine(directory, "inbox"));
        var content = LocalRunnerReader.ReadSmall(Path.Combine(directory, "capability.json"), 1024);
        if (content is null || !Directory.Exists(Path.Combine(directory, "inbox")))
            return "Run the latest installer once to enable updates from Preferences. Future updates can then be installed here.";
        using var document = JsonDocument.Parse(content);
        if (document.RootElement.ValueKind != JsonValueKind.Object || !document.RootElement.TryGetProperty("protocol", out var protocol) || !protocol.TryGetInt32(out var version) || version != 1)
            throw new AppInstallationException("Unsupported update-service protocol.");
        return null;
    }

    private bool Pending()
    {
        try { return Processing() is not null || ReadRequest() is not null || ReadStatus() is { } status && Active(status.State); }
        catch (Exception error) when (InvalidState(error)) { return true; }
    }

    // Management also checks the persisted handshake before its background worker starts,
    // including while the replacement dashboard is being health-checked by the helper.
    internal static bool HasPendingInstallation()
    {
        if (!OperatingSystem.IsLinux()) return false;
        try
        {
            var request = Path.Combine(UpdaterDirectory, "inbox", "request"); NoLinks(request);
            if (File.Exists(request)) return true;
            var processing = Path.Combine(UpdaterDirectory, "processing"); NoLinks(processing);
            if (File.Exists(processing)) return true;
            var status = Path.Combine(UpdaterDirectory, "status.json"); NoLinks(status);
            if (!File.Exists(status)) return false;
            using var document = JsonDocument.Parse(LocalRunnerReader.ReadSmall(status, 8192) ?? throw new AppInstallationException("Invalid update status."));
            var state = document.RootElement.GetProperty("state").GetString();
            return state is not ("succeeded" or "failed" or "rolled_back");
        }
        catch (Exception error) when (InvalidState(error)) { return true; }
    }

    private DateTimeOffset? Processing()
    {
        var path = Path.Combine(directory, "processing"); NoLinks(path);
        var file = new FileInfo(path);
        return file.Exists ? file.LastWriteTimeUtc : null;
    }

    private AppInstallationSnapshot? ReadStatus()
    {
        var path = Path.Combine(directory, "status.json"); NoLinks(path);
        if (!File.Exists(path)) return null;
        using var document = JsonDocument.Parse(LocalRunnerReader.ReadSmall(path, 8192) ?? throw new AppInstallationException("Invalid update status."));
        var root = document.RootElement;
        var id = root.GetProperty("requestId").GetString();
        var state = root.GetProperty("state").GetString();
        var message = root.GetProperty("message").GetString();
        var at = root.GetProperty("updatedAt").GetDateTimeOffset();
        if (!ValidId(id) || state is null || !(Active(state) || state is "succeeded" or "failed" or "rolled_back") ||
            message is null || message.Length > 2048 || at > clock.GetUtcNow().AddMinutes(5))
            throw new AppInstallationException("Invalid update status.");
        string? version = null;
        if (root.TryGetProperty("targetVersion", out var target) && target.ValueKind != JsonValueKind.Null)
        {
            version = target.GetString();
            if (version?.Length > 200) throw new AppInstallationException("Invalid update version.");
        }
        return new(true, false, state, LocalRunnerReader.Text(message, 2048) ?? "Update status unavailable.", id, at, version);
    }

    private (string Id, DateTimeOffset At)? ReadRequest()
    {
        var path = Path.Combine(directory, "inbox", "request"); NoLinks(path);
        var file = new FileInfo(path);
        if (!file.Exists) return null;
        if (file.Length != 33) throw new AppInstallationException("Invalid pending update request.");
        var text = LocalRunnerReader.ReadSmall(path, 33);
        if (text is null || text.Length != 33 || text[32] != '\n' || !ValidId(text[..32])) throw new AppInstallationException("Invalid pending update request.");
        return (text[..32], file.LastWriteTimeUtc);
    }

    private void WriteRequest(string id)
    {
        var path = Path.Combine(directory, "inbox", "request"); NoLinks(path);
        var temporary = Path.Combine(directory, "inbox", ".request-" + id);
        try
        {
            var settings = new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write, Share = FileShare.None };
            if (OperatingSystem.IsLinux()) settings.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
            using (var file = new FileStream(temporary, settings))
            {
                file.Write(Encoding.ASCII.GetBytes(id + "\n")); file.Flush(flushToDisk: true);
            }
            File.Move(temporary, path, overwrite: false);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    private static bool ValidId(string? value) => value is { Length: 32 } && value.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f');
    private static bool Active(string state) => state is "queued" or "checking" or "downloading" or "installing" or "restarting";
    private static bool InvalidState(Exception error) => error is IOException or UnauthorizedAccessException or JsonException or
        AppInstallationException or InvalidOperationException or KeyNotFoundException or FormatException;
    private static void NoLinks(string path)
    {
        if (new FileInfo(path).LinkTarget is not null || new DirectoryInfo(path).LinkTarget is not null)
            throw new AppInstallationException("Update service paths cannot be symbolic links.");
        for (var parent = Directory.GetParent(path); parent is not null; parent = parent.Parent)
            if (parent.LinkTarget is not null) throw new AppInstallationException("Update service paths cannot contain symbolic links.");
    }
}
