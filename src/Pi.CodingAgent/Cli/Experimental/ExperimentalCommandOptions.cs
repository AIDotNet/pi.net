using System.Globalization;
using System.Text;
using Pi.Protocol;

namespace Pi.CodingAgent.Cli.Experimental;

// ============================================================================
// 实验性子命令共用的选项与地址解析 — TS `cli/experimental/command-options.ts`（106 行）。
//
// 两块内容：
//   1. 认证来源 `--auth-token` / `--auth-token-file`（互斥）；
//   2. `--connect` 的传输地址：`unix:///<绝对路径>` 或 `radius://<小写 UUIDv4>`。
//
// `parseTransportAddress` 是**逐条照抄 WHATWG URL 行为**的结果，不是「差不多」：
//   - `new URL(value)` 失败（无合法 scheme）→ Invalid；
//   - `radius:` 分支：拒绝 userinfo / port / 非空且非 "/" 的 path / query / hash，
//     并要求 `value` 与 `radius://${hostname}${pathname}` **逐字相等**（这一条同时挡掉
//     大写 scheme、大写 host 等所有「会被规范化」的写法）；
//   - `unix:` 分支：拒绝任何 authority；要求以 `unix:///` 开头、不以 `unix:////` 开头、
//     不含 `?`/`#`，且 `url.href === value`（同样挡掉需要重新百分号编码的写法，如空格、
//     非 ASCII）；随后 `decodeURIComponent(pathname)` 必须成功（`%zz` 这类会失败）；
//   - 最后要求 `posix.isAbsolute(path)`，即 path 以 `/` 开头。
//
// C# 没有 WHATWG URL：`System.Uri` 对非特殊 scheme 的规范化规则不同（例如 `unix:///x`
// 会被当成 host 为空、且 `OriginalString` 不做 path 编码），直接用它会让上述「逐字相等」
// 判定失真。故这里自己拆 scheme/authority/path，并用
// <see cref="EncodePathLikeWhatwg"/> 复刻 path 的百分号编码集合来做 href 对比。
//
// 已知差异：WHATWG 的 path 编码集合之外还有几个细节（如 tab/newline 会被剥离），
// 端口不做剥离——含这些字符的地址会被判 Invalid（比 TS 更严格，但都是异常输入）。
// ============================================================================

/// <summary>对应 TS <c>AuthInput</c>（判别联合 <c>token</c> / <c>file</c>）。</summary>
public abstract record AuthInput
{
    public sealed record Token(string Value) : AuthInput;

    public sealed record File(string Path) : AuthInput;
}

/// <summary>对应 TS <c>TransportAddress</c>（判别联合 <c>unix</c> / <c>radius</c>）。</summary>
public abstract record TransportAddress
{
    public sealed record Unix(string Path) : TransportAddress;

    public sealed record Radius(string ServerId) : TransportAddress;
}

/// <summary>对应 TS <c>command-options.ts</c> 的模块级导出。</summary>
public static class ExperimentalCommandOptions
{
    public static readonly CommandOption<string> AuthTokenOption = CommandOptions.StringOption("--auth-token");

    public static readonly CommandOption<string> AuthTokenFileOption =
        CommandOptions.StringOption("--auth-token-file");

    public static readonly CommandOption<TransportAddress> ConnectOption = CommandOptions.ValueOption<TransportAddress>(
        "--connect",
        value =>
        {
            var (address, error) = ParseTransportAddress(value);
            return address is not null
                ? new CommandOptionParseResult<TransportAddress>.Ok(address)
                : new CommandOptionParseResult<TransportAddress>.Failed(error ?? $"Invalid --connect address \"{value}\"");
        });

    /// <summary>对应 TS <c>parseAuth</c>：从已解析的输入取两个认证选项。</summary>
    public static (AuthInput? Auth, IReadOnlyList<string> Errors) ParseAuth(IParsedCommandInput input) =>
        ParseAuthInput(input.Value(AuthTokenOption), input.Value(AuthTokenFileOption));

