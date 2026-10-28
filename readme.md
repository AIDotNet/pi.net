# Pi.NET

[English](https://github.com/AIDotNet/pi.net/blob/main/readme.md) | [简体中文](https://github.com/AIDotNet/pi.net/blob/main/readme.zh-CN.md)

[![NuGet](https://img.shields.io/nuget/v/Pi.Ai?label=nuget)](https://www.nuget.org/packages/Pi.Ai)
[![CI](https://github.com/AIDotNet/pi.net/actions/workflows/ci.yml/badge.svg)](https://github.com/AIDotNet/pi.net/actions/workflows/ci.yml)
[![License: MIT](https://img.shields.io/github/license/AIDotNet/pi.net)](https://github.com/AIDotNet/pi.net/blob/main/LICENSE)

Pi.NET is a .NET 10 port of [Pi](https://github.com/earendil-works/pi). Pi is a minimal, extensible Agent harness (TypeScript monorepo, ~171k lines); this project ports its core libraries one by one to `net9.0` and `net10.0`, bringing the same capabilities to the .NET ecosystem.

Pi's philosophy is "make Pi fit your workflow, not the other way around": each library does one minimal-but-complete thing, and you compose the layers above it yourself. This project carries that philosophy over — rather than mechanically translating TS into C#, it rebuilds everything in idiomatic C# with semantic equivalence (discriminated unions → abstract records with pattern matching, JS Proxy → explicit verb APIs).

> **This project is entirely AI-driven.** The code, tests and docs under `src/`, `tests/`, `samples/` and `docs/` are written and maintained by AI against the TypeScript implementation as the behavioral reference. That buys a lot of speed and consistency — and it also means there will be rough edges. **Hit a bug? [Open an issue](https://github.com/AIDotNet/pi.net/issues/new) — reports are exactly how it gets fixed.**

<p align="center">
  <a href="https://github.com/AIDotNet/pi.net/blob/main/docs/media/pi-net-promo.mp4">
    <img src="https://raw.githubusercontent.com/AIDotNet/pi.net/main/docs/media/pi-net-promo-poster.png" alt="Pi.NET product promo" width="820">
  </a>
  <br>
  <sub>39-second promo (with voice-over) · click the cover to play (1080p / MP4 / 5.4 MB)</sub>
</p>

## What It Does

- **Unified multi-provider LLM API** — 10 built-in API implementations (openai-completions / openai-responses / openai-codex-responses / azure-openai-responses / anthropic-messages / google-generative-ai / google-vertex / mistral-conversations / bedrock-converse-stream / pi-messages) plus a **full catalog of 42 built-in providers**; streaming events, tool calls, thinking budgets, deferred responses, image generation (images) and text classification (classify); OAuth login flows for 9 providers (PKCE / RFC 8628 device code / loopback callback / refresh orchestration).
- **Agent runtime** — agent state machine with dual queues, the agent-loop main loop, sequential / parallel tool execution, abort / reset.
- **MCP client** — JSON-RPC protocol layer + in-memory / stdio / streamable-http transports + a complete OAuth layer (metadata discovery, PKCE authorization code, dynamic registration, credential-invalidation retry).
- **Application orchestration runtime (Chord)** — services and replicated state, delta engine, facets dependency graph with topological activation, external service source bindings.
- **TCP RPC channel** — handshake validation, request dispatch, cancellation, push — prepared for remotely controlling the coding-agent.
- **Vendor-neutral telemetry** — contracts + reference adapter + conformance test suite.

## Packages

| Package | Description |
|---|---|
| **Pi.Telemetry** | Vendor-neutral telemetry contracts, reference adapter and typed schemas, with a conformance test suite |
| **Pi.Protocol** | CBOR wire protocol: framing / codec, definite-length strict subset + JS safe-integer boundaries, **wire-compatible** with the TS implementation |
| **Pi.Ai** | Unified multi-provider LLM API: native API boilerplate + compat-provider registry; streaming, tool calls, thinking, images / classify, full OAuth login flows |
| **Pi.Agent** | Agent runtime: state machine / dual queues, agent-loop main loop, tool execution |
| **Pi.Mcp** | MCP client: JSON-RPC + three transports + the full OAuth layer |
| **Pi.Chord** | Standalone application orchestration runtime: services, replicated state, RPC and plugins |
| **Pi.Server / Pi.Client** | RPC transport: handshake / requests / cancellation / push, session routing, Unix domain sockets and local server discovery |
| **Pi.Codemode** | Sandboxed JavaScript execution whose only capability is calling injected tools; @options source parsing, TypeScript declaration rendering and sandbox orchestration, with a default Jint VM engine behind the replaceable `ICodemodeJsEngine` seam |
| **Pi.Tui** | Terminal UI framework: differential rendering + synchronized output, grapheme-level width measurement and wrapping, ANSI/OSC parsing with SGR tracking, Kitty keyboard protocol and key decoding, overlay stack, focus management, mouse event dispatch, colors (OKLCH/OKHSL) and keybinding registry, stdin escape-sequence buffering, inline terminal images (Kitty / iTerm2 protocols), stack/scroll layout engine, LaTeX math rendering, slash-command and file-path autocomplete (with `fd` fuzzy search), single-line input component (Emacs-style kill/yank, undo, bracketed paste), select-list component (filtering / scroll window / mouse / wheel), multi-line editor, markdown rendering, settings list, image component, and the alternate-screen renderer (full-screen mode: search / select-and-copy / scrollbar / Kitty image cache / overlay routing) |
| **Pi.Durable** | Durable sessions, memory / SQLite / JSONL-file storage backends, task graph, recovery, and coding-agent harness |
| **Pi.Env** | Remote execution environments over SSH: the `pi-env` daemon client and the framed protocol it speaks (the daemon itself is an upstream native artifact) |
| **Pi.CodingAgent / Pi.Cli** | Interactive coding agent, tools, extensions (compiled .NET assemblies **and** upstream TypeScript/JS), CLI, RPC, and experimental server/worker runtime |
| **Pi.Vacation** | Separate research/planning host built on the coding-agent runtime |
| **Pi.DurableHost** | Separate durable-session host (`pi-durable`) built on the coding-agent runtime, with the `subagent` tool and per-directory execution environments |
| **Pi.Evals** | Native .NET host and documentation eval runner; paired documentation evals use Docker |

The table covers the whole repo. **Pi.Telemetry, Pi.Protocol, Pi.Ai, Pi.Agent, Pi.Mcp, Pi.Chord, Pi.Server, Pi.Client, Pi.Codemode, Pi.Tui, Pi.Durable, Pi.Env and Pi.CodingAgent are the 13 libraries published to nuget.org**; `Pi.Cli`, `Pi.Vacation`, `Pi.DurableHost` and `Pi.Evals` are executables built from source and are deliberately not packed.

The TypeScript monorepo remains the behavioral reference. Bundled upstream docs and examples are retained as TypeScript references; use the [.NET extension guide](https://github.com/AIDotNet/pi.net/blob/main/src/Pi.CodingAgent/Assets/docs/dotnet-extensions.md) for C# extension APIs. The authoritative phase ledger, validation evidence, and documented TypeScript/.NET differences are in [migration-execution-status.md](https://github.com/AIDotNet/pi.net/blob/main/docs/migration-execution-status.md).

## Getting Started

### Install from NuGet

All 13 libraries ship to nuget.org under their project names, targeting `net9.0` and `net10.0`:

```bash
dotnet add package Pi.Ai        # unified multi-provider LLM API
dotnet add package Pi.Agent     # agent loop
dotnet add package Pi.Mcp       # MCP client
dotnet add package Pi.CodingAgent   # the whole stack, including the CLI runtime
```

### Usage docs

- **[docs/usage](https://github.com/AIDotNet/pi.net/blob/main/docs/usage/README.md)** — how to consume each package from C#: which package for which job, minimal working code, credentials, and the traps worth knowing.
- **[docs/usage/models-configuration.md](https://github.com/AIDotNet/pi.net/blob/main/docs/usage/models-configuration.md)** — every `models.json` field (provider, model, overrides, input limits, compatibility) with its type, default and description, generated from the published JSON Schema.

Package contents, release process and rollback are documented in [docs/publishing.md](https://github.com/AIDotNet/pi.net/blob/main/docs/publishing.md).

### Build from source

Requires the .NET 10 SDK. The libraries multi-target `net9.0` and `net10.0`; the executables do too.

```bash
dotnet restore Pi.slnx
dotnet build Pi.slnx --no-restore
dotnet run --project src/Pi.Cli/Pi.Cli.csproj -- --help
```

Test projects use xUnit v3 with Microsoft.Testing.Platform. Run a test project directly:

```bash
dotnet run --project tests/Pi.Evals.Tests/Pi.Evals.Tests.csproj --no-restore
```

## Examples

`pi` is the coding agent. Build it once and put it on `PATH`:

```bash
dotnet publish src/Pi.Cli -c Release -o ./pi-bin
export PATH="$PWD/pi-bin:$PATH"    # PowerShell: $env:PATH = "$PWD\pi-bin;$env:PATH"
pi --version
```

### Talk to it in a terminal

```bash
cd /path/to/your/repo
pi
```

The first time, run `/login` inside the session and pick a provider (OAuth, or an API key). After that you just say what you want:

```text
Explain how this repository is structured, then add a test for the argument parser.
```

Pi prints every file it reads, every command it runs and every edit it makes. It does not stop and ask before each tool call — keep the work under version control, or use the restricted setup below.

### Put files in front of it with `@`

```bash
pi @src/Pi.Cli/Pi.Cli.csproj @Directory.Build.props "Why is the assembly named pi, and which packages does it pull in?"
```

`@path` expands into the first message, so the model starts with the file contents already in context instead of spending a turn reading them. Images work the same way.

### Run it from a script

```bash
# -p: do the work, print the final answer to stdout, exit
pi -p "List every project under src/ that targets net10.0, one per line."

# Piped stdin is prepended to the prompt, and print mode is implied
git diff | pi "Review this diff for correctness bugs. Be blunt."
```

Anything non-interactive counts: a piped stdin or a redirected stdout is enough. `-p` is only needed when both ends are a terminal.

### Get JSON instead of prose

```bash
pi --mode json "Find the three slowest test projects and say why" \
  | jq -c 'select(.type == "tool_execution_end") | {tool: .toolName, ms: .durationMs}'
```

`--mode json` writes one JSON record per line: a session header first, then the same session events the TUI renders — message deltas, tool executions, retries, compaction. Diagnostics go to stderr, so stdout stays parseable.

### Limit what it can touch

```bash
# -t replaces the default toolset; none of these four can modify a file
pi -t read,grep,find,ls -p "Review src/Pi.Agent and rank the three riskiest spots."

# -xt removes tools from the default set instead of replacing it
pi -xt write,edit -p "Explain how the files under tools/ are produced."
```

### Continue, name, export

```bash
pi -c                                # continue the most recent session for this directory
pi --resume                          # or pick one from a list
pi --name "refactor auth" "Start with the session state machine"
```

Sessions are saved under `~/.pi/agent/sessions/`, one directory per working folder. Inside a session, `/session` shows the current file and token usage, and `/export` writes it out as HTML or JSONL.

### Teach it your conventions

Three ways, none of them require code:

| You want | Write |
|---|---|
| Standing instructions for one repo | `AGENTS.md` at the root (also reads `CLAUDE.md`, `AGENTS.override.md`) |
| Tools hosted by another process | `mcp.json`, or `pi mcp add` |
| A reusable multi-step procedure | `skills/<name>/SKILL.md` |

```bash
pi mcp add filesystem -- npx -y @modelcontextprotocol/server-filesystem .
pi mcp list
```

```markdown
---
name: release-notes
description: Turn git log into release notes. Use when asked for changelogs, release notes, or "what changed since the last tag".
---
```

Once the file is in place, `/skill:release-notes v1.4.2` names it explicitly; otherwise the model loads it when the task matches the description.

### Embed it in your own .NET app

```csharp
using Pi.Ai.Types;
using Pi.CodingAgent.Core;

var result = await Sdk.CreateAgentSessionAsync(new CreateAgentSessionOptions
{
    Cwd = Directory.GetCurrentDirectory(),
    Tools = ["read", "grep", "find", "ls"],   // omit for the full default toolset
});

var session = result.Session;
if (session.Model is null)
{
    Console.Error.WriteLine(result.ModelFallbackMessage);   // no credentials configured
    return 1;
}

using var subscription = session.Subscribe(evt =>
{
    switch (evt)
    {
        case AgentSessionEvent.MessageUpdateEvent { AssistantMessageEvent: AssistantMessageEvent.TextDelta d }:
            Console.Write(d.Delta);
            break;
        case AgentSessionEvent.ToolExecutionStartEvent t:
            Console.Error.WriteLine($"\n[tool] {t.ToolName}");
            break;
    }
});

await session.PromptAsync("Where is the CLI entry point, and what does it wire up?");

var stats = session.GetSessionStats();
Console.Error.WriteLine($"\n{stats.TotalMessages} messages · {stats.ToolCalls} tool calls · ${stats.Cost:0.0000}");
```

This is not a mock. The session reads credentials and settings from `~/.pi/agent`, loads `.pi/` and `AGENTS.md` from the project, registers the built-in tools, and writes the transcript to the session directory — the same assembly the CLI runs. Pin `Tools` and `Cwd` and you have a restricted agent to hand to your team.

### Runnable projects

Four console projects use the same APIs and are compiled by CI, so they cannot silently rot:

| Project | What it shows | Needs a key |
|---|---|---|
| [Pi.Samples.FauxAgent](samples/Pi.Samples.FauxAgent) | full agent loop, scripted model, real tool execution, event stream | no |
| [Pi.Samples.RealModel](samples/Pi.Samples.RealModel) | streaming from a live provider, plus the model-catalog path | yes |
| [Pi.Samples.CodingSession](samples/Pi.Samples.CodingSession) | the SDK above, end to end, with events and session stats | yes |
| [Pi.Samples.McpClient](samples/Pi.Samples.McpClient) | standalone MCP client: list tools, call one, read a resource | no (needs Node/npx) |

```bash
dotnet run --project samples/Pi.Samples.FauxAgent      # no API key needed
dotnet run --project samples/Pi.Samples.RealModel -- anthropic
dotnet run --project samples/Pi.Samples.CodingSession
dotnet run --project samples/Pi.Samples.McpClient
```

More detail per project in [samples/README.md](samples/README.md). Every flag and subcommand is listed in [docs/usage/cli.md](https://github.com/AIDotNet/pi.net/blob/main/docs/usage/cli.md); the full embedding surface is in [docs/usage/sdk.md](https://github.com/AIDotNet/pi.net/blob/main/docs/usage/sdk.md).

## Migration Status

Implementation progress, phase-by-phase evidence, and known TypeScript/.NET differences are tracked in [docs/migration-execution-status.md](https://github.com/AIDotNet/pi.net/blob/main/docs/migration-execution-status.md).

## Issues

Because the project is generated and maintained by AI, the most valuable thing you can give it is a precise failure report. [Open an issue](https://github.com/AIDotNet/pi.net/issues/new) when something misbehaves — a crash, a wrong result, a behavior that differs from upstream Pi, or a doc sentence that no longer matches the code.

A report that gets triaged fastest includes:

- the package and version (`dotnet list package`, or `pi --version` for the CLI) and your `dotnet --info` summary;
- a minimal repro — ideally a few lines against `samples/Pi.Samples.FauxAgent`, which runs without an API key;
- what you expected versus what happened, and if relevant the upstream TypeScript behavior you were comparing against.

## License

MIT — see [LICENSE](https://github.com/AIDotNet/pi.net/blob/main/LICENSE). Pi.NET is a port of [Pi](https://github.com/earendil-works/pi) by Mario Zechner; the upstream copyright is retained alongside the port's.
