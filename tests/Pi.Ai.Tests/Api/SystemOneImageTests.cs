using System.Text.Json.Nodes;
using Pi.Ai.Api;
using Pi.Ai.Models;
using Pi.Ai.Tests.Auth;
using Pi.Ai.Types;
using Pi.Ai.Utils;
using Xunit;

namespace Pi.Ai.Tests.Api;

/// <summary>
/// 1.4.0（上游 `7f225f25d`）：System One 分类器接受 <see cref="ClassifierContext.Images"/>。
/// 三个服务各有自己的线上形态——TypeSafe 的 `assets` + opt-in 头、Clef 的 `images` data URL 列表、
/// OpenRouter 的状态内 `image_url` 片段——由 <c>SystemOneCompat</c> 的 `imageInput` 选择。
/// </summary>
public class SystemOneImageTests
{
    private static ModelSpec ImageModel(
        string api = TypesafeSystemOne.ApiName,
        string baseUrl = "https://svc.example.com/",
        string id = "classifier-x",
        string provider = "test",
        JsonObject? compat = null)
        => new()
        {
            Id = id,
            Name = "Classifier",
            Api = api,
            Provider = provider,
            BaseUrl = baseUrl,
            Cost = new ModelCostRates(1, 2),
            Input = ["text", "image"],
            Compat = compat,
        };

    private static ClassifierContext Context()
        => new()
        {
            State = new JsonObject { ["step"] = 1 },
            Questions = new Dictionary<string, ClassifierQuestion>
            {
                ["q-bool"] = new ClassifierBoolQuestion("Yes or no", "yes", "no"),
            },
        };

    private static ClassifierContext WithImages(params ImageContent[] images) => Context() with { Images = images };

    private static ImageContent Image(string data, string mimeType) => new(data, mimeType);

    private static JsonObject Answers() => new()
    {
        ["answers"] = new JsonObject
        {
            ["q-bool"] = new JsonObject { ["type"] = "noul", ["noul"] = 0.95 },
        },
    };

    private static System.Net.Http.HttpClient Client(Func<System.Net.Http.HttpRequestMessage, string?, JsonObject> respond,
        List<(JsonObject Body, System.Net.Http.Headers.HttpRequestHeaders Headers)>? captured = null)
    {
        var handler = new FakeHttpHandler((request, body) =>
        {
            var parsed = JsonNode.Parse(body!)!.AsObject();
            captured?.Add((parsed, request.Headers));
            return FakeResponse.Json(respond(request, body));
        });
        return new System.Net.Http.HttpClient(handler);
    }

    private static System.Net.Http.HttpClient ClientThatMustNotBeCalled()
        => Client((_, _) => throw new Xunit.Sdk.XunitException("the request must not be sent"));

    // ------------------------------------------------------------------ TypeSafe

    [Fact]
    public async Task TypesafeRejectsImagesForTextOnlyModels()
    {
        var result = await TypesafeSystemOne.ClassifyAsync(
            ImageModel(id: "jev-latest") with { Input = ["text"] },
            WithImages(Image("aW1hZ2U=", "image/png")),
            new ClassifierOptions { ApiKey = "key" },
            ClientThatMustNotBeCalled());

        Assert.Equal(ClassifierStopReason.Error, result.StopReason);
        Assert.Equal("System One API does not support image input for jev-latest", result.ErrorMessage);
    }

