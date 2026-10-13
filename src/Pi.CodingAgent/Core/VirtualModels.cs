using System.Text.Json;
using System.Text.Json.Nodes;
using Pi.Ai.Api;
using Pi.Ai.Auth;
using Pi.Ai.Models;
using Pi.Ai.Types;
using Pi.Ai.Utils;
using Pi.CodingAgent.Utils;

namespace Pi.CodingAgent.Core;

/// <summary>
/// Virtual models are catalog entries that route each request to a physical model.
/// Port of the TS <c>core/virtual-models.ts</c>.
/// </summary>
/// <remarks>
/// The selection (<c>model_change</c>, <c>agent.state.model</c>, <c>ctx.model</c>) may name a virtual
/// model. Everything below the routing step only sees physical models: providers stream them and
/// assistant messages record them. A virtual model never reaches a provider.
/// <para>
/// 9h-3 兑现了缺口 G-4b-VM 的一半：<c>getVirtualModelState</c>（需要 <c>SessionEntry</c>，此前因
/// 会话层未落地而顺延）。<c>getBranchSelection</c> 仍留给编排本体（9h-4 的 <c>_recordSelection</c>）。
/// <c>findLatestResponse</c> 的 router 用重载在下面（<see cref="ChatMessage"/> 版）。
/// </para>
/// <para>Difference C40: router state is <see cref="JsonNode"/> (TS <c>TState = unknown</c>, documented as
/// JSON-serializable), and provider capability checks that TS expresses as absent members surface here as
/// the same error message the TS caller would have produced.</para>
/// </remarks>
public static class VirtualModels
{
    /// <summary>API id of virtual catalog entries. Requests for it fail unless routed first.</summary>
    public const string Api = "pi-virtual";

    /// <summary>Custom entry type that stores router state on the session branch.</summary>
    public const string StateEntry = "pi.virtual-model-state";

    /// <summary>Whether a model or message names a virtual model.</summary>
    public static bool IsVirtualModel(ModelSpec model) => model.Api == Api;

    /// <summary>Whether an assistant message names a virtual model (failed routing leaves it on the message).</summary>
    public static bool IsVirtualModel(AssistantMessage message) => message.Api == Api;

    /// <summary>
    /// Whether the thin runtime model (<see cref="Pi.Ai.Types.Model"/>) names a virtual model.
    /// 会话的 <c>prepareRequest</c> 投影拿到的模型是 Agent 镜像的瘦形状（差异 C148），
    /// TS 那边是同一个活对象，故需要这条重载补上判别。
    /// </summary>
    public static bool IsVirtualModel(Pi.Ai.Types.Model model) => model.Api == Api;

    /// <summary>
    /// Latest router state a session branch stores for a virtual model. Port of the TS
    /// <c>getVirtualModelState</c>；差异 G-4b-VM 的兑现（9h-3 落地，此前记为缺口）。
    /// </summary>
    /// <returns>
    /// 分支上最后一条匹配的自定义条目的 <c>state</c>；无匹配时为 <c>null</c>
    /// （TS 的 <c>undefined</c>，端口以 <c>JsonNode?</c> 承载，两者在调用点同为「保持当前状态」）。
    /// </returns>
    public static JsonNode? GetVirtualModelState(
        IReadOnlyList<SessionEntry> branch, string provider, string modelId)
    {
        for (var i = branch.Count - 1; i >= 0; i--)
        {
            if (branch[i] is not CustomEntry entry || entry.CustomType != StateEntry)
            {
                continue;
            }

            // TS 的 `entry.data as VirtualModelStateData | undefined`：载荷不是该形状时按不匹配处理。
            var data = entry.Data switch
            {
                VirtualModelStateData typed => typed,
                JsonNode node => DeserializeStateData(node),
                _ => null,
            };
            if (data is not null
                && string.Equals(data.Provider, provider, StringComparison.Ordinal)
                && string.Equals(data.ModelId, modelId, StringComparison.Ordinal))
            {
                return data.State;
            }
        }

        return null;
    }

