using System.Linq.Expressions;
using System.Reflection;
using System.Text.Json;
using Jint.Native;
using Pi.Chord;
using Pi.Chord.Context;
using Pi.Chord.Delta;
using Pi.Chord.Facets;
using Pi.Chord.Services;
using Pi.CodingAgent.Experimental.Services;

using ChordContext = Pi.Chord.Context.Context;
using DeltaPath = Pi.Chord.Delta.Path;

namespace Pi.CodingAgent.Experimental.Plugins;

internal sealed class JintFacetEnvironmentBridge(JintPluginRuntime runtime, IFacetEnvironment environment)
{
    internal static JsonSerializerOptions JsonOptions { get; } = new(JsonSerializerDefaults.Web)
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    public void ReportError(Exception error) => environment.ReportError(error);

    public void Require(string serviceId, bool local)
        => environment.Require(new Service<object>(serviceId, local));

    public void Provide(string serviceId, bool local, string membersJson)
    {
        var members = ReadImplementation(serviceId, local, membersJson);
        environment.Provide(new Service<IReadOnlyDictionary<string, object>>(serviceId, local), members);
    }

    public string ProvideMany(string serviceId, bool local)
        => runtime.AddSpawner(environment.ProvideMany(
            new Service<IReadOnlyDictionary<string, object>>(serviceId, local)), local);

    public string SpawnMany(string spawnerId, string key, string membersJson)
    {
        var (spawner, local) = runtime.GetSpawner(spawnerId);
        var implementation = ReadImplementation(serviceId: null, local, membersJson);
        var close = spawner.Spawn(key, implementation);
        return runtime.AddDisposable(new ActionDisposable(close));
    }

    public string CreateState(string initialJson) => runtime.CreateState(initialJson);

    public string ReadState(string stateId) => runtime.GetState(stateId).ValueJson;

    public void ChangeState(string stateId, string? contextId, string description, string valueJson)
    {
        var context = runtime.ResolveContext(contextId, description, required: true);
        runtime.GetState(stateId).Change(context, valueJson);
    }

    public string SubscribeState(string stateId, string callbackId)
        => runtime.AddDisposable(Subscribe(runtime.GetState(stateId), callbackId));

    public void OwnFunction(string callbackId)
        => environment.Own(async () =>
        {
            await runtime.InvokeFunctionAsync(callbackId, [], null, -1, reportError: ReportError)
                .ConfigureAwait(false);
        });

    public void OnActivate(string callbackId)
        => environment.OnActivate(async () =>
        {
            await runtime.InvokeFunctionAsync(callbackId, [], null, -1, reportError: ReportError)
                .ConfigureAwait(false);
        });

    public void OnDeactivate(string callbackId)
        => environment.OnDeactivate(async () =>
        {
            await runtime.InvokeFunctionAsync(callbackId, [], null, -1, reportError: ReportError)
                .ConfigureAwait(false);
        });

    public void Observe(string serviceId, bool local, string callbackId)
        => environment.Observe(new Service<object>(serviceId, local), (service, context) =>
        {
            var instanceId = runtime.AddObservedService(service);
            environment.Own(() =>
            {
                runtime.RemoveObservedService(instanceId);
                return Task.CompletedTask;
            });
            return runtime.InvokeObservedCallbackAsync(this, callbackId, serviceId, local, instanceId, context);
        });

    public string MemberKind(string serviceId, bool local, string member)
        => MemberKind(Use(serviceId, local), member);

    public string ReadMemberValue(string serviceId, bool local, string member)
    {
        var target = Use(serviceId, local);
        if (!TryResolveMember(target, member, out var value))
        {
            throw new InvalidOperationException($"Unknown service member {serviceId}.{member}");
        }
        if (IsState(value) || value is Delegate)
        {
            throw new InvalidOperationException($"Service member {serviceId}.{member} is not a JSON value");
        }
        return Serialize(value);
    }

    public string ReadMemberState(string serviceId, bool local, string member)
    {
        var target = Use(serviceId, local);
        if (target is RemoteServiceFacade remote)
        {
            return Serialize(remote.Member(member).Value);
        }
        if (!TryResolveMember(target, member, out var value) || !IsState(value))
        {
            throw new InvalidOperationException($"Service member {serviceId}.{member} is not replicated state");
        }
        return Serialize(ReadStateValue(value!));
    }

