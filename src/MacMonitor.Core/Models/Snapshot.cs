namespace MacMonitor.Core.Models;

/// <summary>
/// One stored capture of a tool's parsed payload. <see cref="PayloadJson"/> is the
/// canonical form (System.Text.Json of the parsed list); <see cref="PayloadHash"/> is a
/// sha256 hex of that JSON, used to short-circuit diff computation when nothing changed.
/// </summary>
public sealed record Snapshot(
    string ScanId,
    string ToolName,
    DateTimeOffset CapturedAt,
    string PayloadJson,
    string PayloadHash,
    int ItemCount);
