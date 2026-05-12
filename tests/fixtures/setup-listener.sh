#!/usr/bin/env bash
# Spawns a backgrounded Python listener bound to 127.0.0.1:13337.
#
# What MacMonitor should see:
#   - network_connections diff: ADDED — Low severity (loopback is RFC1918-equivalent
#     so SeverityRules.NetworkAdded keeps it at Low; if you want a Medium, change
#     the bind to 0.0.0.0 and a port that isn't loopback-only).
#   - Agent should call process_detail on the listener's pid; the lsof / ancestry
#     blocks confirm a long-running python process listening on a non-standard port.
#   - Recommended_action probably "kill <pid>" or "lsof -i :13337".
#
# Uses Python instead of `nc -lk` because BSD nc behavior varies across macOS versions.
set -euo pipefail
SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
. "${SCRIPT_DIR}/_lib.sh"

PORT="${MM_LISTENER_PORT:-13337}"

if lsof -nP -iTCP:"${PORT}" -sTCP:LISTEN 2>/dev/null | grep -q LISTEN; then
  mm_log "listener: something is already listening on :${PORT}; not starting another."
  exit 0
fi

# `disown` so the child survives the script's exit. Logs go nowhere on purpose.
python3 -u -c "
import socket, time, signal, sys
s = socket.socket()
s.setsockopt(socket.SOL_SOCKET, socket.SO_REUSEADDR, 1)
s.bind(('127.0.0.1', ${PORT}))
s.listen(1)
signal.signal(signal.SIGTERM, lambda *_: sys.exit(0))
while True:
    time.sleep(60)
" > /dev/null 2>&1 &
PID=$!
disown "${PID}" 2>/dev/null || true

mm_record_pid listener "${PID}"
mm_log "listener: started python on 127.0.0.1:${PORT} (pid ${PID})."
mm_log "  expect a Low 'network_connections: added' finding next scan."
