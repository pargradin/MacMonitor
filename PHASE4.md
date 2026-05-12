# Phase 4 — Notifications & Web UI (design)

> Status: design + skeleton (this round). Notification sink is fully implemented; the
> Blazor pages are stubs the next round fills in.

Phase 1–3 produce a useful stream of triaged findings, but the only way to see them today
is `tail -f` on the JSONL log or the `findings` CLI. Phase 4 closes the UX gap with two
deliverables:

1. A **macOS notification sink** so high-severity findings surface as banners while
   you're working, without you having to remember to look at the logs.
2. A small **Blazor Server web app** for browsing, allow-listing, and triggering scans
   on demand — the kind of thing you'd reach for once a day to clean out noise or
   investigate a finding the agent flagged overnight.

---

## Decisions made up front

| Question | Decision | Why |
|---|---|---|
| Notification delivery | **`osascript display notification`** | Built into macOS, zero deps, works without TCC permission gymnastics. Trade-off: no action buttons, ~256 char limit, banner-only. Good enough for "something happened, go check." |
| Web framework | **Blazor Server** (not WebAssembly) | The data lives in SQLite on the same machine; round-tripping through the SignalR connection is essentially free, and we avoid the WASM bundle weight. |
| Web app runtime | **Separate `MacMonitor.Web` project**, started on demand | Worker stays single-purpose. A user only opens the UI a few times a day; no reason to run an HTTP server 24/7. |
| Web bind address | **`http://127.0.0.1:5050`** by default, no auth | Local-only by design. No TLS, no login — you can't reach it from the LAN. |
| Web scope | **Browse + allow-list + manual scan trigger** | The user picked the full interactive option. |
| Concurrent scans | The worker daemon and a Web-triggered scan can both run simultaneously; SQLite's WAL handles it | Acceptable for a personal tool; full mutex coordination is out of scope. |
| Throttling | Max 5 notifications per scan; per-identity cooldown of 24h, in-memory | Prevents notification fatigue on a noisy day. Cooldown state is process-local — restarting the worker resets the cooldown, which is fine. |

---

## Notification sink

**File:** `src/MacMonitor.Alerts/MacOsNotificationSink.cs`

Minimal flow:

```
EmitAsync(finding) →
  if !Enabled or non-macOS → return
  if finding.Severity < MinSeverity → return
  if (today, identity_key) already shown → return
  if scan-local count exceeds MaxPerScan → return
  build AppleScript fragment, escape quotes / backslashes
  start /usr/bin/osascript -e <fragment>, fire-and-forget (don't await stdout)
  record (now, identity_key) for cooldown
```

Configuration:

```jsonc
"Notifications": {
  "Enabled": true,
  "MinSeverity": "Medium",
  "MaxPerScan": 5,
  "Title": "MacMonitor",
  "IdentityCooldownHours": 24
}
```

The notification body is built from the finding's `Summary` (truncated to 200 chars).
When `RecommendedAction` is present, it's appended to the subtitle as "Action: …".
We don't try to put the full rationale in the banner — too long.

Why no action buttons: macOS's Notification Center buttons require `UNUserNotification`
APIs from a properly bundled .app, which is a much bigger lift than osascript. If
needed, a follow-up phase can add an `alerter` (Homebrew CLI) backend behind the same
`IAlertSink` interface.

---

## Blazor Server web app

**Project:** `src/MacMonitor.Web/`

Targets `net10.0`, `Microsoft.NET.Sdk.Web`. References the existing `MacMonitor.*`
projects so it can reuse `IBaselineStore`, `IScanRepository`, `IKnownGoodRepository`,
`ICostLedger`, and `ScanOrchestrator` (the manual-scan endpoint).

### Page inventory

| Route | Page | What it shows / does |
|---|---|---|
| `/` | redirects to `/findings` | — |
| `/findings` | Findings list | Paginated table of recent findings with filter controls (severity floor, source tool, date range). Click → detail. |
| `/findings/{id}` | Finding detail | Full evidence JSON, agent rationale, recommended action (with a "copy to clipboard" button), and a "mark known-good" button that calls `IKnownGoodRepository.AddAsync`. |
| `/allow-list` | Allow-list manager | Lists every entry from `known_good` with a "remove" button on each row. |
| `/scan` | Manual trigger | Single button that calls `ScanOrchestrator.RunOnceAsync` and streams progress via SignalR. Shows a card per recent scan with timing + finding count. |
| `/cost` | Cost overview | Today's spend vs. cap, last 7 days as a small bar chart, recent API calls. |

### Component inventory

