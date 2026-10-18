namespace Pi.CodingAgent.Cli.Experimental;

// ============================================================================
// `pi experimental server` — TS `cli/experimental/commands/server.ts`（66 行）。
//
// 选项：`--server-id` / `--session-dir` / `--provider` / `--model` / `-e`（可重复）
//      + 共用的 `--auth-token` / `--auth-token-file`。
//
// 校验顺序（与 TS 一致，决定 errors 的排列顺序）：
//   1. 认证互斥（来自 parseAuth）；
//   2. `--provider` 给了但 `--model` 没给 → "--provider requires --model"；
//   3. remainingArgs 非空 → 「不支持既有 CLI 选项」。
// 三者可以同时出现，错误按这个顺序拼接。
//
// 差异：TS 用 `...(x === undefined ? {} : { x })` 逐键省略；C# 侧可空属性为 null 即
// 「不出现」，序列化时由调用方决定是否写键（既有约定：null 即省略）。
// ============================================================================

/// <summary>对应 TS <c>ServerCommand</c>。</summary>
public sealed record ServerCommand : ICommandInvocation
{
    public string Command => "server";

    public AuthInput? Auth { get; init; }

    public string? Provider { get; init; }

    public string? Model { get; init; }

    public IReadOnlyList<string>? PluginPackages { get; init; }

    public string? ServerId { get; init; }

    public string? SessionDir { get; init; }
}

/// <summary>对应 TS <c>ServerCommandContext</c>。</summary>
public interface IServerCommandContext
{
    Task RunServerAsync(ServerCommand command);
}

/// <summary>对应 TS 的 <c>serverCommand</c> 常量。</summary>
public static class ServerCommandDefinition
{
    public static readonly CommandOption<string> ServerIdOption = CommandOptions.ValueOption<string>(
        "--server-id",
        value => Pi.Protocol.ServerIds.IsServerId(value)
            ? new CommandOptionParseResult<string>.Ok(value)
            : new CommandOptionParseResult<string>.Failed($"Invalid --server-id \"{value}\"; expected a lowercase UUIDv4"));

    public static readonly CommandOption<string> SessionDirOption = CommandOptions.StringOption("--session-dir");

    public static readonly CommandOption<string> ProviderOption = CommandOptions.StringOption("--provider");

    public static readonly CommandOption<string> ModelOption = CommandOptions.StringOption("--model");

    public static readonly CommandOption<string> PluginPackageOption =
        CommandOptions.StringOption("-e", repeatable: true);

    public static Command<ServerCommand, IServerCommandContext> Create() =>
        new Command<ServerCommand, IServerCommandContext>("server")
            .Option(ServerIdOption)
            .Option(SessionDirOption)
            .Option(ProviderOption)
            .Option(ModelOption)
            .Option(PluginPackageOption)
            .Option(ExperimentalCommandOptions.AuthTokenOption)
            .Option(ExperimentalCommandOptions.AuthTokenFileOption)
            .Build(input =>
            {
                var (auth, authErrors) = ExperimentalCommandOptions.ParseAuth(input);
                var serverId = input.Value(ServerIdOption);
                var sessionDir = input.Value(SessionDirOption);
                var provider = input.Value(ProviderOption);
                var model = input.Value(ModelOption);
                var pluginPackages = input.Values(PluginPackageOption);

                var errors = new List<string>(authErrors);
                if (provider is not null && model is null)
                {
                    errors.Add("--provider requires --model");
                }

                errors.AddRange(ExperimentalCommandOptions.UnsupportedOptions("server", input));
                if (errors.Count > 0)
                {
                    return new CommandBuildResult<ServerCommand>.Failed(errors);
                }

                return new CommandBuildResult<ServerCommand>.Ok(new ServerCommand
                {
                    Auth = auth,
                    Provider = provider,
                    Model = model,
                    PluginPackages = pluginPackages.Count == 0 ? null : pluginPackages,
                    ServerId = serverId,
                    SessionDir = sessionDir,
                });
            })
            .Action((command, context) => context.RunServerAsync(command));
}
