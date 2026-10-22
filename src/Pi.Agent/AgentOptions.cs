using Pi.Ai.Types;
using Pi.Agent.Types;

namespace Pi.Agent;

/// <summary>
/// <see cref="Agent"/> 的构造选项。对应 TS <c>AgentOptions</c>；
/// 全部钩子为可选项，缺省使用安全回退实现。
/// </summary>
public sealed class AgentOptions
{
    /// <summary>初始 system prompt（与 Tools 一起合成首条 system 消息）。</summary>
    public string? SystemPrompt { get; init; }

    /// <summary>
    /// 初始 transcript。对应 TS <c>AgentInitialState.messages</c>：已以 system 消息开头时，
    /// <see cref="SystemPrompt"/> / <see cref="Tools"/> 不再折成前导 system 消息。
    /// </summary>
    public IReadOnlyList<ChatMessage>? Messages { get; init; }

    /// <summary>初始模型。</summary>
    public Model? Model { get; init; }

    /// <summary>初始思考档位。</summary>
    public ThinkingLevel? ThinkingLevel { get; init; }

    /// <summary>初始可执行工具集。</summary>
    public IReadOnlyList<AgentTool>? Tools { get; init; }

    /// <summary>LLM 调用边界的消息转换器（缺省：四种标准角色原样通过）。</summary>
    public Func<IReadOnlyList<ChatMessage>, Task<IReadOnlyList<ChatMessage>>>? ConvertToLlm { get; init; }

    /// <summary>转换前的上下文变换。</summary>
    public Func<IReadOnlyList<ChatMessage>, CancellationToken, Task<IReadOnlyList<ChatMessage>>>? TransformContext { get; init; }

    /// <summary>流式函数（必填；也可用 DefaultStreamFn.Set 全局注册）。</summary>
    public required StreamFn StreamFn { get; init; }

    /// <summary>按 provider 动态解析 API key。</summary>
    public Func<string, Task<string?>>? GetApiKey { get; init; }

    /// <summary>工具调用前钩子。</summary>
    public Func<BeforeToolCallContext, CancellationToken, Task<BeforeToolCallResult?>>? BeforeToolCall { get; init; }

    /// <summary>工具执行后覆盖钩子。</summary>
    public Func<AfterToolCallContext, CancellationToken, Task<AfterToolCallResult?>>? AfterToolCall { get; init; }

    /// <summary>回合完成后的调度钩子。</summary>
    public Func<AgentTurnContext, CancellationToken, Task<AgentTurnDecision?>>? FinishTurn { get; init; }

    /// <summary>每次 provider 请求前的状态替换钩子。</summary>
    public Func<AgentContext, Model, ThinkingLevel, CancellationToken, Task<AgentLoopTurnUpdate?>>? PrepareRequest { get; init; }

    /// <summary>turn_end 之后、下一回合开始前的准备钩子。</summary>
    public Func<AgentTurnContext, Task<AgentLoopTurnUpdate?>>? PrepareNextTurn { get; init; }

    /// <summary>steering 队列排空模式（缺省 one-at-a-time，与 TS 一致）。</summary>
    public QueueMode? SteeringMode { get; init; }

    /// <summary>follow-up 队列排空模式（缺省 one-at-a-time，与 TS 一致）。</summary>
    public QueueMode? FollowUpMode { get; init; }

    /// <summary>会话 id（转发给缓存感知后端）。</summary>
    public string? SessionId { get; init; }

    /// <summary>多工具调用执行策略。</summary>
    public ToolExecutionMode? ToolExecution { get; init; }

    /// <summary>请求体替换钩子（转发给流选项）。对应 TS <c>AgentOptions.onPayload</c>。</summary>
    public Func<System.Text.Json.Nodes.JsonObject, Pi.Ai.Models.ModelSpec, Task<System.Text.Json.Nodes.JsonObject?>>? OnPayload { get; init; }

    /// <summary>HTTP 响应到达回调（转发给流选项）。对应 TS <c>AgentOptions.onResponse</c>。</summary>
    public Func<ProviderResponse, Pi.Ai.Models.ModelSpec, Task>? OnResponse { get; init; }

    /// <summary>provider 原始流事件观察回调（转发给流选项）。对应 TS <c>AgentOptions.onProviderStreamEvent</c>。</summary>
    public Func<System.Text.Json.Nodes.JsonObject, Pi.Ai.Models.ModelSpec, Task>? OnProviderStreamEvent { get; init; }

    /// <summary>首选传输（auto/sse/websocket/websocket-cached）。对应 TS <c>AgentOptions.transport</c>。</summary>
    public string? Transport { get; init; }

    /// <summary>token 型 provider 的思考预算覆盖。对应 TS <c>AgentOptions.thinkingBudgets</c>。</summary>
    public ThinkingBudgets? ThinkingBudgets { get; init; }

    /// <summary>单次重试的最长退避。对应 TS <c>AgentOptions.maxRetryDelayMs</c>。</summary>
    public int? MaxRetryDelayMs { get; init; }

    /// <summary>
    /// 请求如何向 provider 标识本应用：逐字段覆盖
    /// <see cref="AgentLoop.DefaultAgentIdentity"/>（<c>{ attribution: PI_ATTRIBUTION }</c>）。
    /// 传 <c>new ClientIdentity { Attribution = null }</c> 可不发归属头。
    /// 对应 TS <c>AgentOptions.identity</c>。
    /// </summary>
    public Pi.Ai.Utils.ClientIdentity? Identity { get; init; }
}
