using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using Pi.Ai.Api;
using Pi.Ai.Auth.OAuth;
using Pi.Ai.Models;
using Pi.Ai.Tests.Auth;
using Pi.Ai.Types;
using Pi.Ai.Utils;
using Xunit;

namespace Pi.Ai.Tests.Api;

/// <summary>v1.6.1 / v1.7.0 图片协议与用量测试。</summary>
public class V170ImageTests
{
    private static ModelSpec PiImageModel() => new()
    {
        Id = "nano-banana", Name = "Nano Banana", Api = "pi-image", Provider = "radius",
        BaseUrl = "https://radius.example/v1", Input = ["text", "image"], Type = ModelType.Image,
        Cost = new ModelCostRates(0.3, 2.5, CacheRead: 0.03, CacheWrite: 0.0833, ImageOutput: 4),
    };

    private static ModelSpec OpenRouterImageModel() => new()
    {
        Id = "google/gemini-2.5-flash-image-preview", Name = "Gemini Image", Api = "openrouter-images",
        Provider = "openrouter", BaseUrl = "https://openrouter.ai/api/v1", Input = ["text"],
        Type = ModelType.Image, Cost = new ModelCostRates(0.3, 2.5, ImageOutput: 8),
    };

    [Fact]
    public async Task PiImageParsesOutputUsageAndResponseId()
    {
        var handler = new FakeHttpHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(new JsonObject
            {
                ["output"] = new JsonArray(
                    new JsonObject { ["type"] = "text", ["text"] = "done" },
                    new JsonObject { ["type"] = "image", ["data"] = "iVBORw0KGgo=", ["mimeType"] = "image/png" }),
                ["responseId"] = "gen-img-1",
                ["usage"] = new JsonObject
                {
                    ["input"] = 12, ["output"] = 1290, ["cacheRead"] = 0, ["cacheWrite"] = 0,
                    ["imageOutput"] = 1200, ["reasoning"] = 90, ["totalTokens"] = 1302,
                },
            }.ToJsonString(), Encoding.UTF8, "application/json"),
        });
        var result = await PiImage.GenerateImages(
            PiImageModel(), new ImagesContext { Input = [new TextContent("a fox")] },
            new ImagesOptions { ApiKey = "key", AspectRatio = "16:9", Size = "1K", SessionId = "s-1" },
            new HttpClient(handler));

        Assert.Equal(ImagesStopReason.Stop, result.StopReason);
        Assert.Equal("gen-img-1", result.ResponseId);
        Assert.Equal("done", result.Output.OfType<TextContent>().Single().Text);
        var image = result.Output.OfType<ImageContent>().Single();
        Assert.Equal("image/png", image.MimeType);
        Assert.Equal(1200, result.Usage!.ImageOutput);
        Assert.Equal(90, result.Usage.Reasoning);
        // 1200 图片 token 按 4/M、其余 90 按 2.5/M。
        Assert.Equal(4.0 / 1e6 * 1200 + 2.5 / 1e6 * 90, result.Usage.Cost.Output, 12);

        var request = handler.Requests.Single();
        Assert.Equal("https://radius.example/v1/image", request.Url);
        var body = JsonNode.Parse(request.Body!)!.AsObject();
        Assert.Equal("a fox", body.Obj("context")!["input"]!.AsArray()[0]!["text"]!.GetValue<string>());
        Assert.Equal("16:9", body.Obj("options")!.Str("aspectRatio"));
        Assert.Equal("1K", body.Obj("options")!.Str("size"));
        Assert.Equal("s-1", body.Obj("options")!.Str("sessionId"));
    }

    [Fact]
    public async Task PiImageErrorEnvelopeReturnsErrorResult()
    {
        var handler = new FakeHttpHandler(_ => new HttpResponseMessage(HttpStatusCode.BadRequest)
        {
            Content = new StringContent("{\"error\":{\"message\":\"bad aspect ratio\"}}", Encoding.UTF8, "application/json"),
        });
        var result = await PiImage.GenerateImages(PiImageModel(),
            new ImagesContext { Input = [new TextContent("x")] }, new ImagesOptions { ApiKey = "key" },
            new HttpClient(handler));
        Assert.Equal(ImagesStopReason.Error, result.StopReason);
        Assert.Contains("bad aspect ratio", result.ErrorMessage);
    }

    [Fact]
    public void OpenRouterImagesForwardsImageConfig()
    {
        var payload = OpenRouterImages.BuildParams(OpenRouterImageModel(),
            new ImagesContext { Input = [new TextContent("cat")] },
            new ImagesOptions { AspectRatio = "4:3", Size = "2K" });
        var config = payload.Obj("image_config");
        Assert.NotNull(config);
        Assert.Equal("4:3", config!.Str("aspect_ratio"));
        Assert.Equal("2K", config.Str("image_size"));
    }

    [Fact]
    public void OpenRouterUsageReportsImageAndReasoningTokens()
    {
        var usage = OpenRouterImages.ParseUsage(new JsonObject
        {
            ["prompt_tokens"] = 100, ["completion_tokens"] = 200,
            ["completion_tokens_details"] = new JsonObject { ["reasoning_tokens"] = 50, ["image_tokens"] = 120 },
        }, OpenRouterImageModel());
        Assert.Equal(50, usage.Reasoning);
        Assert.Equal(120, usage.ImageOutput);
        Assert.Equal(8.0 / 1e6 * 120 + 2.5 / 1e6 * 80, usage.Cost.Output, 12);
    }

    [Fact]
    public async Task OpenRouterImagesRejectsNoChoicesAndBadFinishReason()
    {
        var noChoices = new FakeHttpHandler(_ => Ok(new JsonObject { ["error"] = new JsonObject { ["message"] = "nope" } }));
        var result = await OpenRouterImages.GenerateImages(OpenRouterImageModel(),
            new ImagesContext { Input = [new TextContent("x")] }, new ImagesOptions { ApiKey = "key" },
            new HttpClient(noChoices));
        Assert.Equal(ImagesStopReason.Error, result.StopReason);
        Assert.Equal("nope", result.ErrorMessage);

        var badFinish = new FakeHttpHandler(_ => Ok(new JsonObject
        {
            ["choices"] = new JsonArray(new JsonObject
            {
                ["finish_reason"] = "error", ["native_finish_reason"] = "MALFORMED_RESPONSE",
                ["message"] = new JsonObject { ["content"] = "" },
            }),
        }));
        var result2 = await OpenRouterImages.GenerateImages(OpenRouterImageModel(),
            new ImagesContext { Input = [new TextContent("x")] }, new ImagesOptions { ApiKey = "key" },
            new HttpClient(badFinish));
        Assert.Equal(ImagesStopReason.Error, result2.StopReason);
        Assert.Contains("native_finish_reason: MALFORMED_RESPONSE", result2.ErrorMessage);
    }

    private static HttpResponseMessage Ok(JsonObject body) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json"),
    };

    [Fact]
    public async Task PiDecisionClassifiesWithResponseId()
    {
        var model = new ModelSpec
        {
            Id = "jev-latest", Name = "Jev", Api = "pi-decision", Provider = "radius",
            BaseUrl = "https://radius.example/v1", Input = ["text"], Type = ModelType.Classifier,
            Cost = new ModelCostRates(0.1, 0.2),
        };
        var handler = new FakeHttpHandler(_ => Ok(new JsonObject
        {
            ["answers"] = new JsonObject
            {
                ["approved"] = new JsonObject { ["type"] = "bool", ["probability"] = 0.91 },
            },
            ["responseId"] = "gen-1",
            ["usage"] = new JsonObject { ["input"] = 10, ["output"] = 5 },
        }));
        var result = await PiDecision.ClassifyAsync(model, new ClassifierContext
        {
            State = new JsonObject { ["ok"] = true },
            Questions = new Dictionary<string, ClassifierQuestion>
            {
                ["approved"] = new ClassifierBoolQuestion("Approved?", "yes", "no"),
            },
        }, new ClassifierOptions { ApiKey = "key" }, new HttpClient(handler));

        Assert.Equal(ClassifierStopReason.Stop, result.StopReason);
        Assert.Equal("gen-1", result.ResponseId);
        Assert.Equal(0.91, ((ClassifierBoolAnswer)result.Answers["approved"]).Probability);
        Assert.Equal("https://radius.example/v1/decision", handler.Requests.Single().Url);
    }

    [Fact]
    public void RadiusConfigParsesDecisionAndImages()
    {
        var config = RadiusConfig.SanitizeGatewayConfig(new JsonObject
        {
            ["baseUrl"] = "https://radius.example/v1",
            ["models"] = new JsonArray(),
            ["decision"] = new JsonArray(new JsonObject
            {
                ["id"] = "jev-latest", ["name"] = "Jev", ["input"] = new JsonArray("text"),
                ["cost"] = new JsonObject { ["input"] = 0.1, ["output"] = 0.2 },
                ["contextWindow"] = 262144, ["maxTokens"] = 8192,
            }),
            ["images"] = new JsonArray(new JsonObject
            {
                ["id"] = "nano-banana", ["name"] = "Nano Banana",
                ["input"] = new JsonArray("text", "image"),
                ["output"] = new JsonArray("image", "text", "bogus"),
                ["cost"] = new JsonObject { ["input"] = 0.3, ["output"] = 2.5 },
            }),
        });
        Assert.NotNull(config);
        Assert.Single(config!.Decision);
        Assert.Single(config.Images);
        Assert.Equal(["image", "text"], config.Images[0].Output);

        var models = Pi.Ai.Providers.RadiusProviderConfig.ModelsFromConfig("radius", config);
        Assert.Equal(ModelType.Classifier, models.Single(m => m.Id == "jev-latest").Type);
        var image = models.Single(m => m.Id == "nano-banana");
        Assert.Equal(ModelType.Image, image.Type);
        Assert.Equal("pi-image", image.Api);
        Assert.Equal(["image", "text"], image.Output);
    }
}
