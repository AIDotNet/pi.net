using System.Text.Json.Serialization;
using Pi.Ai.Models;
using Pi.Ai.Types;
using Pi.CodingAgent.Core.Extensions;
using Pi.CodingAgent.Utils;

namespace Pi.CodingAgent.Core;

/// <summary>
/// 诊断级别。对应 TS <c>AgentSessionRuntimeDiagnostic["type"]</c> 的
/// <c>"info" | "warning" | "error"</c> 字符串联合。
/// </summary>
/// <remarks>
/// 4e-3a 时该类型先落在 <c>SettingsDiagnostics.cs</c> 且 <c>Type</c> 是裸字符串；
/// 4e-3e-9f 把它迁回 TS 的定义文件并收紧为枚举（见 <see cref="AgentSessionRuntimeDiagnostic"/>）。
/// </remarks>
[JsonConverter(typeof(JsonStringEnumConverter<AgentSessionDiagnosticType>))]
public enum AgentSessionDiagnosticType
{
    [JsonStringEnumMemberName("info")]
    Info,

    [JsonStringEnumMemberName("warning")]
    Warning,

    [JsonStringEnumMemberName("error")]
    Error,
}

/// <summary>
/// 创建服务 / 会话期间收集的非致命问题。对应 TS <c>AgentSessionRuntimeDiagnostic</c>。
/// </summary>
/// <remarks>
/// TS 的注释说明：运行时不打印也不退出，只把诊断交回调用方；由 app 层决定是否展示警告、
/// 是否因错误中止启动。端口保持同样的「返回而不抛」契约。
/// </remarks>
public sealed record AgentSessionRuntimeDiagnostic
{
    public required AgentSessionDiagnosticType Type { get; init; }

    public required string Message { get; init; }
}

/// <summary>
/// 一个生效 cwd 对应的成套运行时服务。对应 TS <c>AgentSessionServices</c>。
/// </summary>
/// <remarks>
/// 这是纯基础设施：<c>AgentSession</c> 本身单独创建，好让会话选项先对着这些服务解析。
/// </remarks>
public sealed record AgentSessionServices
{
    public required string Cwd { get; init; }

    public required string AgentDir { get; init; }

    public required ModelRuntime ModelRuntime { get; init; }

    public required SettingsManager SettingsManager { get; init; }

    public required DefaultResourceLoader ResourceLoader { get; init; }

    /// <summary>创建服务期间收集的诊断。对应 TS <c>diagnostics</c>。</summary>
    public IReadOnlyList<AgentSessionRuntimeDiagnostic> Diagnostics { get; init; } = [];
}

/// <summary>服务路径解析结果。对应 TS <c>createAgentSessionServices</c> 的前两行。</summary>
public sealed record ResolvedServicePaths(string Cwd, string AgentDir);

/// <summary>
/// <c>auth.json</c> / <c>models.json</c> 的路径。对应 TS <c>createAgentSession</c> 里的
/// <c>options.agentDir ? join(...) : undefined</c>。
/// </summary>
/// <remarks>
/// 与 <c>createAgentSessionServices</c> 不同：后者总是把两个路径拼到生效 agentDir 下，
/// 而 <c>createAgentSession</c> 只在调用方**显式**给了 <c>agentDir</c> 时才拼——
/// 否则交给 <c>ModelRuntime.create</c> 用它自己的默认值。
/// </remarks>
public sealed record ModelRuntimeFilePaths(string? AuthPath, string? ModelsPath);

/// <summary>
/// 扩展 flag 的输入值。对应 TS <c>Map&lt;string, boolean | string&gt;</c> 的值域。
/// </summary>
/// <remarks>
/// TS 里布尔分支只表示「这个 flag 被提到了」，取值恒被写成 <c>true</c>
/// （<c>extensionsResult.runtime.flagValues.set(name, true)</c>），所以端口只保留
/// 「是不是字符串」这一位信息，不保留布尔字面量。
/// </remarks>
public readonly record struct ExtensionFlagInput
{
    private ExtensionFlagInput(string? textValue) => TextValue = textValue;

    /// <summary>字符串分支的取值；布尔分支为 <c>null</c>。</summary>
    public string? TextValue { get; }

    /// <summary>是否为字符串分支（TS 的 <c>typeof value === "string"</c>）。</summary>
    public bool IsText => TextValue is not null;

    /// <summary>布尔分支（CLI 上只写了 <c>--flag</c>）。</summary>
    public static ExtensionFlagInput Boolean { get; } = new(null);

    /// <summary>字符串分支（CLI 上写了 <c>--flag=value</c>）。</summary>
    public static ExtensionFlagInput Text(string value) => new(value);
}