    private static VirtualModelStateData? DeserializeStateData(JsonNode node)
    {
        try
        {
            return node.Deserialize<VirtualModelStateData>();
        }
        catch (System.Text.Json.JsonException)
        {
            return null;
        }
    }

    /// <summary>
    /// Latest successful assistant response in <paramref name="messages"/>, skipping failed and aborted turns.
    /// Port of the TS <c>findLatestResponse</c> overload that takes the plain <c>Message</c> union.
    /// </summary>
    /// <remarks>
    /// The TS signature also accepts <c>AgentMessage</c> (which adds extension messages). C# transcripts carry
    /// <see cref="ChatMessage"/>, so the wider overload is not needed here; it arrives with the 4e session layer
    /// (gap G-4b-VM) only if extension messages become a distinct type.
    /// </remarks>
    public static AssistantMessage? FindLatestResponse(IReadOnlyList<ChatMessage> messages)
    {
        for (var index = messages.Count - 1; index >= 0; index--)
        {
            if (messages[index] is AssistantMessage message
                && message.StopReason is not StopReason.Error and not StopReason.Aborted)
            {
                return message;
            }
        }
        return null;
    }

    /// <summary>
    /// 分支上最近一次生效的模型选择。TS <c>getBranchSelection(branch, getModel)</c>
    /// （<c>virtual-models.ts:128-145</c>）。
    /// </summary>
    /// <param name="getModel">
    /// 目录查询（TS 的 <c>getModel</c>）；端口用 <c>ModelRuntime.GetPhysicalModel</c> 承担。
    /// </param>
    public static (string Provider, string ModelId)? GetBranchSelection(
        IReadOnlyList<SessionEntry> branch,
        Func<string, string, ModelSpec?> getModel)
    {
        for (var index = branch.Count - 1; index >= 0; index--)
        {
            var entry = branch[index];
            if (entry is ModelChangeEntry modelChange)
            {
                return (modelChange.Provider, modelChange.ModelId);
            }

            if (entry is SessionMessageEntry { Message: AssistantMessage message }
                && !IsVirtualModel(message))
            {
                var response = (message.Provider ?? string.Empty, message.Model ?? string.Empty);
                var change = FindLastModelChange(branch, index);
                var model = change is { } found ? getModel(found.Provider, found.ModelId) : null;
                return change is { } resolved && model is not null && IsVirtualModel(model) ? resolved : response;
            }
        }

        return null;
    }

    /// <summary>TS <c>findLastModelChange(branch, before)</c>（<c>virtual-models.ts:147-155</c>）。</summary>
    private static (string Provider, string ModelId)? FindLastModelChange(
        IReadOnlyList<SessionEntry> branch, int before)
    {
        for (var index = before - 1; index >= 0; index--)
        {
            if (branch[index] is ModelChangeEntry modelChange)
            {
                return (modelChange.Provider, modelChange.ModelId);
            }
        }

        return null;
    }

    /// <summary>Build the catalog entry of a virtual model. The definition's <c>Route</c> is ignored.</summary>
    public static ModelSpec CreateVirtualModel(VirtualModelDefinition definition)
    {
        var levels = definition.ThinkingLevels ?? ["off"];
        var levelMap = new JsonObject();
        foreach (var level in Pi.Ai.Models.ThinkingLevels.ExtendedThinkingLevels)
        {
            levelMap[level] = levels.Contains(level) ? level : null;
        }

        return new ModelSpec
        {
            Id = definition.Id,
            Name = definition.Name,
            Api = Api,
            Provider = definition.Provider,
            BaseUrl = string.Empty,
            Reasoning = levels.Any(level => level != "off"),
            ThinkingLevelMap = ThinkingLevelMap.FromJsonObject(levelMap),
            Input = definition.Input ?? [ModelInput.Text, ModelInput.Image],
            Cost = new ModelCostRates(0, 0, 0, 0),
            ContextWindow = (long)(definition.ContextWindow ?? 0),
            MaxTokens = (long)(definition.MaxTokens ?? 0),
        };
    }