    /// <summary>对应 TS <c>unsupportedOptions</c>：稳定 CLI 的选项在这里一律不支持。</summary>
    public static IReadOnlyList<string> UnsupportedOptions(string command, IParsedCommandInput input) =>
        input.RemainingArgs.Count == 0
            ? []
            : [$"The experimental {command} command does not support existing CLI options yet"];

    private static (AuthInput? Auth, IReadOnlyList<string> Errors) ParseAuthInput(
        string? authToken,
        string? authTokenFile)
    {
        if (authToken is not null && authTokenFile is not null)
        {
            return (null, ["--auth-token and --auth-token-file are mutually exclusive"]);
        }

        if (authToken is not null)
        {
            return (new AuthInput.Token(authToken), []);
        }

        return authTokenFile is not null ? (new AuthInput.File(authTokenFile), []) : (null, []);
    }

    /// <summary>对应 TS <c>parseTransportAddress</c>。</summary>
    internal static (TransportAddress? Address, string? Error) ParseTransportAddress(string value)
    {
        var invalid = $"Invalid --connect address \"{value}\"";

        // 1. 拆 scheme：WHATWG 要求 `[A-Za-z][A-Za-z0-9+-.]*:`，否则 `new URL` 抛错。
        var colon = value.IndexOf(':');
        if (colon <= 0 || !IsScheme(value[..colon]))
        {
            return (null, invalid);
        }

        var protocol = value[..colon].ToLowerInvariant() + ":";
        var rest = value[(colon + 1)..];

        if (protocol == "radius:")
        {
            return ParseRadius(value, rest, invalid);
        }

        if (protocol != "unix:")
        {
            return (null, $"Unsupported --connect transport \"{protocol}\"");
        }

        return ParseUnix(value, rest, invalid);
    }

    private static (TransportAddress? Address, string? Error) ParseRadius(
        string value,
        string rest,
        string invalid)
    {
        if (!rest.StartsWith("//", StringComparison.Ordinal))
        {
            return (null, invalid);
        }

        var afterSlashes = rest[2..];
        var authorityEnd = afterSlashes.IndexOfAny(['/', '?', '#']);
        var authority = authorityEnd < 0 ? afterSlashes : afterSlashes[..authorityEnd];
        var tail = authorityEnd < 0 ? "" : afterSlashes[authorityEnd..];

        // 先剥 query/hash，再看 pathname。
        var fragmentStart = tail.IndexOfAny(['?', '#']);
        var pathname = fragmentStart < 0 ? tail : tail[..fragmentStart];
        var searchOrHash = fragmentStart < 0 ? "" : tail[fragmentStart..];

        // userinfo / port：TS 分别检查 url.username/password 与 url.port，两者都要求为空。
        if (authority.Contains('@', StringComparison.Ordinal))
        {
            return (null, invalid);
        }

        var hostname = authority;
        var port = "";
        var portColon = authority.LastIndexOf(':');
        if (portColon >= 0)
        {
            hostname = authority[..portColon];
            port = authority[(portColon + 1)..];
        }

        if (port.Length > 0 || searchOrHash.Length > 0)
        {
            return (null, invalid);
        }

        if (pathname.Length > 0 && pathname != "/")
        {
            return (null, invalid);
        }

        // `value !== \`radius://${hostname}${pathname}\``：逐字相等，挡掉所有需要规范化的写法。
        if (value != $"radius://{hostname}{pathname}")
        {
            return (null, invalid);
        }

        if (!ServerIds.IsServerId(hostname))
        {
            return (null, "Radius transport address requires a lowercase UUIDv4 server ID");
        }

        return (new TransportAddress.Radius(hostname), null);
    }

