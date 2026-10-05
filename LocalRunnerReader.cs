using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace RunnerRoom;

internal static class LocalRunnerReader
{
    private static readonly Regex ServicePattern = new(@"^[a-zA-Z0-9_.@:\\-]+\.service$", RegexOptions.CultureInvariant);
    private static readonly Regex VersionPattern = new(@"^\d+\.\d+\.\d+(?:[.+-][a-zA-Z0-9.-]+)?$", RegexOptions.CultureInvariant);
    private static readonly HashSet<string> Excluded = new(["_work", "_diag", "bin", "externals"], StringComparer.OrdinalIgnoreCase);

    internal static string? Text(string? text, int max = 200) => string.IsNullOrWhiteSpace(text) ? null :
        new string(text.Trim().Where(c => !char.IsControl(c)).Take(max).ToArray());

    internal static string? ReadSmall(string path, int limit = 65536)
    {
        try
        {
            var file = new FileInfo(path);
            if (!file.Exists || file.LinkTarget is not null || file.Length > limit) return null;
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            var bytes = new byte[limit + 1];
            var count = stream.ReadAtLeast(bytes, limit + 1, false);
            return count > limit ? null : Encoding.UTF8.GetString(bytes, 0, count).TrimStart('\uFEFF');
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return null; }
    }

    internal static string Canonical(string path)
    {
        var info = new DirectoryInfo(System.IO.Path.GetFullPath(path));
        return System.IO.Path.TrimEndingDirectorySeparator(info.ResolveLinkTarget(true)?.FullName ?? info.FullName);
    }

