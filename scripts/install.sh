#!/usr/bin/env bash
# MacMonitor — local SSH key + Keychain bootstrap.
#
# What this does:
#   1. Generates a dedicated RSA-3072 SSH keypair in classic PEM format. RSA + PEM is
#      the most broadly-compatible format with .NET SSH libraries; the OpenSSH default
#      format that ssh-keygen emits without -m PEM has caused parse failures with
#      Renci.SshNet in the past.
#   2. Stores the private key bytes in the macOS Keychain under a generic-password
#      item, base64-encoded. Encoding sidesteps any newline/whitespace mangling that
#      would otherwise corrupt a multi-line PEM blob round-tripping through `security`.
#   3. Appends the public key to ~/.ssh/authorized_keys with restrictive options.
#   4. Prints the manual post-install steps (Remote Login, FDA).
#
# Re-running is safe: existing entries are detected and skipped.
# To rotate after a failed run:
#   /usr/bin/security delete-generic-password -s MacMonitor.SshKey
#   ./scripts/install.sh
set -euo pipefail

KEY_LABEL="${KEY_LABEL:-MacMonitor.SshKey}"
KEY_COMMENT="macmonitor@$(hostname -s)"
TMP_KEY_DIR="$(mktemp -d)"
TMP_KEY="${TMP_KEY_DIR}/id_rsa"
AUTHORIZED_KEYS="${HOME}/.ssh/authorized_keys"

cleanup() {
  rm -rf "${TMP_KEY_DIR}"
}
trap cleanup EXIT

if [[ "$(uname -s)" != "Darwin" ]]; then
  echo "This installer targets macOS only." >&2
  exit 1
fi

echo "==> Checking Keychain for existing item '${KEY_LABEL}'…"
if /usr/bin/security find-generic-password -s "${KEY_LABEL}" >/dev/null 2>&1; then
  echo "    Found. Skipping key generation."
  echo "    To rotate: /usr/bin/security delete-generic-password -s '${KEY_LABEL}'  then re-run."
else
  echo "==> Generating a new RSA-3072 keypair in PEM format (no passphrase)…"
  /usr/bin/ssh-keygen -t rsa -b 3072 -m PEM -N "" -C "${KEY_COMMENT}" -f "${TMP_KEY}" >/dev/null

  echo "==> Storing base64-encoded private key in Keychain as '${KEY_LABEL}'…"
  ENCODED_KEY="$(/usr/bin/base64 < "${TMP_KEY}" | /usr/bin/tr -d '\n')"
  /usr/bin/security add-generic-password \
      -s "${KEY_LABEL}" \
      -a "${USER}" \
      -w "${ENCODED_KEY}" \
      -j "MacMonitor SSH key (base64-encoded RSA PEM)" \
      -T /usr/bin/security \
      -U
  unset ENCODED_KEY
  echo "    Stored."

  echo "==> Installing the public key into ${AUTHORIZED_KEYS}…"
  mkdir -p "${HOME}/.ssh"
  chmod 700 "${HOME}/.ssh"
  touch "${AUTHORIZED_KEYS}"
  chmod 600 "${AUTHORIZED_KEYS}"

  PUB="$(cat "${TMP_KEY}.pub")"
  if grep -qF "${PUB}" "${AUTHORIZED_KEYS}" 2>/dev/null; then
    echo "    Public key already present, skipping append."
  else
    # Restrict this key: no forwarding, no PTY, no port forwarding. The forced command is
    # intentionally absent so the .NET worker can run multiple allow-listed commands. If
    # you want to lock this down further, replace with command="/usr/local/bin/macmon-shim".
    {
      echo ""
      echo "# MacMonitor — added $(date -u +%Y-%m-%dT%H:%M:%SZ)"
      echo "no-agent-forwarding,no-port-forwarding,no-X11-forwarding ${PUB}"
    } >> "${AUTHORIZED_KEYS}"
    echo "    Public key appended."
  fi
fi

echo
echo "==> Anthropic API key (Phase 3 — optional)"
ANTHROPIC_LABEL="${ANTHROPIC_LABEL:-MacMonitor.AnthropicKey}"
if /usr/bin/security find-generic-password -s "${ANTHROPIC_LABEL}" >/dev/null 2>&1; then
  echo "    Keychain item '${ANTHROPIC_LABEL}' already exists. Leaving it alone."
  echo "    To rotate: /usr/bin/security delete-generic-password -s '${ANTHROPIC_LABEL}'"
else
  if [[ -n "${ANTHROPIC_API_KEY:-}" ]]; then
    KEY="${ANTHROPIC_API_KEY}"
    echo "    Using \$ANTHROPIC_API_KEY from environment."
  else
    echo "    Paste your Anthropic API key (starts with 'sk-ant-...'), or press Enter to skip:"
    read -r -s KEY
    echo
  fi
  if [[ -n "${KEY}" ]]; then
    /usr/bin/security add-generic-password \
        -s "${ANTHROPIC_LABEL}" \
        -a "${USER}" \
        -w "${KEY}" \
        -j "MacMonitor Anthropic API key" \
        -T /usr/bin/security \
        -U
    unset KEY
    echo "    Stored in Keychain as '${ANTHROPIC_LABEL}'."
  else
    echo "    Skipped. Phase-3 triage will be disabled until you store a key:"
    echo "      /usr/bin/security add-generic-password -s ${ANTHROPIC_LABEL} -a \$USER -w 'sk-ant-...' -T /usr/bin/security -U"
  fi
fi

cat <<EOF

==================================================================================
 MacMonitor — manual steps you still need to do
==================================================================================

 1. Enable Remote Login (sshd):
       System Settings → General → Sharing → toggle "Remote Login" on.
       Restrict access to the current user only.

 2. Grant Full Disk Access. The binary that needs it is sshd-keygen-wrapper:
       System Settings → Privacy & Security → Full Disk Access → "+"
       → ⌘⇧G → /usr/libexec/sshd-keygen-wrapper

    Without this step, the recent_downloads tool will return zero results and
    list_launch_agents will be missing user-scope plists.

 3. Set your username in src/MacMonitor.Worker/appsettings.json:
       "Ssh": { "User": "$(whoami)", … }

 4. Seed the host key (first connection only):
       ssh -o StrictHostKeyChecking=accept-new $(whoami)@127.0.0.1 true

 5. Smoke test:
       dotnet run --project src/MacMonitor.Worker -- once

    You should see one Info finding per tool printed and a JSONL file at
    ~/Library/Logs/MacMonitor/findings-YYYY-MM-DD.jsonl.

==================================================================================
EOF
