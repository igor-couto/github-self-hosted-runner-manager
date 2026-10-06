using System.Formats.Tar;
using System.IO.Compression;
using System.Security.Cryptography;

namespace RunnerRoom;

internal static class RunnerPackages
{
    internal static async Task Download(RunnerRelease release, string directory, CancellationToken token)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(TimeSpan.FromMinutes(15)); token = deadline.Token;
        Directory.CreateDirectory(directory);
        var archive = Path.Combine(directory, "package.tar.gz");
        // Download transport never receives a PAT and follows only GitHub's release asset hosts.
        using var http = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false }) { Timeout = TimeSpan.FromMinutes(15) };
        var url = new Uri(release.Url);
        for (var redirects = 0; ; redirects++)
        {
            if (!AllowedDownload(url) || redirects > 5) throw new ManagementException("Unexpected release download redirect.");
            using var response = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, token);
            if ((int)response.StatusCode is >= 300 and < 400 && response.Headers.Location is { } next) { url = new Uri(url, next); continue; }
            if (!response.IsSuccessStatusCode) throw new ManagementException("Runner download failed. Check network access to GitHub release assets.");
            await using var input = await response.Content.ReadAsStreamAsync(token);
            await using var output = new FileStream(archive, FileMode.CreateNew, FileAccess.Write);
            var buffer = new byte[81920]; long total = 0; int count;
            while ((count = await input.ReadAsync(buffer, token)) > 0)
            {
                total += count;
                if (total > 1_500_000_000) throw new ManagementException("Runner archive exceeds the download size limit.");
                await output.WriteAsync(buffer.AsMemory(0, count), token);
            }
            break;
        }
        await using (var file = File.OpenRead(archive))
        {
            var hash = Convert.ToHexString(await SHA256.HashDataAsync(file, token));
            if (!hash.Equals(release.Sha256, StringComparison.OrdinalIgnoreCase)) throw new ManagementException("Runner checksum mismatch. The downloaded package was not installed.");
        }
        await using (var file = File.OpenRead(archive))
        await using (var gzip = new GZipStream(file, CompressionMode.Decompress))
            await Extract(gzip, Path.Combine(directory, "files"), token);
    }
    internal static bool AllowedDownload(Uri url) => url.Scheme == "https" && url.IsDefaultPort && url.UserInfo.Length == 0 &&
        url.Host is "github.com" or "release-assets.githubusercontent.com" or "objects.githubusercontent.com";
    internal static async Task Extract(Stream input, string directory, CancellationToken token)
    {
        var root = ManagementValidation.SafeDirectory(directory);
        Directory.CreateDirectory(root);
        using var reader = new TarReader(input, leaveOpen: true);
        var links = new List<(string Path, string Target, bool Hard)>();
        var seen = new HashSet<string>(); long total = 0; int count = 0;
        while (await reader.GetNextEntryAsync(false, token) is { } entry)
        {
            if (++count > 150000 || (total += entry.Length) > 4_000_000_000) throw new ManagementException("Runner archive exceeds the extraction limit.");
            var name = entry.Name;
            if (name.StartsWith("./")) name = name[2..];
            if (name is "" or ".") continue;
            var top = name.Split('/')[0];
            if (top.StartsWith('.') || top is "_work" or "_diag") throw new ManagementException("Unexpected state file in runner release.");
            var path = EntryPath(root, name);
            if (!seen.Add(path)) throw new ManagementException("Runner archive has duplicate paths.");
            ManagementValidation.SafeDirectory(Path.GetDirectoryName(path)!);
            if (entry.EntryType == TarEntryType.Directory) Directory.CreateDirectory(path);
            else if (entry.EntryType is TarEntryType.SymbolicLink or TarEntryType.HardLink)
            {
                var target = entry.EntryType == TarEntryType.HardLink ? EntryPath(root, entry.LinkName) : Path.GetFullPath(Path.Combine(Path.GetDirectoryName(path)!, entry.LinkName));
                if (Path.IsPathRooted(entry.LinkName) || !ManagementValidation.Within(target, root)) throw new ManagementException("Runner archive contains an unsafe link.");
                links.Add((path, target, entry.EntryType == TarEntryType.HardLink));
            }
            else if (entry.EntryType is TarEntryType.RegularFile or TarEntryType.V7RegularFile)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                await using var file = new FileStream(path, FileMode.CreateNew, FileAccess.Write);
                if (entry.DataStream is not null) await entry.DataStream.CopyToAsync(file, token);
                if (OperatingSystem.IsLinux()) File.SetUnixFileMode(path, entry.Mode & (UnixFileMode)0x1FF);
            }
            else throw new ManagementException("Runner archive contains an unsupported entry type.");
        }
        foreach (var link in links)
        {
            ManagementValidation.SafeDirectory(Path.GetDirectoryName(link.Path)!);
            ManagementValidation.SafeDirectory(Path.GetDirectoryName(link.Target)!);
            Directory.CreateDirectory(Path.GetDirectoryName(link.Path)!);
            if (link.Hard) File.Copy(link.Target, link.Path, false);
            else if (Directory.Exists(link.Target)) Directory.CreateSymbolicLink(link.Path, Path.GetRelativePath(Path.GetDirectoryName(link.Path)!, link.Target));
            else File.CreateSymbolicLink(link.Path, Path.GetRelativePath(Path.GetDirectoryName(link.Path)!, link.Target));
        }
        if (!File.Exists(Path.Combine(root, "bin", "Runner.Listener")) || !File.Exists(Path.Combine(root, "config.sh"))) throw new ManagementException("Runner archive is missing its executable or configuration script.");
    }
    internal static string EntryPath(string root, string name)
    {
        if (Path.IsPathRooted(name) || name.Contains('\\') || name.Split('/').Any(p => p == "..")) throw new ManagementException("Unsafe path in runner archive.");
        var path = Path.GetFullPath(Path.Combine(root, name));
        if (!ManagementValidation.Within(path, root)) throw new ManagementException("Unsafe path in runner archive.");
        return path;
    }
    internal static async Task VerifyRuntime(string staging, RunnerRelease release, CancellationToken token)
    {
        var files = Path.Combine(staging, "files");
        var version = await ManagedRunnerRuntime.Run(ManagedRunnerRuntime.Command(Path.Combine(files, "bin", "Runner.Listener"), files, "--version"), token);
        if (version != release.Version) throw new ManagementException("Downloaded runtime version does not match its release metadata.");
        // The real listener creates diagnostics even for --version. Keep these outside the install payload.
        var diagnostics = Path.Combine(files, "_diag");
        if (Directory.Exists(diagnostics)) Directory.Move(diagnostics, Path.Combine(staging, "preflight-diagnostics"));
    }
    // Move complete top-level entries instead of extracting through auto-update symlinks. Keep a rollback copy.
    internal static void Install(string source, string destination, string backup)
    {
        ManagementValidation.SafeDirectory(destination); Directory.CreateDirectory(destination); Directory.CreateDirectory(backup);
        var moved = new List<string>(); var installed = new List<string>();
        try
        {
            foreach (var entry in Directory.EnumerateFileSystemEntries(source))
            {
                var name = Path.GetFileName(entry);
                if (name.StartsWith('.') || name is "_work" or "_diag" or ".service") throw new ManagementException("Unexpected state file in runner release.");
                var target = Path.Combine(destination, name);
                if (File.Exists(target) || Directory.Exists(target)) { Move(target, Path.Combine(backup, name)); moved.Add(name); }
                Move(entry, target); installed.Add(name);
            }
        }
        catch
        {
            foreach (var name in installed.AsEnumerable().Reverse()) Move(Path.Combine(destination, name), Path.Combine(source, name));
            foreach (var name in moved.AsEnumerable().Reverse()) Move(Path.Combine(backup, name), Path.Combine(destination, name));
            throw;
        }
    }
    private static void Move(string source, string target)
    {
        if (Directory.Exists(source)) Directory.Move(source, target); else File.Move(source, target);
    }
}
