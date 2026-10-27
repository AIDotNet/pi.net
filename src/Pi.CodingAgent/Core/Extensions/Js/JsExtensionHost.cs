// ============================================================================
// JavaScript extension host bridge
// ============================================================================
//
// The C# end of `__piHost.call(method, argsJson)`. Every JavaScript request —
// `pi.registerTool`, `ctx.ui.notify`, `fs.readFile`, … — arrives here as one
// method name plus a JSON payload, and is answered with a JSON string.
//
// Why a JSON bridge instead of exposing CLR objects to Jint:
//   - the upstream API is camelCase JavaScript; CLR members are PascalCase and
//     would need a member-name resolver and per-type wrappers,
//   - the payload shapes (tool definitions, contexts, results) already travel as
//     JSON at the provider boundary, so there is one marshalling story,
//   - a bridge is a single place to report "not supported yet" instead of a
//     NullReferenceException from somewhere inside the interpreter.

using System.Text.Json;
using System.Text.Json.Nodes;
using Jint;
using Jint.Native;
using Pi.Agent.Types;
using Pi.Ai.Types;
using Pi.CodingAgent.Core.Extensions.Types;
using Pi.Tui;

namespace Pi.CodingAgent.Core.Extensions.Js;

internal sealed class JsExtensionHost(string cwd, Engine engine) : IDisposable
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
    };

    /// <summary>JSON.stringify as a callable: Jint values only reach C# as JS text this way.</summary>
    private readonly JsValue stringify = engine.Evaluate("(value) => JSON.stringify(value === undefined ? null : value)");

    private readonly Dictionary<string, object> contexts = new(StringComparer.Ordinal);
    private readonly Dictionary<string, AgentToolUpdateCallback> toolUpdates = new(StringComparer.Ordinal);
    private IExtensionApi? api;
    private bool disposed;

    /// <summary>Invokes a JavaScript callback by id; supplied by the runtime.</summary>
    internal Func<string, JsValue[], JsValue>? Invoke { get; set; }

    internal string Cwd { get; } = cwd;

    internal void Bind(IExtensionApi extensionApi) => api = extensionApi;

    internal void Unbind(IExtensionApi extensionApi)
    {
        if (ReferenceEquals(api, extensionApi))
        {
            api = null;
        }
    }

    private IExtensionApi Api => api
        ?? throw new InvalidOperationException(
            "This extension used its pi object outside a host call. Registrations must happen inside the "
            + "factory, a command handler, a tool execution or an event handler.");

    internal string Platform() => OperatingSystem.IsWindows() ? "win32" : OperatingSystem.IsMacOS() ? "darwin" : "linux";

    internal string EnvironmentJson()
    {
        var values = new JsonObject();
        foreach (System.Collections.DictionaryEntry entry in Environment.GetEnvironmentVariables())
        {
            if (entry.Key is string key && entry.Value is string value)
            {
                values[key] = value;
            }
        }

        return values.ToJsonString();
    }

    internal void Log(string level, string message)
    {
        if (string.IsNullOrEmpty(message))
        {
            return;
        }

        // Extension output goes to the same diagnostic stream as the rest of the
        // port; `error`/`warn` keep their level so a failed extension is visible.
        Console.Error.WriteLine($"[extension:{level}] {message}");
    }

    // ------------------------------------------------------------------ calls

    internal string? Call(string method, string? argsJson)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        var payload = ParsePayload(argsJson);

        switch (method)
        {
            // ---- registration ----
            case "registerTool": RegisterTool(payload); return null;
            case "registerCommand": RegisterCommand(payload); return null;
            case "registerShortcut": RegisterShortcut(payload); return null;
            case "registerFlag": RegisterFlag(payload); return null;
            case "getFlag": return JsonNode.Parse(JsonSerializer.Serialize(Api.GetFlag(RequiredString(payload, "name")), JsonOptions))?.ToJsonString();
            case "on": RegisterEventHandler(payload); return null;
            case "off": return null;

            // ---- session actions ----
            case "exec": return Exec(payload);
            case "sendMessage": SendMessage(payload); return null;
            case "sendUserMessage": SendUserMessage(payload); return null;
            case "appendEntry": Api.AppendEntry(RequiredString(payload, "customType"), ToObject(payload["data"])); return null;
            case "setSessionName": Api.SetSessionName(RequiredString(payload, "name")); return null;
            case "getSessionName": return JsonSerializer.Serialize(Api.GetSessionName(), JsonOptions);
            case "setLabel": Api.SetLabel(RequiredString(payload, "entryId"), payload["label"]?.GetValue<string>()); return null;

            // ---- inspection ----
            case "getActiveTools": return JsonSerializer.Serialize(Api.GetActiveTools(), JsonOptions);
            case "getAllTools": return JsonSerializer.Serialize(Api.GetAllTools().Select(ToolInfoJson), JsonOptions);
            case "setActiveTools": Api.SetActiveTools(StringArray(payload["names"])); return null;
            case "getCommands": return JsonSerializer.Serialize(Api.GetCommands(), JsonOptions);
            case "getSettings": return JsonSerializer.Serialize(Api.GetSettings(), JsonOptions);
            case "setModel": return JsonSerializer.Serialize(SetModel(payload), JsonOptions);
            case "getThinkingLevel": return JsonSerializer.Serialize(Api.GetThinkingLevel(), JsonOptions);
            case "setThinkingLevel": Api.SetThinkingLevel(ParseThinkingLevel(RequiredString(payload, "level"))); return null;

            // ---- host paths ----
            case "agentDir": return JsonSerializer.Serialize(Config.GetAgentDir(), JsonOptions);
            case "packageDir": return JsonSerializer.Serialize(AppContext.BaseDirectory, JsonOptions);

            // ---- tool streaming ----
            case "tool.update": RaiseToolUpdate(payload); return null;
            case "tool.executeBuiltin": return ExecuteBuiltinTool(payload);
            case "host.warn": Log("warn", RequiredString(payload, "message")); return null;

            // ---- ui ----
            case "ui.notify":
            case "ui.setStatus":
            case "ui.setWidget":
            case "ui.setTitle":
            case "ui.setEditorText":
            case "ui.getEditorText":
            case "ui.select":
            case "ui.confirm":
            case "ui.input":
            case "ui.editor":
            case "ui.custom":
            case "ui.unsupported":
                return UiCall(method, payload);

            // ---- context actions ----
            case "ctx.shutdown":
            case "ctx.reload":
            case "ctx.compact":
            case "ctx.getSystemPrompt":
            case "ctx.getContextUsage":
            case "ctx.newSession":
            case "ctx.fork":
            case "ctx.switchSession":
            case "ctx.navigateTree":
                return ContextCall(method, payload);

            default:
                if (method.StartsWith("path.", StringComparison.Ordinal)) return PathCall(method, payload);
                if (method.StartsWith("fs.", StringComparison.Ordinal)) return FileSystemCall(method, payload);
                if (method.StartsWith("os.", StringComparison.Ordinal)) return OperatingSystemCall(method);
                if (method.StartsWith("crypto.", StringComparison.Ordinal)) return CryptoCalls(method, payload);
                if (method == "childProcess.run") return RunProcess(payload);
                if (method.StartsWith("zlib.", StringComparison.Ordinal)) return ZlibCall(method, payload);
                if (method.StartsWith("events.", StringComparison.Ordinal)) return EventsCall(method, payload);
                throw new InvalidOperationException($"Unknown host method '{method}'.");
        }
    }

    // ------------------------------------------------------- registrations

    private void RegisterTool(JsonObject payload)
    {
        var executeId = payload["execute"]?.GetValue<string>();
        var definesExecute = executeId is not null;
        var parameters = ToObjectDictionary(payload["parameters"]) ?? [];

        var definition = new ToolDefinition
        {
            Name = RequiredString(payload, "name"),
            Label = payload["label"]?.GetValue<string>() ?? RequiredString(payload, "name"),
            Description = payload["description"]?.GetValue<string>() ?? string.Empty,
            Parameters = new ToolSchema(parameters),
            PromptSnippet = payload["promptSnippet"]?.GetValue<string>(),
            PromptGuidelines = StringArrayOrNull(payload["promptGuidelines"]),
            RenderShell = payload["renderShell"]?.GetValue<string>(),
            Exposure = payload["exposure"]?.GetValue<string>(),
            DefaultActive = payload["defaultActive"]?.GetValue<bool>(),
            Execute = (toolCallId, arguments, cancellationToken, update, context) =>
            {
                if (!definesExecute)
                {
                    return Task.FromResult(AgentToolResult.FromError(
                        $"The JavaScript tool '{payload["name"]}' did not define execute()."));
                }

                return Task.FromResult(InvokeToolExecute(executeId!, toolCallId, arguments, update, context));
            },
        };

        Api.RegisterTool(definition);
    }

    private AgentToolResult InvokeToolExecute(
        string executeId,
        string toolCallId,
        IReadOnlyDictionary<string, object?> arguments,
        AgentToolUpdateCallback? update,
        IExtensionToolContext? context)
    {
        var updateToken = update is null ? null : RegisterToolUpdate(update);
        var scope = new ContextScope(this);
        try
        {
            var ctxPayload = scope.Register(context);
            var argsJson = JsonSerializer.Serialize(arguments, JsonOptions);

            var result = Invoke!(executeId,
            [
                JsValue.FromObject(engine, toolCallId),
                engine.Evaluate($"JSON.parse({JsonSerializer.Serialize(argsJson)})"),
                ctxPayload is null ? JsValue.Null : engine.Evaluate($"JSON.parse({JsonSerializer.Serialize(ctxPayload)})"),
                updateToken is null ? JsValue.Null : JsValue.FromObject(engine, updateToken),
            ]);

            return ParseToolResult(result);
        }
        finally
        {
            if (updateToken is not null)
            {
                toolUpdates.Remove(updateToken);
            }

            scope.Dispose();
        }
    }

    private string ToJson(JsValue value) => engine.Call(stringify, JsValue.Undefined, [value]).AsString();

    private AgentToolResult ParseToolResult(JsValue value)
    {
        var json = value.IsUndefined() || value.IsNull() ? null : ToJson(value);
        if (string.IsNullOrEmpty(json))
        {
            return new AgentToolResult([new TextContent(string.Empty)]);
        }

        var node = JsonNode.Parse(json) as JsonObject;
        if (node is null)
        {
            return new AgentToolResult([new TextContent(json)]);
        }

        var content = new List<ContentBlock>();
        if (node["content"] is JsonArray blocks)
        {
            foreach (var block in blocks)
            {
                if (block is not JsonObject entry)
                {
                    continue;
                }

                switch (entry["type"]?.GetValue<string>())
                {
                    case "text":
                        content.Add(new TextContent(entry["text"]?.GetValue<string>() ?? string.Empty));
                        break;
                    case "image":
                        content.Add(new ImageContent(
                            entry["data"]?.GetValue<string>() ?? string.Empty,
                            entry["mimeType"]?.GetValue<string>()));
                        break;
                }
            }
        }

        return new AgentToolResult(
            content,
            Details: ToObject(node["details"]),
            StructuredContent: ToObject(node["structuredContent"]),
            IsError: node["isError"]?.GetValue<bool>() ?? false,
            Terminate: node["terminate"]?.GetValue<bool>() ?? false);
    }

    /// <summary>
    /// Runs a built-in tool on behalf of a `create*ToolDefinition()` wrapper: the
    /// extension re-registers the host's tool with custom rendering, and execution
    /// has to reach the same implementation.
    /// </summary>
    private string? ExecuteBuiltinTool(JsonObject payload)
    {
        var handle = payload["handle"]?.GetValue<string>();
        if (handle is null || !contexts.TryGetValue(handle, out var context) || context is not IExtensionToolContext toolContext)
        {
            throw new InvalidOperationException(
                "createBashTool()/createReadToolDefinition() and friends need a tool context. Call them from a "
                + "registered tool or command, not from the extension factory.");
        }

        var outcome = toolContext
            .ExecuteTool(RequiredString(payload, "name"), ToObject(payload["params"]))
            .GetAwaiter()
            .GetResult();

        return JsonSerializer.Serialize(ToolResultJson(outcome.Result), JsonOptions);
    }

    private static JsonObject ToolResultJson(AgentToolResult result)
    {
        var content = new JsonArray();
        foreach (var block in result.Content)
        {
            switch (block)
            {
                case TextContent text:
                    content.Add(new JsonObject { ["type"] = "text", ["text"] = text.Text });
                    break;
                case ImageContent image:
                    content.Add(new JsonObject
                    {
                        ["type"] = "image",
                        ["data"] = image.Data,
                        ["mimeType"] = image.MimeType,
                    });
                    break;
            }
        }

        var json = new JsonObject { ["content"] = content };
        if (result.Details is not null)
        {
            json["details"] = JsonSerializer.SerializeToNode(result.Details, JsonOptions);
        }

        if (result.IsError)
        {
            json["isError"] = true;
        }

        return json;
    }

    private string RegisterToolUpdate(AgentToolUpdateCallback update)
    {
        var token = Guid.NewGuid().ToString("N");
        toolUpdates[token] = update;
        return token;
    }

    private void RaiseToolUpdate(JsonObject payload)
    {
        var token = RequiredString(payload, "token");
        if (!toolUpdates.TryGetValue(token, out var update))
        {
            return;
        }

        var partialJson = payload["partial"]?.ToJsonString() ?? "{}";
        var result = ParseToolResult(engine.Evaluate($"({partialJson})"));
        update(result).GetAwaiter().GetResult();
    }

    private void RegisterCommand(JsonObject payload)
    {
        var name = RequiredString(payload, "name");
        var handlerId = payload["handler"]?.GetValue<string>();
        var completionsId = payload["completions"]?.GetValue<string>();

        Api.RegisterCommand(name, new CommandRegistration
        {
            Description = payload["description"]?.GetValue<string>(),
            GetArgumentCompletions = completionsId is null
                ? null
                : args =>
                {
                    var scope = new ContextScope(this);
                    try
                    {
                        var result = Invoke!(completionsId, [JsValue.FromObject(engine, args)]);
                        var json = result.IsUndefined() || result.IsNull() ? "[]" : ToJson(result);
                        var items = JsonSerializer.Deserialize<List<AutocompleteItem>>(json, JsonOptions) ?? [];
                        return Task.FromResult<IReadOnlyList<AutocompleteItem>?>(items);
                    }
                    finally
                    {
                        scope.Dispose();
                    }
                },
            Handler = (args, context) =>
            {
                if (handlerId is null)
                {
                    return Task.CompletedTask;
                }

                var scope = new ContextScope(this);
                try
                {
                    var ctxPayload = scope.Register(context);
                    Invoke!(handlerId,
                    [
                        JsValue.FromObject(engine, args),
                        ctxPayload is null ? JsValue.Null : engine.Evaluate($"JSON.parse({JsonSerializer.Serialize(ctxPayload)})"),
                    ]);
                    return Task.CompletedTask;
                }
                finally
                {
                    scope.Dispose();
                }
            },
        });
    }

    private void RegisterShortcut(JsonObject payload)
    {
        var handlerId = payload["handler"]?.GetValue<string>();
        Api.RegisterShortcut(RequiredString(payload, "shortcut"), new ShortcutRegistration
        {
            Description = payload["description"]?.GetValue<string>(),
            Handler = context =>
            {
                if (handlerId is null)
                {
                    return Task.CompletedTask;
                }

                var scope = new ContextScope(this);
                try
                {
                    var ctxPayload = scope.Register(context);
                    Invoke!(handlerId,
                    [
                        ctxPayload is null ? JsValue.Null : engine.Evaluate($"JSON.parse({JsonSerializer.Serialize(ctxPayload)})"),
                    ]);
                    return Task.CompletedTask;
                }
                finally
                {
                    scope.Dispose();
                }
            },
        });
    }

    private void RegisterFlag(JsonObject payload)
    {
        Api.RegisterFlag(RequiredString(payload, "name"), new FlagRegistration
        {
            Description = payload["description"]?.GetValue<string>(),
            Type = payload["type"]?.GetValue<string>() ?? FlagType.Boolean,
            Default = ToObject(payload["defaultValue"]),
        });
    }

    private void RegisterEventHandler(JsonObject payload)
    {
        var eventName = RequiredString(payload, "event");
        var handlerId = RequiredString(payload, "handler");

        // The runner dispatches `(event, ctx)` as loosely typed args, so the JS
        // handler receives the serialized event plus a rebuilt context.
        Api.On<object>(eventName, (@event, ctx) =>
        {
            var scope = new ContextScope(this);
            var ctxPayload = scope.Register(ctx);
            var eventJson = SerializeEvent(@event);
            try
            {
                var result = Invoke!(handlerId,
                [
                    engine.Evaluate($"JSON.parse({JsonSerializer.Serialize(eventJson)})"),
                    ctxPayload is null ? JsValue.Null : engine.Evaluate($"JSON.parse({JsonSerializer.Serialize(ctxPayload)})"),
                ]);

                var returned = result.IsUndefined() || result.IsNull() ? null : JsonNode.Parse(ToJson(result));
                var response = BuildEventResult(returned);
                return Task.FromResult(response);
            }
            finally
            {
                scope.Dispose();
            }
        });
    }

    /// <summary>
    /// Event results are plain objects upstream (`{ block: true }`, `{ message }`,
    /// `{ entries }`). The runner expects the port's result records, so the shape
    /// is mapped here; an unrecognised shape falls through as null (no effect).
    /// </summary>
    private object? BuildEventResult(JsonNode? returned)
    {
        if (returned is not JsonObject result)
        {
            return null;
        }

        // `{ block: true, reason }` is shared by the tool_call / input guards.
        if (result["block"]?.GetValue<bool>() is true)
        {
            return new ToolCallEventResult
            {
                Block = true,
                Reason = result["reason"]?.GetValue<string>(),
                Terminate = result["terminate"]?.GetValue<bool>(),
            };
        }

        return null;
    }

    private static string SerializeEvent(object? @event)
    {
        if (@event is null)
        {
            return "null";
        }

        try
        {
            return JsonSerializer.Serialize(@event, @event.GetType(), JsonOptions);
        }
        catch (Exception)
        {
            // Event records with a delegate or a live component cannot be
            // serialized; the extension sees the fields it can read.
            return JsonSerializer.Serialize(new { type = @event.GetType().Name }, JsonOptions);
        }
    }

    // ------------------------------------------------------------ session

    private string Exec(JsonObject payload)
    {
        var command = RequiredString(payload, "command");
        var args = StringArray(payload["args"]);
        var options = payload["options"] as JsonObject;

        var execOptions = new ExecOptions
        {
            Cwd = options?["cwd"]?.GetValue<string>(),
            Timeout = options?["timeout"]?.GetValue<double>() is { } timeout ? (int)timeout : null,
        };

        var result = Api.Exec(command, args, execOptions).GetAwaiter().GetResult();
        return JsonSerializer.Serialize(result, JsonOptions);
    }

    private void SendMessage(JsonObject payload)
    {
        var message = payload["message"] as JsonObject
            ?? throw new InvalidOperationException("pi.sendMessage needs a message draft.");

        var options = payload["options"] as JsonObject;
        Api.SendMessage(
            new CustomMessageDraft
            {
                CustomType = message["customType"]?.GetValue<string>() ?? "extension",
                Content = ParseMessageContent(message["content"]),
                Display = message["display"]?.GetValue<bool>() ?? false,
                Details = ToObject(message["details"]),
            },
            options is null
                ? null
                : new SendMessageOptions
                {
                    TriggerTurn = options["triggerTurn"]?.GetValue<bool>(),
                    DeliverAs = options["deliverAs"]?.GetValue<string>(),
                });
    }

    private void SendUserMessage(JsonObject payload)
    {
        var options = payload["options"] as JsonObject;
        var content = ParseMessageContent(payload["content"]);
        var userOptions = options is null
                ? null
                : new SendUserMessageOptions
                {
                    DeliverAs = options["deliverAs"]?.GetValue<string>(),
                    ExpandPromptTemplates = options["expandPromptTemplates"]?.GetValue<bool>(),
                };
        if (content is MessageContent.Text text) Api.SendUserMessage(text.Value, userOptions);
        else if (content is MessageContent.Blocks blocks) Api.SendUserMessage(blocks.Value, userOptions);
    }

    private static MessageContent ParseMessageContent(JsonNode? node) => node switch
    {
        null => new MessageContent.Text(string.Empty),
        JsonValue value when value.TryGetValue<string>(out var text) => new MessageContent.Text(text),
        JsonArray array => new MessageContent.Blocks(
        [
            .. array.OfType<JsonObject>().Select(block => block["type"]?.GetValue<string>() switch
            {
                "image" => (ContentBlock)new ImageContent(
                    block["data"]?.GetValue<string>() ?? string.Empty,
                    block["mimeType"]?.GetValue<string>()),
                _ => new TextContent(block["text"]?.GetValue<string>() ?? string.Empty),
            }),
        ]),
        _ => new MessageContent.Text(node.ToJsonString()),
    };

    private bool SetModel(JsonObject payload)
    {
        var model = payload["model"] as JsonObject;
        if (model is null)
        {
            return false;
        }

        var spec = model.Deserialize<Pi.Ai.Types.Model>(JsonOptions);
        return spec is not null && Api.SetModel(spec).GetAwaiter().GetResult();
    }

    private static ThinkingLevel ParseThinkingLevel(string value) => value.ToLowerInvariant() switch
    {
        "off" or "none" => ThinkingLevel.Off,
        "minimal" => ThinkingLevel.Minimal,
        "low" => ThinkingLevel.Low,
        "medium" => ThinkingLevel.Medium,
        "high" => ThinkingLevel.High,
        _ => ThinkingLevel.Medium,
    };

    private static JsonObject ToolInfoJson(ToolInfo info) => new()
    {
        ["name"] = info.Name,
        ["description"] = info.Description,
        ["parameters"] = JsonSerializer.SerializeToNode(info.Parameters.JsonSchema, JsonOptions),
        ["promptGuidelines"] = info.PromptGuidelines is null ? null : new JsonArray([.. info.PromptGuidelines.Select(line => (JsonNode?)JsonValue.Create(line))]),
        ["exposure"] = info.Exposure,
        ["sourceInfo"] = new JsonObject
        {
            ["path"] = info.SourceInfo.Path,
            ["source"] = info.SourceInfo.Source,
        },
    };

    // ------------------------------------------------------------------ ui

    private string? UiCall(string method, JsonObject payload)
    {
        var context = RequireContext(payload, out var ui);
        _ = context;

        switch (method)
        {
            case "ui.notify": ui.Notify(RequiredString(payload, "message"), payload["level"]?.GetValue<string>() ?? "info"); return null;
            case "ui.setStatus": ui.SetStatus(RequiredString(payload, "key"), payload["text"]?.GetValue<string>()); return null;
            case "ui.setWidget":
                var lines = payload["lines"] is JsonArray array
                    ? array.Select(line => line?.ToString() ?? string.Empty).ToList()
                    : null;
                ui.SetWidget(RequiredString(payload, "key"), lines);
                return null;
            case "ui.setTitle": ui.SetTitle(RequiredString(payload, "title")); return null;
            case "ui.setEditorText": ui.SetEditorText(RequiredString(payload, "text")); return null;
            case "ui.getEditorText": return JsonSerializer.Serialize(ui.GetEditorText(), JsonOptions);
            case "ui.select":
                return JsonSerializer.Serialize(
                    ui.Select(
                        RequiredString(payload, "title"),
                        StringArray(payload["options"]),
                        ReadDialogOptions(payload)).GetAwaiter().GetResult(),
                    JsonOptions);
            case "ui.confirm":
                return JsonSerializer.Serialize(
                    ui.Confirm(
                        RequiredString(payload, "title"),
                        RequiredString(payload, "message"),
                        ReadDialogOptions(payload)).GetAwaiter().GetResult(),
                    JsonOptions);
            case "ui.input":
                return JsonSerializer.Serialize(
                    ui.Input(
                        RequiredString(payload, "title"),
                        payload["placeholder"]?.GetValue<string>(),
                        ReadDialogOptions(payload)).GetAwaiter().GetResult(),
                    JsonOptions);
            case "ui.editor":
                return JsonSerializer.Serialize(
                    ui.Editor(
                        RequiredString(payload, "title"),
                        payload["prefill"]?.GetValue<string>()).GetAwaiter().GetResult(),
                    JsonOptions);
            case "ui.custom":
                throw new InvalidOperationException(
                    "ctx.ui.custom() is not available: it needs the terminal render loop. Use ctx.ui.select / "
                    + "confirm / input / notify, or render with the tools available in this host.");
            case "ui.unsupported":
                Log("warn", $"ctx.ui.{payload["method"]?.GetValue<string>()} is not supported by the JavaScript extension host");
                return null;
            default:
                throw new InvalidOperationException($"Unknown ui method '{method}'.");
        }
    }

    private IExtensionContext RequireContext(JsonObject payload, out IExtensionUIContext ui)
    {
        var handle = RequiredString(payload, "handle");
        if (!contexts.TryGetValue(handle, out var value) || value is not IExtensionContext context)
        {
            throw new InvalidOperationException(
                "This ctx is stale: extension contexts are only valid for the duration of the callback that "
                + "received them.");
        }

        ui = context.Ui;
        return context;
    }

    private static ExtensionUIDialogOptions? ReadDialogOptions(JsonObject payload)
        => payload["opts"] is JsonObject options ? options.Deserialize<ExtensionUIDialogOptions>(JsonOptions) : null;

    // -------------------------------------------------------------- context

    private string? ContextCall(string method, JsonObject payload)
    {
        var handle = RequiredString(payload, "handle");
        if (!contexts.TryGetValue(handle, out var value))
        {
            throw new InvalidOperationException("This ctx is stale: extension contexts are only valid for the duration of the callback that received them.");
        }

        switch (method)
        {
            case "ctx.reload" when value is IExtensionCommandContext command:
                command.Reload();
                return null;
            case "ctx.shutdown" when value is IExtensionCommandContext command:
                command.Shutdown();
                return null;
            case "ctx.compact" when value is IExtensionCommandContext command:
                command.Compact(payload["options"]?.Deserialize<CompactOptions>(JsonOptions));
                return null;
            case "ctx.getSystemPrompt" when value is IExtensionCommandContext command:
                return JsonSerializer.Serialize(command.GetSystemPrompt(), JsonOptions);
            case "ctx.newSession" when value is IExtensionCommandContext command:
                return JsonSerializer.Serialize(
                    command.NewSession(payload["options"]?.Deserialize<NewSessionOptions>(JsonOptions)).GetAwaiter().GetResult(),
                    JsonOptions);
            case "ctx.fork" when value is IExtensionCommandContext command:
                return JsonSerializer.Serialize(
                    command.Fork(RequiredString(payload, "entryId"), payload["options"]?.Deserialize<ForkOptions>(JsonOptions)).GetAwaiter().GetResult(),
                    JsonOptions);
            case "ctx.switchSession" when value is IExtensionCommandContext command:
                return JsonSerializer.Serialize(
                    command.SwitchSession(RequiredString(payload, "sessionPath"), payload["options"]?.Deserialize<SwitchSessionOptions>(JsonOptions)).GetAwaiter().GetResult(),
                    JsonOptions);
            case "ctx.navigateTree" when value is IExtensionCommandContext command:
                return JsonSerializer.Serialize(
                    command.NavigateTree(RequiredString(payload, "targetId"), payload["options"]?.Deserialize<NavigateTreeOptions>(JsonOptions)).GetAwaiter().GetResult(),
                    JsonOptions);
            default:
                throw new InvalidOperationException($"{method} is not available on this context.");
        }
    }

    private string? RegisterContext(IExtensionContext? context)
    {
        if (context is null)
        {
            return null;
        }

        var handle = Guid.NewGuid().ToString("N");
        contexts[handle] = context;

        var payload = new JsonObject
        {
            ["handle"] = handle,
            ["cwd"] = context.Cwd,
            ["mode"] = context.Mode,
            ["hasUI"] = context.HasUI,
            ["isIdle"] = context.IsIdle(),
            ["thinkingLevel"] = context.ThinkingLevel?.ToString().ToLowerInvariant(),
            ["model"] = context.Model is null ? null : JsonSerializer.SerializeToNode(context.Model, JsonOptions),
            ["sessionManager"] = new JsonObject
            {
                ["sessionId"] = context.SessionManager.SessionId,
                ["sessionFile"] = context.SessionManager.SessionFile,
                ["getSessionId"] = JsFunctionMarker,
                ["getSessionFile"] = JsFunctionMarker,
                ["getBranch"] = JsFunctionMarker,
                ["getEntries"] = JsFunctionMarker,
            },
            ["ui"] = new JsonObject
            {
                ["theme"] = JsonSerializer.SerializeToNode(context.Ui.Theme?.Name, JsonOptions),
            },
        };

        // Functions cannot cross the JSON boundary; the prelude rebuilds them.
        return payload.ToJsonString();
    }

    private const string JsFunctionMarker = "__pi_fn__";

    /// <summary>Tracks the contexts handed out during one callback and drops them afterwards.</summary>
    private sealed class ContextScope(JsExtensionHost host) : IDisposable
    {
        private readonly List<string> handles = [];

        public string? Register(IExtensionContext? context)
        {
            if (context is null)
            {
                return null;
            }

            var payload = host.RegisterContext(context);
            if (payload is not null)
            {
                handles.Add(JsonNode.Parse(payload)!["handle"]!.GetValue<string>());
            }

            return payload;
        }

        public void Dispose()
        {
            foreach (var handle in handles)
            {
                host.contexts.Remove(handle);
            }
        }
    }

    // ----------------------------------------------------------- node path

    private static string? PathCall(string method, JsonObject payload)
    {
        var args = payload["args"] is JsonArray array
            ? array.Select(arg => arg?.GetValue<string>() ?? string.Empty).ToArray()
            : [];

        return method switch
        {
            "path.sep" => JsonSerializer.Serialize(Path.DirectorySeparatorChar.ToString()),
            "path.delimiter" => JsonSerializer.Serialize(Path.PathSeparator.ToString()),
            "path.join" => JsonSerializer.Serialize(Path.Combine(args)),
            "path.resolve" => JsonSerializer.Serialize(ResolvePath(args)),
            "path.dirname" => JsonSerializer.Serialize(Path.GetDirectoryName(args[0]) ?? "."),
            "path.basename" => JsonSerializer.Serialize(Basename(args)),
            "path.extname" => JsonSerializer.Serialize(Path.GetExtension(args[0])),
            "path.relative" => JsonSerializer.Serialize(Path.GetRelativePath(args[0], args[1])),
            "path.isAbsolute" => JsonSerializer.Serialize(Path.IsPathRooted(args[0])),
            "path.normalize" => JsonSerializer.Serialize(Path.GetFullPath(args[0])),
            "path.parse" => JsonSerializer.Serialize(new JsonObject
            {
                ["root"] = Path.GetPathRoot(args[0]) ?? string.Empty,
                ["dir"] = Path.GetDirectoryName(args[0]) ?? string.Empty,
                ["base"] = Path.GetFileName(args[0]),
                ["ext"] = Path.GetExtension(args[0]),
                ["name"] = Path.GetFileNameWithoutExtension(args[0]),
            }),
            "path.format" => JsonSerializer.Serialize(FormatPath(payload["args"] is JsonArray formatArgs ? formatArgs[0] as JsonObject : null)),
            _ => throw new InvalidOperationException($"Unknown path method '{method}'."),
        };
    }

    private static string ResolvePath(string[] segments)
    {
        var path = Path.GetFullPath(segments.Length == 0 ? "." : segments[0]);
        for (var i = 1; i < segments.Length; i++)
        {
            path = Path.GetFullPath(Path.Combine(path, segments[i]));
        }

        return path;
    }

    private static string Basename(string[] args)
    {
        var name = Path.GetFileName(args[0]);
        if (args.Length > 1 && args[1].Length > 0 && name.EndsWith(args[1], StringComparison.Ordinal))
        {
            return name[..^args[1].Length];
        }

        return name;
    }

    private static string FormatPath(JsonObject? parts)
    {
        if (parts is null)
        {
            return string.Empty;
        }

        var directory = parts["dir"]?.GetValue<string>();
        var baseName = parts["base"]?.GetValue<string>();
        if (baseName is null)
        {
            baseName = (parts["name"]?.GetValue<string>() ?? string.Empty) + (parts["ext"]?.GetValue<string>() ?? string.Empty);
        }

        return string.IsNullOrEmpty(directory) ? baseName : Path.Combine(directory, baseName);
    }

    // ------------------------------------------------------------- node fs

    private string? FileSystemCall(string method, JsonObject payload)
    {
        switch (method)
        {
            case "fs.readFile":
                return JsonSerializer.Serialize(File.ReadAllText(RequiredString(payload, "path")), JsonOptions);
            case "fs.writeFile":
                var path = RequiredString(payload, "path");
                var directory = Path.GetDirectoryName(path);
                if (!string.IsNullOrEmpty(directory))
                {
                    Directory.CreateDirectory(directory);
                }

                File.WriteAllText(path, payload["data"]?.GetValue<string>() ?? string.Empty);
                return null;
            case "fs.appendFile":
                File.AppendAllText(RequiredString(payload, "path"), payload["data"]?.GetValue<string>() ?? string.Empty);
                return null;
            case "fs.exists":
                var candidate = RequiredString(payload, "path");
                return JsonSerializer.Serialize(File.Exists(candidate) || Directory.Exists(candidate));
            case "fs.stat":
                return StatJson(RequiredString(payload, "path"));
            case "fs.readdir":
                return ReadDirectory(RequiredString(payload, "path"));
            case "fs.mkdir":
                Directory.CreateDirectory(RequiredString(payload, "path"));
                return null;
            case "fs.rm":
                var target = RequiredString(payload, "path");
                if (payload["recursive"]?.GetValue<bool>() is true && Directory.Exists(target))
                {
                    Directory.Delete(target, recursive: true);
                }
                else if (Directory.Exists(target))
                {
                    Directory.Delete(target);
                }
                else if (File.Exists(target))
                {
                    File.Delete(target);
                }
                else if (payload["force"]?.GetValue<bool>() is not true)
                {
                    throw new FileNotFoundException($"ENOENT: no such file or directory, rm '{target}'");
                }

                return null;
            case "fs.unlink":
                File.Delete(RequiredString(payload, "path"));
                return null;
            case "fs.rename":
                Move(RequiredString(payload, "from"), RequiredString(payload, "to"));
                return null;
            case "fs.copyFile":
                File.Copy(RequiredString(payload, "from"), RequiredString(payload, "to"), overwrite: true);
                return null;
            case "fs.realpath":
                return JsonSerializer.Serialize(Path.GetFullPath(RequiredString(payload, "path")), JsonOptions);
            case "fs.mkdtemp":
                var temp = Path.Combine(Path.GetTempPath(), RequiredString(payload, "prefix").TrimEnd('/', '\\') + Guid.NewGuid().ToString("N")[..8]);
                Directory.CreateDirectory(temp);
                return JsonSerializer.Serialize(temp, JsonOptions);
            default:
                throw new InvalidOperationException($"Unknown fs method '{method}'.");
        }
    }

    private static string StatJson(string path)
    {
        FileSystemInfo info = Directory.Exists(path) ? new DirectoryInfo(path) : new FileInfo(path);
        if (!info.Exists)
        {
            throw new FileNotFoundException($"ENOENT: no such file or directory, stat '{path}'", path);
        }

        var kind = info is DirectoryInfo ? "directory" : info.LinkTarget is not null ? "symlink" : "file";
        return JsonSerializer.Serialize(new JsonObject
        {
            ["kind"] = kind,
            ["size"] = info is FileInfo file ? file.Length : 0,
            ["mtimeMs"] = new DateTimeOffset(info.LastWriteTimeUtc).ToUnixTimeMilliseconds(),
            ["mode"] = 0,
        });
    }

    private static string ReadDirectory(string path)
    {
        var directory = new DirectoryInfo(path);
        if (!directory.Exists)
        {
            throw new DirectoryNotFoundException($"ENOENT: no such file or directory, scandir '{path}'");
        }

        var entries = new JsonArray();
        foreach (var entry in directory.EnumerateFileSystemInfos())
        {
            entries.Add(new JsonObject
            {
                ["name"] = entry.Name,
                ["kind"] = entry is DirectoryInfo ? "directory" : entry.LinkTarget is not null ? "symlink" : "file",
            });
        }

        return entries.ToJsonString();
    }

    private static void Move(string from, string to)
    {
        if (Directory.Exists(from))
        {
            Directory.Move(from, to);
        }
        else
        {
            File.Move(from, to, overwrite: true);
        }
    }

    private static string? OperatingSystemCall(string method) => method switch
    {
        "os.eol" => JsonSerializer.Serialize(Environment.NewLine),
        "os.platform" => JsonSerializer.Serialize(OperatingSystem.IsWindows() ? "win32" : OperatingSystem.IsMacOS() ? "darwin" : "linux"),
        "os.arch" => JsonSerializer.Serialize(System.Runtime.InteropServices.RuntimeInformation.OSArchitecture.ToString().ToLowerInvariant()),
        "os.homedir" => JsonSerializer.Serialize(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)),
        "os.tmpdir" => JsonSerializer.Serialize(Path.GetTempPath().TrimEnd(Path.DirectorySeparatorChar)),
        "os.hostname" => JsonSerializer.Serialize(Environment.MachineName),
        _ => throw new InvalidOperationException($"Unknown os method '{method}'."),
    };

    private static string CryptoCalls(string method, JsonObject payload) => method switch
    {
        "crypto.randomUUID" => JsonSerializer.Serialize(Guid.NewGuid().ToString()),
        "crypto.randomHex" => JsonSerializer.Serialize(
            Convert.ToHexStringLower(System.Security.Cryptography.RandomNumberGenerator.GetBytes(
                payload["size"]?.GetValue<int>() ?? 16))),
        _ => throw new InvalidOperationException($"Unknown crypto method '{method}'."),
    };

    private string RunProcess(JsonObject payload)
    {
        var command = RequiredString(payload, "command");
        var args = StringArray(payload["args"]);
        var startInfo = new System.Diagnostics.ProcessStartInfo
        {
            FileName = command,
            WorkingDirectory = payload["cwd"]?.GetValue<string>() ?? Cwd,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = payload["input"] is not null,
            UseShellExecute = false,
        };

        foreach (var arg in args)
        {
            startInfo.ArgumentList.Add(arg);
        }

        if (payload["env"] is JsonObject environment)
        {
            foreach (var (key, value) in environment)
            {
                if (value is not null)
                {
                    startInfo.Environment[key] = value.ToString();
                }
            }
        }

        using var process = System.Diagnostics.Process.Start(startInfo)
            ?? throw new InvalidOperationException($"Failed to start '{command}'.");

        if (payload["input"] is not null)
        {
            process.StandardInput.Write(payload["input"]!.ToString());
            process.StandardInput.Close();
        }

        var stdout = process.StandardOutput.ReadToEnd();
        var stderr = process.StandardError.ReadToEnd();
        process.WaitForExit();

        return JsonSerializer.Serialize(new JsonObject
        {
            ["code"] = process.ExitCode,
            ["stdout"] = stdout,
            ["stderr"] = stderr,
            ["pid"] = process.Id,
        });
    }

    // -------------------------------------------------------------- events
    //
    // `pi.events` is the inter-extension bus. Each extension owns an engine, so the
    // bus lives here: subscribers are (host, callback id) pairs and an emit fans out
    // to every host except the emitter itself, matching the upstream contract that a
    // publisher does not receive its own event.

    private static readonly object BusGate = new();
    private static readonly Dictionary<string, List<(JsExtensionHost Host, string CallbackId)>> Subscriptions =
        new(StringComparer.Ordinal);

    private string? EventsCall(string method, JsonObject payload)
    {
        var name = RequiredString(payload, "name");
        switch (method)
        {
            case "events.subscribe":
            {
                var callbackId = RequiredString(payload, "handler");
                lock (BusGate)
                {
                    if (!Subscriptions.TryGetValue(name, out var list))
                    {
                        list = [];
                        Subscriptions[name] = list;
                    }

                    list.Add((this, callbackId));
                }

                return null;
            }

            case "events.unsubscribe":
            {
                var callbackId = RequiredString(payload, "handler");
                lock (BusGate)
                {
                    if (Subscriptions.TryGetValue(name, out var list))
                    {
                        list.RemoveAll(entry => entry.Host == this && entry.CallbackId == callbackId);
                    }
                }

                return null;
            }

            case "events.emit":
            {
                (JsExtensionHost Host, string CallbackId)[] targets;
                lock (BusGate)
                {
                    targets = Subscriptions.TryGetValue(name, out var list)
                        ? [.. list.Where(entry => entry.Host != this)]
                        : [];
                }

                var dataJson = payload["data"]?.ToJsonString();
                foreach (var (target, callbackId) in targets)
                {
                    try
                    {
                        target.InvokeFromBus(callbackId, dataJson);
                    }
                    catch (Exception error)
                    {
                        target.Log("error", $"pi.events handler for '{name}' threw: {error.Message}");
                    }
                }

                return null;
            }

            default:
                throw new InvalidOperationException($"Unknown events method '{method}'.");
        }
    }

    private void InvokeFromBus(string callbackId, string? dataJson)
    {
        var value = dataJson is null
            ? JsValue.Undefined
            : engine.Evaluate($"JSON.parse({JsonSerializer.Serialize(dataJson)})");
        Invoke!(callbackId, [value]);
    }

    // ---------------------------------------------------------------- zlib

    private static string ZlibCall(string method, JsonObject payload)
    {
        var input = Convert.FromBase64String(RequiredString(payload, "data"));
        using var source = new MemoryStream(input);
        using var output = new MemoryStream();

        switch (method)
        {
            case "zlib.deflate":
            {
                using var deflate = new System.IO.Compression.ZLibStream(
                    output, System.IO.Compression.CompressionLevel.Fastest, leaveOpen: true);
                deflate.Write(input);
                deflate.Flush();
                break;
            }

            case "zlib.inflate":
            {
                using var inflate = new System.IO.Compression.ZLibStream(
                    source, System.IO.Compression.CompressionMode.Decompress);
                inflate.CopyTo(output);
                break;
            }

            case "zlib.gunzip":
            {
                using var gunzip = new System.IO.Compression.GZipStream(
                    source, System.IO.Compression.CompressionMode.Decompress);
                gunzip.CopyTo(output);
                break;
            }

            default:
                throw new InvalidOperationException($"Unknown zlib method '{method}'.");
        }

        return JsonSerializer.Serialize(Convert.ToBase64String(output.ToArray()));
    }

    // ------------------------------------------------------------- helpers

    private static JsonObject ParsePayload(string? argsJson)
    {
        if (string.IsNullOrEmpty(argsJson))
        {
            return [];
        }

        return JsonNode.Parse(argsJson) as JsonObject ?? [];
    }

    private static string RequiredString(JsonObject payload, string name)
        => payload[name]?.GetValue<string>()
            ?? throw new InvalidOperationException($"Missing required argument '{name}'.");

    private static IReadOnlyList<string> StringArray(JsonNode? node)
        => node is JsonArray array
            ? [.. array.Select(item => item?.GetValue<string>() ?? string.Empty)]
            : [];

    private static IReadOnlyList<string>? StringArrayOrNull(JsonNode? node)
        => node is JsonArray array ? [.. array.Select(item => item?.GetValue<string>() ?? string.Empty)] : null;

    /// <summary>JSON → the `object?` graph the tool/schema APIs accept.</summary>
    internal static Dictionary<string, object?>? ToObjectDictionary(JsonNode? node)
    {
        if (node is not JsonObject obj)
        {
            return null;
        }

        var result = new Dictionary<string, object?>(StringComparer.Ordinal);
        foreach (var (key, value) in obj)
        {
            result[key] = ToObject(value);
        }

        return result;
    }

    internal static object? ToObject(JsonNode? node) => node switch
    {
        null => null,
        JsonObject obj => ToObjectDictionary(obj),
        JsonArray array => array.Select(ToObject).ToList(),
        JsonValue value => value.GetValueKind() switch
        {
            JsonValueKind.String => value.GetValue<string>(),
            JsonValueKind.Number => value.GetValue<double>(),
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            _ => null,
        },
        _ => null,
    };

    public void Dispose()
    {
        disposed = true;
        contexts.Clear();
        toolUpdates.Clear();

        lock (BusGate)
        {
            foreach (var list in Subscriptions.Values)
            {
                list.RemoveAll(entry => entry.Host == this);
            }
        }
    }
}
