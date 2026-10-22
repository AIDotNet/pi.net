// ---------------------------------------------------------------------------
// 嵌入式 coding agent：用 Pi.CodingAgent 的 SDK 工厂开一个会话并跑一条提示词。
//
//   dotnet run --project samples/Pi.Samples.CodingSession -- [cwd] [prompt]
//
// 与 Pi.Samples.FauxAgent 的区别：这个会话是「真东西」——它会自己装配模型运行时
// （读 ~/.pi/auth.json 与 ~/.pi/models.json 里的凭据 / 自定义模型）、加载项目里的
// 技能与扩展、注册内置工具（read / bash / edit / write …），并把会话存档写进
// ~/.pi/sessions 或项目目录。
//
// 第一次运行前需要有可用凭据：用本仓库的 CLI 登录一次即可
//   dotnet run --project src/Pi.Cli -- login
// 或者直接设 ANTHROPIC_API_KEY / OPENAI_API_KEY 等环境变量（Pi.Ai 会做环境发现）。
// ---------------------------------------------------------------------------

using Pi.Agent.Types;
using Pi.Ai.Types;
using Pi.CodingAgent.Core;

var cwd = args.Length > 0 ? Path.GetFullPath(args[0]) : Directory.GetCurrentDirectory();
var prompt = args.Length > 1
    ? string.Join(' ', args[1..])
    : "用一句话说明这个项目是做什么的，然后列出 src 下的顶层目录。";

if (!Directory.Exists(cwd))
{
    Console.Error.WriteLine($"目录不存在: {cwd}");
    return 2;
}

// CreateAgentSessionAsync 的求值顺序与 TS 版一致：
// cwd/agentDir 解析 → 模型运行时 → 设置/会话管理器 → 资源加载（技能/扩展/AGENTS.md）
// → 模型恢复或兜底 → 思考档位 → 工具解析 → Agent 装配。
var result = await Sdk.CreateAgentSessionAsync(new CreateAgentSessionOptions
{
    Cwd = cwd,
    // Tools 是 allowlist（支持 * 通配）；ExcludeTools 是 denylist。
    // 不写则用内置默认工具集。
    // Tools = ["read", "bash"],
    // NoTools = NoToolsMode.All,   // 从零工具开始
});

var session = result.Session;

if (result.ModelFallbackMessage is { } warning)
{
    Console.Error.WriteLine($"[warn] {warning}");
}

if (session.Model is null)
{
    Console.Error.WriteLine("# 没有可用模型：先配置凭据（pi login 或 ANTHROPIC_API_KEY / OPENAI_API_KEY）。");
    return 1;
}

Console.Error.WriteLine($"# model = {session.Model.Provider}/{session.Model.Id}");
Console.Error.WriteLine($"# cwd   = {cwd}");
Console.Error.WriteLine($"# tools = {string.Join(", ", session.GetCallableToolNames())}");
Console.Error.WriteLine();

// 会话事件是全量的（消息、工具、压缩、重试、队列……）。
// 这里只挑几类打印，真实宿主按需渲染。
using var subscription = session.Subscribe(sessionEvent =>
{
    switch (sessionEvent)
    {
        case AgentSessionEvent.ToolExecutionStartEvent start:
            Console.Error.WriteLine($"[tool] -> {start.ToolName}");
            break;

        case AgentSessionEvent.ToolExecutionEndEvent end:
            Console.Error.WriteLine($"[tool] <- {(end.IsError ? "ERROR" : "ok")}");
            break;

        case AgentSessionEvent.MessageUpdateEvent { AssistantMessageEvent: AssistantMessageEvent.TextDelta delta }:
            Console.Write(delta.Delta);
            break;

        case AgentSessionEvent.AutoRetryStartEvent retry:
            Console.Error.WriteLine($"[retry] {retry.Attempt}/{retry.MaxAttempts} in {retry.DelayMs} ms: {retry.ErrorMessage}");
            break;

        case AgentSessionEvent.CompactionStartEvent compaction:
            Console.Error.WriteLine($"[compaction] {compaction.Reason}");
            break;
    }
});

await session.PromptAsync(prompt);
Console.WriteLine();

// 会话统计：消息数、工具调用数、token 与成本。
var stats = session.GetSessionStats();
Console.Error.WriteLine();
Console.Error.WriteLine($"# session {stats.SessionId}");
Console.Error.WriteLine($"# messages={stats.TotalMessages} tools={stats.ToolCalls} " +
                        $"tokens={stats.Tokens.Total} cost=${stats.Cost:0.0000}");
if (stats.SessionFile is { } file)
{
    Console.Error.WriteLine($"# archive {file}");
}

return 0;
