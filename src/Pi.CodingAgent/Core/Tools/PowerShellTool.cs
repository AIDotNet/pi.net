using Pi.Agent.Types;
using Pi.CodingAgent.Core.Extensions;
using Pi.CodingAgent.Utils;
using ToolDefinition = Pi.CodingAgent.Core.Extensions.ToolDefinition;

namespace Pi.CodingAgent.Core.Tools;

/// <summary>
/// The PowerShell tool. Port of <c>core/tools/powershell.ts</c>: the same shell execution path as
/// bash, with a PowerShell-specific tool description and prompt.
/// </summary>
public static class PowerShellTool
{
    /// <summary>The TS <c>UTF8_OUTPUT_PREFIX</c>.</summary>
    /// <remarks>
    /// Prepended to every command so a Windows PowerShell host emits UTF-8 instead of its OEM code
    /// page. The <c>try</c>/<c>catch</c> keeps the prefix harmless on hosts where the assignment is
    /// rejected.
    /// </remarks>
    private const string Utf8OutputPrefix = "try { [Console]::OutputEncoding=[System.Text.Encoding]::UTF8 } catch {}\n";

    /// <summary>The TS <c>powershellToolSystemPromptContribution.snippet</c>.</summary>
    public const string Snippet = "Execute PowerShell commands";

    /// <summary>The TS <c>powershellToolSystemPromptContribution.guidelines</c>.</summary>
    public static readonly IReadOnlyList<string> Guidelines =
        ["You can inspect PI_* environment variables for current model and session details."];

    private static readonly ShellToolConfig PowerShellToolConfig = new()
    {
        Name = "powershell",
        Label = "powershell",
        ShellName = "PowerShell",
        Prompt = "PS>",
        PromptSnippet = Snippet,
        PromptGuidelines = Guidelines,
        TempFilePrefix = "pi-powershell",
    };

    /// <summary>
    /// The TS <c>createLocalPowerShellOperations</c>: pi's local shell backend plus the UTF-8 output
    /// prefix.
    /// </summary>
    /// <remarks>
    /// TS exports this from both <c>powershell.ts</c> and the <c>tools/index.ts</c> barrel; C# has no
    /// re-export, so callers reach it as <c>PowerShellTool.CreateLocalPowerShellOperations</c>.
    /// </remarks>
    public static IBashToolOperations CreateLocalPowerShellOperations()
    {
        var operations = BashTool.CreateLocalShellOperations("PowerShell", Shell.GetPowerShellConfig);
        return new Utf8PrefixedOperations(operations);
    }

    /// <summary>The TS <c>createPowerShellToolDefinition(cwd, options)</c>.</summary>
    /// <remarks>
    /// The TS default is <c>operations: options?.operations ?? createLocalPowerShellOperations()</c>,
    /// so the UTF-8 prefix applies unless a caller supplies its own operations. The other two fields
    /// are copied across so <see cref="BashTool.CreateShellToolDefinition"/>'s own defaults
    /// (<c>exposeSessionEnvironment ?? true</c>) still decide the outcome.
    /// </remarks>
    public static ToolDefinition CreatePowerShellToolDefinition(string cwd, PowerShellToolOptions? options = null) =>
        BashTool.CreateShellToolDefinition(cwd, PowerShellToolConfig, new BashToolOptions
        {
            Operations = options?.Operations ?? CreateLocalPowerShellOperations(),
            ExposeSessionEnvironment = options?.ExposeSessionEnvironment,
            SpawnHook = options?.SpawnHook,
        });

    /// <summary>The TS <c>createPowerShellTool(cwd, options)</c>.</summary>
    public static AgentTool CreatePowerShellTool(string cwd, PowerShellToolOptions? options = null)
    {
        var definition = CreatePowerShellToolDefinition(cwd, options);
        return ToolDefinitionWrapper.WrapToolDefinition(definition) with
        {
            PromptSnippet = definition.PromptSnippet,
            PromptGuidelines = definition.PromptGuidelines,
        };
    }

    /// <summary>
    /// Prepends <see cref="Utf8OutputPrefix"/> to each command before delegating. The TS
    /// <c>exec</c> arrow drops the signal argument, which the port's
    /// <see cref="IBashToolOperations.ExecAsync"/> carries as a token.
    /// </summary>
    private sealed class Utf8PrefixedOperations(IBashToolOperations inner) : IBashToolOperations
    {
        public Task<ShellExecResult> ExecAsync(
            string command,
            string cwd,
            ShellExecOptions options,
            CancellationToken cancellationToken) =>
            inner.ExecAsync(Utf8OutputPrefix + command, cwd, options, cancellationToken);
    }
}

/// <summary>
/// Port of the TS <c>PowerShellToolOptions</c>, which is
/// <c>Pick&lt;BashToolOptions, "operations" | "exposeSessionEnvironment" | "spawnHook"&gt;</c>.
/// </summary>
/// <remarks>
/// Deliberately narrower than <see cref="BashToolOptions"/>: TS does not let a PowerShell caller set
/// <c>commandPrefix</c> or <c>shellPath</c>, and the port keeps that restriction instead of widening
/// the parameter to the full bash option record.
/// </remarks>
public sealed record PowerShellToolOptions
{
    /// <summary>Custom operations for command execution. Default: local PowerShell shell.</summary>
    public IBashToolOperations? Operations { get; init; }

    /// <summary>Expose current Pi session metadata as PI_* environment variables. Default: true.</summary>
    public bool? ExposeSessionEnvironment { get; init; }

    /// <summary>Hook to adjust command, cwd, or env before execution.</summary>
    public BashSpawnHook? SpawnHook { get; init; }
}
