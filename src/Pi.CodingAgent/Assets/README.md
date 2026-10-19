# Pi.NET Coding Agent

This runtime is the .NET 10 port of Pi's coding-agent package. From the repository root, build the
solution and run the native CLI:

```bash
dotnet build Pi.slnx
dotnet run --project src/Pi.Cli/Pi.Cli.csproj -- --help
```

For the current migration inventory, phase evidence, and known platform limitations, see the
[migration ledger](../../../docs/migration-execution-status.md).

## .NET extensions

Pi.NET loads compiled C# assemblies implementing `IExtensionEntry`; it does not load TypeScript
extensions. See the [.NET extension guide](docs/dotnet-extensions.md) for the assembly contract,
tool registration, and security considerations.

## Upstream reference material

Most other pages under `docs/` and files under `examples/` are retained from the upstream
TypeScript project as behavioral and API references. Their Node.js, Bun, TypeScript SDK, npm, and
source-extension instructions apply to that runtime, not to Pi.NET. Consult the .NET-specific guide
when implementing C# extensions.
