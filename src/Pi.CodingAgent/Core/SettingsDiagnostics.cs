namespace Pi.CodingAgent.Core;

// `AgentSessionRuntimeDiagnostic` / `AgentSessionDiagnosticType` 已迁回 TS 的定义文件
// （AgentSessionServices.cs，对应 agent-session-services.ts），Type 由裸字符串收紧为枚举。

/// <summary>设置加载错误转会话诊断。对应 TS <c>core/settings-diagnostics.ts</c>。</summary>
public static class SettingsDiagnostics
{
    /// <summary>把 <see cref="SettingsManager.DrainErrors"/> 转成运行时诊断。对应 TS <c>collectSettingsDiagnostics</c>。</summary>
    public static IReadOnlyList<AgentSessionRuntimeDiagnostic> CollectSettingsDiagnostics(SettingsManager settingsManager)
        => [.. settingsManager.DrainErrors().Select(error => new AgentSessionRuntimeDiagnostic
        {
            Type = AgentSessionDiagnosticType.Warning,
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
