namespace MacMonitor.Alerts;

public sealed class NotificationOptions
{
    public const string SectionName = "Notifications";

    /// <summary>Master on/off switch. Default: true.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>Findings below this severity never produce a notification. Default: Medium.</summary>
    public string MinSeverity { get; set; } = "Medium";

    /// <summary>Maximum notifications to fire from a single scan run. Default: 5.</summary>
    public int MaxPerScan { get; set; } = 5;

    /// <summary>Notification title (the bold first line in the banner). Default: "MacMonitor".</summary>
    public string Title { get; set; } = "MacMonitor";

    /// <summary>
    /// After firing for an identity_key, suppress further notifications for the same key
    /// for this many hours. Process-local — restarting the worker resets the cooldown.
    /// Default: 24 hours.
    /// </summary>
    public int IdentityCooldownHours { get; set; } = 24;
}
