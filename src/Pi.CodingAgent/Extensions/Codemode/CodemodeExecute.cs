using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Pi.Agent.Types;
using Pi.Ai.Models;
using Pi.Ai.Types;
using Pi.Codemode;
using Pi.Codemode.Runtime;
using Pi.CodingAgent.Core;
using Pi.CodingAgent.Core.Extensions;
using Pi.CodingAgent.Core.Tools;
using Pi.CodingAgent.Extensions.ToolSearch;
using Pi.CodingAgent.Utils;
using Pi.CodingAgent.Utils.Image;
using ToolSearchApi = Pi.CodingAgent.Extensions.ToolSearch.ToolSearch;

namespace Pi.CodingAgent.Extensions.Codemode;

/// <summary>
/// Runs one codemode script in the sandbox. Port of the TS
/// <c>extensions/codemode/execute.ts</c> (<c>execute.lazy.ts</c> has no counterpart: the QuickJS
/// wasm/worker preload it defers is carried by the <see cref="ICodemodeJsEngine"/> injection point,
/// so the orchestration itself is cheap to load; TS <c>worker.ts</c> likewise folds into the engine).
/// </summary>
public static class CodemodeExecute
{
    private const int ArgsPreviewChars = 200;
    private const int ErrorPreviewChars = 500;

    /// <summary>
    /// <c>models.classify()</c> and <c>models.generateImages()</c> calls one script may have in
    /// flight; <c>Promise.all</c> over many items queues the rest.
    /// </summary>
    private const int MaxConcurrentModelCalls = 4;

    /// <summary>
    /// Heap limit for the script VM. Overruns throw <c>InternalError: out of memory</c> inside the
    /// script; the limit is forwarded to the engine by way of the sandbox options.
    /// </summary>
    private const long CodeModeMemoryLimitBytes = 256 * 1024 * 1024;

    /// <summary>TS <c>MODEL_TYPES</c> — a Set with iteration order; the port keeps the order (List) for the type-probe message.</summary>
    private static readonly string[] ModelTypes = ["chat", "image", "classifier"];

    /// <summary>Default token budget for script output.</summary>
    private const long DefaultMaxOutputTokens = 10_000;

    /// <summary>Characters per token when estimating.</summary>
    private const int CharsPerToken = 4;

    /// <summary>
    /// Sandbox JS engine seam. TS runs QuickJS wasm in a worker; C# carries VM execution by the
    /// <see cref="ICodemodeJsEngine"/> injection point (see Pi.Codemode). The default is the Jint
    /// engine; tests and hosts can replace it, or disable execution with
    /// <see cref="UnsupportedCodemodeJsEngine"/>.
    /// </summary>
    public static class EngineDefaults
    {
        public static Func<ICodemodeJsEngine>? Override { get; set; }

        public static ICodemodeJsEngine Engine => Override?.Invoke() ?? JintCodemodeEngine.Instance;
    }

    /// <summary>Kind-safe read of a JSON object's string member (the TS <c>typeof x === "string"</c>).</summary>
    private static string? GetString(JsonObject? @object, string key) =>
        @object?[key] is JsonValue value && value.GetValueKind() == JsonValueKind.String
            ? value.GetValue<string>()
            : null;

    private static string? GetString(JsonNode? node) =>
        node is JsonValue value && value.GetValueKind() == JsonValueKind.String ? value.GetValue<string>() : null;

    private static string TruncateText(string text, int maxChars) =>
        text.Length > maxChars ? string.Concat(text.AsSpan(0, maxChars - 3), "...") : text;

    /// <summary>TS <c>previewArgs</c>. The sandbox passes "no arguments" as JSON null, so the port
    /// returns the empty string for null (the TS <c>undefined</c> path).</summary>
    private static string PreviewArgs(JsonNode? args)
    {
        if (args is null)
        {
            return "";
        }

        try
        {
            return TruncateText(CodemodeToolApi.JsonStringify(args), ArgsPreviewChars);
        }
        catch
        {
            return "";
        }
    }

    private static string TextOf(AgentToolResult result) =>
        string.Join("\n", result.Content.OfType<TextContent>().Select(block => block.Text));

    private static ModelType ToModelType(JsonNode? value)
    {
        var text = GetString(value);
        if (text is not null && ModelTypes.Contains(text))
        {
            return text switch
            {
                "chat" => ModelType.Chat,
                "image" => ModelType.Image,
                _ => ModelType.Classifier,
            };
        }

        throw new InvalidOperationException(
            $"Unknown model type {CodemodeToolApi.JsonStringify(value)}. Use \"chat\", \"image\", or \"classifier\".");
    }

    private static string? ToProvider(JsonNode? value)
    {
        if (value is null)
        {
            return null;
        }

        return GetString(value) ?? throw new InvalidOperationException("provider must be a string");
    }

    /// <summary>
    /// Catalog entry for scripts. <c>headers</c> is dropped because models.json headers can carry
    /// credentials. TS <c>toModelInfo</c> (spread + delete).
    /// </summary>
    private static JsonObject ToModelInfo(ModelSpec model)
    {
        var info = ModelSpecJson.ToJsonObject(model);
        info.Remove("headers");
        return info;
    }

    /// <summary><c>an image</c>, <c>a classifier</c>.</summary>
    private static string WithArticle(string word) =>
        word.Length > 0 && word[0] is 'a' or 'e' or 'i' or 'o' or 'u' ? $"an {word}" : $"a {word}";

    private static bool IsRecord(JsonNode? value) => value is JsonObject;

    /// <summary>
    /// A script value in an error message: JSON null, <c>a string</c>, <c>an array</c>, or its keys
    /// (<c>{ prompt }</c>). TS <c>describeValue</c>. The port's "null" covers the TS
    /// <c>undefined</c> and <c>null</c> pair — values cross the sandbox as JSON, where both are null.
    /// </summary>
    private static string DescribeValue(JsonNode? value) => value switch
    {
        null => "null",
        JsonArray array => array.Count == 0 ? "an empty array" : "an array",
        JsonObject @object => KeysOf(@object),
        JsonValue primitive => primitive.GetValueKind() switch
        {
            JsonValueKind.String => "a string",
            JsonValueKind.Number => "a number",
            JsonValueKind.True or JsonValueKind.False => "a boolean",
            _ => "a null",
        },
        _ => "a null",
    };

