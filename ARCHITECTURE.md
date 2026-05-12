# MacMonitor — Feasibility & Architecture

A .NET worker service that runs in the background on macOS, periodically inspects the host for malware indicators, and uses an Anthropic Claude agent with tool use to triage findings. Inspections are executed by shelling out over **SSH to localhost** rather than running commands in-process.

---

## TL;DR — is this doable?

Yes. Every piece of the stack exists today and runs natively on macOS:

- .NET 8/9 background workers run fine on macOS via the Generic Host.
- `Renci.SshNet` is a mature SSH client for .NET; the macOS Remote Login service (sshd) is built in and toggled in System Settings → General → Sharing.
- The Anthropic .NET SDK (or plain `HttpClient` against `api.anthropic.com`) supports the full tool-use loop with Claude.
- All four inspection targets (processes, persistence, network, recent files) are reachable through standard macOS CLI tools (`ps`, `lsof`, `launchctl`, `codesign`, `mdfind`, `sqlite3` against the LaunchServices quarantine DB).

The hard parts are not technical capability but rather (1) macOS privacy permissions (TCC), (2) keeping the AI loop bounded in cost and prompt-injection-safe, and (3) being honest about the threat model: a userland scanner that runs on the same box it's monitoring is best understood as a **noise filter for opportunistic malware**, not a hardened EDR. A determined attacker with root can disable it.

The rest of this document lays out the design choices and the phased build plan.

---

## Constraints recap (decided up front)

| Decision | Choice |
|---|---|
| Runtime | .NET 8 (LTS) Worker Service on macOS |
| Inspection channel | SSH to `localhost` (sshd / Remote Login) |
| AI backend | Anthropic Claude API, tool-use loop |
| Scope | Single Mac, self-monitoring |
| Deliverable focus (this doc) | Architecture write-up only — no code yet |

---

## Threat model — what this realistically catches

Be explicit about this before building, because it determines what "good" looks like.

**Catches well**
- Commodity adware and PUPs that drop a `~/Library/LaunchAgents/*.plist` and a binary in `/Applications` or `~/Library/Application Support`.
- Cryptominers and reverse shells running as long-lived user processes with non-Apple parent chains and outbound connections to non-CDN IPs.
- Recently downloaded executables that bypassed Gatekeeper (no `com.apple.quarantine` xattr cleared by a notarized opener) or that fail `codesign --verify`.
- Net-new persistence entries appearing between scans (diff against a stored baseline).

**Catches poorly or not at all**
- Kernel-level rootkits, anything below the userland.
- In-memory-only payloads that never touch disk.
- Malware that has already escalated to root and tampered with the worker, sshd config, or launchd plists used by the worker itself.
- Living-off-the-land abuse of legitimate notarized binaries (the AI can flag suspicious *behavior*, but signal-to-noise is poor here).

State this in the README so users don't treat MacMonitor as a replacement for XProtect/Gatekeeper or a commercial EDR.

---

## High-level architecture

```
┌──────────────────────────────────────────────────────────────────────────┐
│                           MacMonitor Worker (dotnet)                     │
│                                                                          │
│  ┌──────────────┐    ┌──────────────────┐    ┌────────────────────────┐  │
│  │  Scheduler   │───▶│ Inspection       │───▶│ Baseline Store         │  │
│  │ (PeriodicTmr)│    │ Orchestrator     │    │ (SQLite, EF Core)      │  │
│  └──────────────┘    └────────┬─────────┘    └────────────────────────┘  │
│                               │                                          │
│                               ▼                                          │
│                      ┌──────────────────┐                                │
│                      │ Tool Registry    │  e.g. ListProcesses,           │
│                      │ (ITool registry) │       ListLaunchAgents,        │
│                      └────────┬─────────┘       NetConnections, …       │
│                               │                                          │
│                               ▼                                          │
│                      ┌──────────────────┐                                │
│                      │ SSH Executor     │  Renci.SshNet → ssh user@127  │
│                      │ (allow-listed)   │                                │
│                      └────────┬─────────┘                                │
│                               │                                          │
│  ┌────────────────────────────┴────────────────────────────────────┐     │
│  │                          Claude Agent Loop                      │     │
│  │  system prompt + diffed findings ──▶ Claude (tool use)          │     │
│  │                          ◀── tool_use blocks                    │     │
│  │  execute tool via SSH    ──▶ tool_result                        │     │
│  │                          ◀── final report (JSON)                │     │
│  └────────────────────────────┬────────────────────────────────────┘     │
│                               │                                          │
│                               ▼                                          │
│             ┌────────────────────────────────────┐                       │
│             │ Alert Sinks                        │                       │
│             │ • macOS Notification (osascript)   │                       │
│             │ • Structured log file (JSON)       │                       │
│             │ • Optional: email / Slack webhook  │                       │
│             └────────────────────────────────────┘                       │
│                                                                          │
└──────────────────────────────────────────────────────────────────────────┘
                                    │
                                    ▼
                        ┌─────────────────────────┐
                        │ macOS sshd (Remote Login)│
                        │  ssh keypair, allow-list │
                        └─────────────────────────┘
```

