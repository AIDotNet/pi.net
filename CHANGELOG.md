# Changelog

All notable changes to `pi.net` — the .NET 10 port of [pi](https://github.com/badlogic/pi-mono) — are documented here.
Format follows [Keep a Changelog](https://keepachangelog.com/); versions track the upstream `packages/*` release line.

## [Unreleased]

### Fixed

- **启动横幅与 `--version` 仍报 `v1.0.4`**: `Config.Version` 沿用 TS 语义，优先读随包分发的
  `src/Pi.CodingAgent/Assets/package.json` 的 `version`，而 1.4.0 发版只改了
  `Directory.Build.props` 的 `<Version>`（只影响程序集版本，是回落路径）。该资产已重新从上游
  `v1.4.0` 同步（含依赖版本与 `schemas` 条目），`Assets/CHANGELOG.md` 一并对齐，`/changelog`
  不再停在 1.0.4。新增构建期守卫 `ValidatePackagedVersion`，两处版本不一致直接报错。

## [1.4.0] - 2026-10-23

Version alignment with upstream Pi `1.4.0`: the port now tracks the newer stable
release line, with the user-visible fixes from upstream `1.1.0`–`1.4.0` backported
in this batch. The full inventory and the remaining backlog live in
[`docs/upstream-1.4.0-delta.md`](docs/upstream-1.4.0-delta.md).

### Added

- **Usage docs** (`docs/usage/`): per-package guides for consuming the 13 NuGet packages from C# —
  which package for which job, minimal working code, credential sources, and pitfalls. Covers
  `Pi.Ai`, `Pi.Agent`, `Pi.CodingAgent`, `Pi.Mcp`, plus one-liners for the remaining nine.
- **Runnable samples** (`samples/`): four console projects registered in `Pi.slnx`, so CI compiles
  them and the documented APIs cannot silently drift. `Pi.Samples.FauxAgent` runs the full agent
  loop with a scripted provider (no API key), `Pi.Samples.RealModel` streams from a real provider
  through both the legacy `Model` entry and the catalog `ModelSpec` path,
  `Pi.Samples.CodingSession` embeds a coding agent via `Sdk.CreateAgentSessionAsync`, and
  `Pi.Samples.McpClient` drives an MCP server over stdio or streamable HTTP.

### Fixed

- **Upstream 1.1.0–1.4.0 backport (first batch)**: eleven user-visible fixes from the newer
  stable releases, with the full inventory and the remaining backlog in
  `docs/upstream-1.4.0-delta.md`.
  - `grep` runs ripgrep with `--no-config`, so `RIPGREP_CONFIG_PATH` (e.g. `--fixed-strings`)
    can no longer silently turn regex searches into literal ones (upstream #11203).
  - `mcp.json` files that start with a UTF-8 BOM are parsed instead of skipping every server
    (upstream #11191).
  - `~` is expanded in `extensions` / `skills` / `prompts` / `themes` patterns, so `+~/x`,
    `-~/x` and `!~/skills/pdf` match (upstream #10765).
  - Empty quoted prompt-template arguments (`""`, `''`) are kept, so later positional
    arguments no longer shift and positional defaults apply (upstream #10867).
  - Print mode keeps printing the partial text but warns on stderr and exits `1` when the
    response stopped at the model's output token limit (upstream #10799).
  - `PI_CLIPBOARD_OSC52=1` forces OSC 52 copies for remote clients that leave no SSH markers,
    such as multiplexer attaches or remote IDE daemons (upstream #10826/#10835/#10114).
  - Compaction, turn-prefix and branch summaries no longer derive a fixed output cap from
    `reserveTokens`; the provider sizes them from the request, so long reasoning can no longer
    make summarization fail with "generation hit the token cap" (upstream #9512/#9075/#10908).
  - Whitespace-only tool output (a bare `\r\n` from bash) uses the empty-output placeholder on
    OpenAI Completions, OpenAI Responses and Google instead of being sent verbatim (#8720).
  - Invalid `\u` escapes such as `\usepackage` are kept as literal text, so tool-call arguments
    no longer lose the field and everything after it (upstream #10868).
  - Retry classification matches the newer upstream table: HTTP status codes only count as
    standalone numbers, and gateway/Codex-proxy transient failures (`unable to forward upstream`,
    `upstream_stream_read_error`, `stream disconnected before completion`, Vertex OAuth refresh
    failures) are retried (upstream #10920/#10965/#10996/#11057/#10842).
  - A display-math block can no longer span a GFM table, so an unclosed `$$` stops swallowing
    tables while streaming (upstream #11159).
- `readme.md` / `readme.zh-CN.md` links now use absolute GitHub URLs. Relative links were rewritten
  into self-links by nuget.org, so every doc link on the package page was dead.
- Dropped the `-m:1` recommendation from the build-from-source snippet: serial execution is slower
  and reproduces two `Pi.Ai.Tests` failures plus a multi-minute `Pi.CodingAgent.Tests` hang that a
  parallel build does not.

## [1.0.4] - 2026-10-21

Initial stable release of the .NET port. Full functional parity with the upstream
TypeScript release `1.0.4` (baseline `eb326d265`), audited end to end:

- **13 runtime projects**: `Pi.Ai`, `Pi.Agent`, `Pi.Tui`, `Pi.CodingAgent` (+ `Pi.Cli`,
  `Pi.Vacation`, `Pi.DurableHost`), `Pi.Durable`, `Pi.Chord`, `Pi.Server`, `Pi.Client`,
  `Pi.Mcp`, `Pi.Protocol`, `Pi.Env`, `Pi.Telemetry`, `Pi.Codemode`, `Pi.Evals`.
- **Public API & wire audit**: all 1,426 barrel exports of the 13 source packages mapped
  (`docs/public-api-audit.md`); OpenAI / Anthropic / MCP wire shapes locked by test suites.
- **Tests**: 30,103 items across 13 test projects, 0 failures (5 platform skips on Windows);
  container (Docker 29.7.1) and POSIX/SSH acceptance completed on external Ubuntu 24.04.
- **Radius catalog persistence**: dynamic provider refresh now writes back through the models
  store (`RadiusProvider` implements `IProvider.RefreshModels`), superseding the in-memory-only
  note from the 1.0.4 baseline.
- **Differences from TypeScript** are registered with IDs in
  `docs/coding-agent-porting-status.md` (C-series) and `docs/tui-porting-status.md`
  (T-series), with an independent review in `docs/review-inconsistencies-2026-10-21.md`.
  User-visible ones:
  - Mermaid diagrams render as plain text (no bundled renderer; `IMermaidRenderer` seam).
  - Word-wise cursor movement treats a run of CJK characters as one word (no ICU dictionary
    segmentation in .NET); ASCII behavior matches upstream.
  - Extensions cannot rewrite `tool_call` input in place (block/terminate instead).
  - Bun/Node runtime shims (`bun/*`, jiti loaders, virtual modules, lazy loaders) are
    intentionally not ported; experimental plugin bundles run on a Jint engine.