    public string ReadObservedMemberValue(string instanceId, string member)
    {
        var target = runtime.GetObservedService(instanceId);
        if (!TryResolveMember(target, member, out var value))
        {
            throw new InvalidOperationException($"Unknown observed service member {member}");
        }
        if (IsState(value) || value is Delegate)
        {
            throw new InvalidOperationException($"Observed service member {member} is not a JSON value");
        }
        return Serialize(value);
    }

    public string ReadObservedMemberState(string instanceId, string member)
    {
        var target = runtime.GetObservedService(instanceId);
        if (target is RemoteServiceFacade remote)
        {
            return Serialize(remote.Member(member).Value);
        }
        if (!TryResolveMember(target, member, out var value) || !IsState(value))
        {
            throw new InvalidOperationException($"Observed service member {member} is not replicated state");
        }
        return Serialize(ReadStateValue(value!));
    }

    public string ObservedMemberKind(string instanceId, string member)
        => MemberKind(runtime.GetObservedService(instanceId), member);

    public string? LocalFunctionId(string serviceId, bool local, string member)
    {
        var target = Use(serviceId, local);
        return TryResolveMember(target, member, out var value)
            ? runtime.GetLocalFunctionId(value)
            : null;
    }

    public string? ObservedFunctionId(string instanceId, string member)
    {
        var target = runtime.GetObservedService(instanceId);
        return TryResolveMember(target, member, out var value)
            ? runtime.GetLocalFunctionId(value)
            : null;
    }

    public async Task<string> InvokeObserved(string instanceId, string member, string argumentsJson,
        string? contextId, string description)
    {
        var result = await InvokeTargetAsync(runtime.GetObservedService(instanceId), serviceId: null, local: false,
            member, argumentsJson, contextId, description).ConfigureAwait(false);
        return Serialize(result);
    }

    public async Task<string> Invoke(string serviceId, bool local, string member, string argumentsJson,
        string? contextId, string description)
    {
        var result = await InvokeTargetAsync(Use(serviceId, local), serviceId, local,
            member, argumentsJson, contextId, description).ConfigureAwait(false);
        return Serialize(result);
    }

    public void ShowStatus(string message, string? contextId, string description)
    {
        var context = runtime.ResolveContext(contextId, description, required: true);
        environment.Use(PresentationUI.Service).ShowStatus(message, context);
    }

    public string SubscribeMemberState(string serviceId, bool local, string member, string callbackId)
        => runtime.AddDisposable(Subscribe(ResolveState(Use(serviceId, local), member), callbackId));

    public string SubscribeObservedMemberState(string instanceId, string member, string callbackId)
        => runtime.AddDisposable(Subscribe(ResolveState(runtime.GetObservedService(instanceId), member), callbackId));

    public void Dispose(string handleId) => runtime.DisposeHandle(handleId);

    public string RegisterSlashCommand(string method, string commandJson)
    {
        var command = JsSlashCommandContribution.Parse(runtime, commandJson, ReportError);
        var commands = environment.Use(SlashCommands.Service);
        var handle = method switch
        {
            "replace" => commands.Replace(command),
            "register" => commands.Register(command),
            _ => throw new InvalidOperationException($"Unknown slash command operation {method}"),
        };
        return runtime.AddDisposable(handle);
    }

    public string ListSlashCommands()
        => Serialize(environment.Use(SlashCommands.Service).List().Select(CommandMetadata).ToList());

    public string SubscribeSlashCommands(string callbackId)
    {
        var commands = environment.Use(SlashCommands.Service);
        var handle = commands.Subscribe(items =>
        {
            var payload = items.Select(CommandMetadata).ToList();
            _ = InvokeAndReportErrorAsync(callbackId, [payload]);
        });
        return runtime.AddDisposable(handle);
    }

    private object Use(string serviceId, bool local)
        => environment.Use(new Service<object>(serviceId, local));

    private async Task InvokeAndReportErrorAsync(string callbackId, IReadOnlyList<object?> arguments)
    {
        try
        {
            await runtime.InvokeFunctionAsync(callbackId, arguments, null, -1, reportError: ReportError)
                .ConfigureAwait(false);
        }
        catch (Exception error)
        {
            ReportError(error);
        }
    }

