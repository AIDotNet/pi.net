using Pi.Ai.Models;
using Pi.Ai.Utils;
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

        // flatten：跨 api 分组按模型 id 索引。目录由上游 catalog/generate.ts 导出
        // （见 tools/catalog-export.mjs）。
        var gpt55 = openai.ChatModels["gpt-5.5"];
        Assert.Equal("openai-responses", gpt55.Api);
        Assert.Equal("https://api.openai.com/v1", gpt55.BaseUrl);
        Assert.True(gpt55.Reasoning);
        Assert.Equal(272000, gpt55.ContextWindow);

        // chat 是缺省类型（JSON 无 type 字段）。
        Assert.Equal(ModelType.Chat, gpt55.Type);
        // 未发布批补入 GPT-6.2 Astra/Sol → 46。
        Assert.Equal(46, openai.ChatModels.Count);
        Assert.Empty(openai.ImageModels);
        // openai-decisions 的分类模型落在独立桶里。
        Assert.Equal("openai-decisions", Assert.Single(openai.ClassifierModels.Values).Api);
    }

    /// <summary>43 份随包目录都必须能解析出模型，且 provider 键与文件名一致。</summary>
    [Fact]
    public void EveryEmbeddedCatalogParses()
    {
        var providers = Pi.Ai.Providers.All.GetBuiltinProviders();
        Assert.Equal(43, providers.Count);

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
        // 上游 v1.4.0 的 42 份数据 flatten 后按 id 去重 1713 个条目；1.4.4 补入
        // GPT-6.1 Terra（openai / openai-codex / azure 三条），总计 1716。
        // 1.5.0 批重新导出后为 1718。1.5.2 补入 qwen-token-plan-individual-cn（10 条）、
        // qwen-token-plan-individual 的 glm-5.3、anthropic 的 claude-fable-5-2，共 +12 → 1730。
        // 未发布批：GPT-6.2 Astra/Sol 在 openai/azure/openai-codex 各 2 条（+6）、
        // Qwen Token Plan Team 去掉 2 个 deprecated（-4）并补 qwen4-max-preview（+2）、
        // Individual 补 deepseek-v4.1-flash/1017 与 qwen4-max-preview（+6）→ 1740。
        Assert.Equal(1740, total);
    }

    /// <summary>
    /// 目录数据的关键字段必须齐全。上一批只断言了「能解析」，但字段名拼错（如 <c>contextwindow</c>）
    /// 会静默解析成 0 / 空串而不报错——那条断言抓不到。
    /// </summary>
    [Fact]
    public void EveryChatModelKeepsItsCoreFields()
    {
        var incomplete = new List<string>();
        var total = 0;
        foreach (var provider in Pi.Ai.Providers.All.GetBuiltinProviders())
        {
            foreach (var model in ModelCatalog.LoadFromResource(provider).ChatModels.Values)
            {
                total++;
                // baseUrl 不在此列：azure 的端点由部署名决定，目录里本就没有。
                if (model.Api.Length == 0
                    || model.Name.Length == 0
                    || model.Input.Count == 0
                    || model.Cost is null
                    || model.ContextWindow == 0)
                {
                    incomplete.Add($"{provider}/{model.Id}");
                }
            }
        }

        Assert.Empty(incomplete);
        // chat 桶 1602 → 1598：openrouter 的 openai-completions 组净减 16（+10 / -26），
        // 其余 provider 净增 12。总量反而涨了 2——image / classifier 桶加得更多。
        // 1.5.2 新增 12 个 chat 模型 → 1610；未发布批 +10 → 1620。
        Assert.Equal(1620, total);
    }

    /// <summary>
    /// 上游 1.5.1 #7961：TEMPERATURE_UNSUPPORTED 从 "sonnet-5-5" 收宽为 "sonnet-5"，
    /// 所有 Claude Sonnet 5（含 5.5 与 :batch 变体）的模型都不支持 temperature。
    /// </summary>
    [Fact]
    public void ClaudeSonnet5ModelsDoNotSupportTemperature()
    {
        var checkedCount = 0;
        foreach (var provider in Pi.Ai.Providers.All.GetBuiltinProviders())
        {
            foreach (var (id, model) in ModelCatalog.LoadFromResource(provider).ChatModels)
            {
                var lowered = id.ToLowerInvariant();
                if (!lowered.Contains("sonnet-5", StringComparison.Ordinal)
                    && !lowered.Contains("sonnet.5", StringComparison.Ordinal)) continue;
                Assert.NotNull(model.Compat);
                Assert.Equal(false, model.Compat!.Bool("supportsTemperature"));
                checkedCount++;
            }
        }
        Assert.True(checkedCount > 0, "没有命中任何 sonnet-5 模型，目录数据结构可能已变化");
    }

    /// <summary>
    /// 上游 1.5.1 #11742 / #11948：Azure GPT-6 用 Foundry 的 1.05M 上下文，并保留长上下文价档。
    /// </summary>
    [Fact]
    public void AzureGptSixModelsUseFoundryWindowAndLongContextTier()
    {
        var azure = ModelCatalog.LoadFromResource("azure");
        foreach (var id in new[] { "gpt-6-astra", "gpt-6-luna", "gpt-6-sol", "gpt-6.1-sol", "gpt-6.1-terra" })
        {
            var model = azure.ChatModels[id];
            Assert.Equal(1050000, model.ContextWindow);
            Assert.NotNull(model.Cost);
            Assert.True(model.Cost!.Tiers is { Count: > 0 }, $"{id} 缺长上下文价档");
        }
    }

    /// <summary>上游 1.5.1：新增 provider / 模型 / allowlist / 默认模型切换逐项可查。</summary>
    [Fact]
    public void Version152CatalogAdditionsArePresent()
    {
        // #7989：CN 端点上的 Individual provider，模型来自 cn 目录 ∩ Individual allowlist。
        var individualCn = ModelCatalog.LoadFromResource("qwen-token-plan-individual-cn");
        // 未发布批把 Individual 扩到 13 条（deepseek-v4.1-flash/1017 + qwen4-max-preview）。
        Assert.Equal(13, individualCn.ChatModels.Count);
        Assert.Equal("qwen-token-plan-individual-cn", individualCn.Provider);
        Assert.All(individualCn.ChatModels.Values, m => Assert.Equal("qwen-token-plan-individual-cn", m.Provider));
        Assert.Contains("qwen3.8-max", individualCn.ChatModels.Keys);
        Assert.Contains("glm-5.3", individualCn.ChatModels.Keys);

        // #11658：GLM 5.3 加入 Individual allowlist。
        var individual = ModelCatalog.LoadFromResource("qwen-token-plan-individual");
        Assert.Contains("glm-5.3", individual.ChatModels.Keys);

        // 1.5.1 内联新增 Claude Fable 5.2；finalize 规则带来 mid-convo effort 与 fallback。
        var anthropic = ModelCatalog.LoadFromResource("anthropic");
        var fable = anthropic.ChatModels["claude-fable-5-2"];
        Assert.Equal(1000000, fable.ContextWindow);
        Assert.Equal(8, fable.Cost!.Input);
        Assert.True(fable.Compat!.Bool("supportsMidConvoEffort"));
        Assert.True(fable.Compat!.Bool("supportsMidConvoSystemMessages"));
    }

    /// <summary>未发布批：GPT-6.2 双模型、Qwen Token Plan 网关/清单、Copilot strict 兼容位。</summary>
    [Fact]
    public void UnreleasedCatalogAdditionsArePresent()
    {
        // e56d35ce5 + cead615b4：GPT-6.2 Astra/Sol，Sol 为三家默认模型。
        var openai = ModelCatalog.LoadFromResource("openai");
        var sol = openai.ChatModels["gpt-6.2-sol"];
        Assert.Equal("GPT-6.2 Sol", sol.Name);
        Assert.Equal(2, sol.Cost!.Input);
        Assert.Equal(10, sol.Cost.Output);
        Assert.Equal(272000, sol.ContextWindow);
        Assert.Null(sol.ThinkingLevelMap!["off"]);
        Assert.Null(sol.ThinkingLevelMap["minimal"]);
        Assert.True(sol.Compat!.Bool("supportsToolSearch"));
        Assert.True(sol.Compat!.Bool("supportsMidConvoEffort"));
        var astra = openai.ChatModels["gpt-6.2-astra"];
        Assert.Equal(8, astra.Cost!.Input);
        Assert.Equal(40, astra.Cost.Output);
        Assert.True(astra.Cost.Tiers is { Count: 1 });
        Assert.Equal(16, astra.Cost.Tiers![0].Input);

        // Azure 用 Foundry 的 1.05M 窗口；Codex 用 /models 的默认值（Sol 500K、Astra 272K）。
        Assert.Equal(1050000, ModelCatalog.LoadFromResource("azure").ChatModels["gpt-6.2-sol"].ContextWindow);
        var codex = ModelCatalog.LoadFromResource("openai-codex");
        Assert.Equal(500000, codex.ChatModels["gpt-6.2-sol"].ContextWindow);
        Assert.Equal(272000, codex.ChatModels["gpt-6.2-astra"].ContextWindow);
        Assert.True(codex.ChatModels["gpt-6.2-sol"].Compat!.Bool("supportsAdditionalTools"));

        // fcc33744b：GPT-6 家族的档位改走 configuration_update。目录必须带标志，
        // 否则 GetConfigurationUpdateEffort 恒为 null，修复形同虚设（Codex 曾缺这一条）。
        foreach (var id in new[]
                 {
                     "gpt-6-astra", "gpt-6-sol", "gpt-6-luna",
                     "gpt-6.1-sol", "gpt-6.1-terra", "gpt-6.2-astra", "gpt-6.2-sol",
                 })
        {
            Assert.True(openai.ChatModels[id].Compat!.Bool("supportsMidConvoEffort"), id);
            Assert.True(codex.ChatModels[id].Compat!.Bool("supportsMidConvoEffort"), id);
        }

        // 12112：四个 Token Plan provider 迁到新网关；Team 清单 20 条（含 qwen4-max-preview）。
        var team = ModelCatalog.LoadFromResource("qwen-token-plan");
        Assert.Equal(20, team.ChatModels.Count);
        Assert.All(team.ChatModels.Values, m =>
            Assert.Equal("https://token-plan.maas.qwencloudapi.com/compatible-mode/v1", m.BaseUrl));
        Assert.Contains("qwen4-max-preview", team.ChatModels.Keys);
        Assert.DoesNotContain("deepseek-v3.2", team.ChatModels.Keys);
        Assert.DoesNotContain("deepseek-v4-flash", team.ChatModels.Keys);
        var teamCn = ModelCatalog.LoadFromResource("qwen-token-plan-cn");
        Assert.Equal(20, teamCn.ChatModels.Count);
        Assert.All(teamCn.ChatModels.Values, m =>
            Assert.Equal("https://token-plan.maas.qianwenaiapi.com/compatible-mode/v1", m.BaseUrl));

        // 12313：Individual 的 allowlist 补 qwen4-max-preview（1M / 393216，effort 低中高）。
        var individual = ModelCatalog.LoadFromResource("qwen-token-plan-individual");
        Assert.Equal(13, individual.ChatModels.Count);
        var preview = individual.ChatModels["qwen4-max-preview"];
        Assert.Equal(1000000, preview.ContextWindow);
        Assert.Equal(393216, preview.MaxTokens);
        Assert.Equal(new[] { "text", "image" }, preview.Input);
        Assert.Equal("low", preview.ThinkingLevelMap!["low"]);
        Assert.Equal("medium", preview.ThinkingLevelMap["medium"]);
        Assert.Equal("xhigh", preview.ThinkingLevelMap["xhigh"]);
        Assert.Null(preview.ThinkingLevelMap["high"]);
        Assert.Contains("deepseek-v4.1-flash", individual.ChatModels.Keys);
        Assert.Contains("deepseek-v4.1-flash-1017", individual.ChatModels.Keys);

        // 12329 / 12332 / 12110：Copilot Responses 发 strict:false、Claude 支持 strict 工具、
        // completions 的 Gemini/Kimi 发 reasoning_effort。
        var copilot = ModelCatalog.LoadFromResource("github-copilot");
        Assert.All(copilot.ChatModels.Values.Where(m => m.Api == "openai-responses"),
            m => Assert.True(m.Compat!.Bool("supportsStrictMode")));
        Assert.All(copilot.ChatModels.Values.Where(m => m.Api == "anthropic-messages"),
            m => Assert.True(m.Compat!.Bool("supportsStrictTools")));
        Assert.All(copilot.ChatModels.Values.Where(m => m.Api == "openai-completions"),
            m => Assert.True(m.Compat!.Bool("supportsReasoningEffort")));

        // 0a25dd15e：Together 的 Kimi K3 采用上游 1M 输出上限。
        Assert.Equal(1048576, ModelCatalog.LoadFromResource("together").ChatModels["moonshotai/Kimi-K3"].MaxTokens);
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
