namespace MacMonitor.Core.Models;

/// <summary>
/// One user-managed allow-list entry. <see cref="IdentityKey"/> matches whatever
/// the corresponding <c>IDiffer.IdentityKey</c> produced for the item the user wants
/// to suppress.
/// </summary>
public sealed record KnownGoodEntry(
    string ToolName,
    string IdentityKey,
    string? Note,
    DateTimeOffset AddedAt);
