# Testing MacMonitor on a clean Mac

This document is for "I'm not infected — how do I know the system actually works?" The
approach: drop synthetic indicators that *look* like malware artifacts but are completely
benign in their actual behavior, run a scan, inspect the findings, then tear everything
down. The fixtures live in [tests/fixtures/](./tests/fixtures/).

> Nothing in these scripts modifies your shell, your network configuration, your real
> launch agents, your real downloads, or anything in `/Library`, `/System`, or
> `~/.ssh`. Every artifact is created with a `macmonitor-test` marker in its filename or
> command line, and `teardown-all.sh` will only delete files containing that marker.

---

## End-to-end recipe

You need a populated baseline first — otherwise the first post-fixture scan emits a
single "baseline established" `Info` finding per tool and no diffs.

```bash
cd ~/Documents/Claude/Projects/MacMonitor

# 1. Establish a clean baseline (run this on a clean machine).
dotnet run --project src/MacMonitor.Worker -- once

# 2. Drop the synthetic indicators.
bash tests/fixtures/setup-all.sh

# 3. Run a scan that should now produce diff findings + agent triage.
dotnet run --project src/MacMonitor.Worker -- once

# 4. Inspect what was emitted.
dotnet run --project src/MacMonitor.Worker -- findings 20

# 5. Watch the JSONL log directly if you want to see raw evidence.
tail -n +1 ~/Library/Logs/MacMonitor/findings-$(date -u +%F).jsonl | jq .

# 6. Clean up.
bash tests/fixtures/teardown-all.sh

# 7. Optional: scan once more — removed items should appear as 'removed' diff findings
#    for the persistence + download cases.
dotnet run --project src/MacMonitor.Worker -- once
```

If your daily Anthropic spend has been hit, agent triage pauses and you'll see only the
raw Phase-2 findings (default severities, no `Rationale` / `RecommendedAction`). That's
fine for verifying the diff layer; bump `Agent:DailyCostCapUsd` in `appsettings.json`
or wait until tomorrow to test the agent path.

---

## What each scenario should produce

### `setup-persistence.sh` — suspicious launch agent

**Drops:** `~/Library/LaunchAgents/com.macmonitor-test.persistence.plist` containing
`Label`, `RunAtLoad=true`, `KeepAlive=true`, and
`ProgramArguments=[/bin/bash, -c, "while true; do : ; sleep 60; done"]`.

**Phase-2 diff:** one `Medium` `list_launch_agents: added` finding with the path as the
identity key.

**Phase-3 agent:** should call `read_launch_plist` on the path, see the bash-loop
ProgramArguments, and either keep `Medium` or escalate to `High`. `RecommendedAction`
will likely be something like:

```text
launchctl bootstrap gui/$(id -u) ~/Library/LaunchAgents/com.macmonitor-test.persistence.plist; \
  rm ~/Library/LaunchAgents/com.macmonitor-test.persistence.plist
```

The agent does NOT execute that — it's text for you to read and act on (or ignore).

The plist is intentionally not loaded with `launchctl bootstrap`; the file's mere
presence is enough to fire the persistence diff. If you want a co-occurring process
finding too, load it manually after running setup.

### `setup-download.sh` — Gatekeeper bypass shape

**Drops:** `~/Downloads/macmonitor-test-payload.sh` (executable bit set, no
`com.apple.quarantine` xattr).

**Phase-2 diff:** one `Medium` `recent_downloads: added` finding (the Medium severity
comes from `SeverityRules.DownloadAdded` recognising the `.sh` extension + missing
quarantine xattr as the classic Gatekeeper-bypass pattern).

**Phase-3 agent:** should call `verify_signature` on the file. Because it's an ad-hoc
script, codesign reports it isn't signed. Agent likely keeps `Medium` (or escalates if
it's feeling spicy) with a recommended action of `rm` or `xattr -w com.apple.quarantine`.

### `setup-listener.sh` — TCP listener on port 13337

**Spawns:** a backgrounded Python process listening on `127.0.0.1:13337`. The pid is
recorded in `~/.cache/macmonitor-tests/listener.pids` for teardown.

**Phase-2 diff:** one `Low` `network_connections: added` finding. Identity key is
`Python|TCP|LISTEN:13337`. Loopback bind keeps severity at `Low`; if you want a `Medium`
tweak the script to bind `0.0.0.0`.