    private Dictionary<string, object> ReadImplementation(string? serviceId, bool local, string membersJson)
    {
        using var document = JsonDocument.Parse(membersJson);
        var members = new Dictionary<string, object>(StringComparer.Ordinal);
        foreach (var descriptor in document.RootElement.EnumerateArray())
        {
            var name = descriptor.GetProperty("name").GetString()
                ?? throw new InvalidOperationException("JavaScript service member has no name");
            var kind = descriptor.GetProperty("kind").GetString();
            var displayServiceId = serviceId ?? "keyed service";
            object value = kind switch
            {
                "state" => runtime.GetState(descriptor.GetProperty("stateId").GetString()!),
                "function" => CreateFunctionMember(descriptor.GetProperty("functionId").GetString()!),
                "value" when local => ParseJson(descriptor.GetProperty("json").GetString()!)!,
                "value" => throw new InvalidOperationException(
                    $"Remote service member {displayServiceId}.{name} is not a method or replicated state"),
                _ => throw new InvalidOperationException($"Unsupported JavaScript service member kind {kind}"),
            };
            if (!members.TryAdd(name, value))
            {
                throw new InvalidOperationException($"Duplicate JavaScript service member {displayServiceId}.{name}");
            }
        }
        if (!local && members.Count == 0)
        {
            throw new InvalidOperationException($"Remote service {serviceId ?? "keyed service"} has no members");
        }
        return members;
    }

