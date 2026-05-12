#!/usr/bin/env bash
# Spawns a backgrounded Python process with a deliberately-suspicious-looking
# command line. No network. No persistence. Just a long-running interpreter.
#
# What MacMonitor should see:
#   - list_processes diff: ADDED — Low severity. The identity key is
#     "<command>@<user>" so the same launched-twice command would only fire once.
#   - Agent may call process_detail; ancestry will show the user's shell as parent,
#     codesign will show the system python as signed by Apple.
#   - This is the most ambiguous of the four scenarios — the agent should ideally
#     stay at Low or downgrade to Info once it sees Apple-signed python.
set -euo pipefail
SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
. "${SCRIPT_DIR}/_lib.sh"

# Use a marker string in the command line so it stands out in ps and so duplicate
# runs are idempotent.
MARKER="${MM_TAG}-sentinel"

if pgrep -f "${MARKER}" >/dev/null 2>&1; then
  mm_log "process: a sentinel python is already running; not starting another."
  exit 0
fi

python3 -u -c "
# ${MARKER}
import time
while True:
    time.sleep(60)
" > /dev/null 2>&1 &
PID=$!
disown "${PID}" 2>/dev/null || true

mm_record_pid process "${PID}"
mm_log "process: started python sentinel (pid ${PID})."
mm_log "  expect a Low 'list_processes: added' finding next scan."