/// <summary>
/// <c>applyExtensionFlagValues</c> 的结果：诊断 + 要写回运行时的 flag 值。
/// </summary>
/// <param name="Diagnostics">按 TS 顺序排列的诊断（先是「缺值」错误，最后是「未知选项」错误）。</param>
/// <param name="FlagValues">要写入 <c>runtime.flagValues</c> 的键值；布尔 flag 恒为 <c>true</c>。</param>
public sealed record ExtensionFlagResolution(
    IReadOnlyList<AgentSessionRuntimeDiagnostic> Diagnostics,
    IReadOnlyDictionary<string, object> FlagValues);

/// <summary>
/// <c>/import</c> 的目标路径解析。对应 TS <c>importFromJsonl</c> 的目标文件段。
/// </summary>
/// <param name="DestinationPath">去重后的落盘路径。</param>
/// <param name="SourceAlreadyStored">源文件已经在会话目录里（此时不复制，只打开）。</param>
public sealed record ImportDestination(string DestinationPath, bool SourceAlreadyStored);

/// <summary>
/// 服务装配的纯逻辑。对应 TS <c>core/agent-session-services.ts</c>（233 行）。
/// </summary>
/// <remarks>
/// <para>
/// 本批（4e-3e-9f）沿用「叶子先行」惯例（同 4e-3e-9a … 9e）：把实例/IO 无关的决策与文案抽成
/// 显式入参的静态函数。<c>createAgentSessionServices</c> 与 <c>createAgentSessionFromServices</c>
/// 的编排本体需要 <c>ModelRuntime.create</c> 的异步装配、<c>DefaultResourceLoader</c> 的
/// 扩展加载（4e-3e-9g），顺延 4e-3e-9h。
/// </para>
/// <para>
/// 差异 C109：TS 的 <c>extensionFlagValues</c> 是 <c>Map</c>（保留插入顺序）；
/// 端口用 <see cref="KeyValuePair{TKey,TValue}"/> 序列承载，因为「缺值」错误的消息顺序
/// 与输入顺序绑定，不能用无序字典代替。
/// </para>
/// </remarks>
public static class AgentSessionServicesBuilder
{
    /// <summary>对应 TS <c>createAgentSessionServices</c> 的 cwd / agentDir 解析。</summary>
    public static ResolvedServicePaths ResolveServicePaths(
        string cwd,
        string? agentDir,
        string defaultAgentDir)
        => new(
            Paths.ResolvePath(cwd),
            agentDir is null ? defaultAgentDir : Paths.ResolvePath(agentDir));

    /// <summary>对应 TS <c>createAgentSession</c> 的 <c>authPath</c> / <c>modelsPath</c>。</summary>
    public static ModelRuntimeFilePaths ResolveModelRuntimeFilePaths(bool agentDirProvided, string agentDir)
        => agentDirProvided
            ? new ModelRuntimeFilePaths(
                Path.Combine(agentDir, "auth.json"),
                Path.Combine(agentDir, "models.json"))
            : new ModelRuntimeFilePaths(null, null);

    /// <summary>
    /// 汇总扩展注册的 flag 类型表。对应 TS <c>applyExtensionFlagValues</c> 的
    /// <c>registeredFlags</c> 构造段。
    /// </summary>
    /// <remarks>后加载的扩展覆盖先加载的（TS 的 <c>Map.set</c> 语义）。</remarks>
    public static Dictionary<string, string> BuildRegisteredFlagTypes(IEnumerable<Extension> extensions)
    {
        var registered = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var extension in extensions)
        {
            foreach (var (name, flag) in extension.Flags)
            {
                registered[name] = flag.Type;
            }
        }

