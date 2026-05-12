namespace MacMonitor.Core.Models;

/// <summary>
/// A file in the user's Downloads folder, with the quarantine xattr if present.
/// </summary>
public sealed record DownloadedFile(
    string Path,
    DateTimeOffset ModifiedAt,
    long SizeBytes,
    string Owner,
    string? QuarantineAttribute);