    private sealed class ActionDisposable(Action dispose) : IDisposable
    {
        private bool _disposed;

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            dispose();
        }
    }

    private string MemberKind(object target, string member)
    {
        if (target is RemoteServiceFacade remote)
        {
            if (!remote.TryGetValue(member, out var remoteValue)) return "unknown";
            return IsState(remoteValue) ? "state" : "method";
        }
        if (TryResolveMember(target, member, out var value))
        {
            if (IsState(value)) return "state";
            if (value is Delegate) return "method";
            return "value";
        }
        return ResolveMethod(target, member) is null ? "unknown" : "method";
    }

    private static bool TryResolveMember(object target, string member, out object? value)
    {
        if (target is IReadOnlyDictionary<string, object?> nullableMap)
        {
            return nullableMap.TryGetValue(member, out value);
        }
        if (target is IReadOnlyDictionary<string, object> map)
        {
            var found = map.TryGetValue(member, out var item);
            value = item;
            return found;
        }
        var property = target.GetType().GetProperty(ToPascalCase(member),
            BindingFlags.Public | BindingFlags.Instance);
        if (property is not null && property.GetIndexParameters().Length == 0)
        {
            value = property.GetValue(target);
            return true;
        }
        var field = target.GetType().GetField(ToPascalCase(member),
            BindingFlags.Public | BindingFlags.Instance);
        if (field is not null)
        {
            value = field.GetValue(target);
            return true;
        }
        value = null;
        return false;
    }

    private static bool IsState(object? value)
        => value is IReplicatedStateInternals || value is RemoteServiceMember
            || value is not null && value.GetType().GetInterfaces().Any(type =>
                type.IsGenericType && type.GetGenericTypeDefinition() == typeof(IReplicatedState<>));

    private static object? ReadStateValue(object value)
    {
        if (value is JintReplicatedState state) return state.Value;
        if (value is IReplicatedState<object?> objectState) return objectState.Value;
        var property = value.GetType().GetProperty("Value", BindingFlags.Public | BindingFlags.Instance)
            ?? throw new InvalidOperationException("Replicated state has no value");
        return property.GetValue(value);
    }

    private async Task<object?> InvokeTargetAsync(object target, string? serviceId, bool local, string member,
        string argumentsJson, string? contextId, string description)
    {
        var arguments = ParseJson(argumentsJson) as List<object?>
            ?? throw new InvalidOperationException("JavaScript service arguments must be an array");
        var context = runtime.ResolveContext(contextId, description, required: !local);
        if (target is RemoteServiceFacade remote)
        {
            return await remote.Member(member).CallAsync([.. arguments], context).ConfigureAwait(false);
        }
        if (TryResolveMember(target, member, out var memberValue) && memberValue is Delegate)
        {
            var resolved = new ResolvedServiceMember(memberValue, target);
            var result = ServiceSlot.InvokeValue(resolved, member, [.. arguments, context]);
            return await AwaitResult(result).ConfigureAwait(false);
        }

        if (target is IAgentController controller)
        {
            return member switch
            {
                "prompt" => await controller.PromptAsync(ToPromptRequest(RequiredArgument(arguments, 0)), context)
                    .ConfigureAwait(false),
                "steer" => await controller.SteerAsync(ToPromptRequest(RequiredArgument(arguments, 0)), context)
                    .ConfigureAwait(false),
                "followUp" => await controller.FollowUpAsync(ToPromptRequest(RequiredArgument(arguments, 0)), context)
                    .ConfigureAwait(false),
                "cancelQueued" => await controller.CancelQueuedAsync(
                    ReadString(RequiredArgument(arguments, 0), "entryId"), context).ConfigureAwait(false),
                "abort" => await AbortAsync(controller, context).ConfigureAwait(false),
                "compact" => await controller.CompactAsync(ToCompactionRequest(RequiredArgument(arguments, 0)), context)
                    .ConfigureAwait(false),
                "waitForPrompt" => await controller.WaitForPromptAsync(
                    ReadString(RequiredArgument(arguments, 0), "operationId"), context).ConfigureAwait(false),
                _ => throw new InvalidOperationException($"Unknown AgentController method {member}"),
            };
        }
        if (target is IPresentationUI ui)
        {
            if (member != "select")
            {
                throw new InvalidOperationException($"Unknown PresentationUI method {member}");
            }
            var items = ReadSelectItems(RequiredArgument(arguments, 1));
            return await ui.SelectAsync(
                ReadString(RequiredArgument(arguments, 0), "title"),
                items,
                OptionalString(arguments, 2),
                context).ConfigureAwait(false);
        }

        var method = ResolveMethod(target, member)
            ?? throw new InvalidOperationException($"Unknown service method {serviceId ?? member}.{member}");
        var parameters = method.GetParameters();
        var needsContext = parameters.Length > 0 && parameters[^1].ParameterType == typeof(ChordContext);
        if (parameters.Length != arguments.Count + (needsContext ? 1 : 0))
        {
            throw new InvalidOperationException($"Incorrect argument count for service method {member}");
        }
        var callArguments = new object?[parameters.Length];
        for (var index = 0; index < arguments.Count; index++)
        {
            callArguments[index] = ConvertArgument(arguments[index], parameters[index].ParameterType);
        }
        if (needsContext) callArguments[^1] = context;
        return await AwaitResult(method.Invoke(target, callArguments)).ConfigureAwait(false);
    }

    private async Task<AgentSubmissionResponse?> RunSlashCommandAsync(string callbackId, string args,
        ChordContext context)
    {
        var result = await runtime.InvokeFunctionAsync(callbackId, [args], context, 1,
            context.AbortSignal ?? CancellationToken.None, ReportError).ConfigureAwait(false);
        return ToSubmissionResponse(result);
    }

    private async Task<IReadOnlyList<SlashCommandCompletion>?> CompleteSlashCommandAsync(
        string callbackId, string prefix)
    {
        var result = await runtime.InvokeFunctionAsync(callbackId, [prefix], null, -1).ConfigureAwait(false);
        if (result is null) return null;
        if (result is not List<object?> items)
        {
            throw new InvalidOperationException("Slash command completions must be an array or undefined");
        }
        return items.Select(item => new SlashCommandCompletion(
            ReadString(RequiredObjectValue(item, "value"), "value"),
            ReadString(RequiredObjectValue(item, "label"), "label"),
            ReadOptionalString(RequiredObjectValue(item, "description")))).ToList();
    }

    private static object CommandMetadata(ISlashCommandContribution command)
        => new
        {
            name = command.Name,
            description = command.Description,
            argumentHint = command.ArgumentHint,
        };

    private static async Task<object?> AbortAsync(IAgentController controller, ChordContext context)
    {
        await controller.AbortAsync(context).ConfigureAwait(false);
        return null;
    }

    private static AgentPromptRequest ToPromptRequest(object? value)
    {
        var request = RequireMap(value, "prompt request");
        var message = ReadString(RequiredObjectValue(request, "message"), "message");
        if (!request.TryGetValue("images", out var imageValue) || imageValue is null)
        {
            return new AgentPromptRequest(message, null);
        }
        if (imageValue is not List<object?> images)
        {
            throw new InvalidOperationException("Prompt images must be an array or null");
        }
        return new AgentPromptRequest(message, images.Select(image =>
        {
            var item = RequireMap(image, "prompt image");
            return new AgentPromptImage(
                ReadString(RequiredObjectValue(item, "data"), "data"),
                ReadString(RequiredObjectValue(item, "mimeType"), "mimeType"));
        }).ToList());
    }

    private static AgentCompactionRequest ToCompactionRequest(object? value)
        => new(OptionalString(RequireMap(value, "compaction request"), "customInstructions"));

    private static IReadOnlyList<PresentationSelectItem> ReadSelectItems(object? value)
    {
        if (value is not List<object?> items)
        {
            throw new InvalidOperationException("Presentation select items must be an array");
        }
        return items.Select(item =>
        {
            var map = RequireMap(item, "presentation select item");
            return new PresentationSelectItem(
                ReadString(RequiredObjectValue(map, "value"), "value"),
                ReadString(RequiredObjectValue(map, "label"), "label"),
                ReadOptionalString(RequiredObjectValue(map, "description")));
        }).ToList();
    }

    private static object? RequiredArgument(IReadOnlyList<object?> arguments, int index)
        => index < arguments.Count
            ? arguments[index]
            : throw new InvalidOperationException($"Missing service argument {index + 1}");

    private static object? RequiredObjectValue(object? value, string name)
    {
        var map = RequireMap(value, name);
        return map.TryGetValue(name, out var result)
            ? result
            : throw new InvalidOperationException($"Missing {name}");
    }

    private static Dictionary<string, object?> RequireMap(object? value, string description)
        => value as Dictionary<string, object?>
            ?? throw new InvalidOperationException($"{description} must be an object");

    private static string ReadString(object? value, string description)
        => value as string ?? throw new InvalidOperationException($"{description} must be a string");

    private static string? ReadOptionalString(object? value)
        => value is null ? null : value as string
            ?? throw new InvalidOperationException("Optional value must be a string or null");

    private static string? OptionalString(IReadOnlyList<object?> values, int index)
        => index >= values.Count ? null : ReadOptionalString(values[index]);

    private static string? OptionalString(Dictionary<string, object?> values, string name)
        => values.TryGetValue(name, out var value) ? ReadOptionalString(value) : null;

    private static MethodInfo? ResolveMethod(object target, string member)
        => target.GetType().GetMethods(BindingFlags.Public | BindingFlags.Instance)
            .FirstOrDefault(method => !method.IsSpecialName && !method.ContainsGenericParameters
                && method.Name == ToPascalCase(member));

    private static string ToPascalCase(string name)
        => name.Length == 0 ? name : char.ToUpperInvariant(name[0]) + name[1..];

    private static object? ConvertArgument(object? value, Type type)
    {
        if (value is null) return null;
        if (type.IsInstanceOfType(value)) return value;
        return JsonSerializer.Deserialize(JsonSerializer.Serialize(value, JsonOptions), type, JsonOptions);
    }

    private static async Task<object?> AwaitResult(object? result)
    {
        if (result is not Task task) return result;
        await task.ConfigureAwait(false);
        return task.GetType().GetProperty("Result")?.GetValue(task);
    }

    private static object? NormalizeResult(object? value)
    {
        switch (value)
        {
            case AgentOperationResponse.Accepted accepted:
                return new { accepted = true, operationId = accepted.OperationId };
            case AgentQueueResponse.Accepted accepted:
                return new { accepted = true, entryId = accepted.EntryId };
            case AgentOperationResponse.Rejected operationRejected:
                return new
                {
                    accepted = false,
                    error = new { code = operationRejected.Reason.Code, message = operationRejected.Reason.Message },
                };
            case AgentQueueResponse.Rejected queueRejected:
                return new
                {
                    accepted = false,
                    error = new { code = queueRejected.Reason.Code, message = queueRejected.Reason.Message },
                };
            case AgentPromptResult.Done done:
                return new { status = "done", text = done.Text };
            case AgentPromptResult.Unanswered unanswered:
                return new { status = "unanswered", reason = unanswered.Reason };
            case AgentCancelOutcome.Cancelled:
                return "cancelled";
            case AgentCancelOutcome.AlreadyConsumed:
                return "alreadyConsumed";
            case AgentCancelOutcome.NotFound:
                return "notFound";
            default:
                return value;
        }
    }

    private static AgentSubmissionResponse? ToSubmissionResponse(object? value)
    {
        if (value is null) return null;
        var map = RequireMap(value, "slash command result");
        var accepted = map.TryGetValue("accepted", out var acceptedValue)
            && acceptedValue is bool acceptedFlag
            ? acceptedFlag
            : throw new InvalidOperationException("Slash command result must include accepted");
        if (accepted)
        {
            if (map.TryGetValue("operationId", out var operationId))
            {
                return new AgentOperationResponse.Accepted(ReadString(operationId, "operationId"));
            }
            if (map.TryGetValue("entryId", out var entryId))
            {
                return new AgentQueueResponse.Accepted(ReadString(entryId, "entryId"));
            }
            throw new InvalidOperationException("Accepted slash command result must include an operationId or entryId");
        }
        var error = RequireMap(RequiredObjectValue(map, "error"), "slash command error");
        return new AgentOperationResponse.Rejected(new AgentOperationError(
            ReadString(RequiredObjectValue(error, "code"), "code"),
            ReadString(RequiredObjectValue(error, "message"), "message")));
    }

    private static string Serialize(object? value)
        => JsonSerializer.Serialize(NormalizeResult(value), JsonOptions);

    internal static object? ParseJson(string json)
    {
        using var document = JsonDocument.Parse(json);
        return ParseJson(document.RootElement);
    }

    private static object? ParseJson(JsonElement value)
        => value.ValueKind switch
        {
            JsonValueKind.Null => null,
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            JsonValueKind.String => value.GetString(),
            JsonValueKind.Number when value.TryGetInt64(out var integer) => integer,
            JsonValueKind.Number => value.GetDouble(),
            JsonValueKind.Array => value.EnumerateArray().Select(ParseJson).ToList(),
            JsonValueKind.Object => value.EnumerateObject()
                .ToDictionary(property => property.Name, property => ParseJson(property.Value), StringComparer.Ordinal),
            _ => throw new InvalidOperationException("JavaScript value is not JSON"),
        };

    private Delegate CreateFunctionMember(string functionId)
    {
        Func<object?[], object?> function = arguments =>
        {
            if (arguments.Length == 0 || arguments[^1] is not ChordContext context)
            {
                throw new InvalidOperationException("Remote JavaScript service calls require a trailing Context");
            }
            return InvokeProvidedFunctionAsync(functionId, arguments[..^1], context);
        };
        runtime.RegisterFunctionDelegate(function, functionId);
        return function;
    }

    private object? InvokeProvidedFunctionAsync(string functionId, object?[] arguments, ChordContext context)
        => runtime.InvokeFunctionAsync(functionId, arguments, context, arguments.Length,
            context.AbortSignal ?? CancellationToken.None, ReportError);

    private IDisposable Subscribe(JintReplicatedState state, string callbackId)
        => state.Subscribe((value, context, delivery) =>
            DispatchStateCallback(callbackId, value, context, delivery));

    private Task? DispatchStateCallback(string callbackId, object? value, ChordContext context,
        ReplicatedStateDelivery delivery)
        => runtime.InvokeFunctionAsync(callbackId,
            [value, new Dictionary<string, object?>
            {
                ["kind"] = delivery.Kind == ReplicatedStateDeliveryKind.Hydrate ? "hydrate" : "update",
                ["sequence"] = delivery.Sequence,
            }], context, 1, context.AbortSignal ?? CancellationToken.None, ReportError);

    private IDisposable Subscribe(object state, string callbackId)
    {
        if (state is RemoteServiceMember member)
        {
            return member.Subscribe((value, context, delivery) =>
                DispatchStateCallback(callbackId, value, context, delivery));
        }
        if (state is JintReplicatedState jsState)
        {
            return jsState.Subscribe((value, context, delivery) =>
                DispatchStateCallback(callbackId, value, context, delivery));
        }
        if (state is IReplicatedState<object?> objectState)
        {
            return objectState.Subscribe((value, context, delivery) =>
                DispatchStateCallback(callbackId, value, context, delivery));
        }

        var stateInterface = state.GetType().GetInterfaces().FirstOrDefault(type =>
            type.IsGenericType && type.GetGenericTypeDefinition() == typeof(IReplicatedState<>))
            ?? throw new InvalidOperationException("Unsupported replicated state implementation");
        var subscribe = stateInterface.GetMethod(nameof(IReplicatedState<object>.Subscribe))!;
        var listenerType = subscribe.GetParameters()[0].ParameterType;
        var parameters = listenerType.GetMethod("Invoke")!.GetParameters()
            .Select(parameter => Expression.Parameter(parameter.ParameterType, parameter.Name))
            .ToArray();
        var dispatch = typeof(JintFacetEnvironmentBridge).GetMethod(
            nameof(DispatchStateCallback), BindingFlags.Instance | BindingFlags.NonPublic)!;
        var body = Expression.Call(Expression.Constant(this), dispatch,
            Expression.Constant(callbackId),
            Expression.Convert(parameters[0], typeof(object)),
            parameters[1],
            parameters[2]);
        var callback = Expression.Lambda(listenerType, body, parameters).Compile();
        return (IDisposable)subscribe.Invoke(state, [callback])!;
    }

    private object ResolveState(object target, string member)
    {
        if (target is RemoteServiceFacade remote) return remote.Member(member);
        if (!TryResolveMember(target, member, out var value) || !IsState(value))
        {
            throw new InvalidOperationException($"Service member {member} is not replicated state");
        }
        return value ?? throw new InvalidOperationException($"Service member {member} is null");
    }
}

