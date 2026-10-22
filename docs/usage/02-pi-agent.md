# Pi.Agent：agent 循环

`Pi.Agent` 是 `Pi.Ai` 之上最薄的一层：一个状态机 + 一个主循环 + 工具执行。
它不认识凭据、不认识 provider 目录、不认识配置文件——它只认一个
`StreamFn` 委托和一组工具。

```bash
dotnet add package Pi.Agent
```

对应示例：[`samples/Pi.Samples.FauxAgent`](../../samples/Pi.Samples.FauxAgent)（不需要密钥，
`dotnet run` 就能看到完整循环）。

## 装配

```csharp
using Pi.Agent;
using Pi.Agent.Types;
using Pi.Ai.Providers;
using Pi.Ai.Types;

var agent = new Agent(new AgentOptions
{
    StreamFn = streamFn,            // 必填
    Model = Faux.DefaultModel,      // 瘦 Model（Id/Name/Api/Provider）
    SystemPrompt = "你是一个计算器。",
    Tools = [addTool],              // AgentTool 列表，可空
});

await agent.Prompt("19 加 23 等于多少？");
```

三件事值得单独说：

**`StreamFn` 是必填项。** 它是唯一的模型出口：

```csharp
public delegate Task<IAssistantMessageEventStream> StreamFn(
    Model model, TranscriptContext context, SimpleStreamOptions? options, CancellationToken cancellationToken);
```

不想每次传就装一个全局默认：`DefaultStreamFn.Set(fn)`，之后 `AgentOptions` 里可以不写（但
`Agent` 拿到 `null` 且没有默认值时仍会抛 `No default stream function configured`）。
把密钥、重试、代理都封在自己的 `StreamFn` 里，是嵌入式场景最干净的做法。

**`Prompt` 返回即结束。** `await agent.Prompt(...)` 在整轮跑完（含所有工具调用轮次）后完成；
中途想插话用 `Steer` / `FollowUp`，想停用 `Abort()`。

**`agent.Messages` 是活列表。** 它就是模型看到的 transcript，可以随时读、可以整体赋值。
system prompt 不是单独字段——它被折成首条 `SystemMessage`，`agent.SystemPrompt` 从
transcript 里反查当前值。

## 工具

`AgentTool` 是 5 个位置参数 + 几个可选属性：

```csharp
public sealed record AgentTool(
    string Name,
    string Description,
    ToolSchema Parameters,                    // JSON Schema 字典
    string Label,
    Func<string, object?, CancellationToken, AgentToolUpdateCallback?, Task<AgentToolResult>> Execute,
    ToolExecutionMode? ExecutionMode = null,
    Func<object?, object?>? PrepareArguments = null);
```

```csharp
var addTool = new AgentTool(
    Name: "add",
    Description: "把两个整数相加并返回它们的和。",
    Parameters: new ToolSchema(new Dictionary<string, object?>
    {
        ["type"] = "object",
        ["properties"] = new Dictionary<string, object?>
        {
            ["a"] = new Dictionary<string, object?> { ["type"] = "integer" },
            ["b"] = new Dictionary<string, object?> { ["type"] = "integer" },
        },
        ["required"] = new[] { "a", "b" },
    }),
    Label: "Add",
    Execute: (callId, args, ct, onUpdate) =>
    {
        // args 来自 JSON 反序列化，实际是 JsonElement / JsonObject；用 SerializeToElement 统一
        var json = JsonSerializer.SerializeToElement(args);
        var sum = json.GetProperty("a").GetInt32() + json.GetProperty("b").GetInt32();
        return Task.FromResult(new AgentToolResult([new TextContent(sum.ToString())]));
    });
```

- **失败**：抛异常或返回 `AgentToolResult.FromError("…")`（`IsError=true`，模型会看到文本）。
- **流式部分结果**：调 `onUpdate(partialResult)`，会变成 `ToolExecutionUpdate` 事件。
- **提前收尾**：`AgentToolResult.Terminate = true` 让本次运行结束，不再继续轮到模型。
- **`ExecutionMode`**：默认走 `AgentOptions.ToolExecution`（缺省 `Parallel`——先顺序预检、
  再并行执行允许并行的工具）；`Sequential` 逐个跑。

## 事件

`Subscribe(Func<AgentEvent, CancellationToken, Task>)` 返回 `IDisposable`：

| 事件 | 何时 |
|---|---|
| `AgentStart` / `AgentEnd` | 整个运行的起止（`AgentEnd` 带全部消息） |
| `TurnStart` / `TurnEnd` | 一个模型回合（`TurnEnd` 带助手消息与工具结果） |
| `MessageStart` / `MessageUpdate` / `MessageEnd` | 单条消息的生命周期；`MessageUpdate` 里包着 `Pi.Ai` 的流事件 |
| `ToolExecutionStart` / `ToolExecutionUpdate` / `ToolExecutionEnd` | 工具执行（`End` 带 `IsError` 与 `DurationMs`） |

渲染流式文本只要一条：

```csharp
case AgentEvent.MessageUpdate { AssistantMessageEvent: AssistantMessageEvent.TextDelta delta }:
    Console.Write(delta.Delta);
    break;
```

## 钩子

`AgentOptions` 上的六个委托覆盖了循环的关键切面：

| 钩子 | 用途 |
|---|---|
| `BeforeToolCall` | 拦下某次工具调用（`Block=true` 直接返回拒绝原因）、改参数、强制终止 |
| `AfterToolCall` | 覆写工具结果（脱敏、加壳、改文本） |
| `ConvertToLlm` | transcript → 模型消息（默认四种标准角色直通） |
| `TransformContext` | 转换前的上下文变换（裁剪、注入环境信息） |
| `FinishTurn` | 回合结束后决定继续还是收尾（`AgentTurnDecision.Continue` / `End`） |
| `PrepareRequest` / `PrepareNextTurn` | 每次请求前 / 下一回合前替换状态（换模型、换思考档位、改工具集） |

`PrepareNextTurn` 是「渐进式升级」的挂点：第一轮用小模型、失败或工具报错后换大模型，
不需要在外面重开会话。

## 队列与中断

```csharp
agent.Steer(Messages.UserText("顺便看下 README"));   // 在当前工具轮次之后注入
agent.FollowUp(Messages.UserText("再总结一遍"));      // 在本轮结束、即将空闲时注入

agent.SteeringMode = QueueMode.OneAtATime;          // 排空点一次只注入最旧的一条
agent.Abort();                                       // 取消当前运行（StopReason.Aborted）
await agent.WaitForIdle();
agent.Reset();                                       // 清 transcript 与队列，回到初始态
```

`agent.IsStreaming`、`agent.StreamingMessage`、`agent.PendingToolCalls` 是给 UI 用的实时状态。

## 从这里往上走

`Pi.Agent` 适合「我自己有会话存储 / 我只要一个 agent 循环」的场景。要现成的工具集
（read / bash / edit / write）、技能、扩展、会话存档、模型目录和凭据管理，直接用
[`Pi.CodingAgent`](03-pi-codingagent.md)——它内部就是这么装配 `Pi.Agent` 的。