Process lifecycle: a `launchd` user agent (`~/Library/LaunchAgents/com.you.macmonitor.plist`) keeps the dotnet binary alive (`KeepAlive=true`, `RunAtLoad=true`). Internal scheduling is done with `PeriodicTimer`/Quartz.NET — *not* by launchd's `StartInterval`, because we want the worker process to own its cadence and state.

---

## Components in detail

### 1. Host & scheduler

A standard `IHostedService` / `BackgroundService` started by `Host.CreateApplicationBuilder`. The scheduler kicks off a scan run on a configured interval (default: every 15 minutes) plus an immediate scan on startup. Use Quartz.NET if cron-like expressions are needed; otherwise a `PeriodicTimer` is enough.

### 2. SSH executor

```csharp
public interface ISshExecutor
{
    Task<CommandResult> RunAsync(string commandId, IDictionary<string, string> args, CancellationToken ct);
}
```

Backed by `Renci.SshNet.SshClient`. **Important constraints:**

- Commands are not free-form strings from the model. The executor takes a `commandId` (e.g., `list-processes`) and parameter dict. The mapping `commandId → final shell template` lives in code and is the only thing actually sent to sshd. This is the boundary that prevents prompt injection from turning into RCE.
- One persistent SSH session per scan run, multiplexed for the tool calls in that run; tear down between runs.
- Authentication: dedicated keypair generated at install time, private key stored in macOS Keychain (`security add-generic-password`), public key written to `~/.ssh/authorized_keys` with a `command="…"` restriction or a `ForceCommand` wrapper if you want belt-and-braces.
- Timeouts on every command (default 10s); kill the channel on timeout.

### 3. Tool registry

Each inspection capability is an `ITool` with:

- `Name` — what Claude sees (`list_launch_agents`).
- `Description` — what it returns and when to use it.
- `InputSchema` — JSON schema for the parameters.
- `ExecuteAsync(args, ct)` — calls the SSH executor with the matching `commandId`, parses output, returns a structured object.

This is the same shape Anthropic's tool-use API expects, so registering tools with the agent is just a projection of this registry.

### 4. Inspection tool catalog (initial)

| Tool | Underlying command | Returns |
|---|---|---|
| `list_processes` | `ps -axo pid,ppid,user,%cpu,%mem,lstart,command` | Process table |
| `process_detail` | `lsof -p <pid>` + `codesign -dv --verbose=4 <path>` | Open files, signing info |
| `list_launch_agents` | `ls -la` over `/Library/LaunchAgents`, `~/Library/LaunchAgents`, `/Library/LaunchDaemons`, `/System/Library/LaunchDaemons` | Plist filenames + mtimes |
| `read_launch_plist` | `plutil -convert xml1 -o - <path>` | Parsed plist (Label, ProgramArguments, RunAtLoad, KeepAlive, etc.) |
| `network_connections` | `lsof -nP -iTCP -iUDP -sTCP:ESTABLISHED,LISTEN` | (pid, proc, laddr, raddr, state) |
| `recent_downloads` | `ls -laT ~/Downloads` + xattr check via `xattr -p com.apple.quarantine` | Files, ages, quarantine flag |
| `quarantine_events` | `sqlite3 ~/Library/Preferences/com.apple.LaunchServices.QuarantineEventsV2 "SELECT …"` | Recently quarantined items, originating URL/agent |
| `recent_executables` | `mdfind 'kMDItemContentType == "public.unix-executable" && kMDItemFSContentChangeDate > $time.now(-7d)'` | Newly written executables |
| `hash_file` | `shasum -a 256 <path>` | SHA-256 |
| `verify_signature` | `codesign --verify --deep --strict <path>; spctl --assess --type execute <path>` | Signing & notarization status |
| `xprotect_version` | `defaults read /Library/Apple/System/Library/CoreServices/XProtect.bundle/Contents/Info CFBundleShortVersionString` | Version string (sanity check) |

