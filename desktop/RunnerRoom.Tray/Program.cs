using System.Diagnostics;
using System.Text.Json;
using RunnerRoom;

internal static class Program
{
    [STAThread]
    private static void Main() { ApplicationConfiguration.Initialize(); Application.Run(new Tray()); }
}

sealed class Tray : ApplicationContext
{
    private readonly NotifyIcon icon = new() { Icon = SystemIcons.Application, Text = "Runner Room · Disconnected", Visible = true };
    private readonly System.Windows.Forms.Timer timer = new() { Interval = 15000 };
    private readonly ToolStripMenuItem status = new("Disconnected") { Enabled = false };
    private DashboardClient? client;
    private readonly ToolStripMenuItem actions = new("Runner controls") { Enabled = false };
    private bool busy, closed;
    public Tray()
    {
        var menu = new ContextMenuStrip(); menu.Items.Add(status);
        menu.Items.Add("Connect…", null, (_, _) => Connect());
        menu.Items.Add("Open dashboard", null, (_, _) => Open("#overview"));
        menu.Items.Add("Open management", null, (_, _) => Open("#management"));
        menu.Items.Add("Refresh", null, async (_, _) => await Refresh()); menu.Items.Add(actions);
        menu.Items.Add("Quit", null, (_, _) => ExitThread()); icon.ContextMenuStrip = menu;
        icon.DoubleClick += (_, _) => Open("#overview"); timer.Tick += async (_, _) => await Refresh(); timer.Start();
        // Connection is explicitly chosen in the tray menu. Passwords/cookies are never saved.
    }
    private void Open(string hash) { if (client is not null) Process.Start(new ProcessStartInfo(client.Address + hash) { UseShellExecute = true }); }
    private void Connect()
    {
        using var form = new Form { Text = "Connect to Runner Room", Width = 440, Height = 380, FormBorderStyle = FormBorderStyle.FixedDialog, MaximizeBox = false, MinimizeBox = false, StartPosition = FormStartPosition.CenterScreen };
        var panel = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.TopDown, Padding = new Padding(20), WrapContents = false };
        var address = new TextBox { Width = 370, Text = client?.Address.ToString() ?? "http://127.0.0.1:8080" };
        var user = new TextBox { Width = 370 }; var password = new TextBox { Width = 370, UseSystemPasswordChar = true };
        var result = new Label { Width = 370, Height = 50 }; var button = new Button { Text = "Connect", Width = 100 };
        foreach (var control in new Control[] { new Label { Text = "Server address", AutoSize = true }, address, new Label { Text = "Local account (optional)", AutoSize = true }, user, new Label { Text = "Password", AutoSize = true }, password, button, result }) panel.Controls.Add(control);
        form.Controls.Add(panel); form.AcceptButton = button;
        button.Click += async (_, _) => {
            button.Enabled = false; DashboardClient? candidate = null;
            try {
                candidate = new(address.Text.Trim());
                if (user.Text.Length > 0) await candidate.Login(user.Text, password.Text); else await candidate.Session();
                await candidate.Get("/api/runners");
                if (closed || form.IsDisposed) return;
                var old = client; client = candidate; candidate = null; old?.Dispose(); password.Clear(); form.Close(); await Refresh();
            } catch (Exception ex) when (ex is HttpRequestException or ArgumentException or InvalidOperationException or OperationCanceledException or JsonException) { if (!form.IsDisposed) result.Text = ex.Message; }
            finally { candidate?.Dispose(); if (!form.IsDisposed) button.Enabled = true; }
        };
        form.ShowDialog();
    }
    private async Task Refresh()
    {
        if (busy || client is null || closed) return; busy = true; var current = client;
        try {
            var data = await current.Get("/api/runners"); if (closed || current != client) return;
            if (data.TryGetProperty("error", out var error) && error.ValueKind == JsonValueKind.String) throw new InvalidOperationException("Runner scan unavailable");
            var rows = data.GetProperty("runners").EnumerateArray().ToArray();
            string message = $"{rows.Count(r => r.GetProperty("status").GetString() == "busy")} busy · {rows.Count(r => r.GetProperty("status").GetString() == "idle")} idle · {rows.Length} total";
            status.Text = message; icon.Text = "Runner Room · " + message; icon.Icon = rows.Any(r => r.GetProperty("status").GetString() is "offline" or "unknown") ? SystemIcons.Warning : SystemIcons.Information;
            actions.DropDownItems.Clear(); actions.Enabled = false;
            var session = await current.Session(); if (closed || current != client || !session.GetProperty("canAdmin").GetBoolean()) return;
            var management = await current.Get("/api/management"); if (closed || current != client) return;
            if (management.TryGetProperty("runners", out var runners)) foreach (var runner in runners.EnumerateArray()) {
                var id = runner.GetProperty("id").GetString(); var name = runner.GetProperty("settings").GetProperty("name").GetString() ?? id;
                var item = new ToolStripMenuItem(name) { AutoToolTip = true }; actions.DropDownItems.Add(item);
                foreach (var action in new[] { "start", "drain", "restart" }) item.DropDownItems.Add(action, null, async (_, _) => {
                    if (current != client || MessageBox.Show($"Request {action} for {name}?", "Runner Room", MessageBoxButtons.OKCancel) != DialogResult.OK) return;
                    try { await current.Post("/api/management/action", new { ids = new[] { id }, action }); MessageBox.Show("Request queued. Open management to track its result.", "Runner Room"); }
                    catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException or InvalidOperationException) { MessageBox.Show(ex.Message, "Runner Room"); }
                });
            }
            actions.Enabled = actions.DropDownItems.Count > 0;
        } catch (Exception ex) when (ex is HttpRequestException or JsonException or OperationCanceledException or InvalidOperationException or ObjectDisposedException) {
            if (!closed && current == client) { status.Text = "Unavailable · check connection / permissions"; icon.Text = "Runner Room · Status unavailable"; icon.Icon = SystemIcons.Warning; actions.Enabled = false; }
        } finally { busy = false; }
    }
    protected override void ExitThreadCore() { closed = true; timer.Stop(); timer.Dispose(); client?.Dispose(); icon.Visible = false; icon.Dispose(); base.ExitThreadCore(); }
}
