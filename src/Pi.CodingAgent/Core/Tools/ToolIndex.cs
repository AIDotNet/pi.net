using Pi.Agent.Types;
using ToolDefinition = Pi.CodingAgent.Core.Extensions.ToolDefinition;

namespace Pi.CodingAgent.Core.Tools;

/// <summary>
/// The built-in tool aggregator. Port of <c>core/tools/index.ts</c>.
/// </summary>
/// <remarks>
/// <para>
/// <b>差异 C144</b>：TS 导出 <c>type ToolName = "read" | "bash" | ... | "ls"</c>（编译期字符串联合）。
/// C# 没有等价物，端口用 <see cref="ToolIndex"/> 上的 8 个 <c>const string</c> 表达该联合，
/// 并把「非法工具名」的检查留在运行时——这正是 TS 自己
/// <c>createToolDefinition</c>/<c>createTool</c> 的 <c>default: throw</c> 分支所做的事。
/// 端口其余部分本就把工具名当 <see cref="string"/> 传递（<c>InitialActiveToolNames</c>、
/// <c>ToolFilter</c> 等），因此不引入 enum，避免在每个边界反复转换。
/// </para>
/// <para>
/// <b>差异 C145</b>：TS <c>Record&lt;ToolName, ToolDef&gt;</c> 是「8 个键必然齐全」的固定形状对象；
/// C# 端口用按插入顺序的 <see cref="Dictionary{TKey, TValue}"/> 表示。这与
/// <c>AgentSession._baseToolDefinitions</c>（TS <c>Map&lt;string, ToolDefinition&gt;</c>）以及
/// <c>AgentSessionExtension.BuildDefinitionRegistry</c> 的入参形状直接对齐。
/// 代价是丢掉了「键齐全」的编译期保证，改由测试钉住（见 <c>ToolIndexTests</c>）。
/// </para>
/// <para>
/// <b>差异 C146</b>：TS <c>allToolNames</c> 是 <c>Set&lt;ToolName&gt;</c>（有序 + O(1) 查询）。
/// BCL 没有「有序集合」，端口用有序的 <see cref="IReadOnlyList{T}"/> 保留迭代顺序，
/// 成员判断走 <c>Contains</c>（8 项，代价可忽略）。TS 全仓未使用该集合，
/// 保序只是为了让导出面在语义上等价。
/// </para>
/// </remarks>
public static class ToolIndex
{
    // -------------------------------------------------------------------------
    // TS `ToolName` 联合的 8 个成员
    // -------------------------------------------------------------------------

    public const string Read = "read";

    public const string Bash = "bash";

    public const string PowerShell = "powershell";

    public const string Edit = "edit";

    public const string Write = "write";

    public const string Grep = "grep";

    public const string Find = "find";

    public const string Ls = "ls";

    /// <summary>The TS <c>allToolNames</c>, in the same insertion order.</summary>
    public static readonly IReadOnlyList<string> AllToolNames =
        [Read, Bash, PowerShell, Edit, Write, Grep, Find, Ls];

    // -------------------------------------------------------------------------
    // 单工具工厂
    // -------------------------------------------------------------------------

    /// <summary>The TS <c>createToolDefinition(toolName, cwd, options)</c>.</summary>
    public static ToolDefinition CreateToolDefinition(string toolName, string cwd, ToolsOptions? options = null) =>
        toolName switch
        {
            Read => ReadTool.CreateReadToolDefinition(cwd, options?.Read),
            Bash => BashTool.CreateBashToolDefinition(cwd, options?.Bash),
            PowerShell => PowerShellTool.CreatePowerShellToolDefinition(cwd, options?.PowerShell),
            Edit => EditTool.CreateEditToolDefinition(cwd, options?.Edit),
            Write => WriteTool.CreateWriteToolDefinition(cwd, options?.Write),
            Grep => GrepTool.CreateGrepToolDefinition(cwd, options?.Grep),
            Find => FindTool.CreateFindToolDefinition(cwd, options?.Find),
            Ls => LsTool.CreateLsToolDefinition(cwd, options?.Ls),
            _ => throw new InvalidOperationException($"Unknown tool name: {toolName}"),
        };

    /// <summary>The TS <c>createTool(toolName, cwd, options)</c>.</summary>
    public static AgentTool CreateTool(string toolName, string cwd, ToolsOptions? options = null) =>
        toolName switch
        {
            Read => ReadTool.CreateReadTool(cwd, options?.Read),
            Bash => BashTool.CreateBashTool(cwd, options?.Bash),
            PowerShell => PowerShellTool.CreatePowerShellTool(cwd, options?.PowerShell),
            Edit => EditTool.CreateEditTool(cwd, options?.Edit),
            Write => WriteTool.CreateWriteTool(cwd, options?.Write),
            Grep => GrepTool.CreateGrepTool(cwd, options?.Grep),
            Find => FindTool.CreateFindTool(cwd, options?.Find),
            Ls => LsTool.CreateLsTool(cwd, options?.Ls),
            _ => throw new InvalidOperationException($"Unknown tool name: {toolName}"),
        };

    // -------------------------------------------------------------------------
    // 组合工厂（定义）
    // -------------------------------------------------------------------------