    private static string KeysOf(JsonObject @object)
    {
        var keys = @object.Select(pair => pair.Key).ToList();
        if (keys.Count == 0)
        {
            return "{}";
        }

        return $"{{ {string.Join(", ", keys.Take(6))}{(keys.Count > 6 ? ", ..." : "")} }}";
    }

    private const string ClassifierContextShape =
        "{ state: { ... }, questions: { <id>: { type: \"choice\", instructions, criteria: { <label>: <meaning> } } | { type: \"score\", instructions, criteria: [<lowest level>, ..., <highest level>] } | { type: \"bool\", instructions, criteria: { true: <meaning>, false: <meaning> } } } }";

    private static InvalidOperationException ClassifierFail(string problem) => new(
        $"models.classify() {problem}. Expected context: {ClassifierContextShape}. See \"Classify\" in {CodemodeToolApi.CodemodeDocsPath}.");

    /// <summary>
    /// Check a script's classifier context, so mistakes fail with the expected shape instead of a
    /// provider error; then convert the JSON shape into the typed <see cref="ClassifierContext"/>.
    /// TS <c>checkClassifierContext</c> (which only checks — the object crosses into pi-ai as is).
    /// </summary>
    private static ClassifierContext CheckClassifierContext(JsonNode? context)
    {
        if (!IsRecord(context))
        {
            throw ClassifierFail(
                $"expects a context object as its second argument, got {DescribeValue(context)}");
        }

        var root = (JsonObject)context!;
        if (!IsRecord(root["state"]))
        {
            throw ClassifierFail($"context.state must be an object, got {DescribeValue(root["state"])}");
        }

        var state = (JsonObject)root["state"]!;
        var questions = root["questions"] as JsonObject;
        if (questions is null || questions.Count == 0)
        {
            throw ClassifierFail(
                $"context.questions must map question IDs to questions, got {DescribeValue(root["questions"])}");
        }

        var converted = new Dictionary<string, ClassifierQuestion>(StringComparer.Ordinal);
        foreach (var (id, question) in questions)
        {
            var at = $"context.questions.{id}";
            if (!IsRecord(question))
            {
                throw ClassifierFail($"{at} must be a question object, got {DescribeValue(question)}");
            }

            var questionObject = (JsonObject)question!;
            if (GetString(questionObject, "instructions") is not { } instructions)
            {
                throw ClassifierFail($"{at}.instructions must be a string");
            }

            var type = GetString(questionObject, "type");
            switch (type)
            {
                case "choice":
                {
                    if (questionObject["criteria"] is not JsonObject criteria || criteria.Count == 0
                        || criteria.Any(pair => GetString(pair.Value) is null))
                    {
                        throw ClassifierFail(
                            $"{at} is a \"choice\" question, so criteria must map each label to its meaning");
                    }

                    converted[id] = new ClassifierChoiceQuestion(
                        instructions,
                        criteria.ToDictionary(pair => pair.Key, pair => GetString(pair.Value)!));
                    break;
                }

                case "score":
                {
                    if (questionObject["criteria"] is not JsonArray criteriaArray || criteriaArray.Count == 0
                        || criteriaArray.Any(value => GetString(value) is null))
                    {
                        throw ClassifierFail(
                            $"{at} is a \"score\" question, so criteria must list the levels as strings, lowest first");
                    }

                    converted[id] = new ClassifierScoreQuestion(
                        instructions,
                        [.. criteriaArray.Select(value => GetString(value)!)]);
                    break;
                }

                case "bool":
                {
                    if (questionObject["criteria"] is not JsonObject criteria
                        || GetString(criteria, "true") is not { } trueMeaning
                        || GetString(criteria, "false") is not { } falseMeaning)
                    {
                        throw ClassifierFail(
                            $"{at} is a \"bool\" question, so criteria must be {{ true: string, false: string }}");
                    }

                    converted[id] = new ClassifierBoolQuestion(instructions, trueMeaning, falseMeaning);
                    break;
                }

                default:
                    throw ClassifierFail(
                        $"{at}.type must be \"choice\", \"score\", or \"bool\", got {CodemodeToolApi.JsonStringify(questionObject["type"])}");
            }
        }

        return new ClassifierContext { State = state, Questions = converted };
    }

    private static InvalidOperationException ImagesFail(string problem) => new(
        $"models.generateImages() {problem}. Expected context: {{ input: [{{ type: \"text\", text: <prompt> }}, ...optional {{ type: \"image\", data: <base64>, mimeType }} references] }}. See \"Generate images\" in {CodemodeToolApi.CodemodeDocsPath}.");

    /// <summary>
    /// Check a script's image context, so mistakes such as <c>{ prompt }</c> fail with the expected
    /// shape; then convert into the typed <see cref="ImagesContext"/>. TS <c>checkImagesContext</c>.
    /// </summary>
    private static ImagesContext CheckImagesContext(JsonNode? context)
    {
        if (!IsRecord(context))
        {
            throw ImagesFail($"expects a context object as its second argument, got {DescribeValue(context)}");
        }

        var root = (JsonObject)context!;
        if (root["input"] is not JsonArray input || input.Count == 0)
        {
            throw ImagesFail($"context.input must be a non-empty array of blocks, got {DescribeValue(root["input"])}");
        }

        var blocks = new List<ContentBlock>();
        for (var index = 0; index < input.Count; index++)
        {
            var block = input[index];
            if (IsRecord(block))
            {
                var @object = (JsonObject)block!;
                var type = GetString(@object, "type");
                if (type == "text" && GetString(@object, "text") is { } prompt)
                {
                    blocks.Add(new TextContent(prompt));
                    continue;
                }

                if (type == "image" && GetString(@object, "data") is { } data
                    && GetString(@object, "mimeType") is { } mimeType)
                {
                    blocks.Add(new ImageContent(data, mimeType));
                    continue;
                }
            }

            throw ImagesFail($"context.input[{index}] must be a text or image block, got {DescribeValue(block)}");
        }

        return new ImagesContext { Input = blocks };
    }

