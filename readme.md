# Pi.NET

[English](readme.md) | [简体中文](readme.zh-CN.md)

Pi.NET is a .NET 10 port of [Pi](https://github.com/earendil-works/pi). Pi is a minimal, extensible Agent harness (TypeScript monorepo, ~171k lines); this project ports its core libraries one by one to `net10.0`, bringing the same capabilities to the .NET ecosystem.

Pi's philosophy is "make Pi fit your workflow, not the other way around": each library does one minimal-but-complete thing, and you compose the layers above it yourself. This project carries that philosophy over — rather than mechanically translating TS into C#, it rebuilds everything in idiomatic C# with semantic equivalence (discriminated unions → abstract records with pattern matching, JS Proxy → explicit verb APIs).

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
| **Pi.CodingAgent / Pi.Cli** | Interactive coding agent, tools, compiled .NET extensions, CLI, RPC, and experimental server/worker runtime |
| **Pi.Vacation** | Separate research/planning host built on the coding-agent runtime |
| **Pi.DurableHost** | Separate durable-session host (`pi-durable`) built on the coding-agent runtime, with the `subagent` tool and per-directory execution environments |
| **Pi.Evals** | Native .NET host and documentation eval runner; paired documentation evals use Docker |

The TypeScript monorepo remains the behavioral reference. Bundled upstream docs and examples are retained as TypeScript references; use the [.NET extension guide](src/Pi.CodingAgent/Assets/docs/dotnet-extensions.md) for C# extension APIs. The authoritative phase ledger, validation evidence, and documented TypeScript/.NET differences are in [migration-execution-status.md](docs/migration-execution-status.md).

## Getting Started

Requires the .NET 10 SDK.

```bash
dotnet build Pi.slnx --no-restore -m:1
dotnet run --project src/Pi.Cli/Pi.Cli.csproj -- --help
```

Test projects use xUnit v3 with Microsoft.Testing.Platform. Run a test project directly:

```bash
dotnet run --project tests/Pi.Evals.Tests/Pi.Evals.Tests.csproj --no-restore
```

## Migration Status

Implementation progress, phase-by-phase evidence, and known TypeScript/.NET differences are tracked in [docs/migration-execution-status.md](docs/migration-execution-status.md).
