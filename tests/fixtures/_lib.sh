# Shared helpers for the MacMonitor synthetic test fixtures.
# Source this from each setup-*.sh / teardown-*.sh.

# State directory holds path / pid records so teardown can find what to clean up.
# Lives outside the repo so a botched teardown doesn't leave junk under tests/.
MM_STATE_DIR="${MM_STATE_DIR:-${HOME}/.cache/macmonitor-tests}"
mkdir -p "${MM_STATE_DIR}"

# Marker prefix used in every fixture filename so teardown can never accidentally
# touch something the user actually cares about.
MM_TAG="macmonitor-test"

mm_log() {
  printf '%s\n' "$*" >&2
}

mm_record_path() {
  # mm_record_path <name> <path>
  printf '%s\n' "$2" >> "${MM_STATE_DIR}/$1.paths"
}

mm_record_pid() {
  # mm_record_pid <name> <pid>
  printf '%s\n' "$2" >> "${MM_STATE_DIR}/$1.pids"
}

mm_kill_recorded() {
  # mm_kill_recorded <name>
  local f="${MM_STATE_DIR}/$1.pids"
  [[ -f "${f}" ]] || return 0
  while read -r pid; do
    [[ -z "${pid}" ]] && continue
    if kill -0 "${pid}" 2>/dev/null; then
      mm_log "  killing pid ${pid}"
      kill "${pid}" 2>/dev/null || true
    fi
  done < "${f}"
  rm -f "${f}"
}

mm_remove_recorded() {
  # mm_remove_recorded <name>
  local f="${MM_STATE_DIR}/$1.paths"
  [[ -f "${f}" ]] || return 0
  while read -r path; do
    [[ -z "${path}" ]] && continue
    # Belt-and-braces: only remove paths containing our marker tag.
    case "${path}" in
      *"${MM_TAG}"*)
        if [[ -e "${path}" ]]; then
          mm_log "  removing ${path}"
          rm -f "${path}"
        fi
        ;;
      *)
        mm_log "  refusing to remove non-tagged path: ${path}"
        ;;
    esac
  done < "${f}"
  rm -f "${f}"
}
