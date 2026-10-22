// ---------------------------------------------------------------------------
// 最小可运行示例：不需要任何 API key 的 Agent 循环。
//
// Pi.Agent 把「模型调用」抽象成一个委托（StreamFn），所以只要换掉这个委托就能
// 在没有网络、没有密钥的情况下完整跑一遍 agent 循环：
//
//   system prompt -> 模型要求调用工具 -> 工具真的被执行 -> 结果回灌 -> 模型收尾
//
// 这里用 Pi.Ai 自带的 Faux 提供方把模型回合「脚本化」，因此输出可复现，
// 适合当作冒烟测试或示例。接真实模型见 Pi.Samples.RealModel。
// ---------------------------------------------------------------------------

using System.Globalization;
using System.Text.Json;
using Pi.Agent;
using Pi.Agent.Types;
using Pi.Ai.Providers;
using Pi.Ai.Types;

// 1) 脚本化的 StreamFn：每一轮对话按顺序回放一条脚本。
//    第一条 = 模型决定调用 add(19, 23)；第二条 = 模型看到工具结果后的最终回答。
//    脚本耗尽后会以 StopReason.Error 收尾（文案 "No more faux responses queued"）。
var streamFn = Faux.CreateStreamFn(
[
    Faux.Response.ToolUse(
        new ToolCallContent("call-1", "add", new { a = 19, b = 23 })),
    Faux.Response.Text("19 + 23 = 42。"),
]);

// 2) 声明一个真的会被执行的工具。
//    Parameters 是 JSON Schema（ToolSchema 直接持有 Schema 字典，与 wire 形状一致）。
var addTool = new AgentTool(
    Name: "add",
    Description: "把两个整数相加并返回它们的和。",
    Parameters: new ToolSchema(new Dictionary<string, object?>
    {
        ["type"] = "object",
        ["properties"] = new Dictionary<string, object?>
        {
            ["a"] = new Dictionary<string, object?> { ["type"] = "integer", ["description"] = "被加数" },
            ["b"] = new Dictionary<string, object?> { ["type"] = "integer", ["description"] = "加数" },
        },
        ["required"] = new[] { "a", "b" },
        ["additionalProperties"] = false,
    }),
    Label: "Add",
    // Execute(toolCallId, args, cancellationToken, onUpdate)
    // args 由 JSON 反序列化而来（通常是 JsonElement），这里用 JsonElement 统一读取。
    Execute: (_, args, _, _) =>
    {
        var arguments = JsonSerializer.SerializeToElement(args);
        var a = ReadInt32(arguments, "a");
        var b = ReadInt32(arguments, "b");
        return Task.FromResult(new AgentToolResult(
            Content: [new TextContent((a + b).ToString(CultureInfo.InvariantCulture))],
            Details: new { a, b }));
    });

// 3) 装配 Agent。StreamFn 是必填项（也可以用 DefaultStreamFn.Set 全局注册一个默认值）。
var agent = new Agent(new AgentOptions
{
    StreamFn = streamFn,
    Model = Faux.DefaultModel,
    SystemPrompt = "你是一个计算器。只允许用 add 工具做加法，然后用一句话回答。",
    Tools = [addTool],
});

// 4) 订阅事件，观察真实的循环轨迹。
//    把 TextDelta 打到 stdout，其余生命周期事件打到 stderr，便于脚本消费。
using var subscription = agent.Subscribe((agentEvent, _) =>
{
    switch (agentEvent)
    {
        case AgentEvent.ToolExecutionStart start:
            Console.Error.WriteLine($"[tool] -> {start.ToolName} {start.Args}");
            break;

        case AgentEvent.ToolExecutionEnd end:
            Console.Error.WriteLine($"[tool] <- {(end.IsError ? "ERROR" : "ok")} ({end.DurationMs} ms)");
            break;

        case AgentEvent.TurnEnd:
            Console.Error.WriteLine("[turn] end");
            break;

        case AgentEvent.MessageUpdate { AssistantMessageEvent: AssistantMessageEvent.TextDelta delta }:
            Console.Write(delta.Delta);
            break;
    }

    return Task.CompletedTask;
});

await agent.Prompt("19 加 23 等于多少？");
Console.WriteLine();

// agent.Messages 是完整的 transcript（system / user / assistant / toolResult 都在里面）。
// 也可以 continue：await agent.Continue();
Console.Error.WriteLine($"--- transcript: {agent.Messages.Count} 条消息 ---");
foreach (var message in agent.Messages)
{
    Console.Error.WriteLine($"  {message.GetType().Name}");
}

return 0;

static int ReadInt32(JsonElement arguments, string name)
    => arguments.ValueKind == JsonValueKind.Object
       && arguments.TryGetProperty(name, out var value)
       && value.ValueKind == JsonValueKind.Number
        ? value.GetInt32()
        : 0;