    /// <summary>The fields of a classifier or image call that a nested call row reports. TS <c>ModelCallResult</c>.</summary>
    private interface IModelCallResult
    {
        /// <summary>The TS <c>stopReason</c>: "stop" | "error" | "aborted".</summary>
        string StopReason { get; }

        string? ErrorMessage { get; }

        Usage? Usage { get; }
    }

    private sealed class ClassifierCall(ClassifierResult result) : IModelCallResult
    {
        public string StopReason => result.StopReason switch
        {
            ClassifierStopReason.Stop => "stop",
            ClassifierStopReason.Aborted => "aborted",
            _ => "error",
        };

        public string? ErrorMessage => result.ErrorMessage;

        public Usage? Usage => result.Usage;
    }

    private sealed class ImagesCall(AssistantImages result) : IModelCallResult
    {
        public string StopReason => result.StopReason switch
        {
            ImagesStopReason.Stop => "stop",
            ImagesStopReason.Aborted => "aborted",
            _ => "error",
        };

        public string? ErrorMessage => result.ErrorMessage;

        public Usage? Usage => result.Usage;
    }

    /// <summary>
    /// Runs at most <paramref name="limit"/> calls at once, in call order. TS <c>createLimiter</c>:
    /// a waiter bypasses the capacity check when woken and counts itself in, exactly like the TS
    /// closure (which is why the check is skipped on the resume path).
    /// </summary>
    private sealed class CallLimiter(int limit)
    {
        private readonly object gate = new();
        private readonly Queue<TaskCompletionSource> waiting = new();
        private int active;

        public async Task<T> Run<T>(Func<Task<T>> run)
        {
            Task waitTask;
            lock (this.gate)
            {
                if (this.active < limit)
                {
                    this.active++;
                    waitTask = Task.CompletedTask;
                }
                else
                {
                    var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                    this.waiting.Enqueue(tcs);
                    waitTask = tcs.Task;
                }
            }

            if (!waitTask.IsCompleted)
            {
                await waitTask.ConfigureAwait(false);
                lock (this.gate)
                {
                    this.active++;
                }
            }

            try
            {
                return await run().ConfigureAwait(false);
            }
            finally
            {
                TaskCompletionSource? next;
                lock (this.gate)
                {
                    this.active--;
                    next = this.waiting.Count > 0 ? this.waiting.Dequeue() : null;
                }

                next?.TrySetResult();
            }
        }
    }

    private static bool IsStoreEntryData(JsonNode? data)
    {
        if (data is not JsonObject @object)
        {
            return false;
        }

        return @object["set"] is JsonObject
            && @object["delete"] is JsonArray deleted
            && deleted.All(key => key?.GetValue<string>() is not null);
    }

    /// <summary>
    /// Values of <c>load()</c>: the <c>codemode-store</c> entries on the branch, applied from the
    /// root. TS <c>readCodemodeStore</c>.
    /// </summary>
    public static IReadOnlyDictionary<string, JsonNode?> ReadCodemodeStore(IEnumerable<FileEntry> branch)
    {
        var store = new Dictionary<string, JsonNode?>(StringComparer.Ordinal);
        foreach (var entry in branch.OfType<CustomEntry>())
        {
            // In-memory entries carry the payload as a JsonObject (see CodemodeExtension's
            // JsonEntryData), while entries deserialized from the session file surface the
            // `object?` data as a JsonElement — normalize both to a node, like the TS reads
            // `entry.data` regardless of where the entry came from.
            var data = entry.Data switch
            {
                JsonNode node => node,
                JsonElement element => JsonSerializer.SerializeToNode(element),
                _ => null,
            };
            if (entry.CustomType != CodemodeToolApi.CodemodeStoreEntryType
                || data is null
                || !IsStoreEntryData(data))
            {
                continue;
            }

            var payload = (JsonObject)data;
            foreach (var key in (JsonArray)payload["delete"]!)
            {
                store.Remove(key!.GetValue<string>());
            }

            foreach (var (key, value) in (JsonObject)payload["set"]!)
            {
                store[key] = value?.DeepClone();
            }
        }

        return store;
    }

    /// <summary>Like the script's <c>text()</c>: strings as is, other values as compact JSON.</summary>
    private static string ValueText(JsonNode? value) =>
        GetString(value) ?? CodemodeToolApi.JsonStringify(value);

    /// <summary>
    /// Lay out the script's output so the model can tell items apart: providers join adjacent text
    /// blocks with a newline or with nothing. With more than one text item (<c>text()</c> or the
    /// returned value), each starts with a <c>==&gt; text N/M &lt;==</c> line. <c>console.*</c>
    /// lines follow all other output in one <c>&lt;console_output&gt;</c> block. TS <c>formatOutput</c>.
    /// </summary>
    private static List<ContentBlock> FormatOutput(IReadOnlyList<CodemodeOutputItem> output)
    {
        var total = output.Count(item => item is CodemodeOutputItem.Text { Console: null });
        var items = new List<ContentBlock>();
        var consoleLines = new List<string>();
        var index = 0;
        foreach (var item in output)
        {
            switch (item)
            {
                case CodemodeOutputItem.Image image:
                    items.Add(new ImageContent(image.Data, image.MimeType));
                    break;
                case CodemodeOutputItem.Text { Console: not null } console:
                    consoleLines.Add(console.Value);
                    break;
                case CodemodeOutputItem.Text text:
                    index++;
                    items.Add(new TextContent(
                        total > 1 ? $"==> text {index}/{total} <==\n{text.Value}" : text.Value));
                    break;
            }
        }

        if (consoleLines.Count > 0)
        {
            items.Add(new TextContent($"<console_output>\n{string.Join("\n", consoleLines)}\n</console_output>"));
        }

        return items;
    }