        return registered;
    }

    /// <summary>
    /// 把 CLI 传入的扩展 flag 值应用到运行时。对应 TS <c>applyExtensionFlagValues</c>。
    /// </summary>
    /// <remarks>
    /// TS 的三分支逐条对应：布尔 flag 恒写 <c>true</c>；字符串 flag 写原值；
    /// 字符串 flag 收到布尔（即 CLI 只写了 <c>--flag</c>）报「需要取值」。
    /// 未注册的 flag 收集后**最后**统一报「未知选项」。
    /// </remarks>
    public static ExtensionFlagResolution ApplyExtensionFlagValues(
        IReadOnlyDictionary<string, string> registeredFlagTypes,
        IEnumerable<KeyValuePair<string, ExtensionFlagInput>> extensionFlagValues)
    {
        var diagnostics = new List<AgentSessionRuntimeDiagnostic>();
        var values = new Dictionary<string, object>(StringComparer.Ordinal);
        var unknownFlags = new List<string>();

        foreach (var (name, input) in extensionFlagValues)
        {
            if (!registeredFlagTypes.TryGetValue(name, out var flagType))
            {
                unknownFlags.Add(name);
                continue;
            }

            if (flagType == "boolean")
            {
                values[name] = true;
                continue;
            }

            if (input.IsText)
            {
                values[name] = input.TextValue!;
                continue;
            }

            diagnostics.Add(new AgentSessionRuntimeDiagnostic
            {
                Type = AgentSessionDiagnosticType.Error,
                Message = $"Extension flag \"--{name}\" requires a value",
            });
        }

        if (unknownFlags.Count > 0)
        {
            diagnostics.Add(new AgentSessionRuntimeDiagnostic
            {
                Type = AgentSessionDiagnosticType.Error,
                Message = $"Unknown option{(unknownFlags.Count == 1 ? string.Empty : "s")}: "
                    + string.Join(", ", unknownFlags.Select(name => $"--{name}")),
            });
        }

        return new ExtensionFlagResolution(diagnostics, values);
    }

    /// <summary>
    /// 扩展注册动作的诊断文案。对应 TS 三处
    /// <c>Extension "${extensionPath}" error: ${message}</c>。
    /// </summary>
    public static AgentSessionRuntimeDiagnostic RegistrationFailureDiagnostic(string extensionPath, string message)
        => new()
        {
            Type = AgentSessionDiagnosticType.Error,
            Message = $"Extension \"{extensionPath}\" error: {message}",
        };

    // =========================================================================
    // 编排本体（TS 135-233）
    // =========================================================================

    /// <summary>
    /// 创建绑定 cwd 的运行时服务。对应 TS <c>createAgentSessionServices</c>。
    /// </summary>
    /// <returns>服务与诊断。本函数**不**创建 <see cref="AgentSession"/>。</returns>
    /// <remarks>
    /// 三类待处理注册（provider / native provider / virtual model）按 TS 顺序逐个尝试，
    /// 失败只记诊断不中断；随后 <c>refresh({ allowNetwork: false })</c>，最后应用 CLI 扩展 flag。
    /// </remarks>
    public static async Task<AgentSessionServices> CreateAgentSessionServicesAsync(
        CreateAgentSessionServicesOptions options,
        CancellationToken cancellationToken = default)
    {
        var paths = ResolveServicePaths(options.Cwd, options.AgentDir, Config.GetAgentDir());
        var modelRuntime = options.ModelRuntime ?? await ModelRuntime.CreateAsync(new CreateModelRuntimeOptions
        {
            // 与 createAgentSession 不同：这里总是把两个路径拼到生效 agentDir 下（TS 139-146）。
            AuthPath = Path.Combine(paths.AgentDir, "auth.json"),
            ModelsPath = Path.Combine(paths.AgentDir, "models.json"),
            Signal = options.ModelRuntimeSignal,
        }).ConfigureAwait(false);

        var settingsManager = options.SettingsManager ?? SettingsManager.Create(paths.Cwd, paths.AgentDir);
        var extra = options.ResourceLoaderOptions ?? new ResourceLoaderExtraOptions();
        var resourceLoader = new DefaultResourceLoader(
            paths.Cwd,
            paths.AgentDir,
            settingsManager,
            noSkills: extra.NoSkills,
            noPromptTemplates: extra.NoPromptTemplates,
            noThemes: extra.NoThemes,
            noContextFiles: extra.NoContextFiles,
            additionalExtensionPaths: extra.AdditionalExtensionPaths,
            additionalSkillPaths: extra.AdditionalSkillPaths,
            additionalPromptTemplatePaths: extra.AdditionalPromptTemplatePaths,
            additionalThemePaths: extra.AdditionalThemePaths,
            systemPrompt: extra.SystemPrompt,
            appendSystemPrompt: extra.AppendSystemPrompt,
            eventBus: extra.EventBus,
            extensionFactories: extra.ExtensionFactories,
            noExtensions: extra.NoExtensions,
            disabledBuiltinExtensions: extra.DisabledBuiltinExtensions,
            extensionsOverride: extra.ExtensionsOverride,
            skillsOverride: extra.SkillsOverride,
            promptsOverride: extra.PromptsOverride,
            themesOverride: extra.ThemesOverride);
        await resourceLoader.ReloadAsync(options.ResourceLoaderReloadOptions, cancellationToken).ConfigureAwait(false);

        var diagnostics = new List<AgentSessionRuntimeDiagnostic>();
        var extensionsResult = resourceLoader.GetExtensions();
        var runtime = extensionsResult.Runtime;

        foreach (var registration in runtime.PendingProviderRegistrations.ToList())
        {
            try
            {
                modelRuntime.RegisterProvider(registration.Name, registration.Config);
            }
            catch (Exception error)
            {
                diagnostics.Add(RegistrationFailureDiagnostic(registration.ExtensionPath, error.Message));
            }
        }

        runtime.PendingProviderRegistrations.Clear();

        foreach (var registration in runtime.PendingNativeProviderRegistrations.ToList())
        {
            try
            {
                modelRuntime.RegisterNativeProvider(registration.Provider);
            }
            catch (Exception error)
            {
                diagnostics.Add(RegistrationFailureDiagnostic(registration.ExtensionPath, error.Message));
            }
        }

        runtime.PendingNativeProviderRegistrations.Clear();

        foreach (var registration in runtime.PendingVirtualModelRegistrations.ToList())
        {
            try
            {
                modelRuntime.RegisterVirtualModel(registration.Definition);
            }
            catch (Exception error)
            {
                diagnostics.Add(RegistrationFailureDiagnostic(registration.ExtensionPath, error.Message));
            }
        }

        runtime.PendingVirtualModelRegistrations.Clear();

        await modelRuntime.RefreshAsync(new ModelsRefreshOptions { AllowNetwork = false }).ConfigureAwait(false);

        var flags = ApplyExtensionFlagValues(
            BuildRegisteredFlagTypes(extensionsResult.Extensions),
            options.ExtensionFlagValues ?? []);
        foreach (var (name, value) in flags.FlagValues)
        {
            runtime.FlagValues[name] = value;
        }

        diagnostics.AddRange(flags.Diagnostics);

        return new AgentSessionServices
        {
            Cwd = paths.Cwd,
            AgentDir = paths.AgentDir,
            ModelRuntime = modelRuntime,
            SettingsManager = settingsManager,
            ResourceLoader = resourceLoader,
            Diagnostics = diagnostics,
        };
    }

    /// <summary>
    /// 从已创建的服务创建 <see cref="AgentSession"/>。对应 TS <c>createAgentSessionFromServices</c>。
    /// </summary>
    /// <remarks>把服务创建与会话创建分开，好让调用方在构造会话前先对着目标 cwd 解析模型 / 档位 / 工具。</remarks>
    public static Task<CreateAgentSessionResult> CreateAgentSessionFromServicesAsync(
        CreateAgentSessionFromServicesOptions options,
        string? processCwd = null,
        CancellationToken cancellationToken = default)
        => Sdk.CreateAgentSessionAsync(
            new CreateAgentSessionOptions
            {
                Cwd = options.Services.Cwd,
                AgentDir = options.Services.AgentDir,
                ModelRuntime = options.Services.ModelRuntime,
                SettingsManager = options.Services.SettingsManager,
                ResourceLoader = options.Services.ResourceLoader,
                SessionManager = options.SessionManager,
                Model = options.Model,
                ThinkingLevel = options.ThinkingLevel,
                ScopedModels = options.ScopedModels,
                Tools = options.Tools,
                ExcludeTools = options.ExcludeTools,
                NoTools = options.NoTools,
                CustomTools = options.CustomTools,
                SessionStartEvent = options.SessionStartEvent,
            },
            processCwd,
            cancellationToken);
}

