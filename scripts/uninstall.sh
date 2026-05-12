#!/usr/bin/env bash
# MacMonitor — undo what install.sh did.
set -euo pipefail

KEY_LABEL="${KEY_LABEL:-MacMonitor.SshKey}"
AUTHORIZED_KEYS="${HOME}/.ssh/authorized_keys"

if /usr/bin/security find-generic-password -s "${KEY_LABEL}" >/dev/null 2>&1; then
  echo "==> Removing Keychain item '${KEY_LABEL}'…"
  /usr/bin/security delete-generic-password -s "${KEY_LABEL}" >/dev/null
fi

ANTHROPIC_LABEL="${ANTHROPIC_LABEL:-MacMonitor.AnthropicKey}"
if /usr/bin/security find-generic-password -s "${ANTHROPIC_LABEL}" >/dev/null 2>&1; then
  echo "==> Removing Keychain item '${ANTHROPIC_LABEL}'…"
  /usr/bin/security delete-generic-password -s "${ANTHROPIC_LABEL}" >/dev/null
fi

if [[ -f "${AUTHORIZED_KEYS}" ]]; then
  echo "==> Removing MacMonitor lines from ${AUTHORIZED_KEYS}…"
  TMP="$(mktemp)"
  awk '
    /^# MacMonitor/ { skip = 2; next }
    skip > 0       { skip--; next }
    { print }
  ' "${AUTHORIZED_KEYS}" > "${TMP}"
  mv "${TMP}" "${AUTHORIZED_KEYS}"
  chmod 600 "${AUTHORIZED_KEYS}"
fi

echo "Done."
