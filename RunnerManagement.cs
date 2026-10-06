using System.Runtime.InteropServices;
using System.Security.Claims;
using System.Text.Json;
using System.Threading.Channels;

namespace RunnerRoom;

public sealed class RunnerManagement : BackgroundService
{
    private readonly RunnerOptions options;
    private readonly ManagementGitHub github;
    private readonly RunnerMonitor monitor;
    private readonly ManagedRunnerRuntime runtime;
    private readonly object gate = new();
    private readonly Channel<(ManagementOperation Op, Func<CancellationToken, Task> Work)> queue = Channel.CreateBounded<(ManagementOperation, Func<CancellationToken, Task>)>(100);
    private readonly string file;
    private ManagementState state = new();
    private FileStream? ownership;
    private CancellationTokenSource? activeCancellation;
    private string? activeId;
    private readonly HashSet<string> reserved = [];
    private string? warning;
    private RunnerRelease? latest;
    private DateTimeOffset workflowPoll;
    private JsonElement cachedState;
    private bool Available => options.Demo || options.Management.Enabled && warning is null;
    [DllImport("libc")] private static extern uint geteuid();

    public RunnerManagement(RunnerOptions options, ManagementGitHub github, RunnerMonitor monitor)
    {
        this.options = options; this.github = github; this.monitor = monitor; runtime = new(options);
        file = Path.Combine(AccessSetup.StateDirectory(options), "management.json");
        if (options.Demo)
        {
            state.Runners = [new() { Id = "demo-api", Path = "/srv/actions-runners/activities-api", AgentId = 1, Version = "2.337.0", State = "running", DesiredRunning = true,
                Settings = new() { Name = "activities-api", Scope = "repo:igor-couto/activities-api", Pool = "apis", Labels = ["home-lab", "arm64"] } },
                new() { Id = "demo-web", Path = "/srv/actions-runners/newsletter", AgentId = 2, Version = "2.336.0", State = "stopped",
                Settings = new() { Name = "newsletter", Scope = "repo:igor-couto/newsletter", Labels = ["home-lab"], Mode = "user-service" } }];
            state.Pools.Add(new("apis", state.Runners[0].Settings with { }));
            Save();
            return;
        }
        if (!options.Management.Enabled) return;
        if (!OperatingSystem.IsLinux() || !options.Access.Enabled || geteuid() == 0) throw new InvalidOperationException("Runner management requires Linux, Access.Enabled and a non-root runner account.");
        if (string.IsNullOrWhiteSpace(options.Management.RootDirectory)) throw new InvalidOperationException("Set Management.RootDirectory to a dedicated writable directory.");
        options.Management.RootDirectory = ManagementValidation.SafeDirectory(options.Management.RootDirectory);
        if (options.Management.RootDirectory is "/" or "/home" or "/etc" or "/usr" or "/var" || options.Management.MaximumRunners is < 1 or > 500 ||
            options.Management.MaximumBatchSize is < 1 or > 50 || options.Management.DrainTimeoutMinutes is < 1 or > 1440) throw new InvalidOperationException("Invalid management directory, capacity or drain timeout.");
        foreach (var scope in options.Management.AllowedScopes) ManagementValidation.Scope(scope);
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(file)!);
            ownership = new FileStream(file + ".lock", FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            if (File.Exists(file))
            {
                state = JsonSerializer.Deserialize<ManagementState>(LocalRunnerReader.ReadSmall(file, 8 * 1024 * 1024) ?? throw new JsonException()) ?? throw new JsonException();
                if (state.Runners.Count > 500 || state.Operations.Count > 500 || state.Runners.Select(r => r.Id).Distinct().Count() != state.Runners.Count) throw new JsonException();
                foreach (var runner in state.Runners)
                {
                    ManagementValidation.Settings(runner.Settings); runtime.ValidatePath(runner);
                    if (!Guid.TryParseExact(runner.Id, "N", out _)) throw new JsonException();
                }
            }
            foreach (var op in state.Operations.Where(o => o.State is "queued" or "running"))
            {
                op.State = "interrupted"; op.Message = "Application restarted during this operation. Inspect current state before retrying; operations are not replayed."; op.FinishedAt = DateTimeOffset.UtcNow;
                foreach (var runner in state.Runners.Where(r => op.Target.Split(',').Contains(r.Id) || r.State is "creating" or "updating" or "configuring" or "draining"))
                { runner.DesiredRunning = false; runner.State = "error"; runner.Error = op.Message; }
            }
            foreach (var runner in state.Runners)
            {
                if (!runner.Settings.RestoreOnRestart) runner.DesiredRunning = false;
                else if (runner.DesiredRunning && runner.State == "running") runner.State = "restoring";
            }
            Save();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or ManagementException or NullReferenceException)
        { warning = "Management state could not be locked, validated or saved. Original state is preserved; fix permissions or restore its backup before restarting."; }
    }
    private void Save()
    {
        cachedState = JsonSerializer.SerializeToElement(state, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        if (options.Demo) return;
        var temp = file + ".tmp";
        if (File.Exists(temp) && new FileInfo(temp).LinkTarget is not null) throw new ManagementException("Management state temporary file is a symbolic link.");
        File.WriteAllText(temp, JsonSerializer.Serialize(state));
        if (OperatingSystem.IsLinux()) File.SetUnixFileMode(temp, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        File.Move(temp, file, true);
    }
    private void Persist() { lock (gate) Save(); }
    private void Require()
    {
        if (!Available) throw new ManagementException(warning ?? "Management is disabled. Configure Management and enable dashboard authentication on the server.");
    }
    public object Snapshot()
    {
        lock (gate) return new { enabled = Available, demo = options.Demo, warning, root = options.Management.RootDirectory,
            scopes = options.Demo ? new[] { "repo:igor-couto/activities-api", "repo:igor-couto/newsletter", "org:igor-couto" } : options.Management.AllowedScopes,
            runners = cachedState.ValueKind == JsonValueKind.Object ? cachedState.GetProperty("runners") : JsonSerializer.SerializeToElement(Array.Empty<object>()),
            pools = cachedState.ValueKind == JsonValueKind.Object ? cachedState.GetProperty("pools") : JsonSerializer.SerializeToElement(Array.Empty<object>()),
            operations = cachedState.ValueKind == JsonValueKind.Object ? cachedState.GetProperty("operations") : JsonSerializer.SerializeToElement(Array.Empty<object>()),
            workflows = cachedState.ValueKind == JsonValueKind.Object ? cachedState.GetProperty("workflows") : JsonSerializer.SerializeToElement(Array.Empty<object>()), latest };
    }
    internal ManagementState CopyState() { lock (gate) return JsonSerializer.Deserialize<ManagementState>(JsonSerializer.Serialize(state))!; }
    private ManagedRunner Find(string id) => state.Runners.FirstOrDefault(r => r.Id == id) ?? throw new ManagementException("Managed runner not found.");
    private void Allow(string scope) { if (!options.Demo) github.Allow(scope); else ManagementValidation.Scope(scope); }
    private ManagementOperation Enqueue(string action, string target, HttpContext context, Func<CancellationToken, Task> work, string[]? ids = null)
    {
        Require();
        lock (gate)
        {
            ids ??= [];
            if (ids.Any(reserved.Contains)) throw new ManagementException("A selected runner already has a queued or running operation.");
            if (state.Operations.Count(o => o.State is "queued" or "running") >= 90) throw new ManagementException("Operation queue is full. Wait for current operations to finish.");
            var op = new ManagementOperation { Action = action, Target = target, Actor = context.User.FindFirstValue(ClaimTypes.NameIdentifier) ?? "demo" };
            state.Operations = state.Operations.Where(o => o.State is "queued" or "running").Concat(state.Operations.Where(o => o.State is not ("queued" or "running")).TakeLast(390)).Append(op).ToList();
            Save(); foreach (var id in ids) reserved.Add(id);
            queue.Writer.TryWrite((op, async token => { try { await work(token); } finally { lock (gate) foreach (var id in ids) reserved.Remove(id); } }));
            context.RequestServices.GetRequiredService<AccessAudit>().Add(context, "management_" + action, op.Actor);
            return op with { };
        }
    }
    public ManagementOperation Create(CreateRunners request, HttpContext context)
    {
        ManagementValidation.Settings(request.Settings); Allow(request.Settings.Scope);
        if (request.Settings.Mode == "system-service") throw new ManagementException("New runners use process or user-service mode. System services can only be imported.");
        if (request.Count < 1 || request.Count > options.Management.MaximumBatchSize) throw new ManagementException("Batch size exceeds the configured limit.");
        return Enqueue("create", request.Settings.Name, context, async token =>
        {
            for (var i = 0; i < request.Count; i++) await CreateOne(request.Settings with { Name = request.Count == 1 ? request.Settings.Name : request.Settings.Name + "-" + (i + 1) }, request.Start, token);
        });
    }
    private async Task CreateOne(RunnerSettings settings, bool start, CancellationToken token)
    {
        ManagementValidation.Settings(settings); Allow(settings.Scope);
        ManagedRunner runner;
        lock (gate)
        {
            if (state.Runners.Count >= options.Management.MaximumRunners) throw new ManagementException("Managed runner capacity reached.");
            if (state.Runners.Any(r => r.Settings.Scope == settings.Scope && r.Settings.Name.Equals(settings.Name, StringComparison.OrdinalIgnoreCase))) throw new ManagementException("A managed runner already has that name in this scope. Earlier batch items are kept.");
            runner = new() { Settings = settings, State = "creating" };
            runner.Path = Path.Combine(options.Management.RootDirectory ?? "/srv/managed-runners", runner.Id);
            state.Runners.Add(runner); Save();
        }
        try
        {
            if (!options.Demo)
            {
                runtime.ValidatePath(runner);
                if (Directory.Exists(runner.Path)) throw new ManagementException("Runner directory already exists; refusing to overwrite it.");
                await InstallRelease(runner, token);
                await runtime.Configure(runner, await github.RegistrationToken(settings.Scope, token), token);
                if (settings.Mode == "user-service") await runtime.InstallUserService(runner, token);
            }
            else { runner.AgentId = Random.Shared.Next(100, 9999); runner.Version = "2.337.0"; }
            runner.State = "stopped"; Persist();
            if (start) await Start(runner, token, true);
        }
        catch (Exception)
        {
            if (!options.Demo)
            {
                var local = LocalRunnerReader.Read(runner.Path, options);
                if (local.GitHubUrl == ManagementValidation.Url(settings.Scope) && local.Name == settings.Name) runner.AgentId = local.AgentId;
            }
            runner.State = "error"; runner.DesiredRunning = false; runner.Error = "Creation incomplete. Inspect diagnostics; registration and files may already exist. Retry registration or remove this entry after inspection."; Persist(); throw;
        }
    }
    public ManagementOperation Import(ImportRunner request, HttpContext context) => Enqueue("import", request.RunnerId, context, async token =>
    {
        var found = (await monitor.GetSnapshotAsync()).Runners.FirstOrDefault(r => r.Id == request.RunnerId) ?? throw new ManagementException("Installation is no longer in runner discovery.");
        if (found.GitHubUrl is null || !found.GitHubUrl.StartsWith("https://github.com/", StringComparison.Ordinal) || found.AgentId is not > 0) throw new ManagementException("Only configured GitHub.com runners with registration IDs can be imported.");
        var scope = found.Repository is { } repo ? "repo:" + repo : "org:" + found.Organization;
        var s = new RunnerSettings { Name = found.Name, Scope = scope, Mode = request.Mode, RestoreOnRestart = request.RestoreOnRestart,
            GitHubGroup = found.Repository is null ? found.RunnerGroup : null, Labels = found.GitHub.Labels?.Where(l => l is not ("self-hosted" or "Linux" or "X64" or "ARM" or "ARM64")).ToArray() ?? [] };
        if (LocalRunnerReader.ReadSmall(Path.Combine(found.Path, ".runner")) is { } content)
        {
            using var json = JsonDocument.Parse(content);
            if (json.RootElement.TryGetProperty("workFolder", out var work)) s.WorkFolder = work.GetString() ?? "_work";
            s.Ephemeral = json.RootElement.TryGetProperty("ephemeral", out var ephemeral) && ephemeral.ValueKind == JsonValueKind.True;
            s.DisableUpdate = json.RootElement.TryGetProperty("disableUpdate", out var update) && update.ValueKind == JsonValueKind.True;
        }
        ManagementValidation.Settings(s); Allow(s.Scope);
        if ((request.Mode == "process") != (found.ServiceName is null)) throw new ManagementException("Choose the installation's existing service mode, or remove its service locally before importing as a process.");
        var runner = new ManagedRunner { Path = found.Path, Settings = s, Imported = true, AgentId = found.AgentId, Version = found.Version, Service = found.ServiceName,
            DesiredRunning = found.ProcessStatus == "running", State = found.ProcessStatus == "running" ? "running" : "stopped" };
        if (!options.Demo)
        {
            runtime.ValidatePath(runner);
            if (request.Mode != "process") await runtime.ValidateServiceOwnership(runner, token);
        }
        lock (gate)
        {
            if (state.Runners.Any(r => r.Path == runner.Path)) throw new ManagementException("This installation is already managed.");
            if (state.Runners.Count >= options.Management.MaximumRunners) throw new ManagementException("Managed runner capacity reached.");
            state.Runners.Add(runner); Save();
        }
        token.ThrowIfCancellationRequested();
    });
    public ManagementOperation Act(RunnerAction request, HttpContext context)
    {
        if (request.Ids is null || request.Ids.Length is < 1 || request.Ids.Length > options.Management.MaximumBatchSize || request.Ids.Distinct().Count() != request.Ids.Length ||
            request.Action is not ("start" or "stop" or "restart" or "drain" or "unregister" or "remove" or "update" or "register")) throw new ManagementException("Select runners and a supported action within the batch limit.");
        lock (gate) foreach (var id in request.Ids) Find(id);
        return Enqueue(request.Action, string.Join(',', request.Ids), context, async token =>
        {
            var errors = new List<string>();
            foreach (var id in request.Ids)
            {
                var runner = Find(id);
                try { await ActOne(runner, request.Action, token); }
                catch (Exception ex) when (ex is not OperationCanceledException)
                { runner.State = "error"; runner.Error = Error(ex); runner.DesiredRunning = false; errors.Add(runner.Settings.Name + ": " + runner.Error); Persist(); }
                token.ThrowIfCancellationRequested();
            }
            if (errors.Count > 0) throw new ManagementException(string.Join(" ", errors));
        }, request.Ids);
    }
    private async Task ActOne(ManagedRunner runner, string action, CancellationToken token)
    {
        Allow(runner.Settings.Scope);
        if (action == "start") { await Start(runner, token, true); return; }
        if (action == "register")
        {
            if (runner.AgentId is not null || !options.Demo && File.Exists(Path.Combine(runner.Path, ".runner"))) throw new ManagementException("Runner is already configured. Unregister it before registering again.");
            if (!options.Demo) await runtime.Configure(runner, await github.RegistrationToken(runner.Settings.Scope, token), token);
            else runner.AgentId = Random.Shared.Next(100, 9999);
            runner.State = "stopped"; runner.Error = null; Persist(); return;
        }
        var resume = runner.DesiredRunning || !options.Demo && runtime.Read(runner).ProcessStatus == "running";
        runner.DesiredRunning = false; Persist();
        await Drain(runner, token);
        if (action is "stop" or "drain") return;
        if (action == "restart") { await Start(runner, token, true); return; }
        if (action == "update")
        {
            runner.State = "updating"; Persist();
            if (!options.Demo) await InstallRelease(runner, token); else runner.Version = "2.337.0";
            runner.State = "stopped"; Persist();
            if (resume) await Start(runner, token, true);
            return;
        }
        if (!options.Demo)
        {
            await github.Unregister(runner, token);
            ClearRegistration(runner);
        }
        runner.AgentId = null; runner.State = "unregistered"; Persist();
        if (action == "remove")
        {
            if (!options.Demo)
            {
                await runtime.RemoveUserService(runner, token);
                if (!runner.Imported)
                {
                    runtime.ValidatePath(runner);
                    var archive = Path.Combine(options.Management.RootDirectory!, ".removed");
                    ManagementValidation.SafeDirectory(archive); Directory.CreateDirectory(archive);
                    if (Directory.Exists(runner.Path)) Directory.Move(runner.Path, Path.Combine(archive, runner.Id + "-" + DateTimeOffset.UtcNow.ToUnixTimeSeconds()));
                }
            }
            lock (gate) { state.Runners.Remove(runner); Save(); }
        }
    }
    private void ClearRegistration(ManagedRunner runner)
    {
        runtime.ValidatePath(runner);
        foreach (var name in new[] { ".runner", ".runner_migrated", ".credentials", ".credentials_migrated", ".credentials_rsaparams" })
            File.Delete(Path.Combine(runner.Path, name));
    }
    private async Task Start(ManagedRunner runner, CancellationToken token, bool reset)
    {
        if (runner.AgentId is null) throw new ManagementException("Register this runner before starting it.");
        if (reset) runner.Retries = 0;
        runner.Error = null; runner.NextRetry = null; runner.DesiredRunning = true; runner.State = "starting"; Persist();
        if (!options.Demo) await runtime.Start(runner, token);
        runner.State = options.Demo ? "running" : "starting"; runner.StartedAt = DateTimeOffset.UtcNow; Persist();
    }
    private async Task Drain(ManagedRunner runner, CancellationToken token)
    {
        runner.State = "draining"; runner.Error = null; Persist();
        if (!options.Demo)
        {
            var deadline = DateTimeOffset.UtcNow.AddMinutes(options.Management.DrainTimeoutMinutes); var idle = 0;
            while (DateTimeOffset.UtcNow < deadline)
            {
                token.ThrowIfCancellationRequested(); var info = runtime.Read(runner);
                if (info.ProcessStatus == "stopped") break;
                var busy = info.WorkerPid is not null || info.ProcessStatus == "unknown" || await github.Busy(runner, token);
                idle = busy ? 0 : idle + 1;
                if (idle >= 2) break;
                await Task.Delay(5000, token);
            }
            if (DateTimeOffset.UtcNow >= deadline) throw new ManagementException("Drain timed out. Active jobs were left running. Pause workflow producers and retry when the runner is idle.");
            await runtime.Stop(runner, token);
        }
        runner.State = "stopped"; Persist();
    }
    private async Task InstallRelease(ManagedRunner runner, CancellationToken token)
    {
        runtime.ValidatePath(runner); var release = await github.Latest(token);
        var staging = Path.Combine(options.Management.RootDirectory!, ".packages", Guid.NewGuid().ToString("N"));
        ManagementValidation.SafeDirectory(staging);
        await RunnerPackages.Download(release, staging, token);
        await RunnerPackages.VerifyRuntime(staging, release, token);
        RunnerPackages.Install(Path.Combine(staging, "files"), runner.Path, Path.Combine(staging, "previous"));
        runner.Version = release.Version; latest = release; Persist();
    }
    public ManagementOperation Configure(ConfigureRunner request, HttpContext context)
    {
        ManagementValidation.Settings(request.Settings); Allow(request.Settings.Scope);
        return Enqueue("configure", request.Id, context, async token =>
        {
            var runner = Find(request.Id); var old = runner.Settings; var next = request.Settings;
            if (next.Scope != old.Scope || next.Name != old.Name) throw new ManagementException("Name and scope cannot change in place. Create a new runner to move registrations.");
            if (next.Mode != old.Mode)
            {
                if (runner.Imported || next.Mode == "system-service") throw new ManagementException("Imported service execution modes must be changed locally. Runner Room can switch its own installations between process and user-service modes.");
                runner.DesiredRunning = false; Persist(); await Drain(runner, token);
                if (!options.Demo && old.Mode == "user-service") await runtime.RemoveUserService(runner, token);
                runner.Service = null; runner.Settings = old with { Mode = next.Mode }; Persist();
                if (!options.Demo && next.Mode == "user-service") await runtime.InstallUserService(runner, token);
            }
            var reconfigure = next.WorkFolder != old.WorkFolder || next.Ephemeral != old.Ephemeral || next.DisableUpdate != old.DisableUpdate || next.GitHubGroup != old.GitHubGroup;
            if (reconfigure)
            {
                if (runner.Imported && old.Mode != "process") throw new ManagementException("Imported service registration settings must be changed locally. Labels and retry settings can be changed here.");
                await ActOne(runner, "unregister", token); runner.Settings = next; Persist();
                await ActOne(runner, "register", token);
            }
            else
            {
                if (!old.Labels.SequenceEqual(next.Labels) && !options.Demo) await github.Labels(runner, next.Labels, token);
                runner.Settings = next; Persist();
            }
        }, [request.Id]);
    }
    public ManagementOperation Pool(ManagedPool pool, HttpContext context)
    {
        pool.Template.Pool = pool.Name; ManagementValidation.Settings(pool.Template); Allow(pool.Template.Scope);
        if (pool.Template.Mode == "system-service") throw new ManagementException("Pool templates use process or user-service mode.");
        return Enqueue("pool_save", pool.Name, context, token =>
        {
            lock (gate) { if (state.Pools.Count >= 100 && !state.Pools.Any(p => p.Name == pool.Name)) throw new ManagementException("Pool limit reached."); state.Pools.RemoveAll(p => p.Name == pool.Name); state.Pools.Add(pool); Save(); }
            return Task.CompletedTask;
        });
    }
    public ManagementOperation Scale(ScalePool request, HttpContext context)
    {
        if (request.Count < 0 || request.Count > options.Management.MaximumRunners) throw new ManagementException("Requested count exceeds capacity.");
        return Enqueue("scale", request.Name, context, async token =>
        {
            var pool = state.Pools.FirstOrDefault(p => p.Name == request.Name) ?? throw new ManagementException("Save a pool template before scaling.");
            var members = state.Runners.Where(r => r.Settings.Pool == pool.Name).ToArray();
            if (Math.Abs(request.Count - members.Length) > options.Management.MaximumBatchSize) throw new ManagementException("Scale in smaller batches within MaximumBatchSize.");
            lock (gate) if (members.Any(r => reserved.Contains(r.Id))) throw new ManagementException("A pool member already has a queued operation.");
            if (request.Count < members.Length)
                foreach (var runner in members.OrderBy(r => r.DesiredRunning).Take(members.Length - request.Count)) await ActOne(runner, "remove", token);
            else for (var i = members.Length; i < request.Count; i++)
                await CreateOne(pool.Template with { Name = pool.Template.Name[..Math.Min(80, pool.Template.Name.Length)] + "-" + Guid.NewGuid().ToString("N")[..8] }, true, token);
        });
    }
    public ManagementOperation Version(HttpContext context) => Enqueue("version_check", "GitHub release", context, async token =>
    { latest = options.Demo ? new("2.337.0", "arm64", "https://github.com/actions/runner/releases", "sample") : await github.Latest(token); });
    public ManagementOperation Workflow(WorkflowAction request, HttpContext context)
    {
        Allow("repo:" + request.Repository);
        if (request.RunId <= 0 || request.Action is not ("track" or "rerun" or "rerun-failed" or "cancel")) throw new ManagementException("Choose a valid workflow action and positive run ID.");
        return Enqueue("workflow_" + request.Action, request.Repository + "/" + request.RunId, context, async token =>
        {
            var result = options.Demo ? new WorkflowAttempt(request.Repository, request.RunId, request.Action.StartsWith("rerun") ? 2 : 1,
                request.Action == "cancel" ? "completed" : "queued", request.Action == "cancel" ? "cancelled" : null, $"https://github.com/{request.Repository}/actions/runs/{request.RunId}", DateTimeOffset.UtcNow) : await github.Workflow(request, token);
            lock (gate) { state.Workflows.RemoveAll(w => w.Repository == result.Repository && w.RunId == result.RunId); state.Workflows.Add(result); state.Workflows = state.Workflows.TakeLast(30).ToList(); Save(); }
        });
    }
    public JsonElement? GroupResult { get; private set; }
    public ManagementOperation Group(GitHubGroupAction request, HttpContext context)
    {
        Allow("org:" + request.Organization);
        return Enqueue("github_group_" + request.Action, request.Organization, context, async token =>
        {
            var result = options.Demo ? JsonSerializer.SerializeToElement(new { message = "Sample group operation completed. No GitHub changes were made." }) : await github.Groups(request, token);
            GroupResult = result.ValueKind == JsonValueKind.Undefined ? JsonSerializer.SerializeToElement(new { message = "Group action completed." }) : result;
        });
    }
    public void Cancel(string id)
    {
        Require(); lock (gate)
        {
            var op = state.Operations.FirstOrDefault(o => o.Id == id) ?? throw new ManagementException("Operation not found.");
            if (op.State == "queued") { op.State = "cancelled"; op.Message = "Cancelled before execution."; op.FinishedAt = DateTimeOffset.UtcNow; Save(); }
            else if (activeId == id) activeCancellation?.Cancel();
        }
    }
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!Available) return;
        while (!stoppingToken.IsCancellationRequested)
        {
            if (queue.Reader.TryRead(out var item))
            {
                using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
                lock (gate) { activeId = item.Op.Id; activeCancellation = cancellation; if (item.Op.State == "cancelled") cancellation.Cancel(); else item.Op.State = "running"; Save(); }
                try { cancellation.Token.ThrowIfCancellationRequested(); await item.Work(cancellation.Token); item.Op.State = "succeeded"; item.Op.Message = options.Demo ? "Demo operation completed; no host or GitHub changes." : "Completed."; }
                catch (OperationCanceledException) { item.Op.State = "interrupted"; item.Op.Message = "Operation interrupted. Completed changes were kept; inspect runner state before retrying."; }
                catch (Exception ex) { item.Op.State = "failed"; item.Op.Message = Error(ex); }
                finally
                {
                    lock (gate)
                    {
                        foreach (var id in item.Op.Target.Split(',')) reserved.Remove(id);
                        foreach (var runner in state.Runners.Where(r => r.State is "draining" or "configuring" or "updating"))
                        { runner.State = "error"; runner.Error = item.Op.Message; runner.DesiredRunning = false; }
                        item.Op.FinishedAt = DateTimeOffset.UtcNow; activeId = null; activeCancellation = null; Save();
                    }
                }
            }
            else
            {
                try { await Reconcile(stoppingToken); }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
                catch (Exception ex) { lock (gate) warning = Error(ex); break; }
                try { await Task.Delay(2000, stoppingToken); } catch (OperationCanceledException) { break; }
            }
        }
    }
    private async Task Reconcile(CancellationToken token)
    {
        if (options.Demo) return;
        foreach (var runner in state.Runners.ToArray())
        {
            lock (gate) if (reserved.Contains(runner.Id)) continue;
            try
            {
                Allow(runner.Settings.Scope);
                var info = runtime.Read(runner);
                if (info.Version is not null) runner.Version = info.Version;
                if (info.ProcessStatus == "running") { if (runner.State is "starting" or "retry_wait" or "stopped" or "restoring") runner.State = "running"; continue; }
                if (info.ProcessStatus == "unknown") { runner.Error = "Process visibility is incomplete; automatic start is paused."; continue; }
                if (!runner.DesiredRunning) { if (runner.State == "running") runner.State = "stopped"; continue; }
                if (runner.State == "restoring") { await Start(runner, token, false); continue; }
                if (runner.StartedAt is { } started && DateTimeOffset.UtcNow - started < TimeSpan.FromSeconds(15)) continue;
                if (runner.Settings.Ephemeral)
                {
                    runner.DesiredRunning = false; runner.State = "completed";
                    if (!File.Exists(Path.Combine(runner.Path, ".runner"))) runner.AgentId = null;
                    continue;
                }
                if (runner.Retries >= runner.Settings.RetryLimit) { runner.DesiredRunning = false; runner.State = "error"; runner.Error = "Automatic restart limit reached. Inspect diagnostics, then choose Start to reset the retry budget."; continue; }
                if (runner.NextRetry is null) { runner.NextRetry = DateTimeOffset.UtcNow.AddSeconds(runner.Settings.RetryDelaySeconds); runner.State = "retry_wait"; }
                if (DateTimeOffset.UtcNow < runner.NextRetry) continue;
                runner.Retries++; await Start(runner, token, false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException) { runner.Error = Error(ex); runner.State = "error"; runner.DesiredRunning = false; }
        }
        if (DateTimeOffset.UtcNow - workflowPoll > TimeSpan.FromSeconds(30))
        {
            workflowPoll = DateTimeOffset.UtcNow;
            foreach (var workflow in state.Workflows.ToArray())
            {
                try { var updated = await github.ReadWorkflow(workflow.Repository, workflow.RunId, token); lock (gate) { state.Workflows.Remove(workflow); state.Workflows.Add(updated); } }
                catch (Exception ex) when (ex is not OperationCanceledException) { /* Keep the checked timestamp: stale data is visible, not a fabricated result. */ }
            }
        }
        Persist();
    }
    private static string Error(Exception ex) => ex is ManagementException ? ex.Message : ex is UnauthorizedAccessException ?
        "Permission denied. Check runner ownership, writable service paths and systemd authorization." : ex is IOException ?
        "Filesystem operation failed. Check free space, ownership and the configured management directory." : ex is HttpRequestException or TaskCanceledException ?
        "GitHub or download connection failed. Inspect current state before retrying; an operation may have reached GitHub." : "Operation failed. Inspect local runner diagnostics and settings before retrying.";
    public override void Dispose() { ownership?.Dispose(); base.Dispose(); }
}
