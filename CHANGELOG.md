# Changelog

All notable changes to `pi.net` — the .NET 10 port of [pi](https://github.com/badlogic/pi-mono) — are documented here.
Format follows [Keep a Changelog](https://keepachangelog.com/); versions track the upstream `packages/*` release line.

## [Unreleased]

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
