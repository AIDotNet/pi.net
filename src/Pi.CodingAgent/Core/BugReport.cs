using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Pi.Ai.Auth;
using Pi.Ai.Models;
using Pi.Ai.Types;
using PiUserAgent = Pi.CodingAgent.Utils.PiUserAgent;
using Uuid = Pi.Ai.Utils.Uuid;
using Pi.CodingAgent.Core.Extensions;
using Pi.CodingAgent.Utils;

namespace Pi.CodingAgent.Core;

/// <summary>对应 TS <c>BugReportFile</c>。</summary>
public sealed record BugReportFile
{
    public required string Name { get; init; }

    public required string ContentType { get; init; }

    public required string Data { get; init; }
}

/// <summary>对应 TS <c>BugReportBundle</c>。</summary>
public sealed record BugReportBundle
{
    public required object Metadata { get; init; }

    public required object Diagnostics { get; init; }

    /// <summary>会话 JSONL；用户不分享 transcript 时为 null（对应 TS 的 undefined）。</summary>
    public string? SessionJsonl { get; init; }

    /// <summary>模型生成的摘要；未生成时为 null。</summary>
    public string? Summary { get; init; }
}

/// <summary>
/// 对应 TS <c>collectBugReportMetadata</c> 的返回值。TS 直接读返回对象的字段
/// （<c>bundle.metadata.id</c> 等），端口用一个具形记录承载同一数据。
/// </summary>
public sealed record BugReportMetadata(
    object Value,
    string Id,
    string CreatedAt,
    string? Hint,
    bool SessionIncluded,
    bool SummaryIncluded);

/// <summary>对应 TS <c>collectBugReportDiagnostics</c> 的返回值（同样带回写字段）。</summary>
public sealed record BugReportDiagnostics(object Value, int CrashCount);

/// <summary>对应 TS <c>CollectBugReportMetadataOptions</c>。</summary>
public sealed record CollectBugReportMetadataOptions
{
    public string? Id { get; init; }

    public string? Hint { get; init; }

    public required string SessionId { get; init; }

    public required string Cwd { get; init; }

    public required bool IncludeSession { get; init; }

    public required bool IncludeSummary { get; init; }

    public required int MessageCount { get; init; }

    public ModelSpec? Model { get; init; }

    public required ModelRuntime ModelRuntime { get; init; }

    public required ThinkingLevel ThinkingLevel { get; init; }

    public required IReadOnlyList<Extension> Extensions { get; init; }

    public required IReadOnlyList<ExtensionLoadError> ExtensionErrors { get; init; }

    public required Settings GlobalSettings { get; init; }

    public required Settings ProjectSettings { get; init; }
}

/// <summary>
/// Bug report 收集、脱敏与归档。对应 TS <c>core/bug-report.ts</c>。
/// </summary>
public static partial class BugReport
{
    private const string Redacted = "<redacted>";
    private const int BugReportSchemaVersion = 1;

    /// <summary>TS <c>SENSITIVE_KEY</c>，按 camelCase 归一后的键名匹配。</summary>
    [GeneratedRegex(@"(?:^|[-_])(api[-_]?key|secret|token|password|passwd|credential|authorization|cookie)(?:$|[-_])", RegexOptions.IgnoreCase)]
    private static partial Regex SensitiveKeyRegex();

    /// <summary>TS 的嵌套协议头（<c>proxy:https://…</c>），内层 URL 递归脱敏。</summary>
    [GeneratedRegex(@"^([a-z][a-z0-9+.-]*:)([a-z][a-z0-9+.-]*://.*)$", RegexOptions.IgnoreCase)]
    private static partial Regex NestedSchemeRegex();

    /// <summary>TS <c>isSensitiveKey</c>：先把 camelCase 段拍成 snake 再匹配。</summary>
    public static bool IsSensitiveKey(string key)
    {
        var normalized = CamelBoundaryRegex().Replace(key, "$1_$2");
        return SensitiveKeyRegex().IsMatch(normalized);
    }

