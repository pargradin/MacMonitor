# Phase 2 — Baseline & Diff (design)

> Status: design + skeleton. Implementation lands in the follow-up round.

Phase 1 emits one `Info` finding per tool every scan: "list_processes returned 412 items." Useful as a heartbeat, useless as a signal. Phase 2 keeps a per-tool snapshot in SQLite and emits findings only on **diffs** — what's *new since last scan*. This is also the prerequisite for the AI agent in Phase 3: feeding Claude 5–50 changes per scan instead of a 400-row process table is the difference between "useful triage at $X/day" and "$X/scan, 95% noise."

---

## Decisions made up front

| Question | Decision | Why |
|---|---|---|
| ORM vs raw ADO.NET | **`Microsoft.Data.Sqlite` + handwritten SQL** | Schema is small, no migration churn expected, and EF Core's value here is mostly LINQ which we don't need. A 30-line "run DDL files in order if not yet applied" runner replaces EF migrations. |
| Snapshot storage shape | **JSON blob per (scan, tool)**, hashed | Simpler than a table-per-record-type. Diff happens in C#, not in SQL — the data volumes are tiny and C# diffing makes the per-tool identity/hash logic readable. |
| Identity vs equality | **Per-tool `IDiffer<T>`** with `IdentityKey` and `ContentHash` | Different tools care about different fields. `(command, user)` makes sense for processes; path makes sense for plists. Hard-coding equality wouldn't generalize. |
| First-scan behavior | **Baseline-only**, no per-record findings | Otherwise the first scan emits hundreds of "new" findings. One `Info` summary per tool ("baseline established: 412 processes") is enough. |
| Retention | **Keep latest N=5 snapshots per tool** | Diff only needs the previous snapshot. The extra four are for debugging "what changed in the last hour" and for the Phase-3 agent's "fetch full list" tool. |

---

## Storage schema

```sql
-- One row per scan run. completed_at is null while in progress.
CREATE TABLE IF NOT EXISTS scans (
    id           TEXT    PRIMARY KEY,
    started_at   INTEGER NOT NULL,            -- unix seconds
    completed_at INTEGER,
    status       TEXT    NOT NULL DEFAULT 'running'
);

-- One row per (scan, tool). payload_json is the parsed list serialized via System.Text.Json.
CREATE TABLE IF NOT EXISTS snapshots (
    scan_id       TEXT    NOT NULL,
    tool_name     TEXT    NOT NULL,
    captured_at   INTEGER NOT NULL,
    payload_json  TEXT    NOT NULL,
    payload_hash  TEXT    NOT NULL,           -- sha256 of payload_json, hex
    item_count    INTEGER NOT NULL,
    PRIMARY KEY (scan_id, tool_name),
    FOREIGN KEY (scan_id) REFERENCES scans(id) ON DELETE CASCADE
);

CREATE INDEX IF NOT EXISTS snapshots_tool_captured
    ON snapshots(tool_name, captured_at DESC);

-- User-marked benign items. Identity-key is per-tool so the same string in two tools is independent.
CREATE TABLE IF NOT EXISTS known_good (
    tool_name     TEXT    NOT NULL,
    identity_key  TEXT    NOT NULL,
    note          TEXT,
    added_at      INTEGER NOT NULL,
    PRIMARY KEY (tool_name, identity_key)
);

-- Persisted findings. Same shape as Core.Models.Finding plus a column for evidence.
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
CREATE INDEX IF NOT EXISTS findings_severity_created ON findings(severity, created_at DESC);
```

The DB file lives at `~/Library/Application Support/MacMonitor/state.db` by default (configurable via `Storage:DatabasePath`).

---

## Per-tool diff strategy

Each `Core.Models.*` record gets an `IDiffer<T>` companion. The differ defines two pure functions and the orchestrator does the rest:

```csharp
public interface IDiffer<T>
{
    string ToolName { get; }                 // matches ITool.Name, used for storage keys
    string IdentityKey(T item);              // "same item across snapshots"
    string ContentHash(T item);              // "did the content of an identified item change"
    DiffEmissionPolicy Policy { get; }       // which of {Added, Removed, Changed} to emit findings for
}

[Flags]
public enum DiffEmissionPolicy
{
    None    = 0,
    Added   = 1 << 0,
    Removed = 1 << 1,
    Changed = 1 << 2,
}
```

Concrete strategies for the four Phase-1 tools:

| Tool | `IdentityKey` | `ContentHash` | Emit on |
|---|---|---|---|
| `list_processes` (`ProcessInfo`) | `command + "@" + user` | `(none — identity-only)` | Added |
| `list_launch_agents` (`LaunchItem`) | `path` | `mtime + "\|" + size` | Added, Removed, Changed |
| `network_connections` (`NetworkConnection`) | `process + "\|" + protocol + "\|" + (remote ?? "LISTEN:" + localPort)` | `(none)` | Added |
| `recent_downloads` (`DownloadedFile`) | `path` | `mtime + "\|" + size + "\|" + (quarantine != null)` | Added, Changed |

Rationale notes:

- **Processes**: pid is meaningless across snapshots (recycled). `(command, user)` collapses the same long-running daemon into one identity. We don't emit Removed because process exit is too noisy. We don't emit Changed because cpu/mem fluctuating isn't interesting. New `(command, user)` *is* interesting — that's a freshly-launched process category.
- **Launch items**: path is stable. We care about all three operations: a new plist (persistence install), a modified plist (malware updating its loader), and a removed plist (someone uninstalling).
- **Network connections**: ephemeral local ports must not enter the identity (otherwise every TCP connection is "new"). Remote address + process is what matters. Listening sockets get a synthetic `LISTEN:port` in place of remote so opening a new listener counts as "added."
- **Downloads**: path is stable. We care about new files and content changes (size or quarantine flag flipping is a sign someone re-downloaded or stripped quarantine).