/// <summary>
/// <see cref="DefaultResourceLoader"/> 除 cwd / agentDir / settingsManager 之外的选项。
/// 对应 TS <c>Omit&lt;DefaultResourceLoaderOptions, "cwd" | "agentDir" | "settingsManager"&gt;</c>。
/// </summary>
public sealed record ResourceLoaderExtraOptions
{
    public bool NoSkills { get; init; }

    public bool NoPromptTemplates { get; init; }

    public bool NoThemes { get; init; }

    public bool NoContextFiles { get; init; }

    public bool NoExtensions { get; init; }

    public IReadOnlyList<string>? AdditionalExtensionPaths { get; init; }

    public IReadOnlyList<string>? AdditionalSkillPaths { get; init; }

    public IReadOnlyList<string>? AdditionalPromptTemplatePaths { get; init; }

    public IReadOnlyList<string>? AdditionalThemePaths { get; init; }

    public EventBus? EventBus { get; init; }

    public IReadOnlyList<InlineExtension>? ExtensionFactories { get; init; }

    public IReadOnlyList<string>? DisabledBuiltinExtensions { get; init; }

    /// <summary>Explicit system-prompt source, skipping SYSTEM.md discovery.</summary>
    public string? SystemPrompt { get; init; }

    /// <summary>Explicit append-system-prompt sources, skipping APPEND_SYSTEM.md discovery.</summary>
    public IReadOnlyList<string>? AppendSystemPrompt { get; init; }