These cover the four requirement areas:
1. **Processes** → `list_processes`, `process_detail`, `verify_signature`
2. **Persistence** → `list_launch_agents`, `read_launch_plist`
3. **Network** → `network_connections`
4. **Recent downloads/executions** → `recent_downloads`, `quarantine_events`, `recent_executables`, `hash_file`

### 5. Baseline store & diffing

SQLite (via Microsoft.Data.Sqlite or EF Core SQLite) holds the previous snapshot of each tool's output. The orchestrator runs every tool, hashes the normalized output, and produces a **diff** (additions/removals/changes) before it hands anything to Claude.

Why this matters: feeding the full process table to the model on every run is expensive and noisy. Feeding only "new since last scan" gives the model a clean signal and keeps token usage small. The agent can still call a "fetch full list" tool if it wants to dig deeper.

Suggested tables: `Snapshots(ScanId, ToolName, Hash, JsonBlob, CapturedAt)`, `Findings(Id, ScanId, Severity, Category, Summary, Evidence, Decision)`, `KnownGood(Hash, Note, AddedAt)` — the last one lets the user mark a finding as benign and silence it on future scans.

### 6. Claude agent loop

System prompt (sketch):

> You are a macOS malware triage analyst. You will be given a diff of recent system state changes since the last scan. You have tools to fetch more detail about processes, launch items, network connections, and files. Your goal is to produce a JSON report of findings, each with a severity (info|low|medium|high), a category (process|persistence|network|file), a one-sentence summary, the evidence you used, and a recommended action. Do not invent tool calls outside the provided list. If a finding is ambiguous, prefer to gather more evidence with another tool call before concluding. Stop when you have enough evidence; do not loop indefinitely.

Loop:

1. Build the user message: scan id, timestamp, diff summary, top-N suspicious candidates (cheap heuristics: unsigned binaries, unusual parents like `bash`/`python` spawning long-running listeners, plists with `RunAtLoad=true` from non-Apple paths).
2. POST to Claude with the tool list.
3. While the response contains `tool_use` blocks: execute each via the registry → SSH executor, append `tool_result` blocks, send again.
4. When the response is text only (or matches a "final report" tool the model is required to call), parse it as the report.
5. Persist findings to SQLite, fan out to alert sinks.

**Bounding the loop**: cap iterations (e.g., 8), cap total input+output tokens per scan, cap wall-clock (e.g., 60s). Abort and log if exceeded.

**Prompt-injection hardening**: tool outputs are *attacker-controlled* (a malicious launch plist can contain anything). Wrap every tool result in a clear delimiter and remind the model in the system prompt that tool output is untrusted data, not instructions. Critically, the model has no `run_arbitrary_command` tool — only the curated set — so even a successful injection cannot escalate to RCE.

### 7. Alerting

- **macOS notification**: `osascript -e 'display notification "…" with title "MacMonitor"'` for high-severity findings.
- **Structured log**: `~/Library/Logs/MacMonitor/findings-YYYYMMDD.jsonl`, one JSON line per finding. This is the source of truth.
- **Optional**: SMTP, Slack incoming webhook, or a small local web UI (a Blazor page bound to the SQLite DB) for review.

---

## SSH-to-localhost: pros, cons, gotchas

You picked self-monitoring via `ssh localhost`. It's an unusual choice; here's what you actually get and what to watch for.

**What you get**
- A uniform command channel that already works the same way it would for remote machines later (the codebase is portable to multi-host without rewriting the executor).
- An audit trail: every command goes through `sshd`, which logs to the unified log (`log show --predicate 'subsystem == "com.openssh.sshd"'`).
- Clean process separation: the inspection commands run as children of sshd, not as children of the worker. If a tool hangs or misbehaves, the worker survives.
- A trust boundary you can tighten: `authorized_keys` `command=` directive, `Match User`/`ForceCommand` in `sshd_config`, or even a restricted shell.

**What you don't get**
- Real isolation. The sshd process and the worker run on the same kernel as the malware. If the host is fully owned, both are owned.
- Performance. SSH has handshake overhead. Mitigate with one persistent session per scan; don't open a fresh connection per command.