    [Fact]
    public async Task TypesafeSendsImagesAsAssetsWithTheOptInHeader()
    {
        var seen = new List<(JsonObject Body, System.Net.Http.Headers.HttpRequestHeaders Headers)>();
        var client = Client((_, _) => Answers(), seen);
        var model = ImageModel();

        var withImages = await TypesafeSystemOne.ClassifyAsync(
            model,
            WithImages(Image("cG5n", "image/png"), Image("anBlZw==", "image/jpeg")),
            new ClassifierOptions { ApiKey = "key" },
            client);
        var withoutImages = await TypesafeSystemOne.ClassifyAsync(
            model, Context(), new ClassifierOptions { ApiKey = "key" }, client);

        Assert.Equal(ClassifierStopReason.Stop, withImages.StopReason);
        // 状态变成「资源名在前、原状态在后」的数组，图片进 assets。
        var state = seen[0].Body["state"]!.AsArray();
        Assert.Equal("<image-1>", state[0]!.GetValue<string>());
        Assert.Equal("<image-2>", state[1]!.GetValue<string>());
        Assert.Equal(1, state[2]!.AsObject()["step"]!.GetValue<int>());
        var assets = seen[0].Body["assets"]!.AsObject();
        Assert.Equal("png", assets["<image-1>"]!.AsObject()["format"]!.GetValue<string>());
        Assert.Equal("cG5n", assets["<image-1>"]!.AsObject()["data_base64"]!.GetValue<string>());
        Assert.Equal("jpeg", assets["<image-2>"]!.AsObject()["format"]!.GetValue<string>());
        Assert.True(seen[0].Headers.Contains("TypeSafe-Danger-Zone-Images"));

        // 不带图片的请求保持原状态，也不发 opt-in 头。
        Assert.Equal(ClassifierStopReason.Stop, withoutImages.StopReason);
        Assert.Equal(1, seen[1].Body["state"]!.AsObject()["step"]!.GetValue<int>());
        Assert.False(seen[1].Body.ContainsKey("assets"));
        Assert.False(seen[1].Headers.Contains("TypeSafe-Danger-Zone-Images"));
    }

    [Fact]
    public async Task TypesafeRejectsImageFormatsItDoesNotAccept()
    {
        var result = await TypesafeSystemOne.ClassifyAsync(
            ImageModel(id: "jev-latest"),
            WithImages(Image("d2VicA==", "image/webp")),
            new ClassifierOptions { ApiKey = "key" },
            ClientThatMustNotBeCalled());

        Assert.Equal(ClassifierStopReason.Error, result.StopReason);
        Assert.Equal("System One API accepts PNG and JPEG images for jev-latest, got image/webp", result.ErrorMessage);
    }

    [Fact]
    public async Task TypesafeSendsOpenRouterImagesAsStateImageUrls()
    {
        var seen = new List<(JsonObject Body, System.Net.Http.Headers.HttpRequestHeaders Headers)>();
        var client = Client((_, _) => Answers(), seen);

        var result = await TypesafeSystemOne.ClassifyAsync(
            ImageModel(id: "typesafe/jev-1.14", baseUrl: "https://openrouter.ai/api/v1", provider: "openrouter"),
            WithImages(Image("cG5n", "image/png")),
            new ClassifierOptions { ApiKey = "key" },
            client);

        Assert.Equal(ClassifierStopReason.Stop, result.StopReason);
        // OpenRouter 对 assets 应答 200 但静默丢弃，且不接受顶层 images 列表。
        var state = seen[0].Body["state"]!.AsArray();
        Assert.Equal("image_url", state[0]!.AsObject()["type"]!.GetValue<string>());
        Assert.Equal(
            "data:image/png;base64,cG5n",
            state[0]!.AsObject()["image_url"]!.AsObject()["url"]!.GetValue<string>());
        Assert.Equal(1, state[1]!.AsObject()["step"]!.GetValue<int>());
        Assert.False(seen[0].Body.ContainsKey("assets"));
        Assert.False(seen[0].Body.ContainsKey("images"));
        Assert.False(seen[0].Headers.Contains("TypeSafe-Danger-Zone-Images"));
    }

