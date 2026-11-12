using Pi.Agent;
using Pi.Agent.Types;
using Pi.Ai.Auth;
using Pi.Ai.Providers;
using Pi.Ai.Stream;
using Pi.Ai.Types;
using Pi.CodingAgent.Core;
using Pi.CodingAgent.Core.Extensions;
using SkiaSharp;
using Xunit;

namespace Pi.CodingAgent.Tests;

/// <summary>
/// #11738（v1.5.1，上游 4cf69332c）：空闲提示词的图片走 processImage 归一化（缩放到模型档位 +
/// 追加坐标提示），但 agent 忙时排队的 steering / follow-up 消息里的图片跳过了这步，按原尺寸
/// 发送并落库（例如 1200x9000 PNG 超过 Anthropic 8000px 限制，且每次后续请求都重放原图）。
/// 修复：入队时登记带图的消息，投递（message_start）时原地归一化，让扩展/监听器/落库/请求
/// 上下文都看到与空闲提示词一致的缩放结果。
/// </summary>
/// <remarks>
/// 对照 TS <c>test/agent-session-queued-images.test.ts</c>：用一张超过默认 2000px 上限的 PNG，
/// 经 steer / followUp 排队，投递后断言落库/上下文里的图片已非原图且文本追加了缩放提示。
/// 无修复时这些消息保留原图且无提示，用例失败。
/// </remarks>
public sealed class AgentSessionQueuedImagesTests : IDisposable
{
    private readonly string _root;
    private readonly string _cwd;
    private readonly string _agentDir;

    public AgentSessionQueuedImagesTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "pi-queued-images-" + Guid.NewGuid().ToString("N"));
        _cwd = Path.Combine(_root, "cwd");
        _agentDir = Path.Combine(_cwd, ".pi");
        Directory.CreateDirectory(_agentDir);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch
        {
            // 临时目录清理失败不影响测试结论。
        }
    }

    // ------------------------------------------------------------------ helpers

    /// <summary>一张纯灰、高度超过默认 2000px 上限的 PNG（触发缩放），base64 编码。</summary>
    private static string TallPng(int width, int height)
    {
        using var bitmap = new SKBitmap(width, height, SKColorType.Rgba8888, SKAlphaType.Premul);
        using var canvas = new SKCanvas(bitmap);
        canvas.Clear(new SKColor(0x80, 0x80, 0x80));
        using var image = SKImage.FromBitmap(bitmap);
        using var data = image.Encode(SKEncodedImageFormat.Png, 100);
        return Convert.ToBase64String(data.ToArray());
    }

    private async Task<AgentSession> CreateSessionAsync(StreamFn streamFn)
    {
        var settings = SettingsManager.Create(_cwd, _agentDir);
        var loader = new DefaultResourceLoader(
            _cwd,
            _agentDir,
            settings,
            noSkills: true,
            noPromptTemplates: true,
            noThemes: true);
        await loader.ReloadAsync();
        var modelRuntime = await ModelRuntime.CreateAsync(new CreateModelRuntimeOptions
        {
            Credentials = new InMemoryCredentialStore(),
            ModelsPathDisabled = true,
            ModelsStore = new InMemoryCodingAgentModelsStore(),
            RefreshOnCreate = false,
            Env = name => name == "PI_OFFLINE" ? "1" : null,
            AuthContext = new StubAuthContext(name => name == "PI_OFFLINE" ? "1" : null),
        });
        var agent = new Pi.Agent.Agent(new AgentOptions { StreamFn = streamFn });
        var runnerRef = new ExtensionRunnerRef();
        var session = new AgentSession(new AgentSessionConfig
        {
            Agent = agent,
            SessionManager = SessionManager.InMemory(_cwd),
            SettingsManager = settings,
            Cwd = _cwd,
            ResourceLoader = loader,
            ModelRuntime = modelRuntime,
            InitialActiveToolNames = [],
            ExtensionRunnerRef = runnerRef,
        });
        Assert.NotNull(runnerRef.Current);
        return session;
    }

    /// <summary>按首段文本前缀从 agent transcript 里找出排队投递的用户消息。</summary>
    private static UserMessage FindQueuedUser(AgentSession session, string prefix) =>
        session.Agent.Messages
            .OfType<UserMessage>()
            .Single(m => m.Content.ToBlocks()
                .OfType<TextContent>()
                .Any(t => t.Text.StartsWith(prefix, StringComparison.Ordinal)));

    private static void AssertNormalized(UserMessage message, string originalBase64, string originalDimensions)
    {
        var blocks = message.Content.ToBlocks();
        var image = Assert.Single(blocks.OfType<ImageContent>());
        // 图片经 processImage 归一化（缩放 + 重编码），数据不再是原图。
        Assert.NotEqual(originalBase64, image.Data);
        var text = string.Join("\n", blocks.OfType<TextContent>().Select(t => t.Text));
        // 缩放提示：formatDimensionNote 产出 "original WxH, displayed at ..."。
        Assert.Contains($"original {originalDimensions}", text);
        Assert.Contains("displayed at", text);
    }

    // ------------------------------------------------------------------ tests

    [Fact]
    public async Task Steer_WithOversizedImage_IsResizedAtDelivery()
    {
        var big = TallPng(100, 3000);
        var session = await CreateSessionAsync(
            Faux.CreateStreamFn([Faux.Response.Text("hello")]));

        await session.SteerAsync("queued steer", [new ImageContent(big, "image/png")]);
        await session.RunAgentPromptAsync(new UserMessage("idle", 0));

        AssertNormalized(FindQueuedUser(session, "queued steer"), big, "100x3000");
    }

    [Fact]
    public async Task FollowUp_WithOversizedImage_IsResizedAtDelivery()
    {
        var big = TallPng(100, 3000);
        var session = await CreateSessionAsync(
            Faux.CreateStreamFn([Faux.Response.Text("first"), Faux.Response.Text("second")]));

        await session.FollowUpAsync("queued follow up", [new ImageContent(big, "image/png")]);
        await session.RunAgentPromptAsync(new UserMessage("idle", 0));

        AssertNormalized(FindQueuedUser(session, "queued follow up"), big, "100x3000");
    }

    [Fact]
    public async Task IdlePrompt_IsNotDoubleNormalized()
    {
        // 空闲提示词不是排队消息，不应被 _queuedMessagesWithImages 命中（投递路径只处理排队项）。
        var big = TallPng(100, 3000);
        var session = await CreateSessionAsync(
            Faux.CreateStreamFn([Faux.Response.Text("hello")]));

        await session.SteerAsync("queued steer", [new ImageContent(big, "image/png")]);
        await session.RunAgentPromptAsync(new UserMessage("idle", 0));

        // 空闲的 "idle" 消息保持原始文本（无缩放提示），证明归一化只作用于登记的排队消息。
        var idle = FindQueuedUser(session, "idle");
        var idleText = string.Join("\n", idle.Content.ToBlocks().OfType<TextContent>().Select(t => t.Text));
        Assert.DoesNotContain("displayed at", idleText);
    }
}