**Gotchas**
- **Remote Login must be on**: System Settings → General → Sharing → Remote Login. Document this in the installer.
- **TCC permissions are per-binary, and sshd inherits its own TCC context**. Reading `~/Downloads`, `~/Library/Mail`, etc. requires Full Disk Access granted to `sshd-keygen-wrapper` (or to the parent shell — this is fiddly and worth testing early). The worker's own binary may *also* need FDA depending on what it touches directly.
- **Key handling**: don't write the private key to disk in plaintext. Use Keychain (`security`) or, second-best, a file with `chmod 600` in `~/.ssh/`.
- **Locked-down shell**: many users have non-default shells (zsh, fish) with custom rc files that print to stderr; that pollutes command output. Force a clean shell: connect with `ssh -T -o … user@127.0.0.1 'bash -lc "<cmd>"'` and parse stdout only.

---

## Permissions and macOS-specific setup

The single most common reason a Mac inspection tool returns empty results is TCC. Plan for it:

| Scope | Who needs it | Where to grant |
|---|---|---|
| Full Disk Access | The binary that actually reads `~/Library/...`, `~/Downloads`, the QuarantineEvents DB. With SSH-to-localhost, this is `sshd-keygen-wrapper` (and/or your shell). | Settings → Privacy & Security → Full Disk Access |
| Developer Tools | Optional; needed if you want to skip Gatekeeper checks for tools you build locally | Same panel → Developer Tools |
| Accessibility | Not needed for this design | — |

For distribution, sign the worker binary (`codesign --sign "Developer ID Application: …"`) and notarize it (`xcrun notarytool submit …`) so users aren't blocked by Gatekeeper when they install it. If self-use only, ad-hoc signing is fine.

`launchd` plist for the worker (sketch):

```xml
<plist version="1.0">
<dict>
  <key>Label</key><string>com.you.macmonitor</string>
  <key>ProgramArguments</key>
  <array>
    <string>/usr/local/bin/MacMonitor</string>
  </array>
  <key>RunAtLoad</key><true/>
  <key>KeepAlive</key><true/>
  <key>StandardOutPath</key><string>/Users/USERNAME/Library/Logs/MacMonitor/stdout.log</string>
  <key>StandardErrorPath</key><string>/Users/USERNAME/Library/Logs/MacMonitor/stderr.log</string>
  <key>EnvironmentVariables</key>
  <dict>
    <key>DOTNET_ROOT</key><string>/usr/local/share/dotnet</string>
  </dict>
</dict>
</plist>
```

Loaded with `launchctl bootstrap gui/$(id -u) ~/Library/LaunchAgents/com.you.macmonitor.plist`.

---

## Configuration

Use `appsettings.json` for non-secrets, the macOS Keychain for secrets.

```jsonc
{
  "Scan": {
    "IntervalMinutes": 15,
    "MaxScanDurationSeconds": 60,
    "MaxAgentIterations": 8,
    "MaxAgentInputTokens": 40000
  },
  "Ssh": {
    "Host": "127.0.0.1",
    "Port": 22,
    "User": "yourusername",
    "KeychainItemName": "MacMonitor.SshKey"
  },
  "Anthropic": {
    "Model": "claude-sonnet-4-6",
    "KeychainItemName": "MacMonitor.AnthropicApiKey"
  },
  "Alerts": {
    "MinSeverityForNotification": "medium",
    "JsonLogPath": "~/Library/Logs/MacMonitor/findings.jsonl"
  }
}
```

Resolve Keychain items at startup with `security find-generic-password -s <name> -w`.

---

## Suggested project layout

```
MacMonitor/
├── MacMonitor.sln
├── src/
│   ├── MacMonitor.Worker/             # IHostedService entry point, DI wiring
│   │   ├── Program.cs
│   │   ├── Worker.cs                  # the BackgroundService
│   │   └── appsettings.json
│   ├── MacMonitor.Core/               # domain types, interfaces
│   │   ├── ITool.cs
│   │   ├── ISshExecutor.cs
│   │   ├── IBaselineStore.cs
│   │   ├── IAlertSink.cs
│   │   └── Models/                    # ProcessInfo, LaunchItem, NetConn, etc.
│   ├── MacMonitor.Ssh/                # Renci.SshNet wrapper, command allow-list
│   ├── MacMonitor.Tools/              # one file per ITool implementation
│   ├── MacMonitor.Agent/              # Anthropic client + tool-use loop
│   ├── MacMonitor.Storage/            # Microsoft.Data.Sqlite + handwritten migrations, IDiffer<T>s
│   └── MacMonitor.Alerts/             # osascript, file, webhook sinks
└── tests/
    ├── MacMonitor.Tools.Tests/        # parsing tests with captured fixtures
    └── MacMonitor.Agent.Tests/        # mock Anthropic transport, tool dispatch
```

