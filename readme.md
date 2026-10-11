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
| **Pi.Codemode** | Sandboxed JavaScript execution whose only capability is calling injected tools; @options source parsing, TypeScript declaration rendering and sandbox orchestration (VM execution is externalized behind the `ICodemodeJsEngine` injection point) |
| **Pi.Tui** | Terminal UI framework: differential rendering + synchronized output, grapheme-level width measurement and wrapping, ANSI/OSC parsing with SGR tracking, Kitty keyboard protocol and key decoding, overlay stack, focus management, mouse event dispatch, colors (OKLCH/OKHSL) and keybinding registry, stdin escape-sequence buffering, inline terminal images (Kitty / iTerm2 protocols), stack/scroll layout engine, LaTeX math rendering, slash-command and file-path autocomplete (with `fd` fuzzy search), single-line input component (Emacs-style kill/yank, undo, bracketed paste), select-list component (filtering / scroll window / mouse / wheel), multi-line editor, markdown rendering, settings list, image component, and the alternate-screen renderer (full-screen mode: search / select-and-copy / scrollbar / Kitty image cache / overlay routing) |
| **Pi.CodingAgent** | Interactive coding Agent CLI (85.5k lines, in progress): the general utility layer (paths / processes / clipboard / images / MIME / HTML entities / ANSI stripping / chalk styling / JS semantics helpers) has landed; sessions / tool system / extensions / modes are being ported in batches |

> tui is wrapped up (44/45 files, only the `index.ts` barrel file remains — see [tui porting status](docs/tui-porting-status.md)); coding-agent (the interactive CLI main product, 85.5k lines) is in progress — see [coding-agent porting status](docs/coding-agent-porting-status.md); evals has not been ported yet. durable / codemode are fully ported — see [porting status](docs/porting-status.md).

## Getting Started

Requires the .NET 10 SDK.

```bash
dotnet build Pi.slnx    # 0 warnings 0 errors (add -m:1 inside the sandbox)
dotnet test  Pi.slnx    # 24,657 tests
```

Tests are based on xunit.v3 + Microsoft.Testing.Platform (MTP). If `dotnet test` does not trigger execution, run the test binaries directly:

```bash
tests/Pi.<Pkg>.Tests/bin/Debug/net10.0/Pi.<Pkg>.Tests.exe
```

## Porting Status

11 runtime projects + 1 in progress, building with 0 warnings 0 errors, **24,668 tests** (measured 2026-10-09, updated at P65; durable 340 + tui 23,812 + coding-agent 11 + others 505; tui includes 2,822 latex, 5,244 autocomplete, 5,450 input, 2,598 select-list, 7,040 editor, 4,990 markdown, 1,692 extra-components differential vectors and 9,109 case-mapping vectors, plus 37 alternate-screen scenarios; coding-agent includes 409 utils differential vectors). For the intermittent hangs of durable under full load, see the end of [docs/porting-status.md](docs/porting-status.md).

- ✅ Fully ported: telemetry / protocol / agent (incl. proxy.ts) / mcp
- ✅ Fully ported: server / client (RPC main loop, session routing, Unix domain socket transport and local server discovery)
- ✅ Fully ported: ai (10 built-in APIs + full catalog of 42 providers + compat + all utils + CLI)
- ✅ Fully ported: chord (delta / services / facets / node layers / api)
- ✅ Fully ported: codemode (identifier / types / source / declarations / runtime protocol + host sandbox orchestration + prelude source + Wasm loading)
- 🚧 durable: base layer + storage + session + env + harness (incl. the tools/events/harness.ts assembly) + **testing layer (assertions / storage-conformance / env-conformance / runner / storage-benchmark)** complete; the tests corresponding to each `harness-*.test.ts` are being filled in
- 🚧 tui: core layer complete (differential rendering + synchronized output, Unicode width/wrapping/truncation, ANSI/OSC/SGR tracking, keys and the Kitty protocol, overlay stack and focus, mouse event dispatch, colors and OKLCH/OKHSL, keybinding registry, stdin escape-sequence buffering, terminal images (Kitty/iTerm2 encoding and metadata), layout engine, LaTeX, autocomplete, single-line input component, select-list component, multi-line editor, markdown rendering, settings list, image component, alternate-screen search, **alternate-screen renderer (full-screen mode: search / select-and-copy / scrollbar / Kitty image cache / overlay routing)**), 44/45 files, 23,812 tests; only the `index.ts` barrel file remains
- 🚧 coding-agent: the general utility layer (`src/utils/*`) has landed; sessions / tool system / extensions / modes are advancing through seven sub-phases — see [coding-agent porting status](docs/coding-agent-porting-status.md)
- ⏳ Not started: evals

For per-package progress, key TS → C# design differences, CBOR wire-compatibility notes and the roadmap, see **[docs/porting-status.md](docs/porting-status.md)**.
