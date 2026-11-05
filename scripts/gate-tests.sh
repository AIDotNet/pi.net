#!/usr/bin/env bash
# 发布门禁测试：跑不依赖宿主环境（交互终端 / sshd）的测试工程。
#
# 每个工程首次失败后重跑一次，吸收已知的负载敏感 flake：
#   - Pi.Ai.Tests 的本地 HTTP 夹具在满载下偶尔收不到请求体
#   - Pi.Durable.Tests 的计时类用例（sleep / 文档挂载时序）
# 重跑仍失败才算真失败。
#
# 不含 Pi.Tui.Tests 与 Pi.Env.Tests：它们需要真实交互终端和本地 sshd，GitHub runner 不满足
# （实测 ubuntu 失败 959 项、windows 369 项，绝大多数来自这两个工程）。
#
# 用法：bash scripts/gate-tests.sh [配置] [工程...]
#   配置默认 Release，与 CI 一致；给出工程列表时只跑这些（排障用）。

set -uo pipefail

configuration="${1:-Release}"
# 默认只跑 net10.0：net9.0 是兼容目标，要求本机装有 .NET 9 运行时；CI 想覆盖可就地改成 net9.0 再跑一遍。
framework="${GATE_FRAMEWORK:-net10.0}"
root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
cd "${root}"

gate=(
  tests/Pi.Agent.Tests/Pi.Agent.Tests.csproj
  tests/Pi.Ai.Tests/Pi.Ai.Tests.csproj
  tests/Pi.Chord.Tests/Pi.Chord.Tests.csproj
  tests/Pi.Codemode.Tests/Pi.Codemode.Tests.csproj
  tests/Pi.CodingAgent.Tests/Pi.CodingAgent.Tests.csproj
  tests/Pi.Durable.Tests/Pi.Durable.Tests.csproj
  tests/Pi.Evals.Tests/Pi.Evals.Tests.csproj
  tests/Pi.Mcp.Tests/Pi.Mcp.Tests.csproj
  tests/Pi.Protocol.Tests/Pi.Protocol.Tests.csproj
  tests/Pi.Server.Tests/Pi.Server.Tests.csproj
  tests/Pi.Telemetry.Tests/Pi.Telemetry.Tests.csproj
)

if [[ $# -gt 1 ]]; then
  gate=("${@:2}")
fi

failed=()
flaked=()

for project in "${gate[@]}"; do
  name="$(basename "${project}" .csproj)"
  printf '== %s\n' "${name}"

  if dotnet test "${project}" -c "${configuration}" -f "${framework}" --no-build > "test-${name}.log" 2>&1; then
    tail -3 "test-${name}.log"
    continue
  fi

  printf '   首次失败，重跑一次（已知负载敏感 flake）\n'
  if dotnet test "${project}" -c "${configuration}" -f "${framework}" --no-build > "test-${name}.log" 2>&1; then
    flaked+=("${name}")
    tail -3 "test-${name}.log"
    continue
  fi

  failed+=("${name}")
  printf '   重跑仍失败，日志尾部：\n'
  tail -30 "test-${name}.log"
done

printf '\n---\n'
if [[ ${#flaked[@]} -gt 0 ]]; then
  printf '首次失败、重跑通过（flake）：%s\n' "${flaked[*]}"
fi

if [[ ${#failed[@]} -gt 0 ]]; then
  printf '失败（重跑后仍失败）：%s\n' "${failed[*]}"
  exit 1
fi

printf 'OK：%d 个工程全绿\n' "${#gate[@]}"
