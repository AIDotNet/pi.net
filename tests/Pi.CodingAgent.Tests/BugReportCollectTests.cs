// ============================================================================
// Tests for the bug-report collectors landed with 4f-4m-5c's remainder:
// core/bug-report.ts's collectBugReportMetadata / collectBugReportDiagnostics /
// bugReportFiles / bugReportArchiveFileName, plus the upload URL. The expected
// shapes are derived from the TS bodies (and, for the redaction inside the
// metadata, from tools/bug-report-redaction-reference.mjs).
// ============================================================================

using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Text.Json.Nodes;
using Pi.Ai.Auth;
using Pi.Ai.Models;
using Pi.Ai.Types;
using Pi.CodingAgent.Core;
using Xunit;

namespace Pi.CodingAgent.Tests;

public sealed class BugReportCollectTests
{
    private static async Task<ModelRuntime> CreateRuntimeAsync()
        => await ModelRuntime.CreateAsync(new CreateModelRuntimeOptions
        {
            Credentials = new InMemoryCredentialStore(),
            ModelsPathDisabled = true,
            ModelsStore = new InMemoryCodingAgentModelsStore(),
            RefreshOnCreate = false,
            Env = name => name == "PI_OFFLINE" ? "1" : null,
        });

    private static ModelSpec Model() => new()
    {
        Id = "stub-chat",
        Name = "stub-chat",
        Api = "stub-api",
        Provider = "stub",
        BaseUrl = "https://stub.invalid",
    };

    private static CollectBugReportMetadataOptions Options(
        ModelRuntime runtime,
        string? hint = "  it broke  ",
        bool includeSession = true,
        bool includeSummary = false,
        int messageCount = 3)
        => new()
        {
            Hint = hint,
            SessionId = "session-1",
            Cwd = "/work/dir",
            IncludeSession = includeSession,
            IncludeSummary = includeSummary,
            MessageCount = messageCount,
            Model = Model(),
            ModelRuntime = runtime,
            ThinkingLevel = ThinkingLevel.Medium,
            Extensions = [],
            ExtensionErrors = [],
            GlobalSettings = new Settings { DeviceId = "device-1", TrackingId = "track-1", DefaultModel = "stub-chat" },
            ProjectSettings = new Settings(),
        };

    [Fact]
    public async Task CollectBugReportMetadata_ShapesTheReportEnvelope()
    {
        // TS 读 process.env；端口同此，所以测试也要设真实的进程环境变量。
        Environment.SetEnvironmentVariable("PI_OFFLINE", "1");
        try
        {
            var runtime = await CreateRuntimeAsync();
            var metadata = BugReport.CollectBugReportMetadata(Options(runtime));

            var json = (JsonObject)JsonSerializer.SerializeToNode(metadata.Value, BugReport.ReportJson.Options)!;
            Assert.Equal(1, json["schemaVersion"]!.GetValue<int>());
            Assert.Equal(metadata.Id, json["id"]!.GetValue<string>());
            Assert.Equal(metadata.CreatedAt, json["createdAt"]!.GetValue<string>());
            // TS 的 hint 是 trim 后的值；空串按 || 语义变 null。
            Assert.Equal("it broke", json["hint"]!.GetValue<string>());
            Assert.EndsWith("Z", metadata.CreatedAt, StringComparison.Ordinal);

            var session = json["session"]!.AsObject();
            Assert.Equal("session-1", session["id"]!.GetValue<string>());
            Assert.True(session["included"]!.GetValue<bool>());
            Assert.False(session["summaryIncluded"]!.GetValue<bool>());
            Assert.Equal(3, session["messageCount"]!.GetValue<int>());
            Assert.Equal("/work/dir", session["cwd"]!.GetValue<string>());

            var model = json["model"]!.AsObject();
            Assert.Equal("stub", model["provider"]!.GetValue<string>());
            Assert.Equal("stub-chat", model["id"]!.GetValue<string>());
            Assert.Equal("https://stub.invalid", model["baseUrl"]!.GetValue<string>());

            var environment = json["environment"]!.AsObject();
            Assert.False(string.IsNullOrEmpty(environment["version"]!.GetValue<string>()));
            Assert.Contains(environment["piEnvironmentVariables"]!.AsArray(), node => node!.GetValue<string>() == "PI_OFFLINE");
        }
        finally
        {
            Environment.SetEnvironmentVariable("PI_OFFLINE", null);
        }
    }