    public Func<LoadExtensionsResult, LoadExtensionsResult>? ExtensionsOverride { get; init; }

    public Func<SkillLoadOutcome, SkillLoadOutcome>? SkillsOverride { get; init; }

    public Func<PromptLoadOutcome, PromptLoadOutcome>? PromptsOverride { get; init; }

    public Func<ThemeLoadOutcome, ThemeLoadOutcome>? ThemesOverride { get; init; }
}

/// <summary>对应 TS <c>CreateAgentSessionServicesOptions</c>。</summary>
/// <remarks>
/// 这些服务在生效会话 cwd 变化时重建。CLI 提供的资源路径应在此之前解析为绝对路径，
/// 好让后续 cwd 切换不会重新解释它们。
/// </remarks>
public sealed record CreateAgentSessionServicesOptions
{
    public required string Cwd { get; init; }

    public string? AgentDir { get; init; }

    public SettingsManager? SettingsManager { get; init; }

    public ModelRuntime? ModelRuntime { get; init; }

    public CancellationToken ModelRuntimeSignal { get; init; }

    /// <summary>CLI 传入的扩展 flag 值；顺序有意义（见差异 C109）。</summary>
    public IReadOnlyList<KeyValuePair<string, ExtensionFlagInput>>? ExtensionFlagValues { get; init; }

    public ResourceLoaderExtraOptions? ResourceLoaderOptions { get; init; }

    public ResourceLoaderReloadOptions? ResourceLoaderReloadOptions { get; init; }
}

/// <summary>对应 TS <c>CreateAgentSessionFromServicesOptions</c>。</summary>
/// <remarks>
/// 服务已存在、且绑定 cwd 的模型 / 工具 / 会话选项已对着这些服务解析完之后使用。
/// </remarks>
public sealed record CreateAgentSessionFromServicesOptions
{
    public required AgentSessionServices Services { get; init; }

    public required SessionManager SessionManager { get; init; }

    public Extensions.ExtensionEvent.SessionStart? SessionStartEvent { get; init; }

    public ModelSpec? Model { get; init; }

    public ThinkingLevel? ThinkingLevel { get; init; }

    public IReadOnlyList<ScopedModel>? ScopedModels { get; init; }

    public IReadOnlyList<string>? Tools { get; init; }

    public IReadOnlyList<string>? ExcludeTools { get; init; }

    public NoToolsMode? NoTools { get; init; }

    public IReadOnlyList<Extensions.ToolDefinition>? CustomTools { get; init; }
}
