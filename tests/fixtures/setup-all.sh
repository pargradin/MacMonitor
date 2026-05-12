#!/usr/bin/env bash
# Run every setup-*.sh in this directory. Stops on first failure (set -e).
# Re-runnable: each setup script is idempotent.
set -euo pipefail
SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"

for script in "${SCRIPT_DIR}"/setup-*.sh; do
  name="$(basename "${script}")"
  [[ "${name}" == "setup-all.sh" ]] && continue
  echo "==> ${name}"
  bash "${script}"
done

echo
echo "All fixtures installed."
echo "Next step:  dotnet run --project src/MacMonitor.Worker -- once"
echo "Then:       dotnet run --project src/MacMonitor.Worker -- findings 20"
echo "Cleanup:    bash tests/fixtures/teardown-all.sh"