---

## Severity matrix (default)

| Tool | Added | Changed | Removed |
|---|---|---|---|
| `list_processes` | Low | — | — |
| `list_launch_agents` | **Medium** | **Medium** | Low |
| `network_connections` | Low (Medium when remote is non-RFC1918) | — | — |
| `recent_downloads` | Info (**Medium** when quarantine flag is unset on an executable) | Low | — |

Persistence is deliberately the loudest because it's the highest-value signal of these four. Network gets a small bump for outbound to non-private addresses. The "executable + no quarantine" rule for downloads catches the classic Gatekeeper bypass pattern.

These defaults will be tunable in `appsettings.json` later; for the skeleton we'll hard-code them in a `SeverityRules` static class.

---

## First-scan / cold-start behavior

When `IBaselineStore.GetLatestSnapshotAsync(toolName)` returns `null`:

1. Persist the current snapshot.
2. Emit one `Info` finding per tool: `"baseline established: 412 processes"`.
3. Skip all per-record finding emission.

No edge-case toggles — just "previous is null" → baseline path.

---

## KnownGood allow-list

Two ways to populate, both write to the same table:

1. **CLI subcommand** added to `Program.cs`:
   ```bash
   dotnet run --project src/MacMonitor.Worker -- allow list_launch_agents \
       "/Library/LaunchAgents/com.zoom.us.plist" "Zoom auto-updater, verified by hand"
   ```
2. **Direct SQL** for power users who want to edit by hand.

On every scan, the orchestrator runs the diff, then filters Added/Changed/Removed against `known_good (tool_name, identity_key)` before emitting findings. The snapshot itself is *not* filtered — we still want the full picture in storage, we just suppress the alerts.

A symmetrical `deny` subcommand removes an entry.

---

## ScanOrchestrator V2 (planned flow)

Phase 1 today:
```
foreach tool in tools:
    result = await tool.Execute(ssh)
    finding = build_summary_finding(result)         # "X items"
    findings.append(finding)
emit findings to sinks
```

Phase 2 plan:
```
scanId = persist_scan_started()
foreach tool in tools:
    current     = await tool.Execute(ssh)
    differ      = differs[tool.Name]
    previous    = await store.get_latest_snapshot(tool.Name)
    await store.save_snapshot(scanId, tool.Name, current)

    if previous is null:
        findings.append(build_baseline_finding(tool, current))
        continue

    diff        = differ.compute(previous.payload, current.payload)
    filtered    = await known_good.filter(tool.Name, diff)
    findings.extend(build_diff_findings(scanId, tool, differ, filtered))

await store.persist_findings(findings)
emit findings to sinks
await store.persist_scan_completed(scanId)
await store.run_retention()                         # keep latest N per tool
```

ScanOrchestrator gains constructor deps on `IBaselineStore`, `IKnownGoodRepository`, `IScanRepository`, and `IEnumerable<IDiffer>` (or a `DifferRegistry` that maps tool name → differ).

---

## New project layout

```
src/MacMonitor.Storage/
├── MacMonitor.Storage.csproj          # net10.0; refs Microsoft.Data.Sqlite
├── StorageOptions.cs                  # DatabasePath, RetentionSnapshotsPerTool
├── Schema.cs                          # DDL constants + migration runner
├── SqliteBaselineStore.cs             # IBaselineStore impl
├── SqliteScanRepository.cs            # IScanRepository impl
├── SqliteKnownGoodRepository.cs       # IKnownGoodRepository impl
├── DifferRegistry.cs                  # Dictionary<string, IDiffer> by tool name
├── DiffEngine.cs                      # generic diff computation given an IDiffer
├── Differs/
│   ├── ProcessInfoDiffer.cs
│   ├── LaunchItemDiffer.cs
│   ├── NetworkConnectionDiffer.cs
│   └── DownloadedFileDiffer.cs
└── ServiceCollectionExtensions.cs     # AddMacMonitorStorage()

src/MacMonitor.Core/Abstractions/      # additions
├── IBaselineStore.cs
├── IScanRepository.cs
├── IKnownGoodRepository.cs
└── IDiffer.cs

src/MacMonitor.Core/Models/            # additions
├── Snapshot.cs
├── Diff.cs
└── KnownGoodEntry.cs
```

The Worker project gains a project reference to `MacMonitor.Storage` and registers the new services in DI; the orchestrator is rewritten in the implementation round.

---

## Configuration additions

```jsonc
"Storage": {
  "DatabasePath": "~/Library/Application Support/MacMonitor/state.db",
  "RetentionSnapshotsPerTool": 5
}
```

---

## Out of scope for Phase 2

- Anything AI-driven. The diff feeds Phase 3, but Phase 3 isn't part of this work.
- Detail tools (`process_detail`, `read_launch_plist`, `verify_signature`, `hash_file`). Those land with Phase 3 because the agent is what makes them worth running.
- Severity tuning in config. Hardcoded matrix above; tunable later.
- DB encryption at rest. The DB lives in your user-scoped `Application Support`; macOS's filesystem permissions are sufficient for the threat model. SQLCipher can be added later if the agent's findings warrant it.

---

## Open question parked for the implementation round

`Microsoft.Data.Sqlite` on `net10.0` — currently in preview SDK. The implementation round will pick the highest stable version that targets `net8.0`/`net9.0` (works on `net10` via TFM compatibility) so we don't pin to a preview.
