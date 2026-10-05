using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace RunnerRoom;

public sealed record LogFile(string Name, string Kind, long Bytes, DateTimeOffset ModifiedAt);
public sealed record LogLine(DateTimeOffset At, string Level, string Source, string Message);
public sealed record LogExcerpt(string? File, LogLine[] Lines, bool Truncated, string? Message);
public sealed record RunnerLogView(LogFile[] Files, LogExcerpt Excerpt, bool Enabled, DateTimeOffset CheckedAt);

// Reads diagnostic files only. Paths supplied by clients are never passed to the filesystem.
internal static class RunnerLogs
{
    // O_NONBLOCK is ignored for regular files and prevents a named pipe in _diag from hanging a scan.
    [DllImport("libc", EntryPoint = "open", SetLastError = true)]
    private static extern int OpenNonBlocking([MarshalAs(UnmanagedType.LPUTF8Str)] string path, int flags);

    private static FileStream OpenDiagnostic(string path)
    {
        if (!OperatingSystem.IsLinux()) return new(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        var fd = OpenNonBlocking(path, 0x800); // O_RDONLY | O_NONBLOCK
        if (fd < 0) throw new IOException("Diagnostic log could not be opened.");
        var handle = new SafeFileHandle((IntPtr)fd, ownsHandle: true);
        try { return new FileStream(handle, FileAccess.Read); }
        catch { handle.Dispose(); throw; }
    }
    internal const int TailBytes = 256 * 1024;
    internal static readonly Regex Header = new(@"^\[(?<at>\d{4}-\d{2}-\d{2}[^\]]{1,40}) (?<level>INFO|WARN|ERR|VERB) (?<source>[^\]\r\n]{1,100})\] (?<message>.*)$", RegexOptions.CultureInvariant | RegexOptions.NonBacktracking);
    private static readonly Regex FileName = new(@"^(Runner|Worker)_\d{8}-\d{6}-utc(?:_\d+)?\.log$", RegexOptions.CultureInvariant | RegexOptions.NonBacktracking);

    internal static LogFile[] List(string folder)
    {
        try
        {
            var diag = new DirectoryInfo(Path.Combine(folder, "_diag"));
            if (!SafeDirectory(folder) || !diag.Exists || diag.LinkTarget is not null) return [];
            // Bound enumeration as well as the returned list on hosts that retain years of logs.
            return diag.EnumerateFiles("*.log").Take(10000)
                .Where(f => FileName.IsMatch(f.Name) && f.LinkTarget is null)
                .OrderByDescending(f => f.Name[7..], StringComparer.Ordinal).Take(20)
                .Select(f => new LogFile(f.Name, f.Name.StartsWith("Worker_") ? "Worker" : "Listener", f.Length, f.LastWriteTimeUtc)).ToArray();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return []; }
    }

    private static bool SafeDirectory(string folder) => new DirectoryInfo(folder).LinkTarget is null &&
        new DirectoryInfo(Path.Combine(folder, "_diag")).LinkTarget is null;

    internal static (string Text, bool Truncated)? Read(string folder, string name, bool tail, int limit = TailBytes)
    {
        if (!FileName.IsMatch(name)) return null;
        try
        {
            var path = Path.Combine(folder, "_diag", name);
            if (!SafeDirectory(folder) || new FileInfo(path).LinkTarget is not null) return null;
            using var stream = OpenDiagnostic(path);
            if (!stream.CanSeek) return null;
            // Check the opened descriptor on Linux too, so a swapped symlink cannot escape _diag.
            if (OperatingSystem.IsLinux())
            {
                var target = new FileInfo($"/proc/self/fd/{stream.SafeFileHandle.DangerousGetHandle()}").LinkTarget;
                if (target != path) return null;
            }
            var length = stream.Length;
            var offset = tail ? Math.Max(0, length - limit) : 0;
            stream.Seek(offset, SeekOrigin.Begin);
            var bytes = new byte[(int)Math.Min(length, limit)];
            var count = stream.ReadAtLeast(bytes, bytes.Length, false);
            var text = Encoding.UTF8.GetString(bytes, 0, count).TrimStart('\uFEFF');
            if (offset > 0) text = text.Contains('\n') ? text[(text.IndexOf('\n') + 1)..] : "";
            // An incomplete final line can be a partially-written secret; wait for its newline.
            if (tail && text.Length > 0 && !text.EndsWith('\n')) text = text.Contains('\n') ? text[..(text.LastIndexOf('\n') + 1)] : "";
            return (text, length > limit);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return null; }
    }

    internal static LogLine? ParseLine(string line)
    {
        if (line.Length > 16384) return null;
        var match = Header.Match(line.TrimEnd('\r'));
        if (!match.Success || !DateTimeOffset.TryParse(match.Groups["at"].Value, CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal, out var at)) return null;
        return new(at, match.Groups["level"].Value, match.Groups["source"].Value, match.Groups["message"].Value);
    }

    internal static LogExcerpt Excerpt(string folder, string name, LogRedactor redactor)
    {
        var read = Read(folder, name, true);
        if (read is null) return new(name, [], false, "This log is no longer available or cannot be read.");
        var lines = new List<LogLine>();
        foreach (var raw in read.Value.Text.Split('\n'))
        {
            var line = ParseLine(raw);
            if (line is null) continue; // Never expose multi-line job payloads, environment dumps or stack continuations.
            if (line.Source == "Worker" && line.Message.StartsWith("Job message:", StringComparison.Ordinal))
                line = line with { Message = "[Job payload omitted]" };
            lines.Add(line with { Source = redactor.Redact(line.Source), Message = redactor.Redact(line.Message) });
        }
        return new(name, lines.TakeLast(1000).ToArray(), read.Value.Truncated || lines.Count > 1000,
            "Diagnostic excerpt; multiline payloads and unfinished lines are omitted. Common secrets are masked; review before sharing.");
    }
}

internal sealed class LogRedactor
{
    private readonly string[] values;
    private static readonly Regex SensitiveLine = new(@"(?i)(authorization|proxy-authorization|password|passwd|access[_-]?token|refresh[_-]?token|client[_-]?secret|api[_-]?key|private[_-]?key|cookie|\btoken\b|\bsecret\b)\s*[""']?\s*[:=]", RegexOptions.NonBacktracking | RegexOptions.CultureInvariant);
    private static readonly Regex Tokens = new(@"(?:gh[pousr]_[a-zA-Z0-9_]{10,}|github_pat_[a-zA-Z0-9_]{10,}|eyJ[a-zA-Z0-9_-]+\.[a-zA-Z0-9_-]+\.[a-zA-Z0-9_-]+)", RegexOptions.NonBacktracking | RegexOptions.CultureInvariant);
    private static readonly Regex UrlCredentials = new(@"https?://[^\s/]+@|https?://[^\s?]+\?[^\s]+", RegexOptions.NonBacktracking | RegexOptions.CultureInvariant);
    internal LogRedactor(RunnerOptions options)
    {
        var token = options.GitHub.Token;
        if (string.IsNullOrWhiteSpace(token) && !string.IsNullOrWhiteSpace(options.GitHub.TokenFile))
            token = LocalRunnerReader.ReadSmall(options.GitHub.TokenFile, 8192)?.Trim();
        values = options.Logs.RedactValues.Append(token ?? "").Where(v => !string.IsNullOrWhiteSpace(v)).Distinct().OrderByDescending(v => v.Length).ToArray();
    }
    internal string Redact(string text)
    {
        text = new string(text.Where(c => !char.IsControl(c)).ToArray());
        foreach (var value in values) text = text.Replace(value, "[REDACTED]", StringComparison.Ordinal);
        if (SensitiveLine.IsMatch(text) || text.Contains("-----BEGIN", StringComparison.Ordinal)) return "[Sensitive diagnostic line omitted]";
        return UrlCredentials.Replace(Tokens.Replace(text, "[REDACTED]"), "[REDACTED URL]");
    }
}
