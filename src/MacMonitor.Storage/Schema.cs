namespace MacMonitor.Storage;

/// <summary>
/// SQLite DDL for the MacMonitor state database, plus a tiny migration runner.
///
/// Migrations are an ordered list of named, idempotent SQL scripts. Each one runs inside
/// a transaction; the migration table records which scripts have been applied, so
/// re-running <c>InitializeAsync</c> on an existing DB is a no-op.
/// </summary>
internal static class Schema
{
    public const string MigrationsTableName = "schema_migrations";

    /// <summary>
    /// Bootstrap script — creates the migrations table itself. Run unconditionally before
    /// any other script.
    /// </summary>
    public const string Bootstrap = $"""
        CREATE TABLE IF NOT EXISTS {MigrationsTableName} (
            id          TEXT    PRIMARY KEY,
            applied_at  INTEGER NOT NULL
        );
        """;

    /// <summary>Initial schema. Add new migrations as additional entries in <see cref="All"/>.</summary>
    public const string Migration001Initial = """
        CREATE TABLE IF NOT EXISTS scans (
            id           TEXT    PRIMARY KEY,
            started_at   INTEGER NOT NULL,
            completed_at INTEGER,
            status       TEXT    NOT NULL DEFAULT 'running'
        );

        CREATE TABLE IF NOT EXISTS snapshots (
            scan_id       TEXT    NOT NULL,
            tool_name     TEXT    NOT NULL,
            captured_at   INTEGER NOT NULL,
            payload_json  TEXT    NOT NULL,
            payload_hash  TEXT    NOT NULL,
            item_count    INTEGER NOT NULL,
            PRIMARY KEY (scan_id, tool_name),
            FOREIGN KEY (scan_id) REFERENCES scans(id) ON DELETE CASCADE
        );
        CREATE INDEX IF NOT EXISTS snapshots_tool_captured
            ON snapshots(tool_name, captured_at DESC);

        CREATE TABLE IF NOT EXISTS known_good (
            tool_name     TEXT    NOT NULL,
            identity_key  TEXT    NOT NULL,
            note          TEXT,
            added_at      INTEGER NOT NULL,
            PRIMARY KEY (tool_name, identity_key)
        );

        CREATE TABLE IF NOT EXISTS findings (
            id            TEXT    PRIMARY KEY,
            scan_id       TEXT    NOT NULL,
            created_at    INTEGER NOT NULL,
            severity      TEXT    NOT NULL,
            category      TEXT    NOT NULL,
            source        TEXT    NOT NULL,
            summary       TEXT    NOT NULL,
            evidence_json TEXT,
            FOREIGN KEY (scan_id) REFERENCES scans(id) ON DELETE CASCADE
        );
        CREATE INDEX IF NOT EXISTS findings_scan ON findings(scan_id);
        CREATE INDEX IF NOT EXISTS findings_severity_created
            ON findings(severity, created_at DESC);
        """;

    /// <summary>Phase-3 cost ledger for Anthropic API spend.</summary>
    public const string Migration002CostLog = """
        CREATE TABLE IF NOT EXISTS cost_log (
            id                          INTEGER PRIMARY KEY AUTOINCREMENT,
            occurred_at                 INTEGER NOT NULL,
            scan_id                     TEXT,
            model                       TEXT    NOT NULL,
            input_tokens                INTEGER NOT NULL,
            output_tokens               INTEGER NOT NULL,
            cache_read_input_tokens     INTEGER NOT NULL DEFAULT 0,
            cache_creation_input_tokens INTEGER NOT NULL DEFAULT 0,
            cost_usd                    REAL    NOT NULL
        );
        CREATE INDEX IF NOT EXISTS cost_log_occurred ON cost_log(occurred_at);
        """;

    /// <summary>Phase-3: agent-emitted recommended remediation action per finding.</summary>
    public const string Migration003RecommendedAction = """
        ALTER TABLE findings ADD COLUMN rationale TEXT;
        ALTER TABLE findings ADD COLUMN recommended_action TEXT;
        """;

    /// <summary>
    /// Ordered migration list. Append new entries at the end; never reorder or rewrite
    /// already-applied migrations — make a new one instead.
    /// </summary>
    public static readonly IReadOnlyList<(string Id, string Sql)> All = new[]
    {
        ("001-initial", Migration001Initial),
        ("002-cost-log", Migration002CostLog),
        ("003-recommended-action", Migration003RecommendedAction),
    };
}
