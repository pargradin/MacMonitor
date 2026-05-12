namespace MacMonitor.Worker;

public sealed class ScanOptions
{
    public const string SectionName = "Scan";

    /// <summary>How often to run a scan, in minutes. Default: 15.</summary>
    public int IntervalMinutes { get; set; } = 15;

    /// <summary>Maximum wall-clock for a single scan run before it's aborted.</summary>
    public int MaxScanDurationSeconds { get; set; } = 60;

    /// <summary>If true, run a scan immediately on startup before waiting for the timer.</summary>
    public bool RunOnStartup { get; set; } = true;
}
