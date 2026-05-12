#!/usr/bin/env bash
# Reverses every setup-*.sh — kills the recorded pids, removes the recorded files.
# Safe to run repeatedly. Only removes paths whose tracked filename contains the
# MM_TAG marker (see _lib.sh for the safety check).
set -euo pipefail
SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
. "${SCRIPT_DIR}/_lib.sh"

# Same names as the setup scripts use when calling mm_record_path / mm_record_pid.
NAMES=(persistence download listener process)

echo "==> Killing recorded pids…"
for n in "${NAMES[@]}"; do
  mm_kill_recorded "${n}"
done

echo "==> Removing recorded paths…"
for n in "${NAMES[@]}"; do
  mm_remove_recorded "${n}"
done

# Catch-all in case state files were lost: kill anything still listening on the
# test port and any lingering sentinel python processes. Both are idempotent.
TEST_PORT="${MM_LISTENER_PORT:-13337}"
LISTENER_PID="$(lsof -nP -iTCP:"${TEST_PORT}" -sTCP:LISTEN -t 2>/dev/null || true)"
if [[ -n "${LISTENER_PID}" ]]; then
  echo "  (catchall) killing :${TEST_PORT} listener pid ${LISTENER_PID}"
  kill "${LISTENER_PID}" 2>/dev/null || true
fi

if pgrep -f "${MM_TAG}-sentinel" >/dev/null 2>&1; then
  echo "  (catchall) killing leftover sentinel python(s)"
  pkill -f "${MM_TAG}-sentinel" 2>/dev/null || true
fi

# Defensive: remove any tagged plist that might have been added but never tracked.
shopt -s nullglob
for f in "${HOME}"/Library/LaunchAgents/com."${MM_TAG}".*.plist; do
  echo "  (catchall) removing ${f}"
  rm -f "${f}"
done
for f in "${HOME}"/Downloads/"${MM_TAG}"*; do
  echo "  (catchall) removing ${f}"
  rm -f "${f}"
done

echo
echo "Teardown complete."
echo "Verify with:  dotnet run --project src/MacMonitor.Worker -- once"
echo "(removed items should appear as 'list_launch_agents: removed' diff findings.)"