    /// <summary>
    /// Add virtual models to a provider's catalog. Without a provider, the result is a keyless provider
    /// that only lists the virtual models. A virtual model hides a physical chat model with the same id,
    /// which a catalog refresh can add after registration. Availability follows the provider's auth.
    /// </summary>
    public static IProvider WithVirtualModels(
        string providerId, IProvider? provider, IReadOnlyList<ModelSpec> virtualModels)
        => provider is null
            ? new VirtualOnlyProvider(providerId, virtualModels)
            : new VirtualModelsProvider(provider, virtualModels);

    /// <summary>Stream for a virtual model that was not routed, e.g. <c>Stream()</c> with API-specific options.</summary>
    internal static IAssistantMessageEventStream UnroutedStream(ModelSpec model)
        => LazyStream.Run(model, () => throw new InvalidOperationException(
            $"Virtual model {model.Provider}/{model.Id} must be routed before streaming"));

    /// <summary>Reason a request is being routed.</summary>
    public static class RouteReasons
    {
        /// <summary>First request after a message the user wrote (prompt, steering, or follow-up).</summary>
        public const string User = "user";

        /// <summary>Any other request in the agent loop, e.g. after tool results or extension messages.</summary>
        public const string Continuation = "continuation";

        /// <summary>Automatic retry after a failed request, including after compaction for a context overflow.</summary>
        public const string Retry = "retry";

        /// <summary>A request outside the agent loop, e.g. a compaction summary or an extension call.</summary>
        public const string Direct = "direct";
    }
}

/// <summary>Data of a <c>pi.virtual-model-state</c> custom entry.</summary>
public sealed record VirtualModelStateData
{
    public required string Provider { get; init; }

    public required string ModelId { get; init; }

    public JsonNode? State { get; init; }
}

/// <summary>Physical model and thinking level of the latest successful response in the conversation.</summary>
public sealed record ModelRoutePrevious(ModelSpec Model, string? ThinkingLevel);

/// <summary>
/// For <c>retry</c>: the failed request, which <c>messages</c> no longer contains.
/// <c>Message</c> carries its <c>stopReason</c> and <c>errorMessage</c>. Absent when the router itself failed.
/// </summary>
public sealed record ModelRouteFailure(ModelSpec Model, string? ThinkingLevel, AssistantMessage Message);

/// <summary>One routing request handed to a virtual model's router.</summary>
public sealed record ModelRouteRequest
{
    /// <summary>The selected virtual model.</summary>
    public required ModelSpec Model { get; init; }

    /// <summary>The selected thinking level. Its meaning is up to the router.</summary>
    public required string ThinkingLevel { get; init; }

    /// <summary>One of <see cref="VirtualModels.RouteReasons"/>.</summary>
    public required string Reason { get; init; }

    /// <summary>Physical model and thinking level of the latest successful response in <see cref="Messages"/>.</summary>
    public ModelRoutePrevious? Previous { get; init; }

    /// <summary>Set for <c>retry</c> only; see <see cref="ModelRouteFailure"/>.</summary>
    public ModelRouteFailure? Failed { get; init; }

    /// <summary>
    /// Router state last returned on this session branch. Unset before the first state and for
    /// <c>direct</c> requests.
    /// </summary>
    public JsonNode? State { get; init; }

    /// <summary>Conversation for this request, including system messages.</summary>
    public required IReadOnlyList<ChatMessage> Messages { get; init; }

    public CancellationToken Signal { get; init; }
}

/// <summary>Physical model and thinking level for one request.</summary>
public sealed record ModelRoute
{
    public required ModelSpec Model { get; init; }

    public required string ThinkingLevel { get; init; }

