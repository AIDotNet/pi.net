namespace Pi.CodingAgent.Cli.Experimental;

// ============================================================================
// `pi experimental client` — TS `cli/experimental/commands/client.ts`（91 行）。
//
// 选项：`--connect` / `--session-id` / `--continue`(`-c`) / `--resume`(`-r`) /
//      `--provider` / `--model` / `-e`（可重复）+ 共用的两个 auth 选项。
//
// 校验顺序（决定 errors 排列）：
//   1. 认证互斥；
//   2. `--provider` 无 `--model`；
//   3. `--session-id` / `--continue` / `--resume` 三者只能选一个；
//   4. 有 remainingArgs 但没能当成 prompt 消化掉 → 「不支持既有 CLI 选项」。
//
// prompt 判定（TS 原文，容易漏的细节）：
//   `promptArgs = remainingArgs[0] === "--" ? remainingArgs.slice(1) : remainingArgs`
//   只有 **恰好一个** token，且（原始首参是 `--` 或该 token 不以 `-` 开头），
//   且非空串时才算 prompt；否则不认。所以 `client -r "Explain"` 的 prompt 是
//   "Explain"，而 `client -- --model x` 的 prompt 是 "--model"（因为首参是 `--`，
//   后面的 `-` 开头也照收）。
//
// 差异：`continue`/`resume` 在 TS 里是「有键即 true、无键即省略」，C# 用 `bool?`
// （null = 不出现）；`pluginPackages` 空表 → null。
// ============================================================================

/// <summary>对应 TS <c>ClientCommand</c>。</summary>
public sealed record ClientCommand : ICommandInvocation
{
    public string Command => "client";

    public AuthInput? Auth { get; init; }

    public TransportAddress? Connect { get; init; }

    public string? SessionId { get; init; }

    public bool? Continue { get; init; }

    public bool? Resume { get; init; }

    public string? Provider { get; init; }

    public string? Model { get; init; }

    public IReadOnlyList<string>? PluginPackages { get; init; }

    public string? Prompt { get; init; }
}

/// <summary>对应 TS <c>ClientCommandContext</c>。</summary>
public interface IClientCommandContext
{
    Task RunClientAsync(ClientCommand command);
}

/// <summary>对应 TS 的 <c>clientCommand</c> 常量。</summary>
public static class ClientCommandDefinition
{
    public static readonly CommandOption<string> SessionIdOption = CommandOptions.StringOption("--session-id");

    public static readonly CommandOption<bool> ContinueOption = CommandOptions.FlagOption("--continue");

    public static readonly CommandOption<bool> ContinueShortOption = CommandOptions.FlagOption("-c");

    public static readonly CommandOption<bool> ResumeOption = CommandOptions.FlagOption("--resume");

    public static readonly CommandOption<bool> ResumeShortOption = CommandOptions.FlagOption("-r");

    public static readonly CommandOption<string> ProviderOption = CommandOptions.StringOption("--provider");

    public static readonly CommandOption<string> ModelOption = CommandOptions.StringOption("--model");

    public static readonly CommandOption<string> PluginPackageOption =
        CommandOptions.StringOption("-e", repeatable: true);

    public static Command<ClientCommand, IClientCommandContext> Create() =>
        new Command<ClientCommand, IClientCommandContext>("client")
            .Option(ExperimentalCommandOptions.ConnectOption)
            .Option(SessionIdOption)
            .Option(ContinueOption)
            .Option(ContinueShortOption)
            .Option(ResumeOption)
            .Option(ResumeShortOption)
            .Option(ProviderOption)
            .Option(ModelOption)
            .Option(PluginPackageOption)
            .Option(ExperimentalCommandOptions.AuthTokenOption)
            .Option(ExperimentalCommandOptions.AuthTokenFileOption)
            .Build(input =>
            {
                var (auth, authErrors) = ExperimentalCommandOptions.ParseAuth(input);
                var connect = input.Value(ExperimentalCommandOptions.ConnectOption);
                var sessionId = input.Value(SessionIdOption);
                var shouldContinue = input.Value(ContinueOption) || input.Value(ContinueShortOption);
                var shouldResume = input.Value(ResumeOption) || input.Value(ResumeShortOption);
                var provider = input.Value(ProviderOption);
                var model = input.Value(ModelOption);
                var pluginPackages = input.Values(PluginPackageOption);
                var prompt = ExtractPrompt(input.RemainingArgs);

                var errors = new List<string>(authErrors);
                if (provider is not null && model is null)
                {
                    errors.Add("--provider requires --model");
                }

                var selections = (sessionId is not null ? 1 : 0) + (shouldContinue ? 1 : 0) + (shouldResume ? 1 : 0);
                if (selections > 1)
                {
                    errors.Add("--session-id, --continue, and --resume are mutually exclusive");
                }

                if (input.RemainingArgs.Count > 0 && prompt is null)
                {
                    errors.AddRange(ExperimentalCommandOptions.UnsupportedOptions("client", input));
                }

                if (errors.Count > 0)
                {
                    return new CommandBuildResult<ClientCommand>.Failed(errors);
                }

                return new CommandBuildResult<ClientCommand>.Ok(new ClientCommand
                {
                    Auth = auth,
                    Connect = connect,
                    SessionId = sessionId,
                    Continue = shouldContinue ? true : null,
                    Resume = shouldResume ? true : null,
                    Provider = provider,
                    Model = model,
                    PluginPackages = pluginPackages.Count == 0 ? null : pluginPackages,
                    Prompt = prompt,
                });
            })
            .Action((command, context) => context.RunClientAsync(command));

    /// <summary>
    /// 对应 TS 里那段 prompt 判定表达式。`--` 之后的内容即使以 `-` 开头也照收。
    /// </summary>
    internal static string? ExtractPrompt(IReadOnlyList<string> remainingArgs)
    {
        var promptArgs = remainingArgs.Count > 0 && remainingArgs[0] == "--"
            ? remainingArgs.Skip(1).ToList()
            : remainingArgs;

        if (promptArgs.Count != 1)
        {
            return null;
        }

        var candidate = promptArgs[0];
        var allowsDashPrefix = remainingArgs.Count > 0 && remainingArgs[0] == "--";
        if (!allowsDashPrefix && candidate.StartsWith('-'))
        {
            return null;
        }

        return candidate.Length > 0 ? candidate : null;
    }
}