internal abstract class JintReplicatedState : IReplicatedStateInternals, IReplicatedState<object?>
{
    public abstract object? Value { get; }

    public abstract string ValueJson { get; }

    public abstract void Change(ChordContext context, string json);

    public abstract void Replace(ChordContext context, string json);

    public abstract IDisposable Subscribe(StateListener<object?> listener);

    public abstract (object? Value, long Sequence) Snapshot();

    public abstract IDisposable SubscribeSource(SourceListener listener);
}

internal sealed class JintReplicatedState<T> : JintReplicatedState where T : class
{
    private readonly MutableReplicatedState<T> _state;
    private readonly IReplicatedStateInternals _internals;

    public JintReplicatedState(T initial)
    {
        _state = new MutableReplicatedState<T>(initial);
        _internals = (IReplicatedStateInternals)_state;
    }

    public override object? Value => _state.Value;

    public override string ValueJson => JsonSerializer.Serialize(_state.Value, JintFacetEnvironmentBridge.JsonOptions);

    public override void Change(ChordContext context, string json)
    {
        var next = JintFacetEnvironmentBridge.ParseJson(json);
        if (next is not T typed)
        {
            throw new InvalidOperationException("Replicated state cannot change its root container type");
        }
        _state.Change(context, change => JintJsonDiff.Apply(change, _state.Value, typed));
    }

