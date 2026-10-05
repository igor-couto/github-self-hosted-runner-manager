namespace RunnerRoom;

public sealed class RunnerMonitor(RunnerOptions options, GitHubRunnerClient github)
{
    private readonly SemaphoreSlim gate = new(1);
    private readonly SystemMonitor systemMonitor = new();
    private RunnerSnapshot? cached;

    public async Task<RunnerSnapshot> GetSnapshotAsync()
    {
        await gate.WaitAsync();
        try
        {
            if (cached is not null && DateTimeOffset.UtcNow - cached.CheckedAt < TimeSpan.FromSeconds(2)) return cached;
            var now = DateTimeOffset.UtcNow;
            if (options.Demo) return cached = Demo(now) with { System = systemMonitor.Read(null, true) };
            var discovery = LocalRunnerReader.Discover(options);
            if (discovery.Roots.Length == 0) return cached = new(Environment.MachineName, options.RunnersRoot, now, false,
                "No readable runner folders. Configure RunnersRoot or RunnersRoots and check directory permissions.", null, []);
            var processes = RunnerRuntime.ReadProcesses();
            var runners = discovery.Folders.Select(folder => RunnerRuntime.ApplyProcesses(LocalRunnerReader.Read(folder, options), processes, now)).ToArray();
            var services = await RunnerRuntime.ReadServicesAsync(runners.Select(r => r.ServiceName));
            runners = runners.Select(r => r.ServiceName is { } name && services.TryGetValue(name, out var info)
                ? r with { ServiceState = info.State, ServiceSubState = info.SubState } : r).ToArray();
            runners = await github.EnrichAsync(runners);
            var warnings = discovery.Warnings.ToList();
            if (!processes.Reliable) warnings.Add("Process visibility is limited. Activity may be Unknown. Run on Linux as the runner user.");
            return cached = new(Environment.MachineName, discovery.Roots[0], DateTimeOffset.UtcNow, false, null,
                warnings.Count > 0 ? string.Join(" ", warnings) : null, runners, systemMonitor.Read(discovery.Roots[0], false)) { Roots = discovery.Roots };
        }
        finally { gate.Release(); }
    }

    internal static RunnerSnapshot Demo(DateTimeOffset now)
    {
        var runners = new[]
        {
            new RunnerInfo("pifive2", "activities-api-arm64-2.322.0", "busy", 2148) { DisplayName = "Activities API", Repository = "igor-couto/activities-api", Group = "APIs" },
            new RunnerInfo("pifive2", "avatarize-api-arm64-2.321.0", "idle", 2351) { DisplayName = "Avatarize API", Repository = "igor-couto/avatarize-api", Group = "APIs" },
            new RunnerInfo("pifive2", "newsletter-arm64-2.321.0", "offline", null) { DisplayName = "Newsletter", Repository = "igor-couto/newsletter", Group = "Websites" },
            new RunnerInfo("build-linux-01", "shared-build", "unknown", null) { DisplayName = "Shared builds", Group = "Builds", RunnerGroup = "Default" }
        }.Select((r, index) => r with
        {
            Path = "/srv/actions-runners/" + r.Folder, Host = "home-server", Organization = "igor-couto", AgentId = index + 1,
            GitHubUrl = "https://github.com/" + (r.Repository ?? "igor-couto"), OperatingSystem = "Linux", Architecture = "ARM64", Version = "2.337.0",
            ProcessStatus = r.Status is "busy" or "idle" ? "running" : r.Status is "offline" ? "stopped" : "unknown",
            UptimeSeconds = r.Pid is not null ? 172840 + index * 310 : null,
            ServiceName = $"actions.runner.igor-couto.{index + 1}.service", ServiceState = r.Pid is not null ? "active" : index == 2 ? "inactive" : "unknown",
            ServiceSubState = r.Pid is not null ? "running" : null,
            LastJob = index == 3 ? null : new("Build and test", index == 0 ? null : index == 1 ? "Succeeded" : "Failed", now.AddMinutes(-5 - index * 40)),
            LastActivityAt = index == 3 ? null : now.AddMinutes(-5 - index * 40),
            GitHub = index == 3 ? new("not_configured") : new(index == 2 ? "offline" : "online", index == 0, ["self-hosted", "Linux", "ARM64", "home-lab"], now)
        }).ToArray();
        return new("home-server", "/srv/actions-runners", now, true, null, null, runners) { Roots = ["/srv/actions-runners"] };
    }
}