- `SeverityBadge` — renders Info/Low/Medium/High with appropriate color.
- `FindingCard` — used in the list page.
- `EvidencePanel` — collapsible JSON pretty-print of `Finding.Evidence`.
- `NavMenu` — top nav linking the five pages above.
- `MainLayout` — wraps everything; minimal hand-written CSS, no Bootstrap / Tailwind.

### Layout & styling

Default Blazor template strips out, replaced with one small `app.css` (50–100 lines).
No CSS framework — the page is small enough that hand-rolled styles are clearer than
Tailwind utility classes. The look is "static dashboard," not "design-heavy SaaS UI."

### DI wiring

The Web project's `Program.cs` calls the same `AddMacMonitor*` extension methods the
Worker uses: `AddMacMonitorSsh`, `AddMacMonitorTools`, `AddMacMonitorAlerts`,
`AddMacMonitorStorage`, `AddMacMonitorAgent`, plus the cost-cap resolver wiring. Then it
adds `ScanOrchestrator` as a singleton (so the manual-scan page can resolve it) and
configures Blazor Server.

This means `Program.cs` has duplicate DI setup between Worker and Web. **Future-phase
refactor:** extract the shared registrations into a `MacMonitor.Hosting` library or a
single `services.AddMacMonitorRuntime(config)` extension method. Not in scope for
Phase 4 — the duplication is acceptable while the surface stabilises.

### Concurrency

A user-triggered scan from `/scan` and an automatic scan from the worker daemon can
run simultaneously. They use independent `ISshExecutor` instances and write to the same
SQLite DB (which is in WAL mode and tolerates concurrent writers fine). The only
correctness concern is the differ — if both scans run within milliseconds of each
other, both might see the same "previous snapshot" and emit duplicate diff findings.
In practice scans take 30s+, so collision is rare; we accept it for now.

### Bind address & security

- Server listens on `127.0.0.1:5050` only. `Kestrel.EndpointDefaults.Url` = `http://127.0.0.1:5050`.
- No HTTPS (loopback-only).
- No authentication. The threat model assumes a single-user Mac; anyone with shell
  access can already read the DB directly.
- Anti-forgery tokens are still on by default for Blazor Server forms — kept on, costs
  nothing and is good practice.

---

## Configuration additions

```jsonc
"Notifications": {
  "Enabled": true,
  "MinSeverity": "Medium",
  "MaxPerScan": 5,
  "Title": "MacMonitor",
  "IdentityCooldownHours": 24
},
"Web": {
  "BindAddress": "127.0.0.1",
  "Port": 5050
}
```

(Web settings only matter when running `dotnet run --project src/MacMonitor.Web`; the
Worker doesn't read them.)

---

## New project layout

```
src/MacMonitor.Alerts/                CHANGED
└── MacOsNotificationSink.cs          NEW (real implementation this round)
└── NotificationOptions.cs            NEW

src/MacMonitor.Web/                   NEW
├── MacMonitor.Web.csproj             net10.0, Microsoft.NET.Sdk.Web
├── Program.cs                        DI wiring + Kestrel binding
├── appsettings.json                  bind address / port
├── Components/
│   ├── App.razor                     root with HTML layout
│   ├── Routes.razor                  Blazor router
│   ├── _Imports.razor                global usings
│   └── Layout/
│       ├── MainLayout.razor
│       └── NavMenu.razor
├── Components/Pages/
│   ├── Findings.razor                stub
│   ├── FindingDetail.razor           stub
│   ├── AllowList.razor               stub
│   ├── Scan.razor                    stub
│   └── Cost.razor                    stub
├── Components/Shared/
│   ├── SeverityBadge.razor           small reusable badge
│   ├── FindingCard.razor             stub
│   └── EvidencePanel.razor           stub
└── wwwroot/
    └── app.css                       minimal hand-written
```

---

## Implementation-round work (next round)

- Page bodies for Findings (table + filters + pagination), FindingDetail, AllowList, Scan, Cost.
- SignalR-based progress for the manual scan trigger.
- `EvidencePanel` JSON pretty-printer (System.Text.Json indented serialization).
- Cost page mini-chart (small inline SVG, no dependency).
- Optional: small unit tests against a fake `IScanRepository` for the filter/pagination logic.

---

## Out of scope for Phase 4

- **TLS / auth.** Loopback-only by design.
- **CSV / JSON export.** Add later if the JSONL log isn't enough.
- **Notification action buttons.** Would need an alerter backend or a bundled .app.
- **WebSocket-based live finding stream.** SignalR could push findings as scans complete; the simpler "reload to see new findings" UX is fine for now.
- **Multi-user support.** Out of scope for a personal tool.
- **The shared-runtime refactor** (`MacMonitor.Hosting`). Plan calls it out so we don't re-discover the duplication later.
