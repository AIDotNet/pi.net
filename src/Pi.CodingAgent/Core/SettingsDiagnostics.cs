namespace Pi.CodingAgent.Core;

/// <summary>
/// 会话运行时诊断条目。TS 定义在 <c>agent-session-services.ts</c> 的
/// <c>AgentSessionRuntimeDiagnostic</c>；4e-3a 先落在这里供 settings-diagnostics 使用，
/// 4e-3e 移植 agent-session 三件套时继续复用。
/// </summary>
public sealed record AgentSessionRuntimeDiagnostic
{
    /// <summary><c>warning</c> 或 <c>error</c>。</summary>
    public required string Type { get; init; }

    public required string Message { get; init; }
}

/// <summary>设置加载错误转会话诊断。对应 TS <c>core/settings-diagnostics.ts</c>。</summary>
public static class SettingsDiagnostics
{
    /// <summary>把 <see cref="SettingsManager.DrainErrors"/> 转成运行时诊断。对应 TS <c>collectSettingsDiagnostics</c>。</summary>
    public static IReadOnlyList<AgentSessionRuntimeDiagnostic> CollectSettingsDiagnostics(SettingsManager settingsManager)
        => [.. settingsManager.DrainErrors().Select(error => new AgentSessionRuntimeDiagnostic
        {
            Type = "warning",
            Message = error.Path is { Length: > 0 } path
                ? $"Invalid settings file {path}: {error.Error.Message}"
                : $"Invalid {ScopeName(error.Scope)} settings: {error.Error.Message}",
        })];

    /// <summary>
    /// 按 type/message 去重并保留首次出现。启动期与运行期 settings manager 可能报告同一文件错误。
    /// 对应 TS <c>deduplicateDiagnostics</c>。
    /// </summary>
    public static IReadOnlyList<AgentSessionRuntimeDiagnostic> DeduplicateDiagnostics(
        IReadOnlyList<AgentSessionRuntimeDiagnostic> diagnostics)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        return [.. diagnostics.Where(diagnostic => seen.Add($"{diagnostic.Type}\0{diagnostic.Message}"))];
    }

    private static string ScopeName(SettingsScope scope) => scope switch
    {
        SettingsScope.Global => "global",
        SettingsScope.Project => "project",
        _ => scope.ToString().ToLowerInvariant(),
    };
}
