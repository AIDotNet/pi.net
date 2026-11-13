#!/usr/bin/env bash
# Publish pi as a Native AOT (ahead-of-time compiled, no JIT / no .NET runtime
# dependency) executable.
#
# Usage: bash scripts/publish-aot.sh [rid] [output-dir]
#   rid        defaults to win-x64 (linux-x64, osx-arm64, ... also work when
#             the matching ILCompiler + native toolchain are installed)
#   output-dir defaults to artifacts/aot/<rid>
#
# All AOT-specific MSBuild settings live in Directory.Build.props (activated by
# PublishAot=true) and Directory.Build.targets (assembly rooting). The default
# `dotnet build` / `dotnet pack` paths are untouched.
#
# Lossless guarantee: Native AOT keeps the exact same source and public API.
# POCO JSON serialization is routed through source-generated metadata (only
# when RuntimeFeature.IsDynamicCodeSupported is false); every Pi assembly is
# rooted with complete reflection metadata, so no capability is trimmed. JIT
# builds never enter the AOT branch, so their behaviour is byte-for-byte the
# pre-AOT behaviour. Verify with: bash scripts/verify-aot.sh <output-dir>

set -euo pipefail
root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
cd "${root}"

rid="${1:-win-x64}"
out="${2:-artifacts/aot/${rid}}"

echo "== Native AOT publish: rid=${rid} out=${out}"
dotnet publish src/Pi.Cli/Pi.Cli.csproj \
  -c Release -f net10.0 -r "${rid}" \
  -p:PublishAot=true \
  -o "${out}"

exe="${out}/pi"
[[ -f "${exe}.exe" ]] && exe="${exe}.exe"
if [[ ! -f "${exe}" ]]; then
  echo "::error::expected native executable at ${exe}" >&2
  exit 1
fi
echo "OK: native AOT binary -> ${exe} ($(wc -c < "${exe}") bytes)"
echo "    verify with: bash scripts/verify-aot.sh ${out}"
