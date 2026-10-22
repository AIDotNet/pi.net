# Pi.Ai：多 provider LLM API

`Pi.Ai` 只做一件事：把「一组消息 + 一个模型」发出去，把响应收回成**事件流**。
它不管状态、不管工具循环、不管凭据存储——那些都在上层。

```bash
dotnet add package Pi.Ai
```

对应示例：[`samples/Pi.Samples.RealModel`](../../samples/Pi.Samples.RealModel)。

## 最小流式调用

三种入口按「你知道多少」排列：

| 你知道 | 用什么 |
|---|---|
| 只有模型 id 和一把 key | `AnthropicMessages.StreamSimple(model, context, options, ct)` 之类的**旧版入口**，用瘦 `Model` 描述 |
| 内建目录里有这个模型 | `new Models()` + `SetProvider(...)` + `All.GetBuiltinModel(...)`，用 `ModelSpec` |
| 只知道 api 协议名 | `ApiRegistry.GetApiProvider(api)`，按 `model.Api` 分派 |

第一种最省事：

```csharp
using Pi.Ai.Api;
using Pi.Ai.Types;

var model = new Model("claude-sonnet-4-5", "claude-sonnet-4-5", "anthropic-messages", "anthropic");
var options = new SimpleStreamOptions(ApiKey: key, MaxTokens: 1024);

var stream = await AnthropicMessages.StreamSimple(
    model,
    new TranscriptContext([Messages.UserText("用一句话解释流式调用。")]),
    options,
    CancellationToken.None);
```

`StreamSimple` 返回 `IAssistantMessageEventStream`，它同时是
`IAsyncEnumerable<AssistantMessageEvent>` 和「等终态」的句柄：

```csharp
await foreach (var evt in stream)
{
    switch (evt)
    {
        case AssistantMessageEvent.TextDelta delta:
            Console.Write(delta.Delta);      // 增量文本
            break;
        case AssistantMessageEvent.Done done:
            final = done.Message;            // 终态消息（含 usage）
            break;
        case AssistantMessageEvent.Error error:
            Console.Error.WriteLine($"{error.Reason}: {error.ErrorMessage}");
            break;
    }
}
```

不想流式就只等终态：

```csharp
var message = await stream.WaitForDoneAsync();
var text = string.Concat(message.Content.OfType<TextContent>().Select(block => block.Text));
```

`Error` 事件**不是异常**。`StreamFn` 的契约是「不得抛异常或返回失败任务，错误必须编码为
终态事件」——所以网络错误、限流、内容拦截都表现为
`StopReason == Error` / `Aborted` 的 `Error` 或 `Done` 事件。`catch` 抓不到它们。

## 事件类型

| 事件 | 含义 |
|---|---|
| `Start` | 流开始，带部分消息快照 |
| `TextStart` / `TextDelta` / `TextEnd` | 正文 |
| `ThinkingStart` / `ThinkingDelta` / `ThinkingEnd` | 思考（推理模型；`ThinkingDelta` 带 `Signature`） |
| `ToolCallStart` / `ToolCallDelta` / `ToolCallEnd` | 工具调用（参数也是流式的） |
| `Done` | 正常收尾，`Message` 是终态 |
| `Error` | 失败收尾，`Reason` + `ErrorMessage` |

每个事件都带 `Partial`（当前已知的部分消息快照），所以渲染 UI 不需要自己拼状态。

## 目录驱动

需要知道 `BaseUrl`、上下文窗口、计价，或者要在 42 个内建 provider 里挑模型时，走目录：

```csharp
using Pi.Ai.Models;
using Pi.Ai.Providers;

var models = new Models();
models.SetProvider(Anthropic.Provider());          // 每个内建 provider 都有 Provider() 工厂

var spec = All.GetBuiltinModel("anthropic", "claude-sonnet-4-5")
           ?? throw new InvalidOperationException("目录里没有这个模型");

var message = await models.CompleteSimpleAsync(
    spec,
    [Messages.UserText("…")],
    new Dictionary<string, object?> { ["apiKey"] = key, ["maxTokens"] = 1024 });
```

这条路的选项是**字典**，键名与 `SimpleStreamOptions` 的驼峰字段同名
（`apiKey` / `baseUrl` / `maxTokens` / `temperature` / `headers` / `signal` / `reasoning` / …）。
可空字段不写就等于不传；`signal` 用于取消。