    /// <summary>Join adjacent text items into one, each part starting on its own line. TS <c>joinAdjacentText</c>.</summary>
    private static List<ContentBlock> JoinAdjacentText(IReadOnlyList<ContentBlock> items)
    {
        var joined = new List<ContentBlock>();
        foreach (var item in items)
        {
            if (item is TextContent text && joined.Count > 0 && joined[^1] is TextContent last)
            {
                var separator = last.Text.Length == 0 || last.Text.EndsWith("\n", StringComparison.Ordinal)
                    ? ""
                    : "\n";
                joined[^1] = new TextContent($"{last.Text}{separator}{text.Text}");
            }
            else
            {
                joined.Add(item);
            }
        }

        return joined;
    }

    private static string FormatCallSummary(IReadOnlyList<CodemodeToolApi.CodemodeNestedCall> calls)
    {
        if (calls.Count == 0)
        {
            return "No tool calls were made.";
        }

        return $"Tool calls made before the failure (they are not undone): {string.Join(", ", calls.Select(call => $"{call.Name} ({call.Status})"))}";
    }

    private static string FormatError(CodemodeError error, IReadOnlyList<CodemodeToolApi.CodemodeNestedCall> calls)
    {
        var head = error.Kind switch
        {
            CodemodeErrorKind.Script => error.Stack ?? $"{error.Name ?? "Error"}: {error.Message}",
            CodemodeErrorKind.Timeout => $"Script timed out: {error.Message}",
            CodemodeErrorKind.Aborted => $"Script aborted: {error.Message}",
            _ => $"Script sandbox failed: {error.Message}",
        };
        return $"{head}\n\n{FormatCallSummary(calls)}";
    }

    /// <summary>Write the full text output to a temp file, like bash does for truncated output.</summary>
    private static async Task<(string? Path, string? Error)> SpillOutput(string text)
    {
        try
        {
            return (await OutputFiles.WriteOutputFileAsync("pi-codemode", ".txt", text).ConfigureAwait(false), null);
        }
        catch (Exception error)
        {
            return (null, error.Message);
        }
    }

