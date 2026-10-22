using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.Json.Nodes;

namespace Pi.Ai.Utils;

/// <summary>
/// 发给 provider 的 <c>User-Agent</c> 形态。对应 TS <c>UserAgentMode</c>：
/// <see cref="Detailed"/>（默认，<c>pi/&lt;version&gt; (&lt;platform&gt;; &lt;runtime&gt;; &lt;arch&gt;)</c>）、
/// <see cref="Short"/>（<c>pi</c>）、<see cref="Custom"/>（调用方给定值）。
/// </summary>
public sealed record UserAgentMode
{
    private UserAgentMode() { }

    /// <summary>详版 UA（默认）。</summary>
    public static UserAgentMode Detailed { get; } = new();

    /// <summary>短 UA：<c>pi</c>。</summary>
    public static UserAgentMode Short { get; } = new() { IsShort = true };

    /// <summary>自定义 UA。</summary>
    public static UserAgentMode Custom(string value) => new() { CustomValue = value };

    internal bool IsShort { get; private init; }

    internal string? CustomValue { get; private init; }
}

/// <summary>
/// 请求归属于谁。<see cref="Identity.GetAttributionHeaders"/> 把这些字段映射成各 provider 的头。
/// 对应 TS <c>ClientAttribution</c>。
/// </summary>
public sealed record ClientAttribution(string Name, string Url, IReadOnlyList<string>? Categories = null);

/// <summary>
/// pi-ai 向 provider 标识调用方的方式。字段全可缺省，缺省时用 pi-ai 的默认值：
/// 带版本号的详版 <c>User-Agent</c>，且不带归属头。对应 TS <c>ClientIdentity</c>。
/// </summary>
public sealed record ClientIdentity
{
    /// <summary>UA 形态，缺省 <see cref="UserAgentMode.Detailed"/>。</summary>
    public UserAgentMode? UserAgent { get; init; }

    /// <summary>
    /// provider 归属头。未设置表示沿用被合并的身份，显式 <see langword="null"/> 表示关闭归属。
    /// 用 <see cref="AttributionSet"/> 区分这两种情况（对应 TS 的 <c>attribution !== undefined</c>）。
    /// </summary>
    public ClientAttribution? Attribution
    {
        get => _attribution;
        init
        {
            _attribution = value;
            AttributionSet = true;
        }
    }

    /// <summary>是否显式设置过 <see cref="Attribution"/>（含显式置空）。</summary>
    public bool AttributionSet { get; private init; }

    /// <summary>详版 UA 里的版本号，缺省用 Pi.Ai 的程序集版本。</summary>
    public string? Version { get; init; }

    private readonly ClientAttribution? _attribution;
}

/// <summary>请求要去的 provider 与端点。对应 TS <c>ClientIdentityTarget</c>（<c>ModelSpec</c> 结构上满足）。</summary>
public readonly record struct ClientIdentityTarget(string Provider, string BaseUrl);

/// <summary>
/// 客户端身份与 provider 归属头。对应 TS <c>utils/identity.ts</c>。
/// <para>与上游的差异：上游从 <c>process.versions</c> 取 <c>bun/node</c> 运行时标识，端口跑在 .NET 上，
/// 故详版 UA 的运行时段为 <c>dotnet/&lt;version&gt;</c>；平台/架构按 Node 的命名习惯映射
/// （<c>win32</c>/<c>darwin</c>/<c>linux</c>、<c>x64</c>/<c>arm64</c>/…），便于与上游日志对照。</para>
/// </summary>
public static class Identity
{
    /// <summary>pi 自身的归属：agent 核心、durable 与 coding agent 用它。对应 TS <c>PI_ATTRIBUTION</c>。</summary>
    public static ClientAttribution PiAttribution { get; } = new("pi", "https://pi.dev", ["cli-agent"]);

    private const string NvidiaNimHost = "integrate.api.nvidia.com";
    private const string OpenCodeHost = "opencode.ai";

    /// <summary>
    /// 逐字段合并身份：后者覆盖前者，未设置的字段跳过；全为空时返回 <see langword="null"/>。
    /// 对应 TS <c>mergeClientIdentity</c>。
    /// </summary>
    public static ClientIdentity? MergeClientIdentity(params ClientIdentity?[] sources)
    {
        ClientIdentity? merged = null;
        foreach (var source in sources)
        {
            if (source is null) continue;
            merged ??= new ClientIdentity();
            if (source.UserAgent is not null) merged = merged with { UserAgent = source.UserAgent };
            if (source.AttributionSet) merged = merged with { Attribution = source.Attribution };
            if (source.Version is not null) merged = merged with { Version = source.Version };
        }

        return merged;
    }