    public override void Replace(ChordContext context, string json)
    {
        var next = JintFacetEnvironmentBridge.ParseJson(json);
        if (next is not T typed)
        {
            throw new InvalidOperationException("Replicated state cannot change its root container type");
        }
        _state.Replace(context, typed);
    }

    public override IDisposable Subscribe(StateListener<object?> listener)
        => _state.Subscribe((value, context, delivery) => listener(value, context, delivery));

    public override (object? Value, long Sequence) Snapshot() => _internals.Snapshot();

    public override IDisposable SubscribeSource(SourceListener listener) => _internals.SubscribeSource(listener);
}

internal static class JintJsonDiff
{
    public static void Apply<T>(Tracker<T>.Change change, object? current, object? next) where T : class
        => Diff(change, current, next, DeltaPath.Root);

    private static void Diff<T>(Tracker<T>.Change change, object? current, object? next, DeltaPath path)
        where T : class
    {
        if (JsonEqual(current, next)) return;
        if (current is Dictionary<string, object?> currentMap
            && next is Dictionary<string, object?> nextMap)
        {
            foreach (var key in currentMap.Keys.Where(key => !nextMap.ContainsKey(key)))
            {
                change.Delete(path.Append(Seg.Key(key)));
            }
            foreach (var (key, nextValue) in nextMap)
            {
                if (currentMap.TryGetValue(key, out var currentValue))
                {
                    Diff(change, currentValue, nextValue, path.Append(Seg.Key(key)));
                }
                else
                {
                    change.Set(path.Append(Seg.Key(key)), nextValue);
                }
            }
            return;
        }
        if (current is List<object?> currentList && next is List<object?> nextList)
        {
            if (currentList.Count == nextList.Count)
            {
                for (var index = 0; index < currentList.Count; index++)
                {
                    Diff(change, currentList[index], nextList[index], path.Append(Seg.Index(index)));
                }
                return;
            }

            var prefix = 0;
            while (prefix < currentList.Count && prefix < nextList.Count
                && JsonEqual(currentList[prefix], nextList[prefix]))
            {
                prefix++;
            }
            var suffix = 0;
            while (suffix < currentList.Count - prefix && suffix < nextList.Count - prefix
                && JsonEqual(currentList[currentList.Count - suffix - 1], nextList[nextList.Count - suffix - 1]))
            {
                suffix++;
            }
            var inserted = nextList.Skip(prefix).Take(nextList.Count - prefix - suffix).ToList();
            change.Splice(path, prefix, currentList.Count - prefix - suffix, inserted);
            return;
        }
        if (path.IsEmpty)
        {
            throw new InvalidOperationException("Replicated state cannot replace its root container through change()");
        }
        change.Set(path, next);
    }

