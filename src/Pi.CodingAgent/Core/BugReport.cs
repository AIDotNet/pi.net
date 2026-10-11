using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Pi.CodingAgent.Utils;

namespace Pi.CodingAgent.Core;

/// <summary>对应 TS <c>BugReportFile</c>。</summary>
public sealed record BugReportFile
{
    public required string Name { get; init; }

    public required string ContentType { get; init; }

    public required string Data { get; init; }
}

/// <summary>对应 TS <c>BugReportBundle</c> 的可序列化部分。</summary>
public sealed record BugReportBundle
{
    public required object Metadata { get; init; }

    public IReadOnlyList<BugReportFile> ExtraFiles { get; init; } = [];
}

/// <summary>
/// Bug report 脱敏与归档。对应 TS <c>core/bug-report.ts</c>。
/// <c>collectBugReport</c> 依赖 AgentSession，顺延 4e-3e。
/// </summary>
public static partial class BugReport
{
    private static readonly HashSet<string> SecretKeys = new(StringComparer.OrdinalIgnoreCase)
    {
        "apiKey", "api_key", "token", "accessToken", "refreshToken", "password", "secret",
        "authorization", "auth", "credential", "privateKey", "private_key",
    };

    [GeneratedRegex(@"([?&])(key|api[_-]?key|token|access[_-]?token|refresh[_-]?token|secret|password|auth)=([^&]*)", RegexOptions.IgnoreCase)]
    private static partial Regex SecretQueryRegex();

    /// <summary>对应 TS <c>redactUrl</c>。</summary>
    public static string RedactUrl(string url)
        => SecretQueryRegex().Replace(url, "$1$2=***");

    /// <summary>对应 TS <c>redactJsonValue</c>。</summary>
    public static JsonNode? RedactJsonValue(JsonNode? value, string? key = null)
    {
        if (key is not null && SecretKeys.Contains(key) && value is JsonValue)
        {
            return JsonValue.Create("***");
        }

        if (value is JsonObject obj)
        {
            var copy = new JsonObject();
            foreach (var (name, child) in obj)
            {
                copy[name] = RedactJsonValue(child?.DeepClone(), name);
            }

            return copy;
        }

        if (value is JsonArray array)
        {
            var copy = new JsonArray();
            foreach (var child in array)
            {
                copy.Add(RedactJsonValue(child?.DeepClone()));
            }

            return copy;
        }

        if (value is JsonValue jsonValue && jsonValue.TryGetValue<string>(out var text) && LooksLikeUrl(text))
        {
            return JsonValue.Create(RedactUrl(text));
        }

        return value?.DeepClone();
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
                Data = JsonSerializer.Serialize(bundle.Metadata, new JsonSerializerOptions { WriteIndented = true }) + "\n",
            },
        };
        files.AddRange(bundle.ExtraFiles);
        return files;
    }

    /// <summary>对应 TS <c>bugReportArchiveFileName</c>。</summary>
    public static string BugReportArchiveFileName(DateTimeOffset? now = null)
    {
        var stamp = (now ?? DateTimeOffset.UtcNow).ToString("yyyyMMdd-HHmmss", CultureInfoInvariant());
        return $"pi-bug-report-{stamp}.zip";
    }

    /// <summary>对应 TS <c>writeBugReportArchive</c>。</summary>
    public static async Task WriteBugReportArchiveAsync(string filePath, BugReportBundle bundle, CancellationToken cancellationToken = default)
    {
        var entries = BugReportFiles(bundle)
            .Select(file => new ZipEntry(file.Name, Encoding.UTF8.GetBytes(file.Data)))
            .ToList();
        await Zip.WriteArchiveAsync(filePath, entries, cancellationToken: cancellationToken).ConfigureAwait(false);
    }

    /// <summary>对应 TS <c>collectEnvironment</c> 的运行时快照（不含 AgentSession 字段）。</summary>
    public static object CollectEnvironment()
        => new
        {
            version = Config.Version,
            userAgent = PiUserAgent.GetPiUserAgent(Config.Version),
            platform = RuntimeInformation.OSDescription,
            arch = RuntimeInformation.OSArchitecture.ToString().ToLowerInvariant(),
            runtime = RuntimeInformation.FrameworkDescription,
        };

    private static bool LooksLikeUrl(string text)
        => text.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
            || text.StartsWith("https://", StringComparison.OrdinalIgnoreCase);

    private static IFormatProvider CultureInfoInvariant() => System.Globalization.CultureInfo.InvariantCulture;
}
