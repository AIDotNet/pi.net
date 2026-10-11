using System.Net.Http.Headers;
using System.Text;

namespace Pi.CodingAgent.Core;

/// <summary>对应 TS <c>core/bug-report-upload.ts</c>。</summary>
public static class BugReportUpload
{
    /// <summary>对应 TS 的 <c>${getRadiusGatewayUrl()}/v1/pi/bug-reports</c>。</summary>
    public static string UploadUrl() => $"{Radius.GetGatewayUrl()}/v1/pi/bug-reports";

    /// <summary>把 bug report 文件 POST 到 Radius 网关。对应 TS <c>uploadBugReport</c>。</summary>
    public static async Task<HttpResponseMessage> UploadBugReportAsync(
        IReadOnlyList<BugReportFile> files,
        HttpClient? httpClient = null,
        CancellationToken cancellationToken = default)
    {
        var owned = httpClient is null;
        var client = httpClient ?? new HttpClient();
        try
        {
            using var content = new MultipartFormDataContent();
            foreach (var file in files)
            {
                var bytes = new ByteArrayContent(Encoding.UTF8.GetBytes(file.Data));
                bytes.Headers.ContentType = MediaTypeHeaderValue.Parse(file.ContentType);
                content.Add(bytes, "files", file.Name);
            }

            return await client.PostAsync(UploadUrl(), content, cancellationToken).ConfigureAwait(false);
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
