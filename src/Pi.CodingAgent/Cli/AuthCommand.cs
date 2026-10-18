using System.Text.RegularExpressions;
using Pi.Ai.Auth;
using Pi.CodingAgent.Core;

namespace Pi.CodingAgent.Cli;

// ============================================================================
// `pi auth …` 命令解析 — TS `cli/auth-command.ts`（126 行）。
//
// 三个子命令：check / print-api-key / print-bearer-token。本文件只负责
// 参数解析与校验（纯决策层，无 I/O）；执行体在 auth-check.ts / credential-print.ts。
//
// 注意 <c>getAuthCredential</c> 已由 `Core/AuthCredentials.cs` 端口承载
// （4e 期为 share / bug-report 流提前落地），此处不重复声明，两个执行体
// 直接复用那个静态方法。
// ============================================================================

/// <summary>对应 TS <c>AuthCommandKind</c>。</summary>
public enum AuthCommandKind
{
    Check,
    ApiKey,
    BearerToken,
}

/// <summary>对应 TS <c>AuthCommand</c>。</summary>
public sealed record AuthCommand
{
    public required AuthCommandKind Kind { get; init; }

    public required IReadOnlyList<string> Args { get; init; }

    public required bool Json { get; init; }

    public required bool Credentials { get; init; }

    public required bool NoRefresh { get; init; }

    /// <summary>对应 TS <c>minExpiryMs?</c>：仅 print-bearer-token 支持。</summary>
    public long? MinExpiryMs { get; init; }
}

/// <summary>对应 TS <c>AuthCommandError</c>（<c>extends Error</c> 的空壳）。</summary>
public sealed class AuthCommandError : Exception
{
    public AuthCommandError(string message)
        : base(message)
    {
    }
}

/// <summary><c>validateAuthCommandArgs</c> 的返回值：解析出的 provider / model。</summary>
public sealed record AuthCommandTarget(string? Provider, string? Model);

/// <summary>TS <c>auth-command.ts</c> 的模块级函数。</summary>
public static partial class AuthCommands
{
    /// <summary>对应 TS <c>AUTH_COMMAND_USAGE</c>（用 <see cref="Config.AppName"/> 插值）。</summary>
    private static readonly Dictionary<AuthCommandKind, string> Usage = new()
    {
        [AuthCommandKind.Check] =
            $"{Config.AppName} auth check --provider <provider> [--json] [--credentials] [--no-refresh]",
        [AuthCommandKind.ApiKey] =
            $"{Config.AppName} auth print-api-key --provider <provider> [--model <model>]",
        [AuthCommandKind.BearerToken] =
            $"{Config.AppName} auth print-bearer-token --provider <provider> [--model <model>] [--min-expiry <duration>]",
    };

    // TS `/^(\d+)(ms|s|m|h)$/iu`：`\d` 在 JS 只匹配 ASCII 数字（.NET 默认含 Unicode
    // 数字，故写 [0-9]）；`$` 在无 `m` 标志时只匹配输入末尾（对应 .NET `\z` 而非 `$`）。
    [GeneratedRegex(@"^([0-9]+)(ms|s|m|h)\z", RegexOptions.IgnoreCase)]
    private static partial Regex MinExpiryRegex();

    /// <summary>对应 TS <c>getAuthCommandName</c>。</summary>
    public static string GetAuthCommandName(AuthCommandKind kind) => kind switch
    {
        AuthCommandKind.Check => "auth check",
        AuthCommandKind.ApiKey => "auth print-api-key",
        _ => "auth print-bearer-token",
    };

    /// <summary>对应 TS <c>getAuthCommandUsage</c>。</summary>
    public static string GetAuthCommandUsage(AuthCommandKind kind) => Usage[kind];

    /// <summary>对应 TS <c>isAuthCommandHelp</c>。</summary>
    public static bool IsAuthCommandHelp(IReadOnlyList<string> args)
        => args.Count > 0
            && args[0] == "auth"
            && (args.Count < 2 || args[1] == "help" || args.Contains("--help") || args.Contains("-h"));

    /// <summary>对应 TS <c>printAuthCommandHelp</c>。</summary>
    /// <remarks>
    /// TS 在此函数里**写死** <c>pi</c>（与上面用 <c>APP_NAME</c> 插值的
    /// <c>AUTH_COMMAND_USAGE</c> 不一致，属上游既有行为），端口照抄以保文本一致。
    /// </remarks>
    public static void PrintAuthCommandHelp()
    {
        Console.WriteLine($"Usage:\n" +
            "  pi auth print-api-key [--provider <provider>] [--model <model>]\n" +
            "  pi auth print-bearer-token [--provider <provider>] [--model <model>] [--min-expiry <duration>]\n" +
            "  pi auth check [--provider <provider>] [--model <model>] [--json] [--credentials] [--no-refresh]\n" +
            "\n" +
            "Auth commands require at least one of --provider or --model. Checks refresh expired OAuth credentials by default; --no-refresh prevents this. --credentials emits the credential, or includes it in JSON output.");
    }