Why split this way: `Tools` and `Ssh` together are the largest source of bugs (output-parsing) and benefit from being unit-testable against captured `ps`/`lsof` fixtures without needing a real SSH session. `Agent` should be testable with a fake `IAnthropicClient` that returns scripted tool-use responses.

---

## Phased build plan

**Phase 0 — spike (1 evening)**
- New worker project, prove `dotnet run` works, prove `Renci.SshNet` connects to `ssh localhost` with a key from Keychain, run `ps -ax`, log the result. No AI, no DB.

**Phase 1 — inspection skeleton**
- `ITool` interface and the four "list" tools (`list_processes`, `list_launch_agents`, `network_connections`, `recent_downloads`).
- Output normalization + JSON serialization.
- Structured log to `findings.jsonl` (everything is "info" severity at this stage).

**Phase 2 — baseline & diff** *(design done — see [PHASE2.md](./PHASE2.md))*
- `Microsoft.Data.Sqlite` + handwritten DDL (chose this over EF Core; rationale in PHASE2.md).
- Per-tool `IDiffer<T>` strategy with `IdentityKey` and `ContentHash`.
- Diff each tool's output against the previous snapshot; emit findings only on changes.
- `KnownGood` allow-list table + `allow`/`deny` CLI subcommands so users can suppress noise.

**Phase 3 — Claude agent** *(design done — see [PHASE3.md](./PHASE3.md))*
- Raw `HttpClient` against `api.anthropic.com/v1/messages` (no community SDK dependency).
- Default model **Claude Haiku 4.5**; configurable, with hard-coded pricing for cost tracking.
- Bounded tool-use loop: 8 iterations / 40k input tokens / 4k output / 60s wall-clock.
- **$5/day soft cap** with kill-switch; spend persisted in a new `cost_log` SQLite table.
- Read-only detail tools: `process_detail`, `read_launch_plist`, `verify_signature`, `hash_file`, `quarantine_events`.
- New `IScanTool` / `IAgentTool` markers separate orchestrator-driven from agent-driven tools.
- Findings gain `Rationale` + `RecommendedAction` (a string the user runs by hand — agent never executes).

**Phase 4 — alerting & UX** *(design done — see [PHASE4.md](./PHASE4.md))*
- macOS notifications via `osascript display notification` (built into macOS, no deps). Implemented this round.
- Throttled: max N per scan + per-identity cooldown (default 24h) to prevent fatigue.
- Blazor Server web app at `http://127.0.0.1:5050` (separate `MacMonitor.Web` project, started on demand). Skeleton this round; page bodies fill in next.
- Pages: Findings (list + filters), Finding detail (evidence + rationale + mark-known-good), Allow-list manager, Manual scan trigger, Cost overview.

**Phase 5 — hardening & packaging**
- Codesign + notarize the binary.
- launchd plist + install script (key generation, Keychain setup, FDA prompt).
- Sentry/log redaction for API keys.
- Cost guardrails (per-day token cap, kill switch).

A reasonable solo pace is roughly one phase per weekend; Phase 3 is the largest.

---

## Open questions worth deciding before code

These don't block the architecture but they shape Phase 3+:

- **What's the cost ceiling per day?** This sets the diff aggressiveness and the model choice (Sonnet vs Haiku for the loop).
- **Do you want the agent to be able to *quarantine* (rename, chmod -x, unload a launch agent) — or strictly read-only?** Read-only is the safe default and what this design assumes. Adding write actions means a separate, more-restricted tool set and an explicit user-confirmation step.
- **Single user or multi-user Mac?** Multi-user means scanning per-user `~/Library/LaunchAgents` for each user; this design handles only the running user's home.
- **Do you want VirusTotal / hash-reputation lookups?** Easy to add as a tool, but it's third-party data egress and you may not want that.

---

## Bottom line

The design is a small .NET worker plus four well-isolated subsystems (SSH executor, inspection tools, baseline store, Claude agent), wired together by a scheduler and emitting findings to a log and notifications. SSH-to-localhost is a defensible choice for portability and auditability, with the trade-off being TCC fiddliness on first install. Threat-model expectations should be set in the README so it's clear what this is and isn't.

Recommended next step: build Phase 0 and Phase 1, then re-evaluate before wiring in the agent — the real cost question only becomes answerable once you see the size of a typical diff on your machine.
