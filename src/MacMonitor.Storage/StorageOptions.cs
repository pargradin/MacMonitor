namespace MacMonitor.Storage;

public sealed class StorageOptions
{
    public const string SectionName = "Storage";

    /// <summary>
    /// Path to the SQLite DB. <c>~</c> is expanded.
    /// Default: <c>~/Library/Application Support/MacMonitor/state.db</c>.
    /// </summary>
    public string DatabasePath { get; set; } = "~/Library/Application Support/MacMonitor/state.db";

    /// <summary>
    /// Minimum number of snapshots to keep per tool, regardless of age. Older snapshots
    /// beyond this count are dropped during retention. Default: 5.
    /// </summary>
    public int RetentionSnapshotsPerTool { get; set; } = 5;
}
