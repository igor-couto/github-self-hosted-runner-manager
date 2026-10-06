using System.Globalization;
using System.Net.Http.Json;

namespace RunnerRoom;

internal sealed class AlertNotifier
{
    private readonly HttpClient client;
    private readonly Uri? endpoint;
    internal string? Warning { get; }
    internal bool Configured => endpoint is not null;
    internal AlertNotifier(AlertOptions options, HttpClient client)
    {
        this.client = client;
        var url = options.WebhookUrl;
        if (!string.IsNullOrWhiteSpace(options.WebhookUrlFile))
        {
            url = LocalRunnerReader.ReadSmall(options.WebhookUrlFile, 4096)?.Trim();
            if (string.IsNullOrWhiteSpace(url)) { Warning = "The webhook URL file could not be read or is empty. External notifications are disabled."; return; }
        }
        if (string.IsNullOrWhiteSpace(url)) return;
        if (!Uri.TryCreate(url, UriKind.Absolute, out var parsed) || parsed.Scheme != "https" || parsed.UserInfo.Length > 0 || parsed.Fragment.Length > 0)
        { Warning = "Configure an HTTPS webhook URL without user information or a fragment. External notifications are disabled."; return; }
        endpoint = parsed;
    }
    internal async Task<bool> SendAsync(AlertIncident alert, CancellationToken cancellation)
    {
        if (endpoint is null) return true;
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, endpoint)
            {
                Content = JsonContent.Create(new { source = "Runner Room", incidentId = alert.Id, state = alert.State, rule = alert.RuleId,
                    severity = alert.Severity, title = alert.Title, detail = alert.Detail, firedAt = alert.FiredAt, resolvedAt = alert.ResolvedAt })
            };
            using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellation);
            return response.IsSuccessStatusCode;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or IOException) { return false; }
    }
    internal static bool ValidQuietHours(AlertOptions options) =>
        options.QuietHoursStartUtc is null && options.QuietHoursEndUtc is null ||
        TimeOnly.TryParseExact(options.QuietHoursStartUtc, "HH:mm", CultureInfo.InvariantCulture, DateTimeStyles.None, out var start) &&
        TimeOnly.TryParseExact(options.QuietHoursEndUtc, "HH:mm", CultureInfo.InvariantCulture, DateTimeStyles.None, out var end) && start != end;
    internal static bool IsQuiet(AlertOptions options, DateTimeOffset now)
    {
        if (options.QuietHoursStartUtc is null && options.QuietHoursEndUtc is null) return false;
        if (!ValidQuietHours(options)) return true; // Invalid settings must not accidentally send notifications.
        var start = TimeOnly.ParseExact(options.QuietHoursStartUtc!, "HH:mm", CultureInfo.InvariantCulture);
        var end = TimeOnly.ParseExact(options.QuietHoursEndUtc!, "HH:mm", CultureInfo.InvariantCulture);
        var time = TimeOnly.FromDateTime(now.UtcDateTime);
        return start < end ? time >= start && time < end : time >= start || time < end;
    }
}