`Models` 还能做：`GetModels(provider)` 枚举、`GetModelsOfType(ModelType.Image)` 取图片模型、
`GenerateImagesAsync`、`ClassifyAsync`、`StreamDeferred` / `FetchDeferredAsync`（延后响应）。
`All.GetBuiltinProviders()` 列出全部内建 provider id，`All.GetBuiltinModels(provider)` 列出其模型。

## 自建 api 实现

`ApiRegistry` 是「协议名 → 实现」的分派表，`model.Api` 就是键。内建 12 个
（`anthropic-messages` / `openai-completions` / `openai-responses` / `azure-openai-responses` /
`openai-codex-responses` / `google-generative-ai` / `google-vertex` / `mistral-conversations` /
`bedrock-converse-stream` / `pi-messages`，外加两个只做分类的 `*-system-one`）。

```csharp
ApiRegistry.RegisterApiProvider(new ApiProvider
{
    Api = "my-internal-api",
    Stream = (spec, context, optionsJson, httpClient, ct) => ...,
    StreamSimple = (spec, context, options, httpClient, ct) => ...,
}, sourceId: "my-app");

// 卸载时按 sourceId 成组清理
ApiRegistry.UnregisterApiProviders("my-app");
```

`ResetApiProviders()` 清空后重新注册内建实现——测试里用来隔离状态。

## 密钥发现

`EnvApiKeys` 复刻了上游的环境变量约定，可以拿来「这个 provider 现在能不能用」：

```csharp
var apiKey = EnvApiKeys.GetEnvApiKey("deepseek");          // DEEPSEEK_API_KEY
var candidates = EnvApiKeys.GetApiKeyEnvVars("anthropic"); // ["ANTHROPIC_AUTH_TOKEN", "ANTHROPIC_OAUTH_TOKEN", "ANTHROPIC_API_KEY"]
var configured = EnvApiKeys.FindEnvKeys("openai");         // 只返回真正设了的变量名
```

几个刻意的行为：`ANTHROPIC_AUTH_TOKEN` 参与发现但**不会**作为 `GetEnvApiKey` 的返回值
（那个变量必须以 `Authorization: Bearer` 传，不是 `x-api-key`）；Vertex / Bedrock 在
「凭据链完整但取不到具体密钥」时返回 `<authenticated>` 标记。

## 测试用 Faux 提供方

`Faux` 是一个脚本化 provider：把模型回合写死成队列，于是 agent 循环、工具执行、重试、
压缩这些逻辑都能在**没有网络**的情况下测：

```csharp
using Pi.Ai.Providers;

// 直接给 Agent 用的 StreamFn
var streamFn = Faux.CreateStreamFn(
[
    Faux.Response.ToolUse(new ToolCallContent("call-1", "add", new { a = 1, b = 2 })),
    Faux.Response.Text("3"),
]);

// 或者装配成注册表里的 provider（走 model.provider 分派）
var faux = Faux.CreateProvider();
faux.SetResponses([Faux.Response.Text("hi")]);
```

`Faux.DefaultModel` 是一个现成的 `Model`（`faux-1` / `faux`）。脚本耗尽后以
`StopReason.Error`（`No more faux responses queued`）收尾——忘了补脚本会立刻暴露。

## 别踩的坑

- **`Model` 与 `ModelSpec` 是两个类型。** `Model`（`Id`/`Name`/`Api`/`Provider` + 可选 reasoning/metadata）
  是 `StreamFn` 边界的瘦记录；`ModelSpec` 是目录条目（还带 `BaseUrl`、`ContextWindow`、
  `MaxTokens`、`Cost`、`Compat`…）。旧版入口吃 `Model` 并自己补默认 `BaseUrl`；
  `Models` / `ApiProvider` 吃 `ModelSpec`。
- **旧版入口不要重复给 `/v1`。** `AnthropicMessages.StreamSimple` 内部会拼 `/v1/messages`，
  传进来的 `BaseUrl` 会被归一化；`OpenAiCompletions` 同理（拼 `/chat/completions`）。
- **`TranscriptContext` 的 system prompt 在消息里。** 它不是独立字段——system 消息就是
  `context.Messages[0]`；`context.Tools` 单独承载工具声明。
- **工具声明用 `ToolDefinition`**（`Name` / `Description` / `Parameters: ToolSchema`），
  可执行版本是 `Pi.Agent` 的 `AgentTool`（多了 `Execute`），两者不要混。