    private static (TransportAddress? Address, string? Error) ParseUnix(
        string value,
        string rest,
        string invalid)
    {
        // `url.hostname || url.port || url.username || url.password` → 有任何 authority 就拒。
        if (rest.StartsWith("//", StringComparison.Ordinal))
        {
            var afterSlashes = rest[2..];
            var authorityEnd = afterSlashes.IndexOfAny(['/', '?', '#']);
            var authority = authorityEnd < 0 ? afterSlashes : afterSlashes[..authorityEnd];
            if (authority.Length > 0)
            {
                return (null, "Unix transport address must not include an authority");
            }
        }

        if (!value.StartsWith("unix:///", StringComparison.Ordinal)
            || value.StartsWith("unix:////", StringComparison.Ordinal)
            || value.Contains('?', StringComparison.Ordinal)
            || value.Contains('#', StringComparison.Ordinal))
        {
            return (null, invalid);
        }

        var pathname = value["unix://".Length..];

        // `url.href !== value`：host 为空时 href 就是 "unix://" + 编码后的 path。
        if ("unix://" + EncodePathLikeWhatwg(pathname) != value)
        {
            return (null, invalid);
        }

        if (!TryDecodeUriComponent(pathname, out var path))
        {
            return (null, invalid);
        }

        if (path.Contains('\0'))
        {
            return (null, invalid);
        }

        // `posix.isAbsolute`：只认以 "/" 开头的路径。
        return path.StartsWith('/')
            ? (new TransportAddress.Unix(path), null)
            : (null, "Unix transport address requires an absolute path");
    }

    private static bool IsScheme(string candidate)
    {
        if (candidate.Length == 0 || !char.IsAsciiLetter(candidate[0]))
        {
            return false;
        }

        foreach (var c in candidate)
        {
            if (!char.IsAsciiLetterOrDigit(c) && c is not ('+' or '-' or '.'))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// 复刻 WHATWG 对 path 的百分号编码集合（`path percent-encode set` 加上非 ASCII）：
    /// C0 控制符、空格、`"`、`#`、`&lt;`、`&gt;`、`?`、`` ` ``、`{`、`}` 以及所有非 ASCII
    /// 字符。`%` 本身不重新编码（WHATWG 保留既有百分号序列）。
    /// </summary>
    private static string EncodePathLikeWhatwg(string pathname)
    {
        var builder = new StringBuilder(pathname.Length);
        foreach (var c in pathname)
        {
            if (c < 0x80)
            {
                if (c < 0x20 || c == 0x7f || c is ' ' or '"' or '#' or '<' or '>' or '?' or '`' or '{' or '}')
                {
                    builder.Append('%').Append(((int)c).ToString("X2", CultureInfo.InvariantCulture));
                }
                else
                {
                    builder.Append(c);
                }

                continue;
            }

            foreach (var b in Encoding.UTF8.GetBytes(c.ToString()))
            {
                builder.Append('%').Append(b.ToString("X2", CultureInfo.InvariantCulture));
            }
        }

        return builder.ToString();
    }

    /// <summary>
    /// 对应 JS <c>decodeURIComponent</c>：`%XX` 必须是两个十六进制位，否则失败；
    /// 解出的字节按 UTF-8 解码，非法序列同样失败（JS 抛 <c>URIError</c>）。
    /// </summary>
    internal static bool TryDecodeUriComponent(string encoded, out string decoded)
    {
        decoded = "";
        var builder = new StringBuilder(encoded.Length);
        var bytes = new List<byte>();
        var strictUtf8 = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

        try
        {
            for (var i = 0; i < encoded.Length; i++)
            {
                if (encoded[i] != '%')
                {
                    if (bytes.Count > 0)
                    {
                        builder.Append(strictUtf8.GetString(bytes.ToArray()));
                        bytes.Clear();
                    }

                    builder.Append(encoded[i]);
                    continue;
                }

                if (i + 2 >= encoded.Length
                    || !byte.TryParse(
                        encoded.AsSpan(i + 1, 2),
                        NumberStyles.HexNumber,
                        CultureInfo.InvariantCulture,
                        out var value))
                {
                    return false;
                }

                bytes.Add(value);
                i += 2;
            }

            if (bytes.Count > 0)
            {
                builder.Append(strictUtf8.GetString(bytes.ToArray()));
            }
        }
        catch (DecoderFallbackException)
        {
            // JS 的 decodeURIComponent 对非法 UTF-8 抛 URIError。
            return false;
        }

        decoded = builder.ToString();
        return true;
    }
}
