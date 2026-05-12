#!/usr/bin/env bash
# Drops a benign-but-suspicious launch agent plist into ~/Library/LaunchAgents/.
#
# What MacMonitor should see:
#   - list_launch_agents diff: ADDED — Medium severity (persistence is loudest).
#   - Agent should call read_launch_plist on the path; the parsed payload shows
#     RunAtLoad=true, KeepAlive=true, ProgramArguments=[/bin/bash, -c, while ...].
#   - That pattern (bash -c with an infinite loop in a launch agent) is the textbook
#     malware-persistence shape, so the agent should keep severity at Medium or
#     escalate to High with a recommended_action like
#     "launchctl bootstrap gui/$(id -u) ~/Library/LaunchAgents/<file> ; rm <file>".
#
# This file does NOT load the agent (no `launchctl bootstrap`) — the file's mere
# presence on disk is enough to fire the persistence diff.
set -euo pipefail
SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
. "${SCRIPT_DIR}/_lib.sh"

PLIST="${HOME}/Library/LaunchAgents/com.${MM_TAG}.persistence.plist"
LABEL="com.${MM_TAG}.persistence"

if [[ -f "${PLIST}" ]]; then
  mm_log "persistence: plist already present at ${PLIST}; nothing to do."
  exit 0
fi

mkdir -p "$(dirname "${PLIST}")"
cat > "${PLIST}" <<EOF
<?xml version="1.0" encoding="UTF-8"?>
<!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
<plist version="1.0">
<dict>
    <key>Label</key>
    <string>${LABEL}</string>
    <key>ProgramArguments</key>
    <array>
        <string>/bin/bash</string>
        <string>-c</string>
        <string>while true; do : ; sleep 60; done</string>
    </array>
    <key>RunAtLoad</key>
    <true/>
    <key>KeepAlive</key>
    <true/>
</dict>
</plist>
EOF
chmod 644 "${PLIST}"
mm_record_path persistence "${PLIST}"
mm_log "persistence: dropped ${PLIST}"
mm_log "  expect a Medium 'list_launch_agents: added' finding next scan."