    /// <summary>
    /// New router state, stored on the session branch unless it is <c>request.state</c> itself. Return
    /// <c>request.state</c> or <c>null</c> to keep the current state. Must be JSON-serializable.
    /// Ignored for <c>direct</c> requests.
    /// </summary>
    public JsonNode? State { get; init; }
}

/// <summary>A virtual model an extension registers with <c>pi.registerVirtualModel()</c>.</summary>
public sealed record VirtualModelDefinition
{
    /// <summary>Provider the virtual model is listed under. May be a provider with physical models.</summary>
    public required string Provider { get; init; }

    /// <summary>Model id. Must not be the id of a physical model of <see cref="Provider"/>.</summary>
    public required string Id { get; init; }

    public required string Name { get; init; }

    /// <summary>Thinking levels offered for selection. Defaults to <c>["off"]</c>.</summary>
    public IReadOnlyList<string>? ThinkingLevels { get; init; }

    /// <summary>
    /// Limits shown before the first response. Afterwards, Pi uses the limits of the physical model that
    /// answered. Unset limits are unknown (0).
    /// </summary>
    public double? ContextWindow { get; init; }

    public double? MaxTokens { get; init; }

    /// <summary>
    /// Input types accepted for selection. Defaults to text and images; routed models without image
    /// support get placeholders.
    /// </summary>
    public IReadOnlyList<string>? Input { get; init; }

    /// <summary>Pick the physical model, which must have credentials, and thinking level for one request.</summary>
    public required Func<ModelRouteRequest, Task<ModelRoute>> Route { get; init; }
}

/// <summary>Keyless auth of a provider that only lists virtual models.</summary>
internal sealed class VirtualModelApiKeyAuth : IApiKeyAuth
{
    public string Name => "Virtual model";

    public Task<AuthResult?> ResolveAsync(Credential.ApiKey? credential, IAuthContext ctx,
        CancellationToken cancellationToken = default)
        => Task.FromResult<AuthResult?>(new AuthResult { Auth = new ModelAuth(), Source = "virtual" });
}

/// <summary>Shared plumbing of the two provider wrappers below.</summary>
internal abstract class VirtualModelsProviderBase : IProvider, IImagesProvider, IClassifierProvider
{
    protected VirtualModelsProviderBase(string id, string name, ProviderAuth? auth)
    {
        Id = id;
        Name = name;
        Auth = auth;
    }

    public string Id { get; }

    public string Name { get; }

    public virtual string? BaseUrl => null;

    public ProviderAuth? Auth { get; }

    public abstract IReadOnlyList<ModelSpec> GetModels();

    public abstract IReadOnlyList<ModelSpec> GetAllModels();

    public virtual IAssistantMessageEventStream Stream(
        ModelSpec model, IReadOnlyList<ChatMessage> context, IReadOnlyDictionary<string, object?>? options = null)
        => VirtualModels.UnroutedStream(model);

    public virtual IAssistantMessageEventStream StreamSimple(
        ModelSpec model, IReadOnlyList<ChatMessage> context, IReadOnlyDictionary<string, object?>? options = null)
        => VirtualModels.UnroutedStream(model);

    public Task<AssistantImages> GenerateImagesAsync(ModelSpec model, ImagesContext context,
        ImagesOptions? options, CancellationToken cancellationToken)
        => throw new ModelsError(ModelsErrorCode.Provider,
            $"Provider {model.Provider} does not support image generation");

    public Task<ClassifierResult> ClassifyAsync(ModelSpec model, ClassifierContext context,
        ClassifierOptions? options, CancellationToken cancellationToken)
        => throw new ModelsError(ModelsErrorCode.Provider,
            $"Provider {model.Provider} does not support classification");
}

/// <summary>
/// A keyless provider whose catalog is exactly the virtual models. Used when nothing else defines the
/// provider id, so virtual models need no credentials.
/// </summary>
internal sealed class VirtualOnlyProvider : VirtualModelsProviderBase
{
    private readonly IReadOnlyList<ModelSpec> _virtualModels;