    [Fact]
    public async Task CollectBugReportMetadata_OmitsTheCwdWhenTheTranscriptIsNotShared()
    {
        var runtime = await CreateRuntimeAsync();
        var metadata = BugReport.CollectBugReportMetadata(Options(runtime, hint: "   ", includeSession: false));

        var json = (JsonObject)JsonSerializer.SerializeToNode(metadata.Value, BugReport.ReportJson.Options)!;
        var session = json["session"]!.AsObject();
        Assert.False(session.ContainsKey("cwd"));
        Assert.Null(json["hint"]);
        Assert.Null(metadata.Hint);
    }

    [Fact]
    public async Task CollectBugReportMetadata_RedactsTheSettings()
    {
        var runtime = await CreateRuntimeAsync();
        var metadata = BugReport.CollectBugReportMetadata(Options(runtime));

        var json = (JsonObject)JsonSerializer.SerializeToNode(metadata.Value, BugReport.ReportJson.Options)!;
        var global = json["settings"]!["global"]!.AsObject();
        // trackingId / deviceId 被整键摘除（键名是 camelCase，与 settings.json 一致）。
        Assert.False(global.ContainsKey("trackingId"));
        Assert.False(global.ContainsKey("deviceId"));
        Assert.Equal("stub-chat", global["defaultModel"]!.GetValue<string>());
    }

    [Fact]
    public async Task CollectBugReportDiagnostics_CollectsOnlyFailedAssistantTurns()
    {
        var manager = SessionManager.InMemory("/tmp");
        manager.AppendMessage(new UserMessage(
            MessageContent.BlocksOf([new TextContent("hello")]),
            1));
        manager.AppendMessage(new AssistantMessage(
            [],
            StopReason.Error,
            ErrorMessage: "boom",
            Model: "stub-chat",
            Api: "stub-api",
            Provider: "stub"));
        manager.AppendMessage(new AssistantMessage(
            [],
            StopReason.Stop,
            Model: "stub-chat",
            Api: "stub-api",
            Provider: "stub"));
        manager.AppendMessage(new AssistantMessage(
            [],
            StopReason.Aborted,
            Model: "stub-chat",
            Api: "stub-api",
            Provider: "stub"));

        var diagnostics = BugReport.CollectBugReportDiagnostics(manager);
        var json = (JsonObject)JsonSerializer.SerializeToNode(diagnostics.Value, BugReport.ReportJson.Options)!;
        Assert.Equal(manager.GetEntries().Count, json["entryCount"]!.GetValue<int>());
        Assert.Equal(3, json["assistantMessageCount"]!.GetValue<int>());
        var assistant = json["assistant"]!.AsArray();
        // error 与 aborted 两轮入选，stop 一轮不入选。
        Assert.Equal(2, assistant.Count);
        Assert.Equal("boom", assistant[0]!["errorMessage"]!.GetValue<string>());
        Assert.Equal("error", assistant[0]!["stopReason"]!.GetValue<string>());
        Assert.Equal("aborted", assistant[1]!["stopReason"]!.GetValue<string>());
        // TS 的条件展开：rawStopReason 为 undefined 时整个键不出现。
        Assert.False(assistant[0]!.AsObject().ContainsKey("rawStopReason"));
        Assert.Equal(0, diagnostics.CrashCount);
    }