    /// <summary>对应 TS <c>parseAuthCommand</c>：非 auth 命令返回 <c>null</c>。</summary>
    public static AuthCommand? ParseAuthCommand(IReadOnlyList<string> args)
    {
        if (args.Count == 0 || args[0] != "auth")
        {
            return null;
        }

        var kind = args.Count > 1
            ? args[1] switch
            {
                "check" => (AuthCommandKind?)AuthCommandKind.Check,
                "print-api-key" => AuthCommandKind.ApiKey,
                "print-bearer-token" => AuthCommandKind.BearerToken,
                _ => null,
            }
            : null;
        if (kind is null)
        {
            var unknown = args.Count > 1 ? args[1] : "";
            throw new AuthCommandError(
                $"Unknown auth command \"{unknown}\". Use \"{Config.AppName} auth print-api-key\", \"{Config.AppName} auth print-bearer-token\", or \"{Config.AppName} auth check\".");
        }

        var commandArgs = new List<string>();
        var json = false;
        var credentials = false;
        var noRefresh = false;
        long? minExpiryMs = null;
        for (var index = 2; index < args.Count; index++)
        {
            var arg = args[index];
            if (arg == "--min-expiry")
            {
                if (kind != AuthCommandKind.BearerToken)
                {
                    throw new AuthCommandError("--min-expiry is only supported by print-bearer-token");
                }

                var value = index + 1 < args.Count ? args[++index] : null;
                var match = value is null ? null : MinExpiryRegex().Match(value);
                if (match is not { Success: true })
                {
                    throw new AuthCommandError("--min-expiry must use a duration such as 30m or 1h");
                }

                var amount = long.Parse(match.Groups[1].Value);
                // TS 的单位比较是大小写敏感的（`unit === "ms"`），所以 `30M` 落到
                // 最后那个分支按小时算——与 TS 一致，此处不是笔误。
                var unit = match.Groups[2].Value;
                minExpiryMs = amount * (unit == "ms" ? 1 : unit == "s" ? 1_000 : unit == "m" ? 60_000 : 3_600_000);
                continue;
            }

            if (arg is "--json" or "--credentials" or "--no-refresh")
            {
                if (kind != AuthCommandKind.Check)
                {
                    throw new AuthCommandError($"{arg} is only supported by auth check");
                }

                if (arg == "--json")
                {
                    json = true;
                }
                else if (arg == "--credentials")
                {
                    credentials = true;
                }
                else
                {
                    noRefresh = true;
                }

                continue;
            }

            commandArgs.Add(arg);
        }

        return new AuthCommand
        {
            Kind = kind.Value,
            Args = commandArgs,
            Json = json,
            Credentials = credentials,
            NoRefresh = noRefresh,
            MinExpiryMs = minExpiryMs,
        };
    }

    /// <summary>对应 TS <c>validateAuthCommandArgs</c>。</summary>
    public static AuthCommandTarget ValidateAuthCommandArgs(Args args, AuthCommandKind kind)
    {
        // TS `args.provider?.trim() || undefined`：纯空白视为未提供。
        var provider = string.IsNullOrWhiteSpace(args.Provider) ? null : args.Provider.Trim();
        var model = string.IsNullOrWhiteSpace(args.Model) ? null : args.Model.Trim();
        if (args.UnknownFlags.Count > 0)
        {
            var option = args.UnknownFlags.Keys.First();
            throw new AuthCommandError($"Unknown option --{option} for \"{GetAuthCommandName(kind)}\".");
        }

        if (args.ApiKey is not null || args.Messages.Count > 0 || args.FileArgs.Count > 0)
        {
            throw new AuthCommandError("Auth commands only accept --provider and --model");
        }

        if (kind == AuthCommandKind.Check)
        {
            if (provider is null && model is null)
            {
                throw new AuthCommandError("Auth checks require --provider <provider> or --model <model>");
            }

            return new AuthCommandTarget(provider, model);
        }

        if (provider is null && model is null)
        {
            throw new AuthCommandError("Credential printing requires --provider <provider> or --model <model>");
        }

        return new AuthCommandTarget(provider, model);
    }

    /// <summary>
    /// 从解析出的鉴权结果里取 bearer token。转发到 <see cref="AuthCredentials.GetAuthCredential"/>
    /// （TS 的 <c>getAuthCredential</c>，4e 期已由 Core 侧端口承载）。
    /// </summary>
    public static string? GetAuthCredential(AuthResult? auth) => AuthCredentials.GetAuthCredential(auth);
}
