using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace RunnerRoom;

public sealed record JobStep(string Name, string Status, DateTimeOffset StartedAt, DateTimeOffset? CompletedAt = null);
public sealed record CurrentJob(string Name, DateTimeOffset StartedAt, double ElapsedSeconds)
{
    public string Status { get; init; } = "running";
    public string? Workflow { get; init; }
    public string? Repository { get; init; }
    public string? Ref { get; init; }
    public string? Commit { get; init; }
    public string? Actor { get; init; }
    public string? Event { get; init; }
    public string? RunUrl { get; init; }
    public string? LogFile { get; init; }
    public JobStep[] Steps { get; init; } = [];
    public bool Partial { get; init; }
    public string? Message { get; init; }
}

internal static class CurrentJobReader
{
    internal static CurrentJob? Read(RunnerInfo runner, RunnerOptions options, DateTimeOffset now)
    {
        if (runner.Status != "busy" || runner.WorkerStartedAt is not { } start) return null;
        var redactor = new LogRedactor(options);
        // Worker log filenames record process startup in UTC. Never attach an old job just because it was last.
        var files = RunnerLogs.List(runner.Path).Where(f => f.Kind == "Worker")
            .Select(f => (File: f, Started: FileStartedAt(f.Name)))
            .Where(f => f.Started is { } at && Math.Abs((at - start).TotalSeconds) <= 5)
            .OrderBy(f => Math.Abs((f.Started!.Value - start).TotalSeconds)).ToArray();
        var file = files.FirstOrDefault().File;
        var fallback = new CurrentJob("Job details unavailable", start, Math.Max(0, (now - start).TotalSeconds))
        { Partial = true, Message = "A worker is running. Its diagnostic log could not be matched or read." };
        if (file is null) return fallback;
        var head = RunnerLogs.Read(runner.Path, file.Name, false, 1024 * 1024);
        var tail = RunnerLogs.Read(runner.Path, file.Name, true);
        if (head is null || tail is null) return fallback;
        return Parse(head.Value.Text, head.Value.Truncated ? tail.Value.Text : head.Value.Text,
            start, now, redactor, head.Value.Truncated) with { LogFile = file.Name };
    }

    internal static DateTimeOffset? FileStartedAt(string name) => name.Length >= 26 &&
        DateTimeOffset.TryParseExact(name.Substring(7, 15), "yyyyMMdd-HHmmss", CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal, out var at) ? at : null;

    private static JsonElement Property(JsonElement value, string name)
    {
        if (value.ValueKind == JsonValueKind.Object)
            foreach (var p in value.EnumerateObject())
                if (p.Name.Equals(name, StringComparison.OrdinalIgnoreCase)) return p.Value;
        return default;
    }
    private static string? Text(JsonElement value) => value.ValueKind == JsonValueKind.String ? LocalRunnerReader.Text(value.GetString(), 300) : null;
    private static string? Context(JsonElement github, string key)
    {
        var direct = Property(github, key);
        if (direct.ValueKind != JsonValueKind.Undefined) return Text(direct) ?? Text(Property(direct, "s"));
        var pairs = Property(github, "d");
        if (pairs.ValueKind == JsonValueKind.Array)
            foreach (var pair in pairs.EnumerateArray())
                if (Text(Property(pair, "k")) == key)
                { var value = Property(pair, "v"); return Text(value) ?? Text(Property(value, "s")); }
        return null;
    }

    internal static CurrentJob Parse(string head, string progress, DateTimeOffset started, DateTimeOffset now, LogRedactor redactor, bool partial)
    {
        var result = new CurrentJob("Job details unavailable", started, Math.Max(0, (now - started).TotalSeconds)) { Partial = partial };
        try
        {
            var marker = Regex.Match(head, @"^\[[^\r\n]+ INFO Worker\] Job message:\s*", RegexOptions.Multiline | RegexOptions.CultureInvariant | RegexOptions.NonBacktracking);
            if (marker.Success)
            {
                var reader = new Utf8JsonReader(Encoding.UTF8.GetBytes(head[(marker.Index + marker.Length)..]), new JsonReaderOptions { MaxDepth = 64 });
                if (JsonDocument.TryParseValue(ref reader, out var document))
                {
                    using (document)
                    {
                        var root = document.RootElement;
                        var github = Property(Property(root, "contextData"), "github");
                        string? Get(string key) => Context(github, key) is { } value ? redactor.Redact(value) : null;
                        var repository = Get("repository");
                        var run = Get("run_id");
                        var server = Get("server_url");
                        string? runUrl = null;
                        if (repository is not null && Regex.IsMatch(repository, @"^[a-zA-Z0-9_.-]+/[a-zA-Z0-9_.-]+$", RegexOptions.NonBacktracking) &&
                            repository.Split('/').All(part => part is not "." and not "..") &&
                            long.TryParse(run, out var id) && id > 0 && (server is null || server == "https://github.com"))
                            runUrl = $"https://github.com/{repository}/actions/runs/{id}";
                        var name = Text(Property(root, "jobDisplayName")) ?? Text(Property(root, "jobName"));
                        result = result with { Name = name is null ? result.Name : redactor.Redact(name), Workflow = Get("workflow"),
                            Repository = repository, Ref = Get("ref"), Commit = Get("sha"), Actor = Get("actor"), Event = Get("event_name"), RunUrl = runUrl };
                    }
                }
            }
        }
        catch (JsonException) { /* Logs rotate and may end midway through the message. */ }

        var steps = new List<JobStep>();
        var finished = false;
        foreach (var raw in progress.Split('\n'))
        {
            var line = RunnerLogs.ParseLine(raw);
            if (line is null || line.At < started.AddSeconds(-5) || line.At > now.AddSeconds(5)) continue;
            if (line.Source == "StepsRunner")
            {
                const string prefix = "Processing step: DisplayName='";
                if (line.Message.StartsWith(prefix, StringComparison.Ordinal) && line.Message.EndsWith('\''))
                {
                    // A new step is not evidence that the preceding step succeeded.
                    if (steps.Count > 0 && steps[^1].Status == "running") steps[^1] = steps[^1] with { Status = "unknown" };
                    steps.Add(new(redactor.Redact(LocalRunnerReader.Text(line.Message[prefix.Length..^1]) ?? "Unnamed step"), "running", line.At));
                    if (steps.Count > 200) { steps.RemoveAt(0); partial = true; }
                }
                else if (steps.Count > 0)
                {
                    var status = line.Message switch
                    {
                        "Step result: Succeeded" => "succeeded", "Step result: Failed" => "failed",
                        "Step result: Canceled" => "canceled", "Step result: SucceededWithIssues" => "warning",
                        "Skipping step due to condition evaluation." => "skipped", _ => null
                    };
                    if (status is not null) steps[^1] = steps[^1] with { Status = status, CompletedAt = line.At };
                }
            }
            if (line.Source == "Worker" && line.Message == "Job completed.") finished = true;
        }
        if (finished && steps.Count > 0 && steps[^1].Status == "running") steps[^1] = steps[^1] with { Status = "unknown" };
        return result with { Steps = steps.ToArray(), Status = finished ? "finishing" : "running", Partial = partial,
            Message = partial ? "Recent step events only; earlier events have fallen outside the read window." :
                result.Name == "Job details unavailable" ? "Workflow metadata is not available in this diagnostic log." : null };
    }
}