    private static bool JsonEqual(object? left, object? right)
    {
        if (ReferenceEquals(left, right)) return true;
        if (left is Dictionary<string, object?> leftMap && right is Dictionary<string, object?> rightMap)
        {
            return leftMap.Count == rightMap.Count
                && leftMap.All(pair => rightMap.TryGetValue(pair.Key, out var value) && JsonEqual(pair.Value, value));
        }
        if (left is List<object?> leftList && right is List<object?> rightList)
        {
            return leftList.Count == rightList.Count
                && leftList.Zip(rightList).All(pair => JsonEqual(pair.First, pair.Second));
        }
        return Equals(left, right);
    }
}

internal sealed class JsSlashCommandContribution : ISlashCommandContribution
{
    private readonly JintPluginRuntime _runtime;
    private readonly string _runId;
    private readonly string? _completionId;
    private readonly Action<Exception> _reportError;

    private JsSlashCommandContribution(JintPluginRuntime runtime, string name, string? description,
        string? argumentHint, string runId, string? completionId, Action<Exception> reportError)
        => (_runtime, Name, Description, ArgumentHint, _runId, _completionId, _reportError)
            = (runtime, name, description, argumentHint, runId, completionId, reportError);

    public string Name { get; }

    public string? Description { get; }

    public string? ArgumentHint { get; }

