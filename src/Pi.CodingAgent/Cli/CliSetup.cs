namespace Pi.CodingAgent.Cli;

// ============================================================================
// CLI 进程级初始化 — TS `cli/setup.ts`（13 行）。
//
// TS 的四件事里两件有 .NET 对应物、两件没有，逐条说明：
//   1. `process.env.PI_CODING_AGENT = "true"` / `process.env.AI_AGENT = "pi"`
//      → `Environment.SetEnvironmentVariable(..., Process)`，同为进程内修改，
//      子进程继承（.NET 默认把 Process 级变量传给子进程，与 Node 一致）。
//   2. `configureHttpDispatcher()` → **无对应调用**：C# 侧的
//      `Core/HttpDispatcher.cs` 端口已把 undici dispatcher 的安装整体作为
//      平台差异裁掉（docs 差异 C32），连接池/代理由 HttpClient 自行管理。
//   3. `process.title = APP_NAME` → **无对应物**：.NET 不提供设置进程标题的
//      托管 API（Linux 需改 argv 内存，Windows 无此概念）。
//   4. `process.emitWarning = () => {}` → **无对应物**：.NET 没有等价的
//      全局 warning 发射器；相关告警在源头各自处理。
// ============================================================================

/// <summary>对应 TS <c>setupCli</c>。</summary>
public static class CliSetup
{
    /// <summary>
    /// 进程启动时调用一次（TS 的 main.ts 入口第一件事）。
    /// 注意字面量与 TS 一致：setup.ts 直接写死 <c>PI_CODING_AGENT</c> /
    /// <c>AI_AGENT</c>，不走 <see cref="Config.AppName"/> 派生。
    /// </summary>
    public static void SetupCli()
    {
        Environment.SetEnvironmentVariable("PI_CODING_AGENT", "true");
        Environment.SetEnvironmentVariable("AI_AGENT", "pi");
    }
}
