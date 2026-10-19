using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Pi.Chord.Delta;
using Pi.Durable.Types;
using DeltaPath = Pi.Chord.Delta.Path;

namespace Pi.Durable.Storage;

internal static class SqliteStorageJson
{
    private static readonly JsonSerializerOptions Options = CreateOptions();

    public static string Serialize<T>(T value) => JsonSerializer.Serialize(value, Options);

    public static T Deserialize<T>(string json)
        => JsonSerializer.Deserialize<T>(json, Options)
           ?? throw new JsonException($"SQLite JSON value for {typeof(T).Name} was null");

    public static string EncodeIndexedString(string value)
    {
        var output = new StringBuilder(value.Length + 2);
        output.Append('"');
        for (var index = 0; index < value.Length; index++)
        {
            var character = value[index];
            switch (character)
            {
                case '"':
                    output.Append("\\\"");
                    break;
                case '\\':
                    output.Append("\\\\");
                    break;
                case '\b':
                    output.Append("\\b");
                    break;
                case '\f':
                    output.Append("\\f");
                    break;
                case '\n':
                    output.Append("\\n");
                    break;
                case '\r':
                    output.Append("\\r");
                    break;
                case '\t':
                    output.Append("\\t");
                    break;
                default:
                    if (character < 0x20
                        || char.IsLowSurrogate(character)
                        || char.IsHighSurrogate(character)
                        && (index + 1 == value.Length || !char.IsLowSurrogate(value[index + 1])))
                    {
                        output.Append("\\u");
                        output.Append(((int)character).ToString("x4", CultureInfo.InvariantCulture));
                    }
                    else
                    {
                        output.Append(character);
                        if (char.IsHighSurrogate(character)) output.Append(value[++index]);
                    }
                    break;
            }
        }
        output.Append('"');
        return output.ToString();
    }