    [Fact]
    public void CollectBugReportDiagnostics_DropsTheNotifiedCrashMember()
    {
        // TS 的 destructure `({ notified: _notified, ...record }) => record` 只从内存里的记录摘键，
        // 所以先走真实的 Record 写入路径，再用 WithNotified 打上 notified 标记。
        var path = Path.Combine(Path.GetTempPath(), "pi-crash-test-" + Guid.NewGuid().ToString("N") + ".json");
        var recorded = CrashLog.Record(new CrashInput
        {
            Kind = CrashKind.UncaughtException,
            Error = new InvalidOperationException("kaput"),
            Cwd = "/tmp",
        }, path);
        Assert.NotNull(recorded);
        var crash = recorded.WithNotified();
        Assert.True(crash.Notified);

        var manager = SessionManager.InMemory("/tmp");
        var diagnostics = BugReport.CollectBugReportDiagnostics(manager, [crash]);
        var json = (JsonObject)JsonSerializer.SerializeToNode(diagnostics.Value, BugReport.ReportJson.Options)!;
        var crashes = json["crashes"]!.AsArray();
        Assert.Single(crashes);
        Assert.False(crashes[0]!.AsObject().ContainsKey("notified"));
        Assert.Equal("kaput", crashes[0]!["message"]!.GetValue<string>());
        // 其余成员（timestamp/version/kind/stack/sessionFile/cwd）逐字保留。
        Assert.True(crashes[0]!.AsObject().ContainsKey("timestamp"));
        Assert.True(crashes[0]!.AsObject().ContainsKey("cwd"));
        Assert.Equal(1, diagnostics.CrashCount);
    }

    [Fact]
    public void BugReportFiles_ListsTheConditionalFiles()
    {
        var bundle = new BugReportBundle
        {
            Metadata = new { schemaVersion = 1 },
            Diagnostics = new { schemaVersion = 1 },
        };
        var names = BugReport.BugReportFiles(bundle).Select(file => file.Name).ToList();
        Assert.Equal(["report.json", "diagnostics.json"], names);

        var withSession = bundle with { SessionJsonl = "{}\n", Summary = "done" };
        names = BugReport.BugReportFiles(withSession).Select(file => file.Name).ToList();
        Assert.Equal(["report.json", "diagnostics.json", "session.jsonl", "summary.md"], names);

        // summary.md 只在缺结尾换行时补一个。
        var summary = BugReport.BugReportFiles(withSession).Last();
        Assert.Equal("done\n", summary.Data);
        var summaryWithNewline = BugReport.BugReportFiles(withSession with { Summary = "done\n" }).Last();
        Assert.Equal("done\n", summaryWithNewline.Data);
    }

    [Fact]
    public void BugReportArchiveFileName_CarriesTheReportId()
        => Assert.Equal("pi-bug-report-abc123.zip", BugReport.BugReportArchiveFileName("abc123"));

    [Fact]
    public void UploadUrl_TargetsTheGatewayBugReportsEndpoint()
        => Assert.EndsWith("/v1/bug-reports", BugReportUpload.UploadUrl(), StringComparison.Ordinal);

    private static BugReportBundle Bundle() => new()
    {
        Metadata = new { schemaVersion = 1 },
        Diagnostics = new { schemaVersion = 1 },
        SessionJsonl = "{}\n",
        Summary = "done\n",
    };

    private static HttpResponseMessage JsonResponse(string body, HttpStatusCode status = HttpStatusCode.OK)
    {
        var response = new HttpResponseMessage(status);
        response.Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json");
        return response;
    }

    private sealed class StubHandler(Func<HttpRequestMessage, Task<HttpResponseMessage>> respond) : HttpMessageHandler
    {
        public List<HttpRequestMessage> Requests { get; } = [];

        /// <summary>发送时读到的请求正文（发送后 content 即被释放，只能在 handler 内捕获）。</summary>
        public List<string> Bodies { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(request);
            if (request.Content is not null)
            {
                Bodies.Add(await request.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false));
            }

