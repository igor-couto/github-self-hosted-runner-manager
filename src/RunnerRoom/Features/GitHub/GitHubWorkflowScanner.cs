using System.Text;
using System.Text.Json;
using YamlDotNet.Core;
using YamlDotNet.Core.Events;
using YamlDotNet.RepresentationModel;

namespace RunnerRoom.Features.GitHub;

public sealed record GitHubWorkflowJob(string Id, string Name, string[] Labels, string? Group, bool Dynamic, bool SelfHosted);
public sealed record GitHubWorkflowFile(string File, string Name, GitHubWorkflowJob[] Jobs);
public sealed record GitHubWorkflowScan(string Repository, DateTimeOffset CheckedAt, GitHubWorkflowFile[] Workflows, string[] Warnings);

// Reads workflow definitions only. Expressions and reusable workflows are never evaluated.
public sealed class GitHubWorkflowScanner(GitHubSyncClient client)
{
    private readonly SemaphoreSlim gate = new(1);
    private readonly Dictionary<string, GitHubWorkflowScan> cache = new();
    public async Task<GitHubWorkflowScan> ScanAsync(string repository, CancellationToken cancellation)
    {
        repository = GitHubSyncClient.Repository(repository);
        await gate.WaitAsync(cancellation);
        try
        {
            if (cache.TryGetValue(repository, out var previous) && previous.CheckedAt > DateTimeOffset.UtcNow.AddMinutes(-5)) return previous;
            var warnings = new List<string>(); var files = new List<GitHubWorkflowFile>();
            JsonDocument directory;
            try { directory = await client.GetAsync($"repos/{repository}/contents/.github/workflows", cancellation); }
            catch (GitHubSyncException ex) when (ex.StatusCode == 404)
            { return new(repository, DateTimeOffset.UtcNow, [], ["Workflow folder not found, or the credential lacks Contents read access."]); }
            using (directory)
            {
                if (directory.RootElement.ValueKind != JsonValueKind.Array) throw new GitHubSyncException("GitHub returned an unexpected workflow folder.");
                var entries = directory.RootElement.EnumerateArray().Where(item => GitHubSyncClient.Text(item, "type") == "file" &&
                    GitHubSyncClient.Text(item, "name") is { } name && (name.EndsWith(".yml", StringComparison.OrdinalIgnoreCase) || name.EndsWith(".yaml", StringComparison.OrdinalIgnoreCase))).ToArray();
                if (entries.Length > 30) warnings.Add("Only the first 30 workflow files were inspected.");
                foreach (var entry in entries.Take(30))
                {
                    var name = GitHubSyncClient.Text(entry, "name")!;
                    if (name.Contains('/') || name.Contains('\\') || name.Length > 180 || GitHubSyncClient.Number(entry, "size") > 131072)
                    { warnings.Add("A workflow file was skipped because its name or size exceeded inspection limits."); continue; }
                    using var content = await client.GetAsync($"repos/{repository}/contents/.github/workflows/{Uri.EscapeDataString(name)}", cancellation);
                    var root = content.RootElement;
                    if (GitHubSyncClient.Text(root, "encoding") != "base64" || !root.TryGetProperty("content", out var data) || data.ValueKind != JsonValueKind.String)
                    { warnings.Add($"{name}: GitHub did not return readable file content."); continue; }
                    try
                    {
                        var encoded = data.GetString()!;
                        if (encoded.Length > 180000) throw new FormatException();
                        var bytes = Convert.FromBase64String(encoded);
                        if (bytes.Length > 131072) throw new FormatException();
                        files.Add(Parse(name, Encoding.UTF8.GetString(bytes)));
                    }
                    catch (Exception ex) when (ex is YamlException or FormatException or InvalidOperationException or ArgumentException)
                    { warnings.Add($"{name}: could not inspect this workflow safely. Open it on GitHub to review its runner requirements."); }
                }
            }
            warnings.Add("Requirements use the default branch. Expressions, matrix values, and reusable workflows need review on GitHub.");
            var result = new GitHubWorkflowScan(repository, DateTimeOffset.UtcNow, files.ToArray(), warnings.ToArray());
            if (cache.Count >= 25) cache.Clear();
            cache[repository] = result; return result;
        }
        finally { gate.Release(); }
    }

    internal static GitHubWorkflowFile Parse(string file, string yaml)
    {
        if (yaml.Length > 131072) throw new FormatException();
        // Reject aliases and deeply nested input before building a YAML object graph.
        var parser = new Parser(new StringReader(yaml)); var events = 0; var depth = 0;
        while (parser.MoveNext())
        {
            if (++events > 12000 || parser.Current is AnchorAlias) throw new FormatException();
            if (parser.Current is MappingStart or SequenceStart && ++depth > 32) throw new FormatException();
            if (parser.Current is MappingEnd or SequenceEnd) depth--;
        }
        var stream = new YamlStream(); stream.Load(new StringReader(yaml));
        if (stream.Documents.Count != 1 || stream.Documents[0].RootNode is not YamlMappingNode root) throw new FormatException();
        var results = new List<GitHubWorkflowJob>();
        if (Get(root, "jobs") is YamlMappingNode jobs)
            foreach (var pair in jobs.Children.Take(100))
            {
                if (pair.Key is not YamlScalarNode id || pair.Value is not YamlMappingNode job) continue;
                var labels = new List<string>(); string? group = null; var dynamic = Get(job, "uses") is not null;
                void Add(YamlNode? node)
                {
                    if (node is YamlScalarNode scalar && Text(scalar) is { } text) { labels.Add(text); dynamic |= text.Contains("${{"); }
                    else if (node is not null) dynamic = true;
                }
                void Labels(YamlNode? node)
                { if (node is YamlSequenceNode sequence) foreach (var value in sequence.Children.Take(50)) Add(value); else Add(node); }
                var runsOn = Get(job, "runs-on");
                if (runsOn is YamlMappingNode mapping)
                { group = Text(Get(mapping, "group")); dynamic |= group?.Contains("${{") == true; Labels(Get(mapping, "labels")); }
                else Labels(runsOn);
                results.Add(new(Text(id) ?? "job", Text(Get(job, "name")) ?? Text(id) ?? "Job", labels.ToArray(), group, dynamic,
                    labels.Any(l => l.Equals("self-hosted", StringComparison.OrdinalIgnoreCase)) || group is not null));
            }
        return new(file, Text(Get(root, "name")) ?? file, results.ToArray());
    }
    private static YamlNode? Get(YamlMappingNode node, string key) => node.Children.TryGetValue(new YamlScalarNode(key), out var value) ? value : null;
    private static string? Text(YamlNode? node) => node is YamlScalarNode { Value: { } value } ? new(value.Where(c => !char.IsControl(c)).Take(256).ToArray()) : null;
}