    [Theory]
    // provider id 能躲过 baseUrl 覆盖，主机名能覆盖区域端点。
    [InlineData("openrouter", "https://proxy.example.com/v1")]
    [InlineData("custom", "https://us.openrouter.ai/api/v1")]
    public async Task TypesafeDetectsOpenRouterByProviderOrHost(string provider, string baseUrl)
    {
        var seen = new List<(JsonObject Body, System.Net.Http.Headers.HttpRequestHeaders Headers)>();
        var client = Client((_, _) => Answers(), seen);

        await TypesafeSystemOne.ClassifyAsync(
            ImageModel(id: "typesafe/jev-1.14", baseUrl: baseUrl, provider: provider),
            WithImages(Image("cG5n", "image/png")),
            new ClassifierOptions { ApiKey = "key" },
            client);

        Assert.Equal("image_url", seen[0].Body["state"]!.AsArray()[0]!.AsObject()["type"]!.GetValue<string>());
        Assert.False(seen[0].Body.ContainsKey("assets"));
    }

    [Fact]
    public async Task TypesafeUsesTheCompatImageFormBehindAProxy()
    {
        var seen = new List<(JsonObject Body, System.Net.Http.Headers.HttpRequestHeaders Headers)>();
        var client = Client((_, _) => Answers(), seen);

        await TypesafeSystemOne.ClassifyAsync(
            ImageModel(
                id: "typesafe/jev-1.14",
                baseUrl: "https://proxy.example.com/v1",
                provider: "custom",
                compat: new JsonObject { ["imageInput"] = "state-image-urls" }),
            WithImages(Image("cG5n", "image/png")),
            new ClassifierOptions { ApiKey = "key" },
            client);

        Assert.Equal("image_url", seen[0].Body["state"]!.AsArray()[0]!.AsObject()["type"]!.GetValue<string>());
        Assert.False(seen[0].Body.ContainsKey("assets"));
    }

    [Fact]
    public async Task TypesafeLetsCompatOverrideDetectedSettingsFieldByField()
    {
        var seen = new List<(JsonObject Body, System.Net.Http.Headers.HttpRequestHeaders Headers)>();
        var client = Client((_, _) => Answers(), seen);

        // 只覆盖头；检测出来的 assets 形态保留。
        await TypesafeSystemOne.ClassifyAsync(
            ImageModel(compat: new JsonObject { ["sendImageOptInHeader"] = false }),
            WithImages(Image("cG5n", "image/png")),
            new ClassifierOptions { ApiKey = "key" },
            client);

        Assert.True(seen[0].Body.ContainsKey("assets"));
        Assert.False(seen[0].Headers.Contains("TypeSafe-Danger-Zone-Images"));
    }

    // ------------------------------------------------------------------ Cloudflare Workers AI

    private static JsonObject CloudflareDirect() => new()
    {
        ["success"] = true,
        ["result"] = Answers(),
    };

    private static JsonObject CloudflareNested() => new()
    {
        ["success"] = true,
        ["result"] = new JsonObject
        {
            ["state"] = "Completed",
            ["result"] = Answers(),
        },
    };

    [Fact]
    public async Task ClefSendsImagesAsDataUrlsAndKeepsTheState()
    {
        var seen = new List<(JsonObject Body, System.Net.Http.Headers.HttpRequestHeaders Headers)>();
        var client = Client((_, _) => CloudflareDirect(), seen);

        var result = await CloudflareWorkersAiSystemOne.ClassifyAsync(
            ImageModel(api: CloudflareWorkersAiSystemOne.ApiName, id: "@cf/cloudflare/clef"),
            WithImages(Image("cG5n", "image/png"), Image("d2VicA==", "image/webp")),
            new ClassifierOptions { ApiKey = "key" },
            client);

        Assert.Equal(ClassifierStopReason.Stop, result.StopReason);
        // Clef 对 assets 回 422，故状态保持原样，图片走顶层 images；WebP 也收。
        var input = seen[0].Body["input"]!.AsObject();
        Assert.Equal(1, input["state"]!.AsObject()["step"]!.GetValue<int>());
        Assert.Equal(
            ["data:image/png;base64,cG5n", "data:image/webp;base64,d2VicA=="],
            input["images"]!.AsArray().Select(value => value!.GetValue<string>()).ToList());
        Assert.False(input.ContainsKey("assets"));
        Assert.False(seen[0].Headers.Contains("TypeSafe-Danger-Zone-Images"));
    }