            return await respond(request).ConfigureAwait(false);
        }
    }

    [Fact]
    public async Task UploadBugReport_PostsEveryFileUnderItsOwnName()
    {
        var handler = new StubHandler(_ => Task.FromResult(JsonResponse(
            """{"ok":true,"bug_report":{"id":"rpt-1"}}""")));

        var result = await BugReportUpload.UploadBugReportAsync(
            Bundle(),
            new BugReportUpload.Options { Token = "tok" },
            httpClient: new HttpClient(handler));

        Assert.Equal("rpt-1", result.Id);
        var request = Assert.Single(handler.Requests);
        Assert.Equal(HttpMethod.Post, request.Method);
        Assert.Equal("/v1/bug-reports", request.RequestUri!.AbsolutePath);
        Assert.Equal("Bearer", request.Headers.Authorization?.Scheme);
        Assert.Equal("tok", request.Headers.Authorization?.Parameter);

        Assert.IsType<MultipartFormDataContent>(request.Content);
        // 直接读序列化后的多部分正文：TS body.append(file.name, blob, file.name) 的
        // 字段名与文件名同为 file.name。
        var body = Assert.Single(handler.Bodies);
        // TS body.append(file.name, blob, file.name) 的字段名与文件名同为 file.name；
        // .NET 对合法 token 不加引号地序列化（RFC 2183 两者等价）。
        Assert.Contains("name=report.json; filename=report.json", body, StringComparison.Ordinal);
        Assert.Contains("name=diagnostics.json; filename=diagnostics.json", body, StringComparison.Ordinal);
        Assert.Contains("name=session.jsonl; filename=session.jsonl", body, StringComparison.Ordinal);
        Assert.Contains("name=summary.md; filename=summary.md", body, StringComparison.Ordinal);
        Assert.DoesNotContain("name=files", body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task UploadBugReport_OmitsTheAuthorizationHeaderWithoutAToken()
    {
        var handler = new StubHandler(_ => Task.FromResult(JsonResponse(
            """{"ok":true,"bug_report":{"id":"rpt-2"}}""")));

        var result = await BugReportUpload.UploadBugReportAsync(
            Bundle(),
            httpClient: new HttpClient(handler));

        Assert.Equal("rpt-2", result.Id);
        Assert.Null(Assert.Single(handler.Requests).Headers.Authorization);
    }

    [Fact]
    public async Task UploadBugReport_FailureMessagePrefersDescriptionThenError()
    {
        var handler = new StubHandler(_ => Task.FromResult(JsonResponse(
            """{"ok":false,"description":"why","error":"boom"}""", HttpStatusCode.BadRequest)));

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            BugReportUpload.UploadBugReportAsync(Bundle(), httpClient: new HttpClient(handler)));

        // TS `json.description || json.error`：description 优先，error 兜底。
        Assert.Contains("why", error.Message);
        Assert.DoesNotContain("boom", error.Message);
    }

    [Fact]
    public async Task UploadBugReport_FailureMessageFallsBackToErrorThenStatusText()
    {
        var withError = new StubHandler(_ =>
        {
            var response = JsonResponse("""{"ok":false,"error":"boom"}""", HttpStatusCode.BadRequest);
            return Task.FromResult(response);
        });
        var errorOnly = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            BugReportUpload.UploadBugReportAsync(Bundle(), httpClient: new HttpClient(withError)));
        Assert.Contains("boom", errorOnly.Message);

        var withStatus = new StubHandler(_ =>
        {
            var response = JsonResponse("not json at all", HttpStatusCode.InternalServerError);
            response.ReasonPhrase = "Server exploded";
            return Task.FromResult(response);
        });
        var statusOnly = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            BugReportUpload.UploadBugReportAsync(Bundle(), httpClient: new HttpClient(withStatus)));
        Assert.Contains("Server exploded", statusOnly.Message);
    }
}