    public Func<string, Task<IReadOnlyList<SlashCommandCompletion>?>>? GetArgumentCompletions
        => _completionId is null ? null : prefix => CompleteAsync(_completionId, prefix);

    public Task<AgentSubmissionResponse?> RunAsync(string args, ChordContext context)
        => RunAsyncCore(args, context);

    public static JsSlashCommandContribution Parse(
        JintPluginRuntime runtime, string json, Action<Exception> reportError)
    {
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        return new JsSlashCommandContribution(
            runtime,
            root.GetProperty("name").GetString() ?? throw new InvalidOperationException("Slash command name is missing"),
            GetOptionalString(root, "description"),
            GetOptionalString(root, "argumentHint"),
            root.GetProperty("runId").GetString() ?? throw new InvalidOperationException("Slash command run handler is missing"),
            GetOptionalString(root, "completionId"),
            reportError);
    }

    private async Task<AgentSubmissionResponse?> RunAsyncCore(string args, ChordContext context)
    {
        var result = await _runtime.InvokeFunctionAsync(_runId, [args], context, 1,
            context.AbortSignal ?? CancellationToken.None, _reportError).ConfigureAwait(false);
        return ToSubmissionResponse(result);
    }

    private async Task<IReadOnlyList<SlashCommandCompletion>?> CompleteAsync(string callbackId, string prefix)
    {
        var result = await _runtime.InvokeFunctionAsync(callbackId, [prefix], null, -1).ConfigureAwait(false);
        if (result is null) return null;
        if (result is not List<object?> values)
        {
            throw new InvalidOperationException("Slash command completions must be an array or undefined");
        }
        return values.Select(value =>
        {
            var item = value as Dictionary<string, object?>
                ?? throw new InvalidOperationException("Slash command completion must be an object");
            return new SlashCommandCompletion(
                ReadString(item, "value"),
                ReadString(item, "label"),
                item.TryGetValue("description", out var description) && description is not null
                    ? description as string ?? throw new InvalidOperationException("Completion description must be a string")
                    : null);
        }).ToList();
    }

    private static AgentSubmissionResponse? ToSubmissionResponse(object? value)
    {
        if (value is null) return null;
        if (value is not Dictionary<string, object?> map
            || !map.TryGetValue("accepted", out var acceptedValue)
            || acceptedValue is not bool accepted)
        {
            throw new InvalidOperationException("Slash command result must include accepted");
        }
        if (accepted)
        {
            if (map.TryGetValue("operationId", out var operationId) && operationId is string operation)
                return new AgentOperationResponse.Accepted(operation);
            if (map.TryGetValue("entryId", out var entryId) && entryId is string entry)
                return new AgentQueueResponse.Accepted(entry);
            throw new InvalidOperationException("Accepted slash command result must include an operationId or entryId");
        }
        if (map.TryGetValue("error", out var errorValue)
            && errorValue is Dictionary<string, object?> error
            && error.TryGetValue("code", out var code)
            && error.TryGetValue("message", out var message)
            && code is string codeText
            && message is string messageText)
        {
            return new AgentOperationResponse.Rejected(new AgentOperationError(codeText, messageText));
        }
        throw new InvalidOperationException("Rejected slash command result must include an error");
    }

    private static string ReadString(Dictionary<string, object?> item, string name)
        => item.TryGetValue(name, out var value) && value is string text
            ? text
            : throw new InvalidOperationException($"Slash command completion {name} must be a string");

    private static string? GetOptionalString(JsonElement root, string name)
        => root.TryGetProperty(name, out var value) && value.ValueKind != JsonValueKind.Null
            ? value.GetString()
            : null;
}
