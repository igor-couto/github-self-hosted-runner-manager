using System.Text;
using System.Text.Json;

namespace RunnerRoom;

internal static class RoomCommand
{
    internal static string Safe(string? text) => new((text ?? "").Where(c => !char.IsControl(c) && c is not ('\u202a' or '\u202b' or '\u202c' or '\u202d' or '\u202e' or '\u2066' or '\u2067' or '\u2068' or '\u2069')).ToArray());
    private static string Value(JsonElement item, string key) => item.TryGetProperty(key, out var value) ? Safe(value.ToString()) : "";
    internal static async Task<int> Run(string[] args)
    {
        try
        {
            var url = "http://127.0.0.1:8080"; string? user = null, passwordFile = null, id = null;
            var command = "runners"; bool json = false, yes = false, hasCommand = false;
            for (int i = 1; i < args.Length; i++)
            {
                string Next() => i + 1 < args.Length ? args[++i] : throw new ArgumentException("Missing option value.");
                switch (args[i])
                {
                    case "--help": Console.WriteLine("RunnerRoom cli [runners|server|history|alerts|management|quotas|start|stop|restart|drain|update] [--json] [--id ID] [--yes]\nRunnerRoom tui\nOptions: --url ORIGIN --user NAME --password-file PATH\nSign-in uses a local dashboard account; otherwise a password is prompted securely. Remote sign-in requires HTTPS.\nCLI actions require a managed runner ID and --yes. TUI: arrows select, Enter details, F active only, S sort, R refresh, Q quit."); return 0;
                    case "--url": url = Next(); break;
                    case "--user": user = Next(); break;
                    case "--password-file": passwordFile = Next(); break;
                    case "--id": id = Next(); break;
                    case "--json": json = true; break;
                    case "--yes": yes = true; break;
                    default: if (args[i].StartsWith('-') || hasCommand) throw new ArgumentException("Unknown or duplicate command. Use --help."); command = args[i]; hasCommand = true; break;
                }
            }
            using var cancel = new CancellationTokenSource();
            ConsoleCancelEventHandler handler = (_, e) => { e.Cancel = true; cancel.Cancel(); };
            Console.CancelKeyPress += handler;
            try
            {
                using var client = new DashboardClient(url);
                if (passwordFile is not null && user is null) throw new ArgumentException("--password-file requires --user.");
                if (user is not null)
                {
                    if (client.Address.Scheme != "https" && !client.Address.IsLoopback) throw new ArgumentException("Remote sign-in requires HTTPS, or an SSH tunnel to localhost.");
                    string password;
                    if (passwordFile is not null) { var info = new FileInfo(passwordFile); if (info.Length > 8192) throw new ArgumentException("Password file is too large."); password = (await File.ReadAllTextAsync(passwordFile, cancel.Token)).TrimEnd('\r', '\n'); }
                    else { if (Console.IsInputRedirected) throw new ArgumentException("Use --password-file with redirected input."); Console.Error.Write("Password: "); var value = new StringBuilder(); ConsoleKeyInfo key; while ((key = Console.ReadKey(true)).Key != ConsoleKey.Enter) { if (key.Key == ConsoleKey.Backspace) { if (value.Length > 0) value.Length--; } else if (!char.IsControl(key.KeyChar) && value.Length < 4096) value.Append(key.KeyChar); } Console.Error.WriteLine(); password = value.ToString(); }
                    await client.Login(user, password, cancel.Token);
                }
                else await client.Session(cancel.Token);
                if (args[0] == "tui") { await Terminal(client, cancel.Token); return 0; }
                JsonElement data;
                if (command is "start" or "stop" or "restart" or "drain" or "update")
                {
                    if (!yes || string.IsNullOrWhiteSpace(id)) throw new ArgumentException("Actions require --id MANAGED_RUNNER_ID and --yes. List IDs with 'cli management --json'.");
                    data = await client.Post("/api/management/action", new { ids = new[] { id }, action = command }, cancel.Token);
                }
                else
                {
                    var path = command switch { "runners" => "/api/runners", "server" => "/api/system", "history" => "/api/history", "alerts" => "/api/alerts", "management" => "/api/management", "quotas" => "/api/quotas", _ => throw new ArgumentException("Unknown command. Use --help.") };
                    data = await client.Get(path, cancel.Token);
                    if (command == "runners" && data.TryGetProperty("error", out var scanError) && scanError.ValueKind == JsonValueKind.String)
                        throw new InvalidOperationException("Runner scan unavailable: " + Safe(scanError.GetString()));
                    if (id is not null && command == "runners") { var match = data.GetProperty("runners").EnumerateArray().FirstOrDefault(r => Value(r, "id") == id); if (match.ValueKind == JsonValueKind.Undefined) throw new ArgumentException("Runner not found."); data = match; }
                }
                if (json) Console.WriteLine(JsonSerializer.Serialize(data, new JsonSerializerOptions { WriteIndented = true }));
                else if (command == "runners" && id is null) {
                    Console.WriteLine($"{Value(data, "host")} · {data.GetProperty("runners").GetArrayLength()} runners · checked {Value(data, "checkedAt")}");
                    if (Value(data, "warning").Length > 0) Console.WriteLine("Warning: " + Value(data, "warning"));
                    foreach (var runner in data.GetProperty("runners").EnumerateArray()) Console.WriteLine($"{Value(runner, "status"),-9} {Value(runner, "displayName"),-30} {Value(runner, "repository")}  [{Value(runner, "id")}]");
                }
                else Print(data, "", 0);
                return 0;
            }
            finally { Console.CancelKeyPress -= handler; }
        }
        catch (OperationCanceledException) { return 130; }
        catch (Exception ex) when (ex is HttpRequestException or IOException or UnauthorizedAccessException or ArgumentException or InvalidOperationException or JsonException)
        { Console.Error.WriteLine(Safe(ex.Message)); return 1; }
    }
    private static void Print(JsonElement item, string name, int depth)
    {
        var prefix = new string(' ', depth * 2);
        if (item.ValueKind == JsonValueKind.Object) { if (name.Length > 0) Console.WriteLine(prefix + Safe(name) + ":"); foreach (var p in item.EnumerateObject()) Print(p.Value, p.Name, Math.Min(depth + 1, 12)); }
        else if (item.ValueKind == JsonValueKind.Array) { Console.WriteLine(prefix + Safe(name) + ":"); foreach (var row in item.EnumerateArray()) Print(row, "-", Math.Min(depth + 1, 12)); }
        else Console.WriteLine(prefix + Safe(name) + ": " + (item.ValueKind == JsonValueKind.Null ? "Unavailable" : Safe(item.ToString())));
    }
    private static async Task Terminal(DashboardClient client, CancellationToken cancellation)
    {
        if (Console.IsInputRedirected || Console.IsOutputRedirected) throw new ArgumentException("The terminal dashboard needs an interactive terminal. Use 'cli runners --json' for scripts.");
        bool activeOnly = false, byStatus = false, detail = false; int selected = 0; JsonElement? data = null; string? error = null;
        var next = DateTimeOffset.MinValue; var lastSize = (0, 0); var draw = true; var cursor = !OperatingSystem.IsWindows() || Console.CursorVisible;
        try
        {
            Console.CursorVisible = false; Console.Write("\u001b[?1049h");
            while (!cancellation.IsCancellationRequested)
            {
                if (DateTimeOffset.UtcNow >= next)
                {
                    try {
                        var incoming = await client.Get("/api/runners", cancellation);
                        if (incoming.TryGetProperty("error", out var scanError) && scanError.ValueKind == JsonValueKind.String) throw new HttpRequestException("Runner scan failed.");
                        data = incoming; error = Value(incoming, "warning") is { Length: > 0 } warning ? "Warning: " + warning : null;
                    }
                    catch (Exception ex) when (ex is HttpRequestException or JsonException or OperationCanceledException) { if (cancellation.IsCancellationRequested) return; error = "STALE / UNAVAILABLE: " + Safe(ex.Message); }
                    next = DateTimeOffset.UtcNow.AddSeconds(15); draw = true;
                }
                var rows = data?.GetProperty("runners").EnumerateArray().Where(r => !activeOnly || Value(r, "status") is "busy" or "idle").OrderBy(r => byStatus ? Value(r, "status") : "").ThenBy(r => Value(r, "displayName")).ToArray() ?? [];
                selected = Math.Clamp(selected, 0, Math.Max(0, rows.Length - 1));
                var width = Math.Max(10, Console.WindowWidth - 1); var height = Math.Max(1, Console.WindowHeight - 7);
                if (lastSize != (width, height)) { draw = true; lastSize = (width, height); }
                if (draw)
                {
                    var lines = new List<string> { "RUNNER ROOM · " + client.Address.Host, "Arrows select | Enter details | F active | S sort | R refresh | Q quit", error ?? "Live · checked " + (data is { } d ? Value(d, "checkedAt") : "waiting"), $"{rows.Length} runners · filter: {(activeOnly ? "active" : "all")} · sort: {(byStatus ? "status" : "name")}" };
                    if (detail && rows.Length > 0) { var r = rows[selected]; foreach (var key in new[] { "displayName", "name", "status", "repository", "path", "version", "pid", "uptimeSeconds", "serviceState" }) lines.Add(key + ": " + Value(r, key)); }
                    else { var start = selected / height * height; for (int i = start; i < Math.Min(rows.Length, start + height); i++) lines.Add($"{(i == selected ? ">" : " ")} {Value(rows[i], "status"),-8} {Value(rows[i], "displayName"),-28} {Value(rows[i], "repository")}"); }
                    Console.Write("\u001b[H\u001b[2J"); foreach (var line in lines.Take(Console.WindowHeight - 1)) Console.WriteLine(Safe(line).Length > width ? Safe(line)[..width] : Safe(line)); draw = false;
                }
                if (Console.KeyAvailable)
                {
                    var key = Console.ReadKey(true).Key;
                    if (key is ConsoleKey.Q or ConsoleKey.Escape) return;
                    if (key == ConsoleKey.UpArrow) selected--; if (key == ConsoleKey.DownArrow) selected++;
                    if (key == ConsoleKey.PageDown) selected += height; if (key == ConsoleKey.PageUp) selected -= height;
                    if (key == ConsoleKey.R) next = DateTimeOffset.MinValue;
                    if (key == ConsoleKey.F) { activeOnly = !activeOnly; selected = 0; }
                    if (key == ConsoleKey.S) byStatus = !byStatus; if (key == ConsoleKey.Enter) detail = !detail; draw = true;
                }
                await Task.Delay(100, cancellation);
            }
        }
        finally { Console.Write("\u001b[?1049l"); Console.CursorVisible = cursor; }
    }
}
