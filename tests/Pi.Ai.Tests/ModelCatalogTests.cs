using Pi.Ai.Models;
using Xunit;

namespace Pi.Ai.Tests;

/// <summary>ModelCatalog 加载与 flatten 测试。</summary>
public class ModelCatalogTests
{
    [Fact]
    public void LoadsFromEmbeddedResources()
    {
        var openai = ModelCatalog.LoadFromResource("openai");
        Assert.Equal("openai", openai.Provider);

        // flatten：跨 api 分组按模型 id 索引。目录取自上游 v1.4.0 的 providers/data。
        var gpt55 = openai.ChatModels["gpt-5.5"];
        Assert.Equal("openai-responses", gpt55.Api);
        Assert.Equal("https://api.openai.com/v1", gpt55.BaseUrl);
        Assert.True(gpt55.Reasoning);
        Assert.Equal(272000, gpt55.ContextWindow);

        // chat 是缺省类型（JSON 无 type 字段）。
        Assert.Equal(ModelType.Chat, gpt55.Type);
        Assert.Equal(42, openai.ChatModels.Count);
        Assert.Empty(openai.ImageModels);
        // openai-decisions 的分类模型落在独立桶里。
        Assert.Equal("openai-decisions", Assert.Single(openai.ClassifierModels.Values).Api);
    }

    /// <summary>42 份随包目录都必须能解析出模型，且 provider 键与文件名一致。</summary>
    [Fact]
    public void EveryEmbeddedCatalogParses()
    {
        var providers = Pi.Ai.Providers.All.GetBuiltinProviders();
        Assert.Equal(42, providers.Count);

        var empty = new List<string>();
        var total = 0;
        foreach (var provider in providers)
        {
            var catalog = ModelCatalog.LoadFromResource(provider);
            Assert.Equal(provider, catalog.Provider);
            var count = catalog.ChatModels.Count + catalog.ImageModels.Count + catalog.ClassifierModels.Count;
            total += count;
            if (count == 0) empty.Add(provider);
        }

        Assert.Empty(empty);
        // 上游 v1.4.0 的 42 份数据 flatten 后按 id 去重，总计 1713 个条目。
        Assert.Equal(1713, total);
    }

    [Fact]
    public void CrossApiFlattenAndFind()
    {
        var openai = ModelCatalog.LoadFromResource("openai");
        // gpt-5 在 openai-responses 分组，flatten 后与 completions 同表。
        var gpt5 = openai.ChatModels["gpt-5"];
        Assert.Equal("openai-responses", gpt5.Api);
        Assert.True(gpt5.Reasoning);
        Assert.Equal(400000, gpt5.ContextWindow);

        Assert.NotNull(openai.Find("o3"));
        Assert.Null(openai.Find("nonexistent"));
    }

    [Fact]
    public void AnthropicAndDeepseekCatalogs()
    {
        var anthropic = ModelCatalog.LoadFromResource("anthropic");
        var sonnet = anthropic.ChatModels["claude-sonnet-4-5"];
        Assert.Equal("anthropic-messages", sonnet.Api);
        Assert.True(sonnet.Reasoning);
        Assert.Equal(200000, sonnet.ContextWindow);
        Assert.Equal("anthropic", sonnet.Provider);

        var deepseek = ModelCatalog.LoadFromResource("deepseek");
        Assert.Equal(2, deepseek.ChatModels.Count);
        Assert.True(deepseek.ChatModels["deepseek-v4-pro"].Reasoning);
    }

    [Fact]
    public void TypeDispatchAndExtraPreserved()
    {
        var parsed = System.Text.Json.Nodes.JsonNode.Parse(
            """
            {
              "openai-completions": {
                "chat:m1": {"id":"m1","name":"M1","api":"openai-completions","provider":"p","baseUrl":"https://x","input":["text"],"contextWindow":1000,"maxTokens":100,"custom":"kept"},
                "image:d1": {"id":"d1","name":"D1","api":"openai-completions","provider":"p","baseUrl":"https://x","input":["text"],"type":"image","output":["image"]},
                "classifier:c1": {"id":"c1","name":"C1","api":"openai-completions","provider":"p","baseUrl":"https://x","input":["text"],"type":"classifier"}
              }
            }
            """);
        var groups = Assert.IsType<System.Text.Json.Nodes.JsonObject>(parsed);
        var catalog = ModelCatalog.Load("p", groups);
        Assert.Single(catalog.ChatModels);
        Assert.Single(catalog.ImageModels);
        Assert.Single(catalog.ClassifierModels);
        // 未识别字段保留。
        Assert.Equal("kept", catalog.ChatModels["m1"].Extra!["custom"]!.GetValue<string>());
    }

    [Fact]
    public void InputLimitsParsed()
    {
        var parsed = System.Text.Json.Nodes.JsonNode.Parse(
            """
            {
              "openai-completions": {
                "chat:m1": {
                  "id":"m1","name":"M1","api":"openai-completions","provider":"p","baseUrl":"https://x",
                  "input":["text","image"],
                  "inputLimits": {
                    "maxRequestBytes": 20971520,
                    "images": {
                      "resize": {"maxWidth": 1234, "maxHeight": 1000, "maxBytes": 500000, "jpegQuality": 70},
                      "maxPerMessage": 5,
                      "maxPerRequest": 8
                    }
                  }
                }
              }
            }
            """);
        var groups = Assert.IsType<System.Text.Json.Nodes.JsonObject>(parsed);
        var catalog = ModelCatalog.Load("p", groups);

        var model = catalog.ChatModels["m1"];
        Assert.NotNull(model.InputLimits);
        Assert.Equal(20971520, model.InputLimits.MaxRequestBytes);

        var images = model.InputLimits.Images;
        Assert.NotNull(images);
        Assert.Equal(5, images.MaxPerMessage);
        Assert.Equal(8, images.MaxPerRequest);

        var resize = images.Resize;
        Assert.NotNull(resize);
        Assert.Equal(1234, resize.MaxWidth);
        Assert.Equal(1000, resize.MaxHeight);
        Assert.Equal(500000, resize.MaxBytes);
        Assert.Equal(70, resize.JpegQuality);

        // inputLimits 已被消费，不再落 Extra。
        Assert.Null(model.Extra);
    }

    [Fact]
    public void InputLimitsAbsentYieldsNull()
    {
        var parsed = System.Text.Json.Nodes.JsonNode.Parse(
            """
            {
              "openai-completions": {
                "chat:m1": {"id":"m1","name":"M1","api":"openai-completions","provider":"p","baseUrl":"https://x","input":["text"]}
              }
            }
            """);
        var groups = Assert.IsType<System.Text.Json.Nodes.JsonObject>(parsed);
        var catalog = ModelCatalog.Load("p", groups);
        Assert.Null(catalog.ChatModels["m1"].InputLimits);
    }
}