    /// <summary>The TS <c>createCodingToolDefinitions(cwd, options)</c>: read / bash / edit / write.</summary>
    public static List<ToolDefinition> CreateCodingToolDefinitions(string cwd, ToolsOptions? options = null) =>
    [
        ReadTool.CreateReadToolDefinition(cwd, options?.Read),
        BashTool.CreateBashToolDefinition(cwd, options?.Bash),
        EditTool.CreateEditToolDefinition(cwd, options?.Edit),
        WriteTool.CreateWriteToolDefinition(cwd, options?.Write),
    ];

    /// <summary>The TS <c>createReadOnlyToolDefinitions(cwd, options)</c>: read / grep / find / ls.</summary>
    public static List<ToolDefinition> CreateReadOnlyToolDefinitions(string cwd, ToolsOptions? options = null) =>
    [
        ReadTool.CreateReadToolDefinition(cwd, options?.Read),
        GrepTool.CreateGrepToolDefinition(cwd, options?.Grep),
        FindTool.CreateFindToolDefinition(cwd, options?.Find),
        LsTool.CreateLsToolDefinition(cwd, options?.Ls),
    ];

    /// <summary>The TS <c>createAllToolDefinitions(cwd, options)</c>: all 8 built-ins, keyed by name.</summary>
    public static Dictionary<string, ToolDefinition> CreateAllToolDefinitions(
        string cwd,
        ToolsOptions? options = null) =>
        new(StringComparer.Ordinal)
        {
            [Read] = ReadTool.CreateReadToolDefinition(cwd, options?.Read),
            [Bash] = BashTool.CreateBashToolDefinition(cwd, options?.Bash),
            [PowerShell] = PowerShellTool.CreatePowerShellToolDefinition(cwd, options?.PowerShell),
            [Edit] = EditTool.CreateEditToolDefinition(cwd, options?.Edit),
            [Write] = WriteTool.CreateWriteToolDefinition(cwd, options?.Write),
            [Grep] = GrepTool.CreateGrepToolDefinition(cwd, options?.Grep),
            [Find] = FindTool.CreateFindToolDefinition(cwd, options?.Find),
            [Ls] = LsTool.CreateLsToolDefinition(cwd, options?.Ls),
        };

    // -------------------------------------------------------------------------
    // 组合工厂（可执行工具）
    // -------------------------------------------------------------------------

    /// <summary>The TS <c>createCodingTools(cwd, options)</c>: read / bash / edit / write.</summary>
    public static List<AgentTool> CreateCodingTools(string cwd, ToolsOptions? options = null) =>
    [
        ReadTool.CreateReadTool(cwd, options?.Read),
        BashTool.CreateBashTool(cwd, options?.Bash),
        EditTool.CreateEditTool(cwd, options?.Edit),
        WriteTool.CreateWriteTool(cwd, options?.Write),
    ];

    /// <summary>The TS <c>createReadOnlyTools(cwd, options)</c>: read / grep / find / ls.</summary>
    public static List<AgentTool> CreateReadOnlyTools(string cwd, ToolsOptions? options = null) =>
    [
        ReadTool.CreateReadTool(cwd, options?.Read),
        GrepTool.CreateGrepTool(cwd, options?.Grep),
        FindTool.CreateFindTool(cwd, options?.Find),
        LsTool.CreateLsTool(cwd, options?.Ls),
    ];

    /// <summary>The TS <c>createAllTools(cwd, options)</c>: all 8 built-ins, keyed by name.</summary>
    public static Dictionary<string, AgentTool> CreateAllTools(string cwd, ToolsOptions? options = null) =>
        new(StringComparer.Ordinal)
        {
            [Read] = ReadTool.CreateReadTool(cwd, options?.Read),
            [Bash] = BashTool.CreateBashTool(cwd, options?.Bash),
            [PowerShell] = PowerShellTool.CreatePowerShellTool(cwd, options?.PowerShell),
            [Edit] = EditTool.CreateEditTool(cwd, options?.Edit),
            [Write] = WriteTool.CreateWriteTool(cwd, options?.Write),
            [Grep] = GrepTool.CreateGrepTool(cwd, options?.Grep),
            [Find] = FindTool.CreateFindTool(cwd, options?.Find),
            [Ls] = LsTool.CreateLsTool(cwd, options?.Ls),
        };
}

/// <summary>
/// Port of the TS <c>ToolsOptions</c>. The property order mirrors the TS declaration for
/// traceability; option objects are unordered, so nothing depends on it.
/// </summary>
public sealed record ToolsOptions
{
    public ReadToolOptions? Read { get; init; }

    public BashToolOptions? Bash { get; init; }

    /// <remarks>
    /// The TS slot is <c>PowerShellToolOptions</c> (a <c>Pick</c> of <see cref="BashToolOptions"/>),
    /// not the full bash record — see <see cref="PowerShellToolOptions"/>.
    /// </remarks>
    public PowerShellToolOptions? PowerShell { get; init; }

    public WriteToolOptions? Write { get; init; }

    public EditToolOptions? Edit { get; init; }

    public GrepToolOptions? Grep { get; init; }

    public FindToolOptions? Find { get; init; }

    public LsToolOptions? Ls { get; init; }
}
