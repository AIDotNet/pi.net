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

python3 scripts/verify-aot.py --aot "${exe}"