**Phase-3 agent:** should call `process_detail` on the listener's pid. The lsof block
shows the open socket; the ancestry walks up to your shell. Agent's recommended action
is most likely `kill <pid>` or `lsof -i :13337`.

This is the most "investigative" of the four — interesting to see whether the agent
spends a tool call here or skips it.

### `setup-process.sh` — long-running benign-looking python

**Spawns:** a backgrounded `python3` running `while True: time.sleep(60)`. Marker
`macmonitor-test-sentinel` in the source so it stands out in `ps`.

**Phase-2 diff:** one `Low` `list_processes: added` finding. Identity key is
`<full python -u -c …>@<your username>`.

**Phase-3 agent:** the most ambiguous of the four. Apple-signed system Python doing
nothing alarming — a calibrated agent should *downgrade* this to `Info` once
`process_detail` reports a clean codesign. If the agent insists on `Medium` for an
Apple-signed sleeping process, that's a sign the prompt needs tightening.

---

## Spotting Phase-3 vs Phase-2

The cleanest tell is the JSONL log. Phase-2-only findings have `rationale: null` and
`recommendedAction: null`. Triaged findings have both filled in.

```bash
# How many of today's findings did the agent triage?
jq -s 'map(select(.rationale != null)) | length' \
  ~/Library/Logs/MacMonitor/findings-$(date -u +%F).jsonl

# What's the agent's recommended action for each finding it touched?
jq -c 'select(.recommendedAction != null) | {summary, severity, recommendedAction}' \
  ~/Library/Logs/MacMonitor/findings-$(date -u +%F).jsonl
```

You can also compare the same identity across two scans by enabling both raw + triaged
findings — set `Agent:EmitRawFindings: true` in `appsettings.json` for the scan. (Not
done by default because it doubles the noise.)

---

## Running just one scenario

Each `setup-*.sh` is independent. If you only want to check that the persistence path
works end-to-end:

```bash
bash tests/fixtures/setup-persistence.sh
dotnet run --project src/MacMonitor.Worker -- once
dotnet run --project src/MacMonitor.Worker -- findings 10 Medium
bash tests/fixtures/teardown-all.sh   # teardown is always all-or-nothing for safety
```

---

## Marking a fixture as known-good

Useful when you want the diff layer to keep firing but you want to silence one specific
test case so you can focus on the others.

```bash
# Suppress the persistence test's plist
dotnet run --project src/MacMonitor.Worker -- allow list_launch_agents \
  "${HOME}/Library/LaunchAgents/com.macmonitor-test.persistence.plist" \
  "MacMonitor self-test"

# And later, undo
dotnet run --project src/MacMonitor.Worker -- deny list_launch_agents \
  "${HOME}/Library/LaunchAgents/com.macmonitor-test.persistence.plist"
```

---

## Safety notes

- `teardown-all.sh` will refuse to delete any path whose tracking record doesn't include
  the `macmonitor-test` marker — see `_lib.sh`'s `mm_remove_recorded`.
- The catchall section of `teardown-all.sh` also kills anything still listening on the
  test port (`13337` by default) and any leftover python processes whose command line
  contains `macmonitor-test-sentinel`. If you happen to use port 13337 for something
  legitimate, override with `MM_LISTENER_PORT=20000 bash tests/fixtures/setup-listener.sh`
  (and the same env var for teardown).
- State files for tracking pids and paths live in `~/.cache/macmonitor-tests/`. Safe to
  delete by hand if a teardown lost them — the catchall will still find the artifacts.
- None of the fixtures need root, sudo, or any TCC permission you haven't already
  granted MacMonitor itself.

---

## What this doesn't test

- **Real malware techniques** — for that, see MITRE Atomic Red Team's macOS modules.
  Heavier setup, but covers dozens of TTPs the four fixtures here don't.
- **Long-horizon behavior** — diff strategy correctness over many scans, retention,
  clock-skew. Add Phase-2-style unit tests against captured fixtures for that.
- **The actual API call shape** — these tests hit the real Anthropic API. Mock at the
  `HttpMessageHandler` level if you want to iterate on `AnthropicClient` without burning
  tokens.
