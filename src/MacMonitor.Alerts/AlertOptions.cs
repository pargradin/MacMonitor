namespace MacMonitor.Alerts;

public sealed class AlertOptions
{
    public const string SectionName = "Alerts";

    /// <summary>
    /// Directory to write JSONL findings into. <c>~</c> is expanded.
    /// Default: ~/Library/Logs/MacMonitor.
    /// </summary>
    public string LogDirectory { get; set; } = "~/Library/Logs/MacMonitor";

    /// <summary>Filename prefix; the date is appended (e.g. findings-2026-04-30.jsonl).</summary>
    public string FilePrefix { get; set; } = "findings";

    /// <summary>Minimum severity to keep on disk. In Phase 1 default Info means everything is kept.</summary>
    public string MinSeverity { get; set; } = "Info";
}
