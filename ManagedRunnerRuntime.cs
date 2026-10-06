using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace RunnerRoom;

internal sealed class ManagedRunnerRuntime(RunnerOptions options)
{
    [DllImport("libc", SetLastError = true)] private static extern int kill(int pid, int signal);
    internal static ProcessStartInfo Command(string executable, string directory, params string[] args)
    {
        var start = new ProcessStartInfo(executable) { WorkingDirectory = directory, UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true };
        // Never pass the dashboard's configuration, OAuth secret or tokens into a runner job's environment.
        var env = new[] { "HOME", "USER", "LOGNAME", "LANG", "LC_ALL", "XDG_RUNTIME_DIR", "DBUS_SESSION_BUS_ADDRESS" }
            .Select(k => (Key: k, Value: Environment.GetEnvironmentVariable(k))).ToArray();
        start.Environment.Clear(); start.Environment["PATH"] = "/usr/local/bin:/usr/bin:/bin";
        foreach (var item in env) if (item.Value is not null) start.Environment[item.Key] = item.Value;
        foreach (var arg in args) start.ArgumentList.Add(arg);
        return start;
    }
    internal static async Task<string> Run(ProcessStartInfo start, CancellationToken token, int seconds = 60)
    {
        using var process = new Process { StartInfo = start };
        process.Start();
        static async Task<string> Read(StreamReader reader)
        {
            var text = new StringBuilder(); var buffer = new char[4096]; int count;
            while ((count = await reader.ReadAsync(buffer)) > 0) if (text.Length < 16384) text.Append(buffer, 0, Math.Min(count, 16384 - text.Length));
            return text.ToString();
        }
        var stdout = Read(process.StandardOutput); var stderr = Read(process.StandardError);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token); timeout.CancelAfter(TimeSpan.FromSeconds(seconds));
        try { await process.WaitForExitAsync(timeout.Token); }
        catch (OperationCanceledException) { try { process.Kill(true); } catch (InvalidOperationException) { } throw new ManagementException("Command timed out or was interrupted. Inspect the runner and service state before retrying."); }
        var output = await stdout; await stderr;
        // Commands can print credentials: never return stderr/stdout on failure to API, audit or operation history.
        if (process.ExitCode != 0) throw new ManagementException($"Runner/service command exited with code {process.ExitCode}. Check local runner diagnostics, native dependencies, directory ownership and systemd permissions.");
        return output.Trim();
    }
    internal void ValidatePath(ManagedRunner runner)
    {
        var path = ManagementValidation.SafeDirectory(runner.Path);
        var root = ManagementValidation.SafeDirectory(options.Management.RootDirectory!);
        if (!runner.Imported && !ManagementValidation.Within(path, root)) throw new ManagementException("Runner is outside the configured management directory.");
        if (runner.Imported)
        {
            var roots = options.RunnersRoots.Length > 0 ? options.RunnersRoots : options.RunnersRoot is { } single ? [single] : [];
            if (!roots.Select(ManagementValidation.SafeDirectory).Any(r => r == path || ManagementValidation.Within(path, r))) throw new ManagementException("Imported runner is outside configured discovery roots.");
        }
    }
    internal RunnerInfo Read(ManagedRunner runner)
    {
        ValidatePath(runner);
        return RunnerRuntime.ApplyProcesses(LocalRunnerReader.Read(runner.Path, options), RunnerRuntime.ReadProcesses(), DateTimeOffset.UtcNow);
    }
    private static string RunnerPath(ManagedRunner runner)
    {
        var saved = LocalRunnerReader.ReadSmall(Path.Combine(runner.Path, ".path"), 8192)?.Trim();
        return saved is { Length: > 0 } && !saved.Any(char.IsControl) ? saved : "/usr/local/bin:/usr/bin:/bin";
    }
    internal async Task Configure(ManagedRunner runner, string registrationToken, CancellationToken token)
    {
        ValidatePath(runner);
        var s = runner.Settings;
        var args = new List<string> { "configure", "--unattended", "--url", ManagementValidation.Url(s.Scope), "--name", s.Name, "--work", s.WorkFolder };
        if (s.Labels.Length > 0) args.AddRange(["--labels", string.Join(',', s.Labels)]);
        if (!string.IsNullOrEmpty(s.GitHubGroup)) args.AddRange(["--runnergroup", s.GitHubGroup]);
        if (s.Ephemeral) args.Add("--ephemeral");
        if (s.DisableUpdate) args.Add("--disableupdate");
        var start = Command(Executable(runner), runner.Path, args.ToArray());
        start.Environment["PATH"] = RunnerPath(runner);
        start.Environment["ACTIONS_RUNNER_INPUT_TOKEN"] = registrationToken;
        await Run(start, token, 180);
        var info = LocalRunnerReader.Read(runner.Path, options);
        if (info.AgentId is null) throw new ManagementException("Registration did not produce a runner ID. Inspect its diagnostics before retrying.");
        runner.AgentId = info.AgentId;
    }
    private string Executable(ManagedRunner runner)
    {
        var executable = LocalRunnerReader.ResolveExecutable(Path.Combine(runner.Path, "bin", "Runner.Listener"));
        if (!ManagementValidation.Within(executable, runner.Path)) throw new ManagementException("Runner executable points outside its installation.");
        return executable;
    }
    internal async Task Start(ManagedRunner runner, CancellationToken token)
    {
        var info = Read(runner);
        if (info.ProcessStatus == "unknown") throw new ManagementException("Process visibility is incomplete. Run Runner Room as the runner account.");
        if (info.ProcessStatus == "running") return;
        if (runner.Settings.Mode != "process")
        {
            await CheckService(runner, token);
            await Run(ServiceCommand(runner, "start"), token);
        }
        else
        {
            if (info.ServiceName is not null) throw new ManagementException("This runner has a service. Import using its service mode to avoid duplicate listeners.");
            var process = new Process { StartInfo = Command(Executable(runner), runner.Path, "run") };
            process.StartInfo.Environment["PATH"] = RunnerPath(runner);
            process.Start();
            // Diagnostic files are maintained by the runner. Drain pipes without retaining secrets or unbounded output.
            _ = DrainProcess(process);
        }
        runner.StartedAt = DateTimeOffset.UtcNow;
    }
    private static async Task DrainProcess(Process process)
    {
        try { await Task.WhenAll(process.StandardOutput.BaseStream.CopyToAsync(Stream.Null), process.StandardError.BaseStream.CopyToAsync(Stream.Null)); await process.WaitForExitAsync(); }
        catch (IOException) { }
        finally { process.Dispose(); }
    }
    internal async Task Stop(ManagedRunner runner, CancellationToken token)
    {
        var info = Read(runner);
        if (info.WorkerPid is not null || info.ProcessStatus == "unknown") throw new ManagementException("Runner became busy or its process state is unknown. Drain again; no stop signal was sent.");
        if (info.ProcessStatus == "stopped" && runner.Service is null) return;
        if (runner.Settings.Mode != "process")
        {
            await CheckService(runner, token); await Run(ServiceCommand(runner, "stop"), token);
        }
        else if (info.Pid is { } pid)
        {
            // Recheck the executable immediately before signalling; never trust a persisted PID.
            if (LocalRunnerReader.ResolveExecutable($"/proc/{pid}/exe") != Executable(runner)) throw new ManagementException("Runner process changed. Refresh and retry.");
            if (kill(pid, 15) != 0) throw new ManagementException("Could not signal this runner. Check process ownership.");
        }
        for (var i = 0; i < 60; i++)
        {
            if (Read(runner).ProcessStatus == "stopped") return;
            await Task.Delay(1000, token);
        }
        throw new ManagementException("Runner did not stop within 60 seconds. It was not forcibly killed; inspect local diagnostics.");
    }
    internal async Task InstallUserService(ManagedRunner runner, CancellationToken token)
    {
        ValidatePath(runner);
        runner.Service = "runner-room-" + runner.Id + ".service";
        var directory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".config/systemd/user");
        ManagementValidation.SafeDirectory(directory); Directory.CreateDirectory(directory);
        static string Quote(string value) => "\"" + value.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("%", "%%") + "\"";
        var content = "# Managed by Runner Room\n[Unit]\nDescription=Runner Room managed runner\n[Service]\nType=simple\nWorkingDirectory=" + Quote(runner.Path) +
            "\nEnvironment=" + Quote("PATH=" + RunnerPath(runner)) + "\nExecStart=" + Quote(Executable(runner)) + " run\nRestart=no\nKillMode=control-group\nTimeoutStopSec=60\n[Install]\nWantedBy=default.target\n";
        var file = Path.Combine(directory, runner.Service);
        if (File.Exists(file) && !File.ReadAllText(file).StartsWith("# Managed by Runner Room\n")) throw new ManagementException("Service name already exists and is not managed by Runner Room.");
        File.WriteAllText(file, content); File.WriteAllText(Path.Combine(runner.Path, ".service"), runner.Service);
        await Run(Command("systemctl", runner.Path, "--user", "daemon-reload"), token);
        // Runner Room owns desired state and retry policy; units are intentionally not enabled independently.
    }
    internal async Task ValidateServiceOwnership(ManagedRunner runner, CancellationToken token)
    {
        await CheckService(runner, token);
        var restart = await Run(ServiceCommand(runner, "show", "--property=Restart", "--value"), token);
        var enabled = await Run(ServiceCommand(runner, "show", "--property=UnitFileState", "--value"), token);
        if (restart != "no" || enabled is "enabled" or "enabled-runtime")
            throw new ManagementException("Before importing, set this runner service's Restart=no and disable its automatic boot start (without stopping it). Runner Room must be its only restart supervisor so retry limits and stopped state are respected.");
    }
    internal async Task RemoveUserService(ManagedRunner runner, CancellationToken token)
    {
        if (runner.Settings.Mode != "user-service" || runner.Imported) return;
        await CheckService(runner, token);
        await Run(ServiceCommand(runner, "disable"), token);
        var file = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".config/systemd/user", runner.Service!);
        if (!File.ReadAllText(file).StartsWith("# Managed by Runner Room\n")) throw new ManagementException("Refusing to remove a service not created by Runner Room.");
        File.Delete(file); File.Delete(Path.Combine(runner.Path, ".service"));
        await Run(Command("systemctl", runner.Path, "--user", "daemon-reload"), token);
    }
    private static ProcessStartInfo ServiceCommand(ManagedRunner runner, params string[] args) => Command("systemctl", runner.Path,
        (runner.Settings.Mode == "user-service" ? new[] { "--user", "--no-ask-password" } : ["--no-ask-password"]).Concat(args).Concat(["--", runner.Service!]).ToArray());
    private async Task CheckService(ManagedRunner runner, CancellationToken token)
    {
        ValidatePath(runner);
        if (runner.Service is null || !(runner.Service.StartsWith("actions.runner.") || runner.Service == "runner-room-" + runner.Id + ".service") ||
            !System.Text.RegularExpressions.Regex.IsMatch(runner.Service, @"^[A-Za-z0-9_.@:\\-]+\.service$")) throw new ManagementException("Only the service attached to this runner can be controlled.");
        var working = await Run(ServiceCommand(runner, "show", "--property=WorkingDirectory", "--value"), token);
        if (working != runner.Path) throw new ManagementException("Service working directory does not match this installation. Fix the service before managing it.");
    }
}