    [GeneratedRegex(@"([a-z0-9])([A-Z])")]
    private static partial Regex CamelBoundaryRegex();

    /// <summary>对应 TS <c>redactUrl</c>。</summary>
    public static string RedactUrl(string value)
    {
        var nested = NestedSchemeRegex().Match(value);
        if (nested.Success)
        {
            return nested.Groups[1].Value + RedactUrl(nested.Groups[2].Value);
        }

        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri))
        {
            return value;
        }

        var builder = new UriBuilder(uri);
        var changed = false;
        if (!string.IsNullOrEmpty(uri.UserInfo))
        {
            // TS clears both parts; UriBuilder then omits the userinfo entirely.
            builder.UserName = string.Empty;
            builder.Password = string.Empty;
            changed = true;
        }

        var query = uri.Query.TrimStart('?');
        if (query.Length > 0)
        {
            var pairs = new List<string>();
            foreach (var pair in query.Split('&'))
            {
                var separator = pair.IndexOf('=');
                var rawName = separator < 0 ? pair : pair[..separator];
                var rawValue = separator < 0 ? null : pair[(separator + 1)..];
                var name = Uri.UnescapeDataString(rawName);
                // TS 的 searchParams 序列化是 application/x-www-form-urlencoded：未命中脱敏的
                // 键值对也会被重新编码（空格 → '+'），只有整条 URL 未变时才原样返回。
                if (IsSensitiveKey(name))
                {
                    pairs.Add($"{UrlEncodeForm(name)}={UrlEncodeForm(Redacted)}");
                    changed = true;
                }
                else if (rawValue is null)
                {
                    pairs.Add(UrlEncodeForm(name));
                }
                else
                {
                    pairs.Add($"{UrlEncodeForm(name)}={UrlEncodeForm(Uri.UnescapeDataString(rawValue))}");
                }
            }

            builder.Query = string.Join("&", pairs);
        }

        // AbsoluteUri（而非 ToString）保留百分号编码——TS 的 url.toString() 同样保留
        // searchParams 重新序列化出的 %3C/%3E。
        return changed ? builder.Uri.AbsoluteUri : value;
    }

    /// <summary>WHATWG 的 application/x-www-form-urlencoded 序列化（空格 → '+'）。</summary>
    private static string UrlEncodeForm(string value)
    {
        var builder = new StringBuilder();
        foreach (var b in Encoding.UTF8.GetBytes(value))
        {
            var c = (char)b;
            if (c is '*' or '-' or '.' or '_' || char.IsAsciiLetterOrDigit(c))
            {
                builder.Append(c);
            }
            else if (c == ' ')
            {
                builder.Append('+');
            }
            else
            {
                builder.Append('%').Append(b.ToString("X2", System.Globalization.CultureInfo.InvariantCulture));
            }
        }

        return builder.ToString();
    }

    /// <summary>对应 TS <c>redactJsonValue</c>。</summary>
    public static JsonNode? RedactJsonValue(JsonNode? value, string? key = null)
    {
        // TS 的 replacer 对 null 子值跳过键检查（null 原样留下）。
        if (value is null)
        {
            return null;
        }

        if (key is not null && IsSensitiveKey(key))
        {
            return JsonValue.Create(Redacted);
        }

        switch (value)
        {
            case JsonObject obj:
                var copy = new JsonObject();
                foreach (var (name, child) in obj)
                {
                    // TS 的 replacer 收到 undefined 子值时丢键；JsonObject 里 null 是显式的，
                    // 递归保留 null（replacer 对 null 不丢键）。
                    copy[name] = RedactJsonValue(child?.DeepClone(), name);
                }

                return copy;
            case JsonArray array:
                var items = new JsonArray();
                foreach (var child in array)
                {
                    // 数组下标的键是索引字符串，永不敏感。
                    items.Add(RedactJsonValue(child?.DeepClone()));
                }

                return items;
            case JsonValue jsonValue when jsonValue.TryGetValue<string>(out var text):
                return JsonValue.Create(RedactUrl(text));
            default:
                return value.DeepClone();
        }
    }

    /// <summary>对应 TS <c>redactSettings</c>：摘掉跟踪 id 再整体脱敏。</summary>
    public static JsonObject? RedactSettings(Settings settings)
    {
        var node = JsonSerializer.SerializeToNode(settings, SettingsJson.Options) as JsonObject;
        if (node is null)
        {
            return null;
        }

        node.Remove("trackingId");
        node.Remove("deviceId");
        return RedactJsonValue(node) as JsonObject;
    }

    /// <summary>对应 TS <c>bugReportFiles</c>。</summary>
    public static IReadOnlyList<BugReportFile> BugReportFiles(BugReportBundle bundle)
    {
        var files = new List<BugReportFile>
        {
            new()
            {
                Name = "report.json",
                ContentType = "application/json",
                Data = JsonSerializer.Serialize(bundle.Metadata, ReportJson.Options) + "\n",
            },
            new()
            {
                Name = "diagnostics.json",
                ContentType = "application/json",
                Data = JsonSerializer.Serialize(bundle.Diagnostics, ReportJson.Options) + "\n",
            },
        };
        if (bundle.SessionJsonl is not null)
        {
            files.Add(new BugReportFile
            {
                Name = "session.jsonl",
                ContentType = "application/x-ndjson",
                Data = bundle.SessionJsonl,
            });
        }

        if (bundle.Summary is not null)
        {
            files.Add(new BugReportFile
            {
                Name = "summary.md",
                ContentType = "text/markdown",
                // TS: a trailing newline is added only when missing.
                Data = bundle.Summary.EndsWith("\n", StringComparison.Ordinal) ? bundle.Summary : bundle.Summary + "\n",
            });
        }

        return files;
    }

    /// <summary>对应 TS <c>bugReportArchiveFileName</c>：名字带报表 id。</summary>
    public static string BugReportArchiveFileName(string id) => $"pi-bug-report-{id}.zip";

    /// <summary>对应 TS <c>writeBugReportArchive</c>。</summary>
    public static async Task WriteBugReportArchiveAsync(string filePath, BugReportBundle bundle, CancellationToken cancellationToken = default)
    {
        var entries = BugReportFiles(bundle)
            .Select(file => new ZipEntry(file.Name, Encoding.UTF8.GetBytes(file.Data)))
            .ToList();
        await Zip.WriteArchiveAsync(filePath, entries, cancellationToken: cancellationToken).ConfigureAwait(false);
    }

    /// <summary>对应 TS <c>collectEnvironment</c>。</summary>
    public static object CollectEnvironment()
        => new
        {
            version = Config.Version,
            userAgent = PiUserAgent.GetPiUserAgent(Config.Version),
            // TS 报 bun/node；端口是同形状的 dotnet 运行时标识（与 PiUserAgent 一致）。
            runtime = $"dotnet/{RuntimeInformation.FrameworkDescription.Replace(".NET ", string.Empty, StringComparison.Ordinal)}",
            platform = ProcessInfo.Platform,
            arch = ProcessInfo.Arch,
            osRelease = Environment.OSVersion.Version.ToString(),
            osVersion = RuntimeInformation.OSDescription,
            shell = ShellName(),
            terminal = new
            {
                term = Env("TERM"),
                program = Env("TERM_PROGRAM"),
                programVersion = Env("TERM_PROGRAM_VERSION"),
                colorterm = Env("COLORTERM"),
                tmux = Env("TMUX") is not null,
                ssh = Env("SSH_CONNECTION") is not null || Env("SSH_CLIENT") is not null || Env("SSH_TTY") is not null,
                ci = Env("CI") is not null,
            },
            // Names help diagnose configuration; values never leave the machine.
            piEnvironmentVariables = Environment.GetEnvironmentVariables()
                .Keys.OfType<string>()
                .Where(name => name.StartsWith("PI_", StringComparison.Ordinal))
                .OrderBy(name => name, StringComparer.Ordinal)
                .ToList(),
        };

    private static string? Env(string name) => Environment.GetEnvironmentVariable(name) is { Length: > 0 } value ? value : null;

    /// <summary>TS <c>process.env.SHELL?.split(/[\\/]/).pop()</c>。</summary>
    private static string? ShellName()
    {
        var shell = Env("SHELL");
        if (shell is null)
        {
            return null;
        }

        var parts = shell.Split('/', '\\');
        var last = parts[^1];
        return last.Length > 0 ? last : null;
    }

    /// <summary>对应 TS <c>describeModel</c>。</summary>
    private static object DescribeModel(ModelSpec model)
        => new
        {
            provider = model.Provider,
            id = model.Id,
            name = model.Name,
            api = model.Api,
            baseUrl = RedactUrl(model.BaseUrl),
            reasoning = model.Reasoning,
            input = model.Input,
            contextWindow = model.ContextWindow,
            maxTokens = model.MaxTokens,
            samplingParams = model.SamplingParams is null ? null : RedactJsonValue(model.SamplingParams.DeepClone()),
            compat = model.Compat is null ? null : RedactJsonValue(model.Compat.DeepClone()),
            thinkingLevelMap = model.ThinkingLevelMap,
            headerNames = model.Headers is null
                ? new List<string>()
                : model.Headers.Keys.OrderBy(name => name, StringComparer.Ordinal).ToList(),
        };

    /// <summary>对应 TS <c>describeProvider</c>。</summary>
    private static object DescribeProvider(ModelRuntime modelRuntime, IProvider provider)
    {
        var authTypes = new List<string>();
        if (provider.Auth?.ApiKey is not null)
        {
            authTypes.Add("api_key");
        }

        if (provider.Auth?.OAuth is not null)
        {
            authTypes.Add("oauth");
        }

        return new
        {
            id = provider.Id,
            name = provider.Name,
            baseUrl = provider.BaseUrl is null ? null : RedactUrl(provider.BaseUrl),
            // C273：端口的 IProvider 不承载 headers（models.json 的头在请求期经
            // ResolveConfiguredModelHeaders 合并），故 headerNames 恒为空表。
            headerNames = Array.Empty<string>(),
            authTypes,
            authStatus = modelRuntime.GetProviderAuthStatus(provider.Id),
            usingOAuth = modelRuntime.IsUsingOAuth(provider.Id),
            registeredByExtension = modelRuntime.GetRegisteredProviderIds().Contains(provider.Id),
        };
    }

    /// <summary>对应 TS <c>describeExtension</c>。</summary>
    private static object DescribeExtension(Extension extension)
        => new
        {
            path = extension.Path,
            source = RedactUrl(extension.SourceInfo.Source),
            scope = extension.SourceInfo.Scope,
            origin = extension.SourceInfo.Origin,
            hidden = extension.Hidden,
        };

    /// <summary>对应 TS <c>collectBugReportMetadata</c>。</summary>
    public static BugReportMetadata CollectBugReportMetadata(CollectBugReportMetadataOptions options)
    {
        var hint = string.IsNullOrWhiteSpace(options.Hint) ? null : options.Hint.Trim();
        var provider = options.Model is null ? null : options.ModelRuntime.GetProvider(options.Model.Provider);
        var id = options.Id ?? Uuid.Uuidv7();
        var createdAt = DateTimeOffset.UtcNow.ToString(
            "yyyy-MM-dd'T'HH:mm:ss.fff'Z'", System.Globalization.CultureInfo.InvariantCulture);
        var value = new
        {
            schemaVersion = BugReportSchemaVersion,
            id,
            createdAt,
            hint,
            environment = CollectEnvironment(),
            session = new
            {
                id = options.SessionId,
                included = options.IncludeSession,
                summaryIncluded = options.IncludeSummary,
                messageCount = options.MessageCount,
                // TS 的展开写法：不分享 transcript 时没有 cwd 键。
                cwd = options.IncludeSession ? options.Cwd : null,
            },
            model = options.Model is null ? null : DescribeModel(options.Model),
            provider = provider is null ? null : DescribeProvider(options.ModelRuntime, provider),
            thinkingLevel = options.ThinkingLevel,
            extensions = options.Extensions.Select(DescribeExtension).ToList(),
            extensionErrors = options.ExtensionErrors
                .Select(error => new { path = error.Path, error = error.Error })
                .ToList(),
            settings = new
            {
                global = RedactSettings(options.GlobalSettings),
                project = RedactSettings(options.ProjectSettings),
            },
        };
        return new BugReportMetadata(
            value, id, createdAt, hint, options.IncludeSession, options.IncludeSummary);
    }

    /// <summary>对应 TS <c>collectBugReportDiagnostics</c>：只收失败的助手轮次，不收对话内容。</summary>
    public static BugReportDiagnostics CollectBugReportDiagnostics(
        SessionManager sessionManager,
        IReadOnlyList<CrashRecord>? crashes = null)
    {
        var entries = sessionManager.GetEntries();
        var assistant = new List<object>();
        var assistantMessageCount = 0;
        foreach (var entry in entries)
        {
            if (entry is not SessionMessageEntry { Message: AssistantMessage message })
            {
                continue;
            }

            assistantMessageCount++;
            var diagnostics = message.Diagnostics ?? [];
            if (diagnostics.Count == 0
                && message.StopReason != StopReason.Error
                && message.StopReason != StopReason.Aborted
                && message.ErrorMessage is null)
            {
                continue;
            }

            assistant.Add(new
            {
                entryId = entry.Id,
                timestamp = entry.Timestamp,
                provider = message.Provider,
                model = message.Model,
                api = message.Api,
                stopReason = message.StopReason,
                rawStopReason = message.RawStopReason,
                errorMessage = message.ErrorMessage,
                diagnostics,
            });
        }

        var value = new
        {
            schemaVersion = BugReportSchemaVersion,
            sessionId = sessionManager.GetSessionId(),
            entryCount = entries.Count,
            assistantMessageCount,
            assistant,
            crashes = (crashes ?? []).Select(DescribeCrashRecord).ToList(),
        };
        return new BugReportDiagnostics(value, (crashes ?? []).Count);
    }

    /// <summary>TS <c>crashes.map(({ notified: _notified, ...record }) => record)</c>：逐字保留其余成员。</summary>
    private static object DescribeCrashRecord(CrashRecord record)
    {
        var members = new JsonObject();
        foreach (var (name, value) in record.Members ?? new Dictionary<string, JsonElement>())
        {
            if (name == "notified")
            {
                continue;
            }

            members[name] = JsonNode.Parse(value.GetRawText());
        }

        return members;
    }

    private static bool LooksLikeUrl(string text)
        => text.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
            || text.StartsWith("https://", StringComparison.OrdinalIgnoreCase);

    private static IFormatProvider CultureInfoInvariant() => System.Globalization.CultureInfo.InvariantCulture;

    /// <summary>报表 JSON：null 即省略，对齐 <c>JSON.stringify</c> 丢 undefined 键的行为。</summary>
    /// <remarks>对测试可见（InternalsVisibleTo）：测试按产物同样的选项断言信封形状。</remarks>
    internal static class ReportJson
    {
        public static readonly JsonSerializerOptions Options = new()
        {
            WriteIndented = true,
            // JSON.stringify always writes "\n"; .NET 9+ defaults to Environment.NewLine.
            NewLine = "\n",
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        };
    }

    /// <summary>
    /// Settings 的序列化选项（与 <see cref="SettingsManager"/> 的持久化形状一致）。
    /// </summary>
    /// <remarks>
    /// camelCase 命名策略是必需的：TS 的 <c>redactSettings</c> 按 <c>trackingId</c> /
    /// <c>deviceId</c> 解构摘键，少了策略这两个键就会以 PascalCase 漏进报表。
    /// </remarks>
    private static class SettingsJson
    {
        public static readonly JsonSerializerOptions Options = new()
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            PropertyNameCaseInsensitive = true,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        };
    }
}
