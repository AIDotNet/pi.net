// ---------------------------------------------------------------------------
// 直连真实模型：不经过 Agent，只用 Pi.Ai 的流式入口。
//
//   dotnet run --project samples/Pi.Samples.RealModel -- anthropic claude-sonnet-4-5
//   dotnet run --project samples/Pi.Samples.RealModel -- openai gpt-4.1
//
// 环境变量：
//   ANTHROPIC_API_KEY           anthropic 分支必填
//   OPENAI_API_KEY              openai  分支必填
//   OPENAI_BASE_URL             可选，指向任何 OpenAI 兼容端点（DeepSeek / Moonshot / 本地 vLLM …）
//   PI_SAMPLE_BASE_URL          可选，覆盖任一分支的 BaseUrl
//
// 这里调的是「旧版入口」：直接用 Pi.Ai.Types.Model 描述模型，不必先组装模型目录。
// 需要目录能力（按 provider 枚举模型、凭据解析、模型切换）时上层的 Pi.CodingAgent
// 会替你装配，见 Pi.Samples.CodingSession。
// ---------------------------------------------------------------------------

using System.Globalization;
using Pi.Ai;
using Pi.Ai.Api;
using Pi.Ai.Models;
using Pi.Ai.Providers;
using Pi.Ai.Types;

var provider = args.Length > 0 ? args[0] : "anthropic";
var modelId = args.Length > 1
    ? args[1]
    : provider == "openai" ? "gpt-4.1" : "claude-sonnet-4-5";

var prompt = args.Length > 2
    ? string.Join(' ', args[2..])
    : "用三句话说明什么是流式 LLM 调用。";

var baseUrlOverride = Environment.GetEnvironmentVariable("PI_SAMPLE_BASE_URL");

// ---------------------------------------------------------------------------
// 变体 A：目录驱动（provider 子命令 catalog）
//
// 内建目录里有 42 个 provider 的完整模型条目（BaseUrl / ContextWindow / 计价…）。
// 取出 ModelSpec 后交给 Models 门面，由 provider 自己按 model.Provider 分派。
// 这条路的选项走字典（键名与 SimpleStreamOptions 的驼峰字段同名）。
// ---------------------------------------------------------------------------
if (provider == "catalog")
{
    var apiKeyForCatalog = Require("ANTHROPIC_API_KEY");

    var models = new Models();
    models.SetProvider(Anthropic.Provider());   // 每个内建 provider 都有 Provider() 工厂

    var spec = All.GetBuiltinModel("anthropic", modelId);
    if (spec is null)
    {
        Console.Error.WriteLine($"目录里没有 anthropic/{modelId}；可用："
            + string.Join(", ", All.GetBuiltinModels("anthropic").Select(model => model.Id)));
        return 2;
    }

    Console.Error.WriteLine($"# {spec.Provider}/{spec.Id} baseUrl={spec.BaseUrl} ctx={spec.ContextWindow}");

    var completed = await models.CompleteSimpleAsync(
        spec,
        [Messages.UserText(prompt)],
        new Dictionary<string, object?> { ["apiKey"] = apiKeyForCatalog, ["maxTokens"] = 1024 });

    Console.WriteLine(TextOf(completed));
    Console.Error.WriteLine($"# stop={completed.StopReason}");
    return 0;
}

// Anthropic / OpenAI 的「简单流式」入口签名完全一致：
//   StreamSimple(Model, TranscriptContext, SimpleStreamOptions?, CancellationToken)
// 返回 IAssistantMessageEventStream —— 既能当 IAsyncEnumerable<AssistantMessageEvent> 迭代，
// 也能用 WaitForDoneAsync() 直接等终态消息。
string apiKey;
string baseUrl;
Model model;
Func<Model, TranscriptContext, SimpleStreamOptions?, CancellationToken, Task<IAssistantMessageEventStream>> streamSimple;

switch (provider)
{
    case "anthropic":
        apiKey = Require("ANTHROPIC_API_KEY");
        baseUrl = baseUrlOverride ?? string.Empty;
        model = new Model(modelId, modelId, "anthropic-messages", "anthropic");
        streamSimple = AnthropicMessages.StreamSimple;
        break;

    case "openai":
        apiKey = Require("OPENAI_API_KEY");
        baseUrl = baseUrlOverride
                  ?? Environment.GetEnvironmentVariable("OPENAI_BASE_URL")
                  ?? string.Empty;
        model = new Model(modelId, modelId, "openai-completions", "openai");
        streamSimple = OpenAiCompletions.StreamSimple;
        break;

    default:
        Console.Error.WriteLine($"未知 provider: {provider}（可选 anthropic / openai）");
        return 2;
}

var options = new SimpleStreamOptions(ApiKey: apiKey, MaxTokens: 1024)
{
    // BaseUrl 留空时用该 API 的默认端点；指向兼容端点时必须显式给。
    BaseUrl = string.IsNullOrEmpty(baseUrl) ? null : baseUrl,
};

var context = new TranscriptContext([Messages.UserText(prompt)]);

Console.Error.WriteLine($"# {model.Provider}/{model.Id}  ({model.Api})");
Console.Error.WriteLine($"# {prompt}");
Console.Error.WriteLine();

var stream = await streamSimple(model, context, options, CancellationToken.None);

AssistantMessage? final = null;
var textStarted = false;

await foreach (var messageEvent in stream)
{
    switch (messageEvent)
    {
        case AssistantMessageEvent.TextStart:
            textStarted = true;
            break;

        case AssistantMessageEvent.TextDelta delta:
            Console.Write(delta.Delta);
            break;

        case AssistantMessageEvent.ThinkingDelta thinking:
            Console.Error.Write(thinking.Delta);
            break;

        case AssistantMessageEvent.ToolCallEnd call:
            Console.Error.WriteLine($"[tool_call] {call.ToolCall.Name}({call.ToolCall.Arguments})");
            break;

        case AssistantMessageEvent.Done done:
            final = done.Message;
            break;

        case AssistantMessageEvent.Error error:
            Console.Error.WriteLine();
            Console.Error.WriteLine($"# 失败 ({error.Reason}): {error.ErrorMessage}");
            return 1;
    }
}

if (textStarted)
{
    Console.WriteLine();
}

if (final is not null)
{
    Console.Error.WriteLine();
    Console.Error.WriteLine($"# stop={final.StopReason} model={final.Model ?? model.Id}");
    if (final.UsageStats is { } usage)
    {
        Console.Error.WriteLine(string.Create(CultureInfo.InvariantCulture,
            $"# usage: in={usage.Input} out={usage.Output} cache_read={usage.CacheRead} cache_write={usage.CacheWrite}"));
    }
}

return 0;

static string Require(string variable)
    => Environment.GetEnvironmentVariable(variable) is { Length: > 0 } value
        ? value
        : throw new InvalidOperationException($"缺少环境变量 {variable}。");

// 把助手消息里的文本块拼起来（工具调用 / 思考块不在这里展开）。
static string TextOf(AssistantMessage message)
    => string.Concat(message.Content.OfType<TextContent>().Select(block => block.Text));