    public VirtualOnlyProvider(string providerId, IReadOnlyList<ModelSpec> virtualModels)
        : base(providerId, providerId, new ProviderAuth { ApiKey = new VirtualModelApiKeyAuth() })
        => _virtualModels = virtualModels;

    public override IReadOnlyList<ModelSpec> GetModels() => _virtualModels;

    public override IReadOnlyList<ModelSpec> GetAllModels() => _virtualModels;
}

/// <summary>
/// A provider with virtual models added to its catalog. A virtual model hides a physical chat model with
/// the same id, which a catalog refresh can add after registration.
/// </summary>
internal sealed class VirtualModelsProvider : VirtualModelsProviderBase
{
    private readonly IProvider _inner;
    private readonly IReadOnlyList<ModelSpec> _virtualModels;
    private readonly HashSet<string> _virtualIds;

    public VirtualModelsProvider(IProvider inner, IReadOnlyList<ModelSpec> virtualModels)
        : base(inner.Id, inner.Name, inner.Auth)
    {
        _inner = inner;
        _virtualModels = virtualModels;
        _virtualIds = virtualModels.Select(model => model.Id).ToHashSet(StringComparer.Ordinal);
    }

    public override string? BaseUrl => _inner.BaseUrl;

    /// <summary>Physical models, i.e. the ones a virtual model does not hide.</summary>
    private List<ModelSpec> Physical(IReadOnlyList<ModelSpec> models) => models
        .Where(model => !VirtualModels.IsVirtualModel(model)
            && !(model.Type == ModelType.Chat && _virtualIds.Contains(model.Id)))
        .ToList();

    private static List<ModelSpec> VirtualOf(IReadOnlyList<ModelSpec> models)
        => models.Where(VirtualModels.IsVirtualModel).ToList();

    public override IReadOnlyList<ModelSpec> GetModels() => [.. Physical(_inner.GetModels()), .. _virtualModels];

    public override IReadOnlyList<ModelSpec> GetAllModels()
        => [.. Physical(_inner.GetAllModels()), .. _virtualModels];

    public IReadOnlyList<ModelSpec> FilterModels(IReadOnlyList<ModelSpec> models, Credential? credential)
        => [.. _inner.FilterModels(Physical(models), credential), .. VirtualOf(models)];

    public IReadOnlyList<ModelSpec>? FilterAllModels(IReadOnlyList<ModelSpec> models, Credential? credential)
    {
        // TS only defines `filterAllModels` on the wrapper when the inner provider has one.
        var innerFiltered = _inner.FilterAllModels(Physical(models), credential);
        return innerFiltered is null ? null : [.. innerFiltered, .. VirtualOf(models)];
    }

    public override IAssistantMessageEventStream Stream(
        ModelSpec model, IReadOnlyList<ChatMessage> context, IReadOnlyDictionary<string, object?>? options = null)
        => VirtualModels.IsVirtualModel(model)
            ? VirtualModels.UnroutedStream(model)
            : _inner.Stream(model, context, options);

    public override IAssistantMessageEventStream StreamSimple(
        ModelSpec model, IReadOnlyList<ChatMessage> context, IReadOnlyDictionary<string, object?>? options = null)
        => VirtualModels.IsVirtualModel(model)
            ? VirtualModels.UnroutedStream(model)
            : _inner.StreamSimple(model, context, options);

    public bool SupportsFetchDeferred => _inner.SupportsFetchDeferred;

    public bool SupportsCancelDeferred => _inner.SupportsCancelDeferred;

    public IAssistantMessageEventStream? StreamDeferred(ModelSpec model, DeferredHandle handle,
        IReadOnlyDictionary<string, object?>? options = null)
        => _inner.StreamDeferred(model, handle, options);

    public Task CancelDeferredAsync(ModelSpec model, DeferredHandle handle,
        IReadOnlyDictionary<string, object?>? options = null, CancellationToken cancellationToken = default)
        => _inner.CancelDeferredAsync(model, handle, options, cancellationToken);
}