    private static JsonSerializerOptions CreateOptions()
    {
        var options = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            PropertyNameCaseInsensitive = true,
        };
        options.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.CamelCase));
        options.Converters.Add(new DurableIdJsonConverterFactory());
        options.Converters.Add(new DocumentScopeJsonConverter());
        options.Converters.Add(new SubmissionRecordJsonConverter());
        options.Converters.Add(new ContextEditJsonConverter());
        options.Converters.Add(new DeltaOpJsonConverter());
        options.Converters.Add(new JsonValueConverter());
        return options;
    }

    private sealed class DurableIdJsonConverterFactory : JsonConverterFactory
    {
        public override bool CanConvert(Type typeToConvert)
            => typeToConvert == typeof(ConversationId)
                || typeToConvert == typeof(EntryId)
                || typeToConvert == typeof(SubmissionId)
                || typeToConvert == typeof(DocumentId)
                || typeToConvert == typeof(Seq)
                || typeToConvert.IsGenericType && typeToConvert.GetGenericTypeDefinition() == typeof(TaskId<>);

        public override JsonConverter CreateConverter(Type typeToConvert, JsonSerializerOptions options)
            => (JsonConverter)Activator.CreateInstance(
                typeof(DurableIdJsonConverter<>).MakeGenericType(typeToConvert))!;
    }

    private sealed class DurableIdJsonConverter<TId> : JsonConverter<TId>
    {
        public override TId Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            if (reader.TokenType != JsonTokenType.Number || !reader.TryGetInt64(out var value))
                throw new JsonException($"Expected an integer {typeof(TId).Name}");

            object id = typeof(TId) == typeof(ConversationId) ? ConversationId.From(value)
                : typeof(TId) == typeof(EntryId) ? EntryId.From(value)
                : typeof(TId) == typeof(SubmissionId) ? SubmissionId.From(value)
                : typeof(TId) == typeof(DocumentId) ? DocumentId.From(value)
                : typeof(TId) == typeof(Seq) ? Seq.From(value)
                : CreateTaskId(value);
            return (TId)id;
        }

        public override void Write(Utf8JsonWriter writer, TId value, JsonSerializerOptions options)
        {
            var rawValue = typeof(TId).GetProperty(nameof(ConversationId.Value))?.GetValue(value);
            if (rawValue is not long id) throw new JsonException($"Invalid {typeof(TId).Name}");
            writer.WriteNumberValue(id);
        }

        private static object CreateTaskId(long value)
        {
            if (!typeof(TId).IsGenericType || typeof(TId).GetGenericTypeDefinition() != typeof(TaskId<>))
                throw new JsonException($"Unsupported numeric ID type {typeof(TId).Name}");
            var taskIdType = typeof(TaskId<>).MakeGenericType(typeof(TId).GetGenericArguments()[0]);
            return taskIdType.GetMethod(nameof(TaskId<object?>.From), [typeof(long)])!
                .Invoke(null, [value])!;
        }
    }

    private sealed class DocumentScopeJsonConverter : JsonConverter<DocumentScope>
    {
        public override DocumentScope Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            using var document = JsonDocument.ParseValue(ref reader);
            var root = document.RootElement;
            var kind = RequiredString(root, "kind");
            return kind switch
            {
                "session" => new DocumentScope.SessionScope(),
                "conversation" => new DocumentScope.ConversationScope(
                    ConversationId.From(RequiredInt64(root, "conversationId"))),
                "task" => new DocumentScope.TaskScope(
                    TaskId<object?>.From(RequiredInt64(root, "taskId"))),
                _ => throw new JsonException($"Unknown document scope kind: {kind}"),
            };
        }

        public override void Write(Utf8JsonWriter writer, DocumentScope value, JsonSerializerOptions options)
        {
            writer.WriteStartObject();
            switch (value)
            {
                case DocumentScope.SessionScope:
                    writer.WriteString("kind", "session");
                    break;
                case DocumentScope.ConversationScope conversation:
                    writer.WriteString("kind", "conversation");
                    writer.WriteNumber("conversationId", conversation.ConversationId.Value);
                    break;
                case DocumentScope.TaskScope task:
                    writer.WriteString("kind", "task");
                    writer.WriteNumber("taskId", task.TaskId.Value);
                    break;
                default:
                    throw new JsonException($"Unknown document scope type: {value.GetType().Name}");
            }
            writer.WriteEndObject();
        }
    }

    private sealed class SubmissionRecordJsonConverter : JsonConverter<SubmissionRecord>
    {
        public override SubmissionRecord Read(
            ref Utf8JsonReader reader,
            Type typeToConvert,
            JsonSerializerOptions options)
        {
            using var document = JsonDocument.ParseValue(ref reader);
            var root = document.RootElement;
            var type = RequiredString(root, "type");
            var id = SubmissionId.From(RequiredInt64(root, "id"));
            var conversationId = ConversationId.From(RequiredInt64(root, "conversationId"));
            var requestId = OptionalString(root, "requestId");
            var statusElement = RequiredProperty(root, "status");
            var status = JsonSerializer.Deserialize<SubmissionStatus>(statusElement.GetRawText(), options);
            var entry = OptionalId<EntryId>(root, "entry");
            var reason = OptionalString(root, "reason");
            var detail = OptionalValue(root, "detail", options);

            return type switch
            {
                "input" => new SubmissionRecord.InputRecord
                {
                    Id = id,
                    ConversationId = conversationId,
                    RequestId = requestId,
                    Status = status,
                    Entry = entry,
                    Answer = OptionalId<EntryId>(root, "answer"),
                    Reason = reason,
                    Detail = detail,
                },
                "write" => new SubmissionRecord.WriteRecord
                {
                    Id = id,
                    ConversationId = conversationId,
                    RequestId = requestId,
                    Status = status,
                    Entry = entry,
                    Reason = reason,
                    Detail = detail,
                },
                _ => throw new JsonException($"Unknown submission type: {type}"),
            };
        }

        public override void Write(
            Utf8JsonWriter writer,
            SubmissionRecord value,
            JsonSerializerOptions options)
        {
            writer.WriteStartObject();
            writer.WriteNumber("id", value.Id.Value);
            writer.WriteNumber("conversationId", value.ConversationId.Value);
            writer.WriteString("type", value.Type);
            if (value.RequestId is not null) writer.WriteString("requestId", value.RequestId);
            switch (value)
            {
                case SubmissionRecord.InputRecord input:
                    writer.WritePropertyName("status");
                    JsonSerializer.Serialize(writer, input.Status, options);
                    WriteOptionalId(writer, "entry", input.Entry);
                    WriteOptionalId(writer, "answer", input.Answer);
                    WriteOptionalString(writer, "reason", input.Reason);
                    WriteOptionalValue(writer, "detail", input.Detail, options);
                    break;
                case SubmissionRecord.WriteRecord write:
                    writer.WritePropertyName("status");
                    JsonSerializer.Serialize(writer, write.Status, options);
                    WriteOptionalId(writer, "entry", write.Entry);
                    WriteOptionalString(writer, "reason", write.Reason);
                    WriteOptionalValue(writer, "detail", write.Detail, options);
                    break;
                default:
                    throw new JsonException($"Unknown submission record type: {value.GetType().Name}");
            }
            writer.WriteEndObject();

            void WriteOptionalId<TId>(Utf8JsonWriter jsonWriter, string name, TId? id)
                where TId : struct
            {
                if (id is not { } item) return;
                jsonWriter.WriteNumber(name, (long)typeof(TId).GetProperty(nameof(ConversationId.Value))!.GetValue(item)!);
            }

            void WriteOptionalString(Utf8JsonWriter jsonWriter, string name, string? text)
            {
                if (text is not null) jsonWriter.WriteString(name, text);
            }

            void WriteOptionalValue(Utf8JsonWriter jsonWriter, string name, object? item, JsonSerializerOptions serializerOptions)
            {
                if (item is null) return;
                jsonWriter.WritePropertyName(name);
                JsonSerializer.Serialize(jsonWriter, item, serializerOptions);
            }
        }
    }

    private sealed class ContextEditJsonConverter : JsonConverter<ContextEdit>
    {
        public override ContextEdit Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            using var document = JsonDocument.ParseValue(ref reader);
            var root = document.RootElement;
            var target = EntryId.From(RequiredInt64(root, "target"));
            return RequiredString(root, "action") switch
            {
                "omit" => new ContextEdit.Omit(target),
                "replace" => new ContextEdit.Replace(
                    target,
                    JsonSerializer.Deserialize<IReadOnlyList<Pi.Ai.Types.ChatMessage>>(
                        RequiredProperty(root, "messages").GetRawText(),
                        options) ?? throw new JsonException("Context edit messages are missing")),
                var action => throw new JsonException($"Unknown context edit action: {action}"),
            };
        }

        public override void Write(Utf8JsonWriter writer, ContextEdit value, JsonSerializerOptions options)
        {
            writer.WriteStartObject();
            switch (value)
            {
                case ContextEdit.Omit omit:
                    writer.WriteNumber("target", omit.Target.Value);
                    writer.WriteString("action", "omit");
                    break;
                case ContextEdit.Replace replace:
                    writer.WriteNumber("target", replace.Target.Value);
                    writer.WriteString("action", "replace");
                    writer.WritePropertyName("messages");
                    JsonSerializer.Serialize(writer, replace.Messages, options);
                    break;
                default:
                    throw new JsonException($"Unknown context edit type: {value.GetType().Name}");
            }
            writer.WriteEndObject();
        }
    }

    private sealed class DeltaOpJsonConverter : JsonConverter<DeltaOp>
    {
        public override DeltaOp Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            using var document = JsonDocument.ParseValue(ref reader);
            var tuple = document.RootElement;
            if (tuple.ValueKind != JsonValueKind.Array || tuple.GetArrayLength() == 0)
                throw new JsonException("op is not a tuple");
            var verbElement = tuple[0];
            if (verbElement.ValueKind != JsonValueKind.String) throw new JsonException("unknown op verb");
            var verb = verbElement.GetString();
            return verb switch
            {
                "r" when tuple.GetArrayLength() == 2 =>
                    new DeltaOp.Replace(ReadValue(tuple[1], options)),
                "s" when tuple.GetArrayLength() == 3 =>
                    new DeltaOp.Set(ReadPath(tuple[1]), ReadValue(tuple[2], options)),
                "d" when tuple.GetArrayLength() == 2 =>
                    new DeltaOp.Delete(ReadPath(tuple[1])),
                "a" when tuple.GetArrayLength() == 3 =>
                    new DeltaOp.Append(ReadPath(tuple[1]), RequiredString(tuple[2])),
                "t" when tuple.GetArrayLength() == 3 =>
                    new DeltaOp.Truncate(ReadPath(tuple[1]), RequiredInt64(tuple[2])),
                "p" when tuple.GetArrayLength() == 5 =>
                    new DeltaOp.Splice(
                        ReadPath(tuple[1]),
                        RequiredInt64(tuple[2]),
                        RequiredInt64(tuple[3]),
                        ReadValues(tuple[4], options)),
                "m" when tuple.GetArrayLength() == 3 =>
                    new DeltaOp.Move(ReadPath(tuple[1]), ReadLongs(tuple[2])),
                _ => throw new JsonException($"unknown op verb: {verb}"),
            };
        }

        public override void Write(Utf8JsonWriter writer, DeltaOp value, JsonSerializerOptions options)
        {
            writer.WriteStartArray();
            switch (value)
            {
                case DeltaOp.Replace replace:
                    writer.WriteStringValue("r");
                    JsonSerializer.Serialize(writer, replace.Value, options);
                    break;
                case DeltaOp.Set set:
                    writer.WriteStringValue("s");
                    WritePath(writer, set.Path);
                    JsonSerializer.Serialize(writer, set.Value, options);
                    break;
                case DeltaOp.Delete delete:
                    writer.WriteStringValue("d");
                    WritePath(writer, delete.Path);
                    break;
                case DeltaOp.Append append:
                    writer.WriteStringValue("a");
                    WritePath(writer, append.Path);
                    writer.WriteStringValue(append.Text);
                    break;
                case DeltaOp.Truncate truncate:
                    writer.WriteStringValue("t");
                    WritePath(writer, truncate.Path);
                    writer.WriteNumberValue(truncate.Length);
                    break;
                case DeltaOp.Splice splice:
                    writer.WriteStringValue("p");
                    WritePath(writer, splice.Path);
                    writer.WriteNumberValue(splice.Index);
                    writer.WriteNumberValue(splice.Remove);
                    JsonSerializer.Serialize(writer, splice.Items, options);
                    break;
                case DeltaOp.Move move:
                    writer.WriteStringValue("m");
                    WritePath(writer, move.Path);
                    JsonSerializer.Serialize(writer, move.Permutation, options);
                    break;
                default:
                    throw new JsonException($"Unknown delta operation: {value.GetType().Name}");
            }
            writer.WriteEndArray();
        }

        private static DeltaPath ReadPath(JsonElement element)
        {
            if (element.ValueKind != JsonValueKind.Array) throw new JsonException("path is not an array");
            var segments = new List<Seg>();
            foreach (var segment in element.EnumerateArray())
            {
                if (segment.ValueKind == JsonValueKind.String) segments.Add(Seg.Key(segment.GetString()!));
                else if (segment.ValueKind == JsonValueKind.Number && segment.TryGetInt64(out var index))
                    segments.Add(Seg.Index(index));
                else
                    throw new JsonException("path segment is not a string or integer");
            }
            return new DeltaPath(segments);
        }

        private static IReadOnlyList<object?> ReadValues(JsonElement element, JsonSerializerOptions options)
            => element.ValueKind == JsonValueKind.Array
                ? element.EnumerateArray().Select(item => ReadValue(item, options)).ToList()
                : throw new JsonException("p items are not an array");

        private static IReadOnlyList<long> ReadLongs(JsonElement element)
            => element.ValueKind == JsonValueKind.Array
                ? element.EnumerateArray().Select(RequiredInt64).ToList()
                : throw new JsonException("m permutation is not an array");

        private static object? ReadValue(JsonElement element, JsonSerializerOptions options)
            => JsonSerializer.Deserialize<object?>(element.GetRawText(), options);

        private static long RequiredInt64(JsonElement element)
            => element.ValueKind == JsonValueKind.Number && element.TryGetInt64(out var value)
                ? value
                : throw new JsonException("Expected an integer");

        private static string RequiredString(JsonElement element)
            => element.ValueKind == JsonValueKind.String
                ? element.GetString()!
                : throw new JsonException("Expected a string");

        private static void WritePath(Utf8JsonWriter writer, DeltaPath path)
        {
            writer.WriteStartArray();
            foreach (var segment in path.Segments)
            {
                switch (segment.Value)
                {
                    case string text:
                        writer.WriteStringValue(text);
                        break;
                    case int index:
                        writer.WriteNumberValue(index);
                        break;
                    case long index:
                        writer.WriteNumberValue(index);
                        break;
                    default:
                        throw new JsonException("path segment is not a string or integer");
                }
            }
            writer.WriteEndArray();
        }
    }

    private sealed class JsonValueConverter : JsonConverter<object>
    {
        public override object? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
            => ReadValue(ref reader);

        public override void Write(Utf8JsonWriter writer, object value, JsonSerializerOptions options)
        {
            switch (value)
            {
                case string text:
                    writer.WriteStringValue(text);
                    return;
                case bool boolean:
                    writer.WriteBooleanValue(boolean);
                    return;
                case byte number:
                    writer.WriteNumberValue(number);
                    return;
                case sbyte number:
                    writer.WriteNumberValue(number);
                    return;
                case short number:
                    writer.WriteNumberValue(number);
                    return;
                case ushort number:
                    writer.WriteNumberValue(number);
                    return;
                case int number:
                    writer.WriteNumberValue(number);
                    return;
                case uint number:
                    writer.WriteNumberValue(number);
                    return;
                case long number:
                    writer.WriteNumberValue(number);
                    return;
                case ulong number:
                    writer.WriteNumberValue(number);
                    return;
                case float number:
                    writer.WriteNumberValue(number);
                    return;
                case double number:
                    writer.WriteNumberValue(number);
                    return;
                case decimal number:
                    writer.WriteNumberValue(number);
                    return;
                case JsonElement element:
                    element.WriteTo(writer);
                    return;
                default:
                    JsonSerializer.Serialize(writer, value, value.GetType(), options);
                    return;
            }
        }

        private static object? ReadValue(ref Utf8JsonReader reader) => reader.TokenType switch
        {
            JsonTokenType.StartObject => ReadObject(ref reader),
            JsonTokenType.StartArray => ReadArray(ref reader),
            JsonTokenType.String => reader.GetString(),
            JsonTokenType.Number => ReadNumber(ref reader),
            JsonTokenType.True => true,
            JsonTokenType.False => false,
            JsonTokenType.Null => null,
            _ => throw new JsonException($"Unexpected JSON token: {reader.TokenType}"),
        };

        private static object ReadNumber(ref Utf8JsonReader reader)
        {
            if (reader.TryGetInt64(out var integer)) return integer;
            return reader.GetDouble();
        }

        private static Dictionary<string, object?> ReadObject(ref Utf8JsonReader reader)
        {
            var result = new Dictionary<string, object?>(StringComparer.Ordinal);
            while (reader.Read() && reader.TokenType != JsonTokenType.EndObject)
            {
                if (reader.TokenType != JsonTokenType.PropertyName) throw new JsonException("Expected an object property");
                var name = reader.GetString()!;
                if (!reader.Read()) throw new JsonException("Unexpected end of JSON object");
                result[name] = ReadValue(ref reader);
            }
            if (reader.TokenType != JsonTokenType.EndObject) throw new JsonException("Unexpected end of JSON object");
            return result;
        }

        private static List<object?> ReadArray(ref Utf8JsonReader reader)
        {
            var result = new List<object?>();
            while (reader.Read() && reader.TokenType != JsonTokenType.EndArray)
                result.Add(ReadValue(ref reader));
            if (reader.TokenType != JsonTokenType.EndArray) throw new JsonException("Unexpected end of JSON array");
            return result;
        }
    }

    private static JsonElement RequiredProperty(JsonElement element, string name)
        => element.TryGetProperty(name, out var property)
            ? property
            : throw new JsonException($"Missing property: {name}");

    private static string RequiredString(JsonElement element, string name)
        => RequiredProperty(element, name).ValueKind == JsonValueKind.String
            ? element.GetProperty(name).GetString()!
            : throw new JsonException($"Property {name} must be a string");

    private static long RequiredInt64(JsonElement element, string name)
        => RequiredProperty(element, name).ValueKind == JsonValueKind.Number
            && element.GetProperty(name).TryGetInt64(out var value)
                ? value
                : throw new JsonException($"Property {name} must be an integer");

    private static string? OptionalString(JsonElement element, string name)
    {
        if (!element.TryGetProperty(name, out var property) || property.ValueKind == JsonValueKind.Null) return null;
        return property.ValueKind == JsonValueKind.String
            ? property.GetString()
            : throw new JsonException($"Property {name} must be a string");
    }

    private static TId? OptionalId<TId>(JsonElement element, string name)
        where TId : struct
    {
        if (!element.TryGetProperty(name, out var property) || property.ValueKind == JsonValueKind.Null) return null;
        if (property.ValueKind != JsonValueKind.Number || !property.TryGetInt64(out var id))
            throw new JsonException($"Property {name} must be an integer");
        object value = typeof(TId) == typeof(EntryId)
            ? EntryId.From(id)
            : throw new JsonException($"Unsupported ID type: {typeof(TId).Name}");
        return (TId)value;
    }

    private static object? OptionalValue(JsonElement element, string name, JsonSerializerOptions options)
        => element.TryGetProperty(name, out var property)
            ? JsonSerializer.Deserialize<object?>(property.GetRawText(), options)
            : null;
}