    /// <summary>详版 UA 里的版本号。对应 TS <c>getClientVersion</c>。</summary>
    public static string GetClientVersion(ClientIdentity? identity = null) => identity?.Version ?? DefaultVersion();

    /// <summary>身份对应的 <c>User-Agent</c> 值。对应 TS <c>formatUserAgent</c>。</summary>
    public static string FormatUserAgent(ClientIdentity? identity = null)
    {
        var mode = identity?.UserAgent ?? UserAgentMode.Detailed;
        if (mode.CustomValue is { } custom) return custom;
        if (mode.IsShort) return "pi";
        var product = $"pi/{GetClientVersion(identity)}";
        var runtime = RuntimeDescription();
        return runtime is null
            ? $"{product} (browser)"
            : $"{product} ({PlatformName()}; {runtime}; {ArchName()})";
    }

    /// <summary>
    /// 身份对某个请求目标的 provider 归属头；归属关闭时为空。对应 TS <c>getAttributionHeaders</c>。
    /// </summary>
    public static IReadOnlyDictionary<string, string> GetAttributionHeaders(
        ClientIdentityTarget target, ClientIdentity? identity = null)
    {
        var attribution = identity?.Attribution;
        if (attribution is null) return new Dictionary<string, string>();

        if (IsOpenRouter(target))
        {
            var headers = new Dictionary<string, string>
            {
                ["HTTP-Referer"] = attribution.Url,
                ["X-OpenRouter-Title"] = attribution.Name,
            };
            if (attribution.Categories is { Count: > 0 } categories)
            {
                headers["X-OpenRouter-Categories"] = string.Join(",", categories);
            }

            return headers;
        }

        // NVIDIA 期望首字母大写的来源，例如 Pi。
        if (IsNvidiaNim(target))
        {
            var name = attribution.Name;
            return new Dictionary<string, string>
            {
                ["X-BILLING-INVOKE-ORIGIN"] = name.Length == 0 ? name : char.ToUpperInvariant(name[0]) + name[1..],
            };
        }

        if (IsOpenCode(target)) return new Dictionary<string, string> { ["x-opencode-client"] = attribution.Name };
        return new Dictionary<string, string>();
    }

    /// <summary>
    /// OpenCode 的按对话路由头（含指向它的自定义 provider）。无会话或非 OpenCode 目标时为
    /// <see langword="null"/>。对应 TS <c>getOpenCodeSessionHeaders</c>。
    /// </summary>
    public static IReadOnlyDictionary<string, string>? GetOpenCodeSessionHeaders(
        ClientIdentityTarget target, string? sessionId)
        => !string.IsNullOrEmpty(sessionId) && IsOpenCode(target)
            ? new Dictionary<string, string> { ["x-opencode-session"] = sessionId }
            : null;

