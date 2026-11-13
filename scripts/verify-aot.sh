#!/usr/bin/env bash
# Compare a Native AOT pi binary with the JIT build on deterministic offline
# commands (losslessness verification). See verify-aot.py for details.
#
# Usage: bash scripts/verify-aot.sh [aot-output-dir]
#   defaults to artifacts/aot/win-x64

set -euo pipefail
root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
cd "${root}"

out="${1:-artifacts/aot/win-x64}"
exe="${out}/pi.exe"
[[ -f "${exe}" ]] || exe="${out}/pi"

# Resolve a Python 3 interpreter. Windows Git Bash commonly has only the `py`
# launcher (no `python3`/`python` on PATH), while Linux/macOS have `python3`.
if command -v python3 >/dev/null 2>&1; then
  python=(python3)
elif command -v python >/dev/null 2>&1 \
     && python -c 'import sys; raise SystemExit(0 if sys.version_info[0] >= 3 else 1)' >/dev/null 2>&1; then
  python=(python)
elif command -v py >/dev/null 2>&1; then
  python=(py -3)
else
  echo "::error::Python 3 not found (tried python3/python/py -3)" >&2
  exit 2
fi

"${python[@]}" scripts/verify-aot.py --aot "${exe}"
