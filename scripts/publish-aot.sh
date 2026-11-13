#!/usr/bin/env bash
# Publish pi as a Native AOT (ahead-of-time compiled, no JIT / no .NET runtime
# dependency) executable.
#
# Usage: bash scripts/publish-aot.sh [rid] [output-dir] [project]
#   rid        defaults to win-x64 (linux-x64, osx-arm64, ... also work when
#             the matching ILCompiler + native toolchain are installed)
#   output-dir defaults to artifacts/aot/<rid>
#   project    defaults to src/Pi.Cli (the `pi` CLI). Any of the four executable
#             hosts can be published: src/Pi.Cli, src/Pi.DurableHost,
#             src/Pi.Evals, src/Pi.Vacation. A short name (e.g. Pi.Vacation) is
#             also accepted. Each is rooted with the same complete-metadata /
#             TrimmerRootAssembly settings from Directory.Build.targets.
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
project="${3:-src/Pi.Cli/Pi.Cli.csproj}"
if [[ "${project}" != *.csproj && -d "${project}" ]]; then
  project="${project}/$(basename "${project}").csproj"
elif [[ "${project}" != *.csproj && "${project}" != */* ]]; then
  project="src/${project}/${project}.csproj"
fi
if [[ ! -f "${project}" ]]; then
  echo "::error::project not found: ${project}" >&2
  exit 2
fi

# Read <AssemblyName> from the project so the post-publish check works for every
# host (pi / pi-durable / Pi.Evals / pi-vacation) without a hard-coded map.
exe_name="$(sed -n 's/.*<AssemblyName>\([^<]*\)<\/AssemblyName>.*/\1/p' "${project}" | head -1 | tr -d '\r')"
if [[ -z "${exe_name}" ]]; then
  exe_name="$(basename "${project}" .csproj)"
fi

echo "== Native AOT publish: project=${project} rid=${rid} out=${out}"
dotnet publish "${project}" \
  -c Release -f net10.0 -r "${rid}" \
  -p:PublishAot=true \
  -o "${out}"

exe="${out}/${exe_name}"
[[ -f "${exe}.exe" ]] && exe="${exe}.exe"
if [[ ! -f "${exe}" ]]; then
  echo "::error::expected native executable at ${exe}" >&2
  exit 1
fi
echo "OK: native AOT binary -> ${exe} ($(wc -c < "${exe}") bytes)"
if [[ "${exe_name}" == "pi" ]]; then
  echo "    verify with: bash scripts/verify-aot.sh ${out}"
fi
