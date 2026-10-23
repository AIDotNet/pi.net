using Pi.Ai.Types;
using Pi.CodingAgent.Core;
using Xunit;

namespace Pi.CodingAgent.Tests;

/// <summary>
/// models.json 自定义 provider 的流式回归：api 指向内建 API 时，API 注册表必须在流式前就绪。
/// 对应真实缺陷（第五十三批发现）：注册只发生在 `CompatApi` 静态构造，而 CLI 流式路径
/// 不必然触碰它 —— 自定义 OpenAI 兼容端点全部抛 "No API provider registered"。
/// 上游在 compat.ts 模块加载即注册（compat.ts:215），端口在 `ProviderComposer.StreamWith`
/// 的注册表回退路径上显式确保。
/// </summary>
public class ProviderComposerCustomApiTests
{
    [Fact]
    public async Task CustomOpenAiCompatibleProviderCanStream()
    {
        var dir = Path.Combine(Path.GetTempPath(), "pi-net-e2e-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var modelsPath = Path.Combine(dir, "models.json");
        await File.WriteAllTextAsync(modelsPath, """
        {
          "providers": {
            "custom": {
              "name": "Custom",
              "baseUrl": "http://127.0.0.1:9/v1",
              "apiKey": "test-key",
              "api": "openai-completions",
              "models": [{ "id": "custom-1", "name": "Custom One" }]
            }
          }
        }
        """);

        var config = await ModelConfig.LoadAsync(modelsPath);
        Assert.True(config.Error is null, config.Error);
        var provider = ProviderComposer.ComposeModelProvider("custom", baseProvider: null, config, null);
        var model = Assert.Single(provider.GetModels(), m => m.Api == "openai-completions");

        var stream = provider.Stream(
            model,
            [new UserMessage([new TextContent("hi")], 1)],
            null);

        // 端点不可达（端口 9 连接拒绝）：错误必须落在连接层，而不是「API 未注册」。
        string errorText;
        try
        {
            var final = await stream.WaitForDoneAsync();
            errorText = final.ErrorMessage ?? "";
        }
        catch (Exception error)
        {
            errorText = error.Message;
        }

        Assert.DoesNotContain("No API provider registered", errorText);
    }
}