    internal static (string[] Roots, string[] Folders, string[] Warnings) Discover(RunnerOptions options)
    {
        var roots = new List<string>();
        var folders = new HashSet<string>(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
        var warnings = new HashSet<string>();
        var configured = options.RunnersRoots.Length > 0 ? options.RunnersRoots :
            string.IsNullOrWhiteSpace(options.RunnersRoot) ? [] : new[] { options.RunnersRoot };
        var visited = 0;
        bool IsRunner(string path) => File.Exists(System.IO.Path.Combine(path, ".runner")) ||
            File.Exists(System.IO.Path.Combine(path, "bin", "Runner.Listener"));
        void Walk(string path, int depth, List<string> found)
        {
            if (++visited > 4096) { warnings.Add("Discovery reached its 4096-directory limit. Narrow the configured roots."); return; }
            if (depth > 0 && IsRunner(path)) { found.Add(path); return; }
            if (depth > 0 && (!options.Discovery.Recursive || depth >= Math.Clamp(options.Discovery.MaxDepth, 1, 16))) return;
            try
            {
                foreach (var child in Directory.EnumerateDirectories(path))
                {
                    var name = System.IO.Path.GetFileName(child);
                    if (name.StartsWith('.') || Excluded.Contains(name) || name.StartsWith("bin.") || name.StartsWith("externals.")) continue;
                    if (File.GetAttributes(child).HasFlag(FileAttributes.ReparsePoint)) continue;
                    Walk(child, depth + 1, found);
                    if (visited > 4096) break;
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            { warnings.Add($"Could not inspect {path}. Check directory permissions."); }
        }
        foreach (var configuredRoot in configured.Distinct())
        {
            try
            {
                var root = Canonical(configuredRoot);
                if (!Directory.Exists(root)) { warnings.Add($"Runner directory is unavailable: {root}"); continue; }
                if (roots.Contains(root)) continue;
                roots.Add(root);
                var found = new List<string>();
                Walk(root, 0, found);
                // Prefer children over stale registration metadata on a parent.
                if (found.Count == 0 && IsRunner(root)) found.Add(root);
                folders.UnionWith(found);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
            { warnings.Add("A configured runner directory could not be read. Check paths and permissions."); }
        }
        return (roots.ToArray(), folders.Order(StringComparer.Ordinal).ToArray(), warnings.ToArray());
    }

    internal static RunnerInfo Read(string folder, RunnerOptions options)
    {
        var name = System.IO.Path.GetFileName(folder);
        long? id = null;
        string? url = null, pool = null;
        try
        {
            if (ReadSmall(System.IO.Path.Combine(folder, ".runner")) is { } content)
            {
                using var json = JsonDocument.Parse(content);
                if (json.RootElement.ValueKind == JsonValueKind.Object)
                {
                    string? Get(string key) => json.RootElement.TryGetProperty(key, out var value) && value.ValueKind == JsonValueKind.String ? Text(value.GetString()) : null;
                    name = Get("agentName") ?? name;
                    url = Get("gitHubUrl");
                    pool = Get("poolName");
                    if (json.RootElement.TryGetProperty("agentId", out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out var number) && number > 0) id = number;
                }
            }
        }
        catch (JsonException) { }
        var scope = ParseScope(url);
        var service = Text(ReadSmall(System.IO.Path.Combine(folder, ".service"), 512), 256);
        if (service is not null && !ServicePattern.IsMatch(service)) service = null;
        var binary = ResolveExecutable(System.IO.Path.Combine(folder, "bin", "Runner.Listener"));
        var (os, arch) = ReadPlatform(binary);
        var job = ReadLastJob(folder);
        var result = new RunnerInfo(name, System.IO.Path.GetFileName(folder), "unknown", null)
        {
            Path = folder, AgentId = id, Repository = scope.Repository, Organization = scope.Organization,
            GitHubUrl = scope.Url, RunnerGroup = pool, ServiceName = service,
            ServiceState = service is null ? "not_configured" : "unknown", OperatingSystem = os,
            Architecture = arch, Version = ReadVersion(binary), LastJob = job, LastActivityAt = job?.At
        };
        foreach (var item in options.RunnerOverrides)
        {
            try
            {
                if (!System.IO.Path.IsPathFullyQualified(item.Path) || !string.Equals(Canonical(item.Path), folder,
                    OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal)) continue;
                result = result with { DisplayName = Text(item.DisplayName) ?? name, Group = Text(item.Group) };
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException) { }
        }
        return result;
    }

    internal static (string? Repository, string? Organization, string? Url) ParseScope(string? value)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) || uri.Scheme != "https" || uri.UserInfo.Length > 0 ||
            uri.Query.Length > 0 || uri.Fragment.Length > 0) return default;
        var parts = uri.AbsolutePath.Trim('/').Split('/');
        if (parts.Length is < 1 or > 2 || parts.Any(p => !Regex.IsMatch(p, @"^[a-zA-Z0-9_.-]+$", RegexOptions.CultureInvariant) || p is "." or "..")) return default;
        return (parts.Length == 2 ? string.Join('/', parts) : null, parts[0], uri.GetLeftPart(UriPartial.Path).TrimEnd('/'));
    }

    internal static string ResolveExecutable(string executable)
    {
        try
        {
            var directory = Canonical(System.IO.Path.GetDirectoryName(executable)!);
            var file = new FileInfo(System.IO.Path.Combine(directory, System.IO.Path.GetFileName(executable)));
            return file.ResolveLinkTarget(true)?.FullName ?? file.FullName;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return executable; }
    }

    private static (string? Os, string? Arch) ReadPlatform(string binary)
    {
        try
        {
            using var file = File.OpenRead(binary);
            Span<byte> header = stackalloc byte[20];
            if (file.ReadAtLeast(header, 20, false) != 20 || header[0] != 0x7f || header[1] != 'E' || header[2] != 'L' || header[3] != 'F') return default;
            var machine = header[5] == 2 ? header[18] * 256 + header[19] : header[19] * 256 + header[18];
            return ("Linux", machine switch { 62 => "X64", 183 => "ARM64", 40 => "ARM", 3 => "X86", _ => null });
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return default; }
    }

    private static string? ReadVersion(string binary)
    {
        try
        {
            var dll = System.IO.Path.Combine(System.IO.Path.GetDirectoryName(binary)!, "Runner.Listener.dll");
            if (File.Exists(dll))
            {
                var version = FileVersionInfo.GetVersionInfo(dll).ProductVersion;
                if (version is not null && VersionPattern.IsMatch(version)) return version;
            }
            // Never infer from the installation's name: it often contains an old version.
            var bin = System.IO.Path.GetFileName(System.IO.Path.GetDirectoryName(binary));
            if (bin is not null && bin.StartsWith("bin.") && VersionPattern.IsMatch(bin[4..])) return bin[4..];
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException) { }
        return null;
    }

    internal static JobSummary? ReadLastJob(string folder)
    {
        try
        {
            var diag = new DirectoryInfo(System.IO.Path.Combine(folder, "_diag"));
            if (!diag.Exists || diag.LinkTarget is not null) return null;
            var files = diag.EnumerateFiles("Runner_*.log").Where(f => f.LinkTarget is null)
                .OrderByDescending(f => f.Name, StringComparer.Ordinal).Take(5);
            JobSummary? latest = null;
            foreach (var file in files)
            {
                using var stream = new FileStream(file.FullName, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                var offset = Math.Max(0, stream.Length - 262144);
                stream.Seek(offset, SeekOrigin.Begin);
                using var reader = new StreamReader(stream);
                if (offset > 0) reader.ReadLine();
                var buffer = new char[262144];
                var length = reader.ReadBlock(buffer, 0, buffer.Length);
                var parsed = ParseJobLog(new string(buffer, 0, length));
                if (parsed is not null && (latest is null || parsed.At > latest.At)) latest = parsed;
            }
            return latest;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return null; }
    }

    internal static JobSummary? ParseJobLog(string content)
    {
        JobSummary? latest = null;
        foreach (var line in content.Split('\n'))
        {
            // Only recognized listener terminal summaries are exposed. Never return raw logs.
            if (line.Length > 4096) continue;
            var match = Regex.Match(line, @"^\[(?<at>\d{4}-\d{2}-\d{2}[^\]]{1,40}) INFO Terminal\] .*?(?:Running job: (?<running>.+)|Job (?<name>.+) completed with result: (?<result>Succeeded|Failed|Canceled|Cancelled|Skipped|Abandoned|SucceededWithIssues))\s*$",
                RegexOptions.CultureInvariant | RegexOptions.NonBacktracking);
            if (!match.Success || !DateTimeOffset.TryParse(match.Groups["at"].Value, CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal, out var at) || at > DateTimeOffset.UtcNow.AddMinutes(5)) continue;
            var name = Text(match.Groups["name"].Success ? match.Groups["name"].Value : match.Groups["running"].Value);
            if (name is not null && (latest is null || at >= latest.At)) latest = new(name,
                match.Groups["result"].Success ? match.Groups["result"].Value : null, at);
        }
        return latest;
    }
}
