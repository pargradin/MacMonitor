#!/usr/bin/env bash
# Drops a small executable shell script into ~/Downloads/ WITHOUT setting the
# com.apple.quarantine xattr — i.e., the classic Gatekeeper-bypass shape.
#
# What MacMonitor should see:
#   - recent_downloads diff: ADDED — Medium severity (SeverityRules.DownloadAdded
#     bumps to Medium when an executable extension lands without quarantine).
#   - Agent should call verify_signature on the path. Result: unsigned ad-hoc
#     script — codesign reports "code object is not signed at all".
#   - Recommended_action will probably be something like
#     "rm ~/Downloads/<file>" or "xattr -w com.apple.quarantine '0083;...' <file>".
set -euo pipefail
SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
. "${SCRIPT_DIR}/_lib.sh"

PATH_OUT="${HOME}/Downloads/${MM_TAG}-payload.sh"
mkdir -p "$(dirname "${PATH_OUT}")"

if [[ -f "${PATH_OUT}" ]]; then
  mm_log "download: already present at ${PATH_OUT}; nothing to do."
  exit 0
fi

cat > "${PATH_OUT}" <<'EOF'
#!/usr/bin/env bash
# This is a MacMonitor test fixture. It does nothing harmful.
echo "Hello from a synthetic test payload."
EOF
chmod +x "${PATH_OUT}"

# Belt-and-braces: ensure no quarantine xattr is set (some browsers/copy tools may
# add one automatically; we want the "Gatekeeper bypass" shape).
xattr -d com.apple.quarantine "${PATH_OUT}" 2>/dev/null || true

mm_record_path download "${PATH_OUT}"
mm_log "download: dropped ${PATH_OUT} (no quarantine xattr)."
mm_log "  expect a Medium 'recent_downloads: added' finding next scan."
