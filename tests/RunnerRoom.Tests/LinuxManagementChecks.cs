using System.Diagnostics;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using RunnerRoom;

internal static class LinuxManagementChecks
{
    internal static async Task Run(Action<bool, string> check, string root, string stub, ManagementGitHub github)
    {
        if (!OperatingSystem.IsLinux()) return;
        var install = Path.Combine(root, "imported"); Directory.CreateDirectory(Path.Combine(install, "bin"));
        foreach (var name in new[] { "Runner.Listener", "Runner.Worker" }) { File.Copy(stub, Path.Combine(install, "bin", name)); File.SetUnixFileMode(Path.Combine(install, "bin", name), (UnixFileMode)0x1ED); }
        File.WriteAllText(Path.Combine(install, ".runner"), "{\"agentId\":42,\"agentName\":\"linux-test\",\"gitHubUrl\":\"https://github.com/owner/repo\",\"workFolder\":\"_work\"}");
        var stateDir = Path.Combine(root, "state");
        var options = new RunnerOptions { RunnersRoot = install, Monitoring = new() { StateDirectory = stateDir }, Access = new() { Enabled = true },
            Management = new() { Enabled = true, RootDirectory = Path.Combine(root, "created"), AllowedScopes = ["repo:owner/repo"] } };
        var monitor = new RunnerMonitor(options, new GitHubRunnerClient(new HttpClient(), new()));
        var context = new DefaultHttpContext { RequestServices = new ServiceCollection().AddSingleton(new AccessAudit(options)).BuildServiceProvider() };
        var runtime = new ManagedRunnerRuntime(options);
        string id;
        async Task Wait(Func<bool> condition, int seconds = 15) { for (var i = 0; i < seconds * 10; i++) { if (condition()) return; await Task.Delay(100); } throw new Exception("Timed out waiting for Linux management state."); }
        using (var manager = new RunnerManagement(options, github, monitor))
        {
            await manager.StartAsync(default);
            async Task Finish(ManagementOperation op) { await Wait(() => manager.CopyState().Operations.Single(o => o.Id == op.Id).FinishedAt is not null); var result = manager.CopyState().Operations.Single(o => o.Id == op.Id); check(result.State == "succeeded", "Linux operation completed: " + result.Action + ": " + result.Message); }
            await Finish(manager.Import(new((await monitor.GetSnapshotAsync()).Runners.Single().Id), context));
            var runner = manager.CopyState().Runners.Single(); id = runner.Id;
            await Finish(manager.Act(new([id], "start"), context));
            await Wait(() => runtime.Read(runner).ProcessStatus == "running");
            check(runtime.Read(runner).Pid is > 0, "Managed process start launches a real native listener under the non-root account.");
            using (var worker = Process.Start(new ProcessStartInfo(Path.Combine(install, "bin", "Runner.Worker")) { ArgumentList = { "run" } })!)
            {
                await Wait(() => runtime.Read(runner).WorkerPid is not null);
                try { await runtime.Stop(runner, default); check(false, "Stop must refuse active workers."); } catch (ManagementException) { check(true, "Stop does not signal a listener while a worker exists."); }
                worker.Kill(); await worker.WaitForExitAsync();
            }
            await Finish(manager.Act(new([id], "drain"), context));
            check(runtime.Read(runner).ProcessStatus == "stopped", "Drain waits for idle then stops the real listener.");
            await Finish(manager.Configure(new(id, runner.Settings with { RetryLimit = 1, RetryDelaySeconds = 5 }), context));
            await Finish(manager.Act(new([id], "start"), context));
            await Wait(() => runtime.Read(runner).Pid is not null);
            using (var process = Process.GetProcessById(runtime.Read(runner).Pid!.Value)) { process.Kill(); await process.WaitForExitAsync(); }
            await Wait(() => manager.CopyState().Runners.Single().Retries == 1 && runtime.Read(runner).ProcessStatus == "running", 40);
            check(manager.CopyState().Runners.Single().DesiredRunning, "Crashed runner is restarted within its retry budget.");
            using (var process = Process.GetProcessById(runtime.Read(runner).Pid!.Value)) { process.Kill(); await process.WaitForExitAsync(); }
            await Wait(() => !manager.CopyState().Runners.Single().DesiredRunning, 25);
            check(manager.CopyState().Runners.Single().State == "error", "Retry exhaustion stops the crash loop with an actionable error.");
            await Finish(manager.Act(new([id], "start"), context));
            await Wait(() => runtime.Read(runner).Pid is not null);
            await Wait(() => manager.CopyState().Runners.Single().State == "running");
            await manager.StopAsync(default);
            using (var process = Process.GetProcessById(runtime.Read(runner).Pid!.Value)) { process.Kill(); await process.WaitForExitAsync(); }
        }
        using (var restored = new RunnerManagement(options, github, monitor))
        {
            await restored.StartAsync(default);
            await Wait(() => runtime.Read(restored.CopyState().Runners.Single()).ProcessStatus == "running");
            check(restored.CopyState().Runners.Single().DesiredRunning, "Previously running installations are restored from persisted state after restart.");
            var op = restored.Act(new([id], "stop"), context);
            await Wait(() => restored.CopyState().Operations.Single(o => o.Id == op.Id).FinishedAt is not null);
            check(runtime.Read(restored.CopyState().Runners.Single()).ProcessStatus == "stopped", "Restored runner can be stopped without a stale persisted PID.");
            await restored.StopAsync(default);
        }
        var file = Path.Combine(stateDir, "management.json"); var saved = JsonSerializer.Deserialize<ManagementState>(File.ReadAllText(file))!;
        saved.Runners[0].State = "draining"; saved.Runners[0].DesiredRunning = true; saved.Operations.Add(new() { Action = "update", Target = id, State = "running" }); File.WriteAllText(file, JsonSerializer.Serialize(saved));
        using (var interrupted = new RunnerManagement(options, github, monitor))
            check(!interrupted.CopyState().Runners.Single().DesiredRunning && interrupted.CopyState().Operations.Last().State == "interrupted", "Interrupted maintenance never blindly replays or restarts the runner.");
        File.WriteAllText(file, "broken state");
        using (var corrupted = new RunnerManagement(options, github, monitor))
            check(File.ReadAllText(file) == "broken state" && JsonSerializer.Serialize(corrupted.Snapshot()).Contains("preserved"), "Corrupted state fails closed and preserves the original file.");
    }
}
