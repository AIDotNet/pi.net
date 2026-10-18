namespace Pi.CodingAgent.Cli.Experimental;

// ============================================================================
// 实验性 CLI 顶层 — TS `cli/experimental/cli.ts`（16 行）。
//
// 根命令 `experimental` 自己不解析任何选项，只负责把 `server` / `client` 派发下去；
// 没有子命令时统一报「Expected experimental command: server or client」。
//
// 差异：TS 的 `CliContext = ServerCommandContext & ClientCommandContext` 是交叉类型，
// C# 用接口继承表达（<see cref="IExperimentalCliContext"/> 同时继承两个上下文接口），
// 子命令注册时按各自的接口向下转型。
// ============================================================================

/// <summary>对应 TS <c>ExperimentalCommandGroup</c>。</summary>
public sealed record ExperimentalCommandGroup : ICommandInvocation
{
    public string Command => "experimental";
}

/// <summary>对应 TS <c>CliContext</c>（两个上下文接口的交叉）。</summary>
public interface IExperimentalCliContext : IServerCommandContext, IClientCommandContext;

/// <summary>把两个回调包成交叉上下文的现成实现（便于测试与入口装配）。</summary>
public sealed record ExperimentalCliContext(
    Func<ServerCommand, Task> RunServer,
    Func<ClientCommand, Task> RunClient) : IExperimentalCliContext
{
    public Task RunServerAsync(ServerCommand command) => RunServer(command);

    public Task RunClientAsync(ClientCommand command) => RunClient(command);
}

/// <summary>对应 TS 的 <c>cli</c> 导出。</summary>
public static class ExperimentalCli
{
    /// <summary>没有子命令时的唯一错误文案。</summary>
    public const string MissingSubcommandError = "Expected experimental command: server or client";

    /// <summary>构造根命令（每次调用返回新实例，避免注册表被跨测试复用）。</summary>
    public static Command<ExperimentalCommandGroup, IExperimentalCliContext> Create() =>
        new Command<ExperimentalCommandGroup, IExperimentalCliContext>("experimental")
            .Build(_ => new CommandBuildResult<ExperimentalCommandGroup>.Failed([MissingSubcommandError]))
            .RegisterCommand(ServerCommandDefinition.Create())
            .RegisterCommand(ClientCommandDefinition.Create());
}
