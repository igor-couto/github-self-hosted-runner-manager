using System.Text.Json;
using RunnerRoom.Features.Runners;

namespace RunnerRoom.Features.GitHub;

internal sealed class GitHubSyncStore
{
    internal sealed class State
    {
        public int Version { get; set; } = 1;
        public GitHubSyncRepository[] Repositories { get; set; } = [];
        public GitHubSyncRun[] Runs { get; set; } = [];
        public GitHubSyncHistory[] History { get; set; } = [];
        public int HistoryDays { get; set; } = 30;
        public int MaxRunsPerRepository { get; set; } = 100;
    }
    private readonly string file;
    private readonly bool demo;
    private bool corrupt;
    internal string? Warning { get; private set; }
    internal GitHubSyncStore(string directory, bool demo) { file = Path.Combine(directory, "github-sync.json"); this.demo = demo; }
    internal State Load(int historyDays, int maxRuns)
    {
        var initial = new State { HistoryDays = Math.Clamp(historyDays, 1, 90), MaxRunsPerRepository = Math.Clamp(maxRuns, 1, 100) };
        if (demo || !File.Exists(file)) return initial;
        try
        {
            var text = LocalRunnerReader.ReadSmall(file, 32 * 1024 * 1024);
            var state = text is null ? null : JsonSerializer.Deserialize<State>(text);
            if (state is null || state.Version != 1 || state.Repositories is null || state.Runs is null || state.History is null || state.Repositories.Length > 500 || state.Runs.Length > 500 || state.History.Length > 50 ||
                state.HistoryDays is < 1 or > 90 || state.MaxRunsPerRepository is < 1 or > 100 ||
                state.Repositories.Any(r => r is null || GitHubSyncClient.Repository(r.FullName) != r.FullName || r.Url != $"https://github.com/{r.FullName}") ||
                state.Runs.Any(r => r is null || r.Id <= 0 || r.Attempt <= 0 || r.Jobs is null || r.Jobs.Length > 100 || !state.Repositories.Any(repo => repo.Selected && repo.FullName == r.Repository) ||
                    r.Url != $"https://github.com/{r.Repository}/actions/runs/{r.Id}/attempts/{r.Attempt}" || r.Jobs.Any(j => j is null || j.Steps is null || j.Labels is null || j.Steps.Length > 100 || j.Steps.Any(s => s is null))))
                throw new JsonException();
            return state;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or GitHubSyncException or ArgumentException)
        {
            corrupt = true;
            Warning = "Saved GitHub history could not be read. The original file is preserved; back it up and remove it to restore saving.";
            return initial;
        }
    }
    internal void Save(State state)
    {
        if (demo || corrupt) return;
        var temporary = file + ".new";
        try
        {
            var bytes = JsonSerializer.SerializeToUtf8Bytes(state);
            if (bytes.Length > 32 * 1024 * 1024) { Warning = "GitHub history is too large to save. Reduce the import limits; current results remain in memory."; return; }
            Directory.CreateDirectory(Path.GetDirectoryName(file)!);
            var fileOptions = new FileStreamOptions { Mode = FileMode.Create, Access = FileAccess.Write };
            if (!OperatingSystem.IsWindows()) fileOptions.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
            using (var stream = new FileStream(temporary, fileOptions))
            { stream.Write(bytes); stream.Flush(true); }
            File.Move(temporary, file, true);
            Warning = null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or ArgumentException)
        { Warning = "GitHub history could not be saved. Check the state directory permissions and available disk space."; }
        finally { try { if (File.Exists(temporary)) File.Delete(temporary); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { } }
    }
}