    /// <summary>File extensions of the image types <c>image()</c> accepts. Must list every type the sandbox's <c>image()</c> detects.</summary>
    private static readonly IReadOnlyDictionary<string, string> ImageExtensions =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["image/png"] = ".png",
            ["image/jpeg"] = ".jpg",
            ["image/gif"] = ".gif",
            ["image/webp"] = ".webp",
        };

    /// <summary>
    /// Save each image to a temp file and put a text item with its path before it. The model sees
    /// the image but has no other way to reach its bytes: scripts cannot write files, and
    /// <c>write</c> only takes text. Images shown more than once are saved once.
    /// TS <c>saveImages</c>.
    /// </summary>
    private static async Task<List<ContentBlock>> SaveImages(IReadOnlyList<ContentBlock> items)
    {
        var labels = new ConcurrentDictionary<string, Task<string>>();
        async Task<string> LabelAsync(ImageContent image)
        {
            var bytes = LenientBase64.Decode(image.Data ?? "");
            var kind = $"{image.MimeType}, {Truncate.FormatSize(bytes.Length)}";
            if (!ImageExtensions.TryGetValue(image.MimeType ?? "", out var extension))
            {
                throw new InvalidOperationException($"No file extension for image type {image.MimeType}");
            }

            // A failed write (disk full, unwritable temp dir) must not discard the result of a
            // script whose tool calls already ran, so it becomes part of the label.
            try
            {
                var path = await OutputFiles.WriteOutputFileAsync("pi-codemode", extension, bytes).ConfigureAwait(false);
                return $"[Image saved to {path} ({kind})]";
            }
            catch (Exception error)
            {
                return $"[Image ({kind}) could not be saved: {error.Message}]";
            }
        }

        var result = await Task.WhenAll(items.Select(async item =>
        {
            if (item is not ImageContent image)
            {
                return new[] { item };
            }

            var pending = labels.GetOrAdd(image.Data ?? "", _ => LabelAsync(image));
            return new ContentBlock[] { new TextContent(await pending.ConfigureAwait(false)), image };
        })).ConfigureAwait(false);
        return [.. result.SelectMany(rows => rows)];
    }

    /// <summary>
    /// Apply the token budget: when the combined text exceeds it, the text items become one item
    /// that keeps the start and end of the text, and images follow it. The full text is written to
    /// a temp file. TS <c>truncateOutput</c>.
    /// </summary>
    private static async Task<(List<ContentBlock> Items, string? FullOutputPath)> TruncateOutput(
        IReadOnlyList<ContentBlock> items, long maxTokens)
    {
        var texts = items.OfType<TextContent>().Select(item => item.Text).ToList();
        var combined = string.Join("\n", texts);
        var budget = maxTokens * CharsPerToken;
        if (texts.Count == 0 || combined.Length <= budget)
        {
            return ([.. items], null);
        }

        var headChars = (int)Math.Floor((double)budget / 2);
        var tailChars = (int)(budget - headChars);
        var removed = combined.Length - headChars - tailChars;
        var head = combined[..headChars];
        var tail = tailChars > 0 ? combined[^tailChars..] : "";
        var text =
            $"Warning: truncated output (original token count: {Math.Ceiling((double)combined.Length / CharsPerToken)})\nTotal output lines: {combined.Split('\n').Length}\n\n{head}…{Math.Ceiling((double)removed / CharsPerToken)} tokens truncated…{tail}";
        var (path, error) = await SpillOutput(combined).ConfigureAwait(false);
        text += path is not null
            ? $"\n\n[Full output: {path} (read with offset/limit)]"
            : $"\n\n[Could not save the full output: {error}]";
        return ([
            new TextContent(text),
            .. items.OfType<ImageContent>(),
        ], path);
    }

    /// <summary>
    /// The value a script receives for a nested call: a tool that declares
    /// <c>outputSchema</c> resolves to its <c>structuredContent</c>, also for error results that
    /// carry one (such as MCP results with <c>isError</c>); any other tool resolves to its text
    /// content. Other failures reject with the tool's error text. TS <c>toScriptValue</c>.
    /// </summary>
    private static JsonNode? ToScriptValue(AgentTool tool, AgentToolCallOutcome outcome)
    {
        if (tool.OutputSchema is not null && outcome.Result.StructuredContent is not null)
        {
            return ToJsonNode(outcome.Result.StructuredContent);
        }

        var text = TextOf(outcome.Result);
        if (outcome.IsError)
        {
            throw new InvalidOperationException(
                text.Length > 0 ? text : $"Tool \"{tool.Name}\" failed");
        }

        return JsonValue.Create(text);
    }

    /// <summary>Structured content arrives as JSON-shaped CLR values; the sandbox reads JSON nodes.</summary>
    private static JsonNode? ToJsonNode(object? value) => value switch
    {
        null => null,
        JsonNode node => node.DeepClone(),
        string text => JsonValue.Create(text),
        bool flag => JsonValue.Create(flag),
        int or long or double or float => JsonValue.Create(Convert.ToDouble(value, CultureInfo.InvariantCulture)),
        IDictionary<string, object?> dictionary => new JsonObject(
            [.. dictionary.Select(pair => new KeyValuePair<string, JsonNode?>(pair.Key, ToJsonNode(pair.Value)))]),
        System.Collections.IEnumerable enumerable => new JsonArray(
            [.. enumerable.Cast<object?>().Select(ToJsonNode)]),
        _ => JsonValue.Create(value.ToString()),
    };

    /// <summary>
    /// Run one script. Without a session context (a plain Agent or a direct call) scripts cannot
    /// call tools, <c>store()</c> starts empty, and writes are dropped. TS <c>executeCodemode</c>.
    /// </summary>
    public static async Task<AgentToolResult> ExecuteCodemodeAsync(
        string toolCallId,
        IReadOnlyDictionary<string, object?> args,
        CancellationToken signal,
        AgentToolUpdateCallback? onUpdate,
        IExtensionToolContext? ctx,
        CodemodeToolApi.CodemodeToolOptions? options = null)
    {
        options ??= new CodemodeToolApi.CodemodeToolOptions();
        var startedAt = Stopwatch.GetTimestamp();
        var code = ToolArgs.GetString(args, "code")
            ?? throw new InvalidOperationException("codemode tool requires a \"code\" string argument");
        var parsed = CodemodeSource.Parse(code);
        var calls = new List<CodemodeToolApi.CodemodeNestedCall>();

        // Usage of the script's `models.*` calls. Nested tool calls report theirs through the session.
        Usage? modelUsage = null;
        // Images returned by `models.generateImages()`, to notice a script that never shows them.
        var generatedImages = 0;

        CodemodeToolApi.CodemodeToolDetails Snapshot() =>
            new() { Calls = [.. calls.Select(call => call with { })] };

        async Task PublishAsync()
        {
            if (onUpdate is not null)
            {
                await onUpdate(new AgentToolResult([], Snapshot())).ConfigureAwait(false);
            }
        }

        void AddModelUsage(Usage usage) =>
            modelUsage = modelUsage is not null ? UsageTotalsApi.CombineUsage(modelUsage, usage) : usage;

        void AddGeneratedImages(int count) => generatedImages += count;

        IReadOnlyList<AgentTool> callable =
            ctx is not null ? CodemodeToolApi.GetCodemodeCallableTools(ctx.Tools) : [];
        // ALL_TOOLS entries carry the declaration.
        var guidelines = options.GetToolGuidelines?.Invoke();
        IReadOnlyList<string>? GuidelinesOf(string name) =>
            guidelines is not null && guidelines.TryGetValue(name, out var value) ? value : null;
        var samples = callable.ToDictionary(
            tool => tool.Name,
            tool => CodemodeDeclarations.RenderToolSample(
                CodemodeToolApi.ToCodemodeDeclaration(tool, GuidelinesOf(tool.Name)).ToSandboxTool()),
            StringComparer.Ordinal);
        var sandboxTools = new List<CodemodeTool>();
        foreach (var tool in callable)
        {
            var captured = tool;
            sandboxTools.Add(new CodemodeTool
            {
                Name = tool.Name,
                Description = samples[tool.Name],
                Execute = async (argsJson, callContext) =>
                {
                    var record = new CodemodeToolApi.CodemodeNestedCall
                    {
                        Id = $"{toolCallId}/?",
                        Name = captured.Name,
                        Args = PreviewArgs(argsJson),
                    };
                    calls.Add(record);
                    await PublishAsync().ConfigureAwait(false);
                    var callStartedAt = Stopwatch.GetTimestamp();
                    // Only tools from ctx.tools are callable, so ctx is set here.
                    if (ctx is null)
                    {
                        throw new InvalidOperationException("Tool calls need a session");
                    }

                    var outcome = await ctx.ExecuteTool(
                        captured.Name,
                        argsJson,
                        new ExecuteToolOptions { Signal = callContext.Signal }).ConfigureAwait(false);
                    record.Id = outcome.ToolCall.Id;
                    record.DurationMs = ElapsedMs(callStartedAt);
                    if (outcome.IsError)
                    {
                        record.Status = callContext.Signal.IsCancellationRequested
                            ? CodemodeToolApi.NestedCallStatus.Cancelled
                            : CodemodeToolApi.NestedCallStatus.Error;
                        var text = TextOf(outcome.Result);
                        record.Error = TruncateText(
                            text.Length > 0 ? text : $"Tool \"{captured.Name}\" failed",
                            ErrorPreviewChars);
                    }
                    else
                    {
                        record.Status = CodemodeToolApi.NestedCallStatus.Ok;
                    }

                    await PublishAsync().ConfigureAwait(false);
                    return ToScriptValue(captured, outcome);
                },
            });
        }

        var sandbox = new CodemodeSandbox(new CodemodeSandboxOptions
        {
            Tools = sandboxTools,
            Globals =
            [
                .. CreateDiscoveryGlobals(callable, samples, options),
                .. options.Models == true && ctx is not null
                    ? CreateModelGlobals(
                        new CodemodeToolApi.ModelRegistryRuntime(ctx.ModelRegistry),
                        toolCallId,
                        calls,
                        PublishAsync,
                        AddModelUsage,
                        AddGeneratedImages)
                    : [],
            ],
            // TS: sourceOptions.timeoutMs ?? Number.POSITIVE_INFINITY — no timer.
            TimeoutMs = parsed.Options.TimeoutMs ?? long.MaxValue,
            MemoryLimitBytes = CodeModeMemoryLimitBytes,
            Engine = EngineDefaults.Engine,
        });

        CodemodeResult result;
        try
        {
            var store = ctx is not null ? ReadCodemodeStore(ctx.SessionManager.Entries) : null;
            result = await sandbox.ExecuteAsync(
                parsed.Code,
                new CodemodeExecuteOptions { Signal = signal, Store = store }).ConfigureAwait(false);
        }
        finally
        {
            await sandbox.CloseAsync().ConfigureAwait(false);
        }

        // Calls still marked running were cut off by the script ending, a timeout, or an abort.
        foreach (var call in calls)
        {
            if (call.Status == CodemodeToolApi.NestedCallStatus.Running)
            {
                call.Status = CodemodeToolApi.NestedCallStatus.Cancelled;
            }
        }

        List<CodemodeOutputItem> scriptOutput = result switch
        {
            CodemodeResult.Success success0 => [.. success0.Output],
            CodemodeResult.Failure failure0 => [.. failure0.Output],
            _ => [],
        };
        if (result is CodemodeResult.Success success)
        {
            var (set, deleted) = (success.StoreWrites.Set, success.StoreWrites.Delete);
            if (set.Count > 0 || deleted.Count > 0)
            {
                options.AppendEntry?.Invoke(
                    CodemodeToolApi.CodemodeStoreEntryType,
                    new CodemodeToolApi.CodemodeStoreEntryData { Set = set, Delete = deleted });
            }

            // pi extension: a returned value is appended like text(). The port's null check merges
            // the TS `undefined` (no return) and JSON-null return pair — the worker protocol's
            // value string is null for both, so an explicit `return null` appends nothing (TS
            // appends "null").
            if (success.Value is not null)
            {
                scriptOutput.Add(new CodemodeOutputItem.Text(ValueText(success.Value)));
            }
        }

        var items = FormatOutput(scriptOutput);
        if (result is CodemodeResult.Failure failure)
        {
            items.Add(new TextContent($"Script error:\n{FormatError(failure.Error, calls)}"));
        }

        if (generatedImages > 0 && items.All(item => item is not ImageContent))
        {
            items.Add(new TextContent(
                $"Note: models.generateImages() returned {generatedImages} image{(generatedImages == 1 ? "" : "s")} that the script did not show. Show each image block of result.output with image(block)."));
        }

        var (truncatedItems, fullOutputPath) = await TruncateOutput(
            JoinAdjacentText(items),
            parsed.Options.MaxOutputTokens ?? DefaultMaxOutputTokens).ConfigureAwait(false);
        // After truncation, which joins the text items and moves images after them, so each path
        // stays next to its image and is never cut.
        var output = JoinAdjacentText(await SaveImages(truncatedItems).ConfigureAwait(false));
        var wallTime = ElapsedMs(startedAt) / 1000.0;
        var header =
            $"{(result is CodemodeResult.Success ? "Script completed" : "Script failed")}\nWall time {wallTime.ToString("F1", CultureInfo.InvariantCulture)} seconds\nOutput:\n";
        var details = Snapshot();
        if (fullOutputPath is not null)
        {
            details = details with { FullOutputPath = fullOutputPath };
        }

        return new AgentToolResult(
            [new TextContent(header), .. output],
            Details: details,
            UsageStats: modelUsage,
            IsError: result is not CodemodeResult.Success);
    }

    private static long ElapsedMs(long startedAt) =>
        (long)Stopwatch.GetElapsedTime(startedAt).TotalMilliseconds;

    /// <summary>
    /// Whether <paramref name="query"/> names the namespace: its name, its script identifier
    /// (<c>mcp__dev-radius</c> is <c>mcp__dev_radius</c>), or the part after its last <c>__</c> in
    /// either form (<c>dev-radius</c>, <c>dev_radius</c>). TS <c>isNamespaceName</c>.
    /// </summary>
    private static bool IsNamespaceName(string ns, string query)
    {
        var id = CodemodeIdentifiers.ToCodemodeIdentifier(ns);
        var queryId = CodemodeIdentifiers.ToCodemodeIdentifier(query);
        static string? Suffix(string name) => name.Contains("__", StringComparison.Ordinal)
            ? name[(name.LastIndexOf("__", StringComparison.Ordinal) + 2)..]
            : null;
        return ns == query || id == queryId || Suffix(ns) == query || Suffix(id) == queryId;
    }

    /// <summary>
    /// <c>searchTools()</c>, <c>describeTool()</c>, and <c>describeNamespace()</c>: ranked search
    /// and lookup over the script's nested tools and their namespaces. TS <c>createDiscoveryGlobals</c>.
    /// </summary>
    private static List<CodemodeTool> CreateDiscoveryGlobals(
        IReadOnlyList<AgentTool> tools,
        IReadOnlyDictionary<string, string> samples,
        CodemodeToolApi.CodemodeToolOptions options)
    {
        var ranker = new ToolSearchApi.Bm25Ranker();
        JsonObject Entry(string name) => new()
        {
            ["name"] = CodemodeIdentifiers.ToCodemodeIdentifier(name),
            ["description"] = samples.GetValueOrDefault(name) ?? "",
        };

        static JsonArray ArgsAsArray(JsonNode? args) => args as JsonArray ?? [];

        return
        [
            new CodemodeTool
            {
                Name = "searchTools",
                Spread = true,
                Execute = (args, _) =>
                {
                    var pair = ArgsAsArray(args);
                    var query = pair.Count > 0 ? pair[0] : null;
                    var searchOptions = pair.Count > 1 ? pair[1] : null;
                    if (GetString(query) is null)
                    {
                        throw new InvalidOperationException("searchTools() expects a query string");
                    }

                    // TS: searchOptions?.limit ?? DEFAULT_TOOL_SEARCH_LIMIT — JSON null falls back too.
                    var limitValue = (searchOptions as JsonObject)?["limit"]
                        ?? JsonValue.Create(ToolSearchApi.DefaultToolSearchLimit);
                    if (limitValue is not JsonValue
                        || limitValue.GetValueKind() != JsonValueKind.Number
                        || !IsJsonInteger(limitValue)
                        || limitValue.GetValue<double>() <= 0)
                    {
                        throw new InvalidOperationException("searchTools() limit must be a positive integer");
                    }

                    var limit = (int)Math.Min(limitValue.GetValue<double>(), int.MaxValue);
                    var namespaceNode = (searchOptions as JsonObject)?["namespace"];
                    if (namespaceNode is not null && GetString(namespaceNode) is null)
                    {
                        throw new InvalidOperationException("searchTools() namespace must be a string");
                    }

                    var namespaceFilter = GetString(namespaceNode);
                    var documents = tools
                        .Select(tool => (Tool: tool, Namespace: options.GetToolNamespace?.Invoke(tool.Name)))
                        .Where(pair => namespaceFilter is null
                            || (pair.Namespace is not null
                                && IsNamespaceName(pair.Namespace.Name, namespaceFilter)))
                        .Select(pair => ToolSearchApi.CreateToolSearchDocument(
                            pair.Tool.Name, pair.Tool.Description, pair.Tool.Parameters, pair.Namespace))
                        .ToList();
                    var matches = ranker.Rank(GetString(query)!, documents, limit);
                    return Task.FromResult<JsonNode?>(
                        new JsonArray([.. matches.Select(match => (JsonNode?)Entry(match.Name))]));
                },
            },
            new CodemodeTool
            {
                Name = "describeTool",
                Spread = true,
                Execute = (args, _) =>
                {
                    var pair = ArgsAsArray(args);
                    var name = pair.Count > 0 ? pair[0] : null;
                    if (GetString(name) is null)
                    {
                        throw new InvalidOperationException("describeTool() expects a tool name");
                    }

                    var wanted = GetString(name)!;
                    var tool = tools.FirstOrDefault(
                        candidate => candidate.Name == wanted
                            || CodemodeIdentifiers.ToCodemodeIdentifier(candidate.Name) == wanted);
                    JsonNode? value = tool is not null && samples.TryGetValue(tool.Name, out var sample)
                        ? JsonValue.Create(sample)
                        : null;
                    return Task.FromResult(value);
                },
            },
            new CodemodeTool
            {
                Name = "describeNamespace",
                Spread = true,
                Execute = (args, _) =>
                {
                    var pair = ArgsAsArray(args);
                    var name = pair.Count > 0 ? pair[0] : null;
                    if (GetString(name) is null)
                    {
                        throw new InvalidOperationException("describeNamespace() expects a namespace name");
                    }

                    var wanted = GetString(name)!;
                    ToolNamespace? found = null;
                    var names = new List<JsonNode?>();
                    foreach (var tool in tools)
                    {
                        var toolNamespace = options.GetToolNamespace?.Invoke(tool.Name);
                        if (toolNamespace is null || !IsNamespaceName(toolNamespace.Name, wanted))
                        {
                            continue;
                        }

                        found ??= toolNamespace;
                        names.Add(CodemodeIdentifiers.ToCodemodeIdentifier(tool.Name));
                    }

                    JsonNode? value = null;
                    if (found is not null)
                    {
                        var described = new JsonObject { ["name"] = found.Name };
                        // TS: ...(namespace.description ? { description } : {}) — absent, not null.
                        if (!string.IsNullOrEmpty(found.Description))
                        {
                            described["description"] = found.Description;
                        }

                        if (!string.IsNullOrEmpty(found.Instructions))
                        {
                            described["instructions"] = found.Instructions;
                        }

                        described["tools"] = new JsonArray([.. names]);
                        value = described;
                    }

                    return Task.FromResult(value);
                },
            },
        ];
    }

    private static bool IsJsonInteger(JsonNode node)
    {
        var value = node.GetValue<double>();
        return !double.IsNaN(value) && !double.IsInfinity(value) && Math.Floor(value) == value;
    }

    /// <summary>
    /// <c>models.*</c> for scripts: the model registry methods documented in docs/codemode.md.
    /// Classifier and image calls appear as nested call rows so the renderer shows them, and their
    /// usage goes to <c>addUsage</c>. Rows show only the model, never prompts or image data.
    /// TS <c>createModelGlobals</c>.
    /// </summary>
    private static List<CodemodeTool> CreateModelGlobals(
        CodemodeToolApi.ICodemodeModelRuntime models,
        string toolCallId,
        List<CodemodeToolApi.CodemodeNestedCall> calls,
        Func<Task> publish,
        Action<Usage> addUsage,
        Action<int> addGeneratedImages)
    {
        var limiter = new CallLimiter(MaxConcurrentModelCalls);
        var callCount = 0;

        /// <summary>
        /// Resolve the script's model by provider and id only, check the context, then run the call
        /// as a nested call row. A script-supplied baseUrl or headers must never receive the
        /// credentials. TS <c>runModelCall</c>.
        /// </summary>
        async Task<JsonNode?> RunModelCallAsync<TContext, TResult>(
            string name,
            ModelType type,
            JsonNode? args,
            CancellationToken signal,
            Func<JsonNode?, TContext> checkContext,
            Func<ModelSpec, TContext, CancellationToken, Task<TResult>> run,
            Func<TResult, IModelCallResult> outcome,
            Func<TResult, JsonObject> serialize)
        {
            var pair = args as JsonArray ?? [];
            var model = pair.Count > 0 ? pair[0] : null;
            var context = pair.Count > 1 ? pair[1] : null;
            var typeText = type switch
            {
                ModelType.Classifier => "classifier",
                ModelType.Image => "image",
                _ => "chat",
            };
            var listHint = $"List the {typeText} models you can use with models.getAvailableOfType(\"{typeText}\").";
            var provider = GetString(model as JsonObject, "provider");
            var id = GetString(model as JsonObject, "id");
            if (model is not JsonObject || provider is null || id is null)
            {
                // undefined arrives as null: spread arguments cross the sandbox as a JSON array.
                var undefinedHint = model is null
                    ? " models.getModelOfType() returns undefined for an unknown provider or id."
                    : "";
                throw new InvalidOperationException(
                    $"{name}() expects {WithArticle(typeText)} model as its first argument, got {DescribeValue(model)}.{undefinedHint} {listHint}");
            }

            var reference = $"{provider}/{id}";
            var resolved = models.GetModelOfType(type, provider, id);
            if (resolved is null)
            {
                var actualType = ModelTypes.FirstOrDefault(other =>
                    other != typeText
                    && models.GetModelOfType(
                        other switch
                        {
                            "chat" => ModelType.Chat,
                            "image" => ModelType.Image,
                            _ => ModelType.Classifier,
                        },
                        provider,
                        id) is not null);
                throw new InvalidOperationException(
                    actualType is not null
                        ? $"\"{reference}\" is {WithArticle(actualType)} model, not {WithArticle(typeText)} model. {listHint}"
                        : $"Unknown {typeText} model \"{reference}\". {listHint}");
            }

            var checkedContext = checkContext(context);

            var record = new CodemodeToolApi.CodemodeNestedCall
            {
                Id = $"{toolCallId}/{name}/{++callCount}",
                Name = name,
                Args = $"{resolved.Provider}/{resolved.Id}",
            };
            calls.Add(record);
            await publish().ConfigureAwait(false);
            var startedAt = Stopwatch.GetTimestamp();
            var result = await limiter.Run(() => run(resolved, checkedContext, signal)).ConfigureAwait(false);
            var report = outcome(result);
            record.DurationMs = ElapsedMs(startedAt);
            record.Status = report.StopReason switch
            {
                "stop" => CodemodeToolApi.NestedCallStatus.Ok,
                "aborted" => CodemodeToolApi.NestedCallStatus.Cancelled,
                _ => CodemodeToolApi.NestedCallStatus.Error,
            };
            if (report.ErrorMessage is not null)
            {
                record.Error = TruncateText(report.ErrorMessage, ErrorPreviewChars);
            }

            if (report.Usage is not null)
            {
                record.Cost = report.Usage.Cost.Total;
                addUsage(report.Usage);
            }

            await publish().ConfigureAwait(false);
            return serialize(result);
        }

        return
        [
            new CodemodeTool
            {
                Name = "models.getModelsOfType",
                Spread = true,
                Execute = (args, _) =>
                {
                    var pair = args as JsonArray ?? [];
                    var type = pair.Count > 0 ? pair[0] : null;
                    var provider = pair.Count > 1 ? pair[1] : null;
                    var list = models.GetModelsOfType(ToModelType(type), ToProvider(provider));
                    return Task.FromResult<JsonNode?>(
                        new JsonArray([.. list.Select(model => (JsonNode?)ToModelInfo(model))]));
                },
            },
            new CodemodeTool
            {
                Name = "models.getAvailableOfType",
                Spread = true,
                Execute = async (args, context) =>
                {
                    var pair = args as JsonArray ?? [];
                    var type = pair.Count > 0 ? pair[0] : null;
                    var provider = pair.Count > 1 ? pair[1] : null;
                    var available = await models.GetAvailableOfTypeAsync(
                        ToModelType(type), ToProvider(provider), context.Signal).ConfigureAwait(false);
                    return new JsonArray([.. available.Select(model => (JsonNode?)ToModelInfo(model))]);
                },
            },
            new CodemodeTool
            {
                Name = "models.getModelOfType",
                Spread = true,
                Execute = (args, _) =>
                {
                    var pair = args as JsonArray ?? [];
                    var type = pair.Count > 0 ? pair[0] : null;
                    var provider = pair.Count > 1 ? pair[1] : null;
                    var id = pair.Count > 2 ? pair[2] : null;
                    if (GetString(provider) is null || GetString(id) is null)
                    {
                        throw new InvalidOperationException(
                            $"models.getModelOfType(type, provider, id) expects three strings, got ({string.Join(", ", pair.Select(DescribeValue))}). The provider and the id are separate arguments, for example models.getModelOfType(\"classifier\", \"typesafe\", \"jev-latest\").");
                    }

                    var model = models.GetModelOfType(
                        ToModelType(type), GetString(provider)!, GetString(id)!);
                    return Task.FromResult<JsonNode?>(model is null ? null : ToModelInfo(model));
                },
            },
            new CodemodeTool
            {
                Name = "models.classify",
                Spread = true,
                Execute = (args, context) => RunModelCallAsync(
                    "models.classify",
                    ModelType.Classifier,
                    args,
                    context.Signal,
                    static json => CheckClassifierContext(json),
                    (resolved, checkedContext, inner) => models.ClassifyAsync(resolved, checkedContext, inner),
                    static result => new ClassifierCall(result),
                    static result => ModelCallJson.SerializeClassifier(result)),
            },
            new CodemodeTool
            {
                Name = "models.generateImages",
                Spread = true,
                Execute = (args, context) => RunModelCallAsync(
                    "models.generateImages",
                    ModelType.Image,
                    args,
                    context.Signal,
                    static json => CheckImagesContext(json),
                    async (resolved, checkedContext, inner) =>
                    {
                        var result = await models.GenerateImagesAsync(
                            resolved, checkedContext, inner).ConfigureAwait(false);
                        addGeneratedImages(result.Output.Count(block => block is ImageContent));
                        return result;
                    },
                    static result => new ImagesCall(result),
                    static result => ModelCallJson.SerializeImages(result)),
            },
        ];
    }

    /// <summary>
    /// TS returns the raw <c>ClassifierResult</c> / <c>AssistantImages</c> objects, which cross the
    /// sandbox as their JSON. The port serializes with the camelCase policy so field names match
    /// the TS objects the scripts consume.
    /// </summary>
    internal static class ModelCallJson
    {
        private static readonly JsonSerializerOptions Options = new()
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
            Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        };

        public static JsonObject SerializeClassifier(ClassifierResult result) =>
            (JsonObject)JsonSerializer.SerializeToNode(result, Options)!;

        public static JsonObject SerializeImages(AssistantImages result) =>
            (JsonObject)JsonSerializer.SerializeToNode(result, Options)!;
    }
}
