namespace MacMonitor.Core.Models;

/// <summary>
/// Filter / pagination shape for <c>IScanRepository.QueryFindingsAsync</c>. All fields
/// optional except <see cref="Limit"/>; the Web UI builds these from query-string args.
///
/// <see cref="Pattern"/> is a .NET-syntax regular expression evaluated against the
/// finding's summary, rationale, recommended_action, and evidence_json (whichever match,
/// the row qualifies). Matched case-insensitively. Pushed into the SQL layer via a
/// runtime-registered <c>regexp()</c> function so pagination + total counts stay correct.
///
/// <see cref="ExcludePattern"/> is the inverse: a row is dropped when ANY of the four
/// text columns matches. This is a separate field rather than a clever encoding of
/// <see cref="Pattern"/> because the column-level OR in the include predicate inverts
/// wrong for negation (a single column without the term would still satisfy the OR).
/// </summary>
public sealed record FindingsFilter(
    int Limit = 50,
    int Offset = 0,
    Severity? MinSeverity = null,
    string? Source = null,
    DateTimeOffset? SinceUtc = null,
    DateTimeOffset? UntilUtc = null,
    string? Pattern = null,
    string? ExcludePattern = null);

/// <summary>Page of findings + the total count matching the filter (for "page X of Y").</summary>
public sealed record FindingsPage(
    IReadOnlyList<Finding> Items,
    int TotalCount);