    /// <summary>
    /// 身份给一次请求加的默认头：<c>User-Agent</c> 与 provider 归属。API 把它放在 model / provider /
    /// 请求头之下，故配置的头会覆盖它。对应 TS <c>getIdentityHeaders</c>。
    /// </summary>
    public static Dictionary<string, string> GetIdentityHeaders(
        ClientIdentityTarget target, ClientIdentity? identity = null)
    {
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["User-Agent"] = FormatUserAgent(identity),
        };
        foreach (var (key, value) in GetAttributionHeaders(target, identity)) headers[key] = value;
        return headers;
    }

    /// <summary>把 <see cref="ModelSpec"/> 转成身份目标。</summary>
    public static ClientIdentityTarget TargetOf(Models.ModelSpec model) =>
        new(model.Provider, model.BaseUrl);

    /// <summary>
    /// 把身份头写进请求（同名头先移除再加入）。供直接用 <see cref="System.Net.Http.HttpRequestMessage"/>
    /// 的 provider 使用；调用点应随后再写 model / 请求头，让它们覆盖身份头。
    /// </summary>
    public static void ApplyIdentityHeaders(
        System.Net.Http.HttpRequestMessage request, ClientIdentityTarget target, ClientIdentity? identity = null)
    {
        foreach (var (key, value) in GetIdentityHeaders(target, identity))
        {
            request.Headers.Remove(key);
            request.Headers.TryAddWithoutValidation(key, value);
        }
    }

    /// <summary>
    /// 以 <paramref name="identity"/> 作为 <paramref name="models"/> 请求的默认身份：请求级身份逐字段覆盖它，
    /// 它又覆盖 <paramref name="models"/> 自己的身份。其余行为原样共用（同一批 provider、凭据与刷新控制），
    /// 因此 <c>SetProvider</c> / <c>DeleteProvider</c> 仍作用到原对象。对应 TS <c>withClientIdentity</c>。
    /// </summary>
    public static Models.Models WithClientIdentity(Models.Models models, ClientIdentity identity)
        => models.WithIdentity(MergeClientIdentity(identity, models.Identity));

    /// <summary>
    /// 从 JSON 读回身份（<c>userAgent</c> / <c>attribution</c> / <c>version</c>）。缺键按未设置处理，
    /// <c>attribution: null</c> 视为显式关闭归属。对应 TS <c>ClientIdentity</c> 的纯 JSON 形状。
    /// </summary>
    public static ClientIdentity? ParseClientIdentity(JsonNode? node)
    {
        if (node is not JsonObject source) return null;

        var identity = new ClientIdentity();
        bool touched = false;

        switch (source["userAgent"])
        {
            case JsonValue value when value.TryGetValue<string>(out var mode):
                identity = identity with
                {
                    UserAgent = mode == "short" ? UserAgentMode.Short : UserAgentMode.Detailed,
                };
                touched = true;
                break;
            case JsonObject custom when Str(custom, "custom") is { } text:
                identity = identity with { UserAgent = UserAgentMode.Custom(text) };
                touched = true;
                break;
        }

        if (source.ContainsKey("attribution"))
        {
            identity = source["attribution"] is JsonObject attribution
                ? identity with
                {
                    Attribution = new ClientAttribution(
                        Str(attribution, "name") ?? string.Empty,
                        Str(attribution, "url") ?? string.Empty,
                        attribution["categories"] is JsonArray categories
                            ? categories
                                .Select(entry => entry is JsonValue item && item.TryGetValue<string>(out var text) ? text : null)
                                .Where(text => text is not null)
                                .Select(text => text!)
                                .ToList()
                            : null),
                }
                : identity with { Attribution = null };
            touched = true;
        }

        if (Str(source, "version") is { } version)
        {
            identity = identity with { Version = version };
            touched = true;
        }

        return touched ? identity : null;

        static string? Str(JsonObject source, string key)
            => source[key] is JsonValue value && value.TryGetValue<string>(out var text) ? text : null;
    }

    /// <summary>
    /// 把已经写好的头按「后者覆盖」写进请求：同名先移除，值为 <see langword="null"/> 表示删除。
    /// 供 <see cref="ApplyIdentityHeaders"/> 之后的 model / 请求头使用——身份头必须能被覆盖甚至移除，
    /// 否则模型自带的 <c>User-Agent</c>（如 GitHub Copilot 伪装）会与身份头并列成两个值。
    /// </summary>
    public static void ApplyOverrides<TValue>(
        System.Net.Http.HttpRequestMessage request, IReadOnlyDictionary<string, TValue>? headers)
        where TValue : class?
    {
        if (headers is null) return;
        foreach (var (key, value) in headers)
        {
            request.Headers.Remove(key);
            if (value is not null) request.Headers.TryAddWithoutValidation(key, value.ToString());
        }
    }

    private static bool IsOpenRouter(ClientIdentityTarget target)
        => target.Provider == "openrouter" || target.BaseUrl.Contains("openrouter.ai", StringComparison.Ordinal);

    private static bool IsNvidiaNim(ClientIdentityTarget target)
        => target.Provider == "nvidia" || MatchesHost(target.BaseUrl, NvidiaNimHost);

    private static bool IsOpenCode(ClientIdentityTarget target)
        => target.Provider is "opencode" or "opencode-go" || MatchesHost(target.BaseUrl, OpenCodeHost);

    private static bool MatchesHost(string baseUrl, string expectedHost)
        => Uri.TryCreate(baseUrl, UriKind.Absolute, out var uri) && uri.Host == expectedHost;

    private static string DefaultVersion()
    {
        var assembly = typeof(Identity).Assembly;
        var informational = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        if (!string.IsNullOrEmpty(informational))
        {
            // SourceLink 会附加 +<sha>，UA 里只保留版本号。
            var plus = informational.IndexOf('+');
            return plus >= 0 ? informational[..plus] : informational;
        }

        return assembly.GetName().Version?.ToString(3) ?? "0.0.0";
    }

    private static string? RuntimeDescription() => $"dotnet/{Environment.Version}";

    private static string PlatformName()
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows)) return "win32";
        if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX)) return "darwin";
        return "linux";
    }

    private static string ArchName() => RuntimeInformation.ProcessArchitecture switch
    {
        Architecture.X64 => "x64",
        Architecture.Arm64 => "arm64",
        Architecture.X86 => "ia32",
        Architecture.Arm => "arm",
        var other => other.ToString().ToLowerInvariant(),
    };
}
