using System.Net.Http.Headers;
using System.Text;
using System.Text.Json.Nodes;
using Pi.Ai.Auth.OAuth;

namespace Pi.CodingAgent.Core;

/// <summary>对应 TS <c>core/bug-report-upload.ts</c>。</summary>
public static class BugReportUpload
{
    /// <summary>对应 TS 的 <c>new URL("/v1/bug-reports", getRadiusGatewayUrl())</c>。</summary>
    public static string UploadUrl() => $"{Radius.GetGatewayUrl()}/v1/bug-reports";

    /// <summary>对应 TS <c>UploadBugReportOptions</c>。</summary>
    public sealed record Options(string? Token = null, string? GatewayUrl = null);

    /// <summary>对应 TS <c>uploadBugReport</c> 的返回：报表 id。</summary>
    public sealed record Result(string Id);

    /// <summary>把 bug report 文件 POST 到 Radius 网关。对应 TS <c>uploadBugReport</c>。</summary>
    public static async Task<Result> UploadBugReportAsync(
        BugReportBundle bundle,
        Options? options = null,
        HttpClient? httpClient = null,
        CancellationToken cancellationToken = default)
    {
        options ??= new Options();
        var url = options.GatewayUrl is null
            ? UploadUrl()
            : $"{RadiusConfig.NormalizeGatewayUrl(options.GatewayUrl)}/v1/bug-reports";
        var owned = httpClient is null;
        var client = httpClient ?? new HttpClient();
        try
        {
            using var content = new MultipartFormDataContent();
            foreach (var file in BugReport.BugReportFiles(bundle))
            {
                var bytes = new ByteArrayContent(Encoding.UTF8.GetBytes(file.Data));
                bytes.Headers.ContentType = MediaTypeHeaderValue.Parse(file.ContentType);
                // TS body.append(file.name, new Blob([file.data], { type: file.contentType }), file.name)：
                // 表单字段名与文件名同为 file.name。
                content.Add(bytes, file.Name, file.Name);
            }

            using var request = new HttpRequestMessage(HttpMethod.Post, url) { Content = content };
            if (!string.IsNullOrEmpty(options.Token))
            {
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", options.Token);
            }

            var response = await client.SendAsync(request, cancellationToken).ConfigureAwait(false);
            var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            JsonNode? json = null;
            try
            {
                json = JsonNode.Parse(body);
            }
            catch
            {
                // TS .catch(() => null)
            }

            var id = json?["bug_report"]?["id"]?.GetValue<string>();
            if (response.IsSuccessStatusCode && json?["ok"]?.GetValue<bool>() == true && id is not null)
            {
                return new Result(id);
            }

            // TS: `json && !json.ok ? json.description || json.error : undefined`.
            var detail = json is JsonObject obj && obj["ok"]?.GetValue<bool>() != true
                ? obj["description"]?.GetValue<string>() ?? obj["error"]?.GetValue<string>()
                : null;
            throw new InvalidOperationException(
                $"Bug report upload failed: {detail ?? response.ReasonPhrase ?? response.StatusCode.ToString()}");
        }
        finally
        {
            if (owned)
            {
                client.Dispose();
            }
        }
    }
}
