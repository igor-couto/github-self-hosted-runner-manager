namespace RunnerRoom;

public sealed class AlertOptions
{
    public bool Enabled { get; set; } = true;
    public int CheckIntervalSeconds { get; set; } = 60;
    public int RepeatMinutes { get; set; } = 60;
    public int RetentionDays { get; set; } = 30;
    public string? QuietHoursStartUtc { get; set; }
    public string? QuietHoursEndUtc { get; set; }
    public string? WebhookUrl { get; set; }
    public string? WebhookUrlFile { get; set; }
    public AlertRule[] Rules { get; set; } = [];
}

public sealed record AlertRule
{
    public string Id { get; set; } = "";
    public string Kind { get; set; } = "";
    public double Threshold { get; set; }
    public int HoldSeconds { get; set; } = 120;
    public string Severity { get; set; } = "warning";
    // Runner folder/path for runner rules, mount path for disk rules; null selects all.
    public string? Target { get; set; }
}

public sealed record AlertIncident(string Id, string Key, string RuleId, string Title, string Severity, DateTimeOffset FiredAt)
{
    public DateTimeOffset LastObservedAt { get; init; } = FiredAt;
    public DateTimeOffset? ResolvedAt { get; init; }
    public string State { get; init; } = "firing";
    public string Detail { get; init; } = "";
    public string Delivery { get; init; } = "pending";
    public DateTimeOffset? LastNotificationAt { get; init; }
    public DateTimeOffset? NextAttemptAt { get; init; }
    public bool Pending { get; init; } = true;
    public bool Notified { get; init; }
}

public sealed record AlertObservation(string Key, string RuleId, string Title, string Severity, bool? Breached, string Detail, int HoldSeconds);
public sealed record AlertRuleStatus(string Id, string Kind, double Threshold, int HoldSeconds, string Severity, string? Target)
{
    public int Targets { get; init; }
    public int Unavailable { get; init; }
}
public sealed record AlertReport(bool Enabled, bool Demo, int CheckIntervalSeconds, int RepeatMinutes,
    DateTimeOffset? LastCheckedAt, DateTimeOffset? NextCheckAt, bool QuietHours, string? QuietHoursUtc,
    string NotificationChannel, AlertRuleStatus[] Rules, AlertIncident[] Active, AlertIncident[] History, string[] Warnings);