    [Fact]
    public async Task ClefRejectsMoreThanFourImagesBeforeSending()
    {
        var images = Enumerable.Range(0, 5).Select(_ => Image("cG5n", "image/png")).ToArray();

        var result = await CloudflareWorkersAiSystemOne.ClassifyAsync(
            ImageModel(api: CloudflareWorkersAiSystemOne.ApiName, id: "@cf/cloudflare/clef-flash"),
            WithImages(images),
            new ClassifierOptions { ApiKey = "key" },
            ClientThatMustNotBeCalled());

        Assert.Equal(ClassifierStopReason.Error, result.StopReason);
        Assert.Equal(
            "Cloudflare Workers AI accepts at most 4 images for @cf/cloudflare/clef-flash, got 5",
            result.ErrorMessage);
    }

    [Fact]
    public async Task ClefReadsTheImageFormFromCompatBeforeTheModelId()
    {
        var seen = new List<(JsonObject Body, System.Net.Http.Headers.HttpRequestHeaders Headers)>();
        var client = Client((_, _) => CloudflareDirect(), seen);

        // Clef 部署在 id 前缀识别不出的名字下：compat 才是权威。
        await CloudflareWorkersAiSystemOne.ClassifyAsync(
            ImageModel(
                api: CloudflareWorkersAiSystemOne.ApiName,
                id: "@cf/acme/clef-finetune",
                compat: new JsonObject { ["imageInput"] = "images" }),
            WithImages(Image("cG5n", "image/png")),
            new ClassifierOptions { ApiKey = "key" },
            client);

        var input = seen[0].Body["input"]!.AsObject();
        Assert.Equal(["data:image/png;base64,cG5n"], input["images"]!.AsArray().Select(v => v!.GetValue<string>()));
        Assert.False(input.ContainsKey("assets"));
    }

    [Theory]
    [InlineData("typesafe/jev")]
    [InlineData("typesafe/jev-1.14.0")]
    public async Task JevOnWorkersAiSendsAssetsWithoutTheOptInHeader(string id)
    {
        var seen = new List<(JsonObject Body, System.Net.Http.Headers.HttpRequestHeaders Headers)>();
        var client = Client((_, _) => CloudflareNested(), seen);

        var result = await CloudflareWorkersAiSystemOne.ClassifyAsync(
            ImageModel(api: CloudflareWorkersAiSystemOne.ApiName, id: id),
            WithImages(Image("cG5n", "image/png")),
            new ClassifierOptions { ApiKey = "key" },
            client);

        Assert.Equal(ClassifierStopReason.Stop, result.StopReason);
        var input = seen[0].Body["input"]!.AsObject();
        Assert.Equal("<image-1>", input["state"]!.AsArray()[0]!.GetValue<string>());
        Assert.Equal("png", input["assets"]!.AsObject()["<image-1>"]!.AsObject()["format"]!.GetValue<string>());
        // Workers AI 不需要 TypeSafe 的 opt-in 头。
        Assert.False(seen[0].Headers.Contains("TypeSafe-Danger-Zone-Images"));
    }

    // ------------------------------------------------------------------ 其他分类器

    [Fact]
    public async Task LlamaCppRejectsImages()
    {
        var model = new ModelSpec
        {
            Id = "llama-classifier",
            Name = "Llama",
            Api = "llama-cpp-classify",
            Provider = "llama-cpp",
            BaseUrl = "http://127.0.0.1:8080/",
        };

        var result = await LlamaCppClassify.Classify(
            model,
            WithImages(Image("cG5n", "image/png")),
            new ClassifierOptions { ApiKey = "key" },
            ClientThatMustNotBeCalled());

        Assert.Equal(ClassifierStopReason.Error, result.StopReason);
        Assert.Contains("classification does not support image input", result.ErrorMessage);
    }
}
