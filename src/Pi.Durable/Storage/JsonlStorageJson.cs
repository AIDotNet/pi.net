using System.Text;
using System.Text.Json;
using Pi.Chord.Delta;
using Pi.Durable.Types;
using TaskStatus = Pi.Durable.Types.TaskStatus;

namespace Pi.Durable.Storage;

/// <summary>主日志中的一条写入操作。对应 TS <c>storage/jsonl/storage.ts</c> 的 <c>MainOperation</c>。</summary>
internal abstract record JsonlMainOperation
{
    private JsonlMainOperation() { }

    internal sealed record OperationConversation(ConversationRecord Value) : JsonlMainOperation;

    internal sealed record OperationEntry(EntryRecord Value) : JsonlMainOperation;

    internal sealed record OperationTask(TaskRecord Value) : JsonlMainOperation;

    internal sealed record OperationSubmission(SubmissionRecord Value) : JsonlMainOperation;

    internal sealed record DocumentRetire(DocumentId Id) : JsonlMainOperation;

    internal sealed record TaskSidecar(TaskId<object?> Id, int Ordinal) : JsonlMainOperation;

    internal sealed record DocumentCreateOp(DocumentCreate Record, int Ordinal) : JsonlMainOperation;

    internal sealed record DocumentChangeOp(DocumentId Id, int Ordinal) : JsonlMainOperation;
}

/// <summary>侧车记录载荷。对应 TS <c>SidecarPayload</c>。</summary>
internal abstract record JsonlSidecarPayload
{
    private JsonlSidecarPayload() { }

    internal sealed record TaskPayload(TaskRecord Value) : JsonlSidecarPayload;

    internal sealed record DocumentPayload(DocumentId Id, DocumentContent Content) : JsonlSidecarPayload;
}

/// <summary>一条已解析的主日志提交标记。对应 TS <c>MainMarker</c>。</summary>
internal sealed record JsonlCommitMarker(Seq Seq, IReadOnlyList<JsonlMainOperation> Writes);

/// <summary>一条已解析的侧车记录。对应 TS <c>SidecarRecord</c>。</summary>
internal sealed record JsonlSidecarRecord(Seq Seq, int Ordinal, JsonlSidecarPayload Payload);

/// <summary>
/// JSONL 存储的线上格式：编码/解析主标记与侧车记录，并复刻 TS 的严格校验文案。
/// <para>记录体与 SQLite 后端共用 <see cref="SqliteStorageJson"/> 的一套 camelCase 转换器，
/// 保证同一份会话数据在两种后端之间可互换。</para>
/// </summary>
internal static class JsonlStorageJson
{
    public const int FormatVersion = 1;

    private const long MaxSafeInteger = 9007199254740991L;

    private static readonly JsonWriterOptions WriterOptions = new()
    {
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        Indented = false,
    };

    // ─── 编码 ────────────────────────────────────────────────────────────────

    public static string SerializeCommit(Seq seq, IReadOnlyList<JsonlMainOperation> operations)
        => Write(writer =>
        {
            writer.WriteStartObject();
            writer.WriteNumber("format", FormatVersion);
            writer.WriteString("type", "commit");
            writer.WriteNumber("seq", seq.Value);
            writer.WriteStartArray("writes");
            foreach (var operation in operations) WriteOperation(writer, operation);
            writer.WriteEndArray();
            writer.WriteEndObject();
        });

    public static string SerializeSidecar(JsonlSidecarRecord record)
        => Write(writer =>
        {
            writer.WriteStartObject();
            writer.WriteNumber("format", FormatVersion);
            writer.WriteString("type", "record");
            writer.WriteNumber("seq", record.Seq.Value);
            writer.WriteNumber("ordinal", record.Ordinal);
            writer.WritePropertyName("payload");
            writer.WriteStartObject();
            switch (record.Payload)
            {
                case JsonlSidecarPayload.TaskPayload task:
                    writer.WriteString("type", "task");
                    writer.WritePropertyName("value");
                    WriteRecord(writer, task.Value);
                    break;
                case JsonlSidecarPayload.DocumentPayload document:
                    writer.WriteString("type", "document");
                    writer.WriteNumber("id", document.Id.Value);
                    writer.WritePropertyName("content");
                    WriteContent(writer, document.Content);
                    break;
                default:
                    throw new JsonException("Unknown sidecar payload");
            }
            writer.WriteEndObject();
            writer.WriteEndObject();
        });

    private static string Write(Action<Utf8JsonWriter> body)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream, WriterOptions)) body(writer);
        return Encoding.UTF8.GetString(stream.ToArray()) + "\n";
    }

    private static void WriteOperation(Utf8JsonWriter writer, JsonlMainOperation operation)
    {
        writer.WriteStartObject();
        switch (operation)
        {
            case JsonlMainOperation.OperationConversation conversation:
                writer.WriteString("type", "conversation");
                writer.WritePropertyName("value");
                WriteRecord(writer, conversation.Value);
                break;
            case JsonlMainOperation.OperationEntry entry:
                writer.WriteString("type", "entry");
                writer.WritePropertyName("value");
                WriteRecord(writer, entry.Value);
                break;
            case JsonlMainOperation.OperationSubmission submission:
                writer.WriteString("type", "submission");
                writer.WritePropertyName("value");
                WriteRecord(writer, submission.Value);
                break;
            case JsonlMainOperation.OperationTask task:
                writer.WriteString("type", "task");
                writer.WritePropertyName("value");
                WriteRecord(writer, task.Value);
                break;
            case JsonlMainOperation.DocumentRetire retire:
                writer.WriteString("type", "document.retire");
                writer.WriteNumber("id", retire.Id.Value);
                break;
            case JsonlMainOperation.TaskSidecar sidecar:
                writer.WriteString("type", "task.sidecar");
                writer.WriteNumber("id", sidecar.Id.Value);
                writer.WriteNumber("ordinal", sidecar.Ordinal);
                break;
            case JsonlMainOperation.DocumentCreateOp create:
                writer.WriteString("type", "document.create");
                writer.WritePropertyName("record");
                WriteRecord(writer, create.Record);
                writer.WriteNumber("ordinal", create.Ordinal);
                break;
            case JsonlMainOperation.DocumentChangeOp change:
                writer.WriteString("type", "document.change");
                writer.WriteNumber("id", change.Id.Value);
                writer.WriteNumber("ordinal", change.Ordinal);
                break;
            default:
                throw new JsonException("Unknown main operation");
        }
        writer.WriteEndObject();
    }

    private static void WriteRecord<T>(Utf8JsonWriter writer, T value)
        => writer.WriteRawValue(SqliteStorageJson.Serialize(value), skipInputValidation: true);

    private static void WriteContent(Utf8JsonWriter writer, DocumentContent content)
    {
        writer.WriteStartObject();
        switch (content)
        {
            case DocumentContent.Base baseContent:
                writer.WriteString("kind", "base");
                writer.WriteNumber("version", baseContent.Version);
                writer.WritePropertyName("value");
                JsonSerializer.Serialize(writer, baseContent.Value, SqliteStorageJson.SerializerOptions);
                break;
            case DocumentContent.Delta delta:
                writer.WriteString("kind", "delta");
                writer.WriteNumber("version", delta.Version);
                writer.WritePropertyName("ops");
                JsonSerializer.Serialize(writer, delta.Ops, SqliteStorageJson.SerializerOptions);
                break;
            default:
                throw new JsonException("Unknown document content");
        }
        writer.WriteEndObject();
    }

    // ─── 解析 ────────────────────────────────────────────────────────────────

    public static JsonlCommitMarker ParseCommit(string text, int line)
    {
        var description = $"main.jsonl line {line}";
        var value = ParseJson(text, description);
        if (value.ValueKind != JsonValueKind.Object
            || !TryGetString(value, "type", out var type) || type != "commit"
            || !TryGetInt64(value, "format", out var format) || format != FormatVersion
            || !TryGetInt64(value, "seq", out var seq) || seq < 1 || !IsSafeInteger(seq)
            || !value.TryGetProperty("writes", out var writes) || writes.ValueKind != JsonValueKind.Array)
            throw new JsonlCorruptionError($"Invalid commit marker in {description}");

        var operations = new List<JsonlMainOperation>();
        foreach (var item in writes.EnumerateArray()) operations.Add(ParseOperation(item, description));
        return new JsonlCommitMarker(Seq.From(seq), operations);
    }

    public static JsonlSidecarRecord ParseSidecar(string text, string file, int line)
    {
        var description = $"{file} line {line}";
        var value = ParseJson(text, description);
        if (value.ValueKind != JsonValueKind.Object
            || !TryGetString(value, "type", out var type) || type != "record"
            || !TryGetInt64(value, "format", out var format) || format != FormatVersion
            || !TryGetInt64(value, "seq", out var seq) || seq < 1 || !IsSafeInteger(seq)
            || !TryGetInt64(value, "ordinal", out var ordinal) || ordinal < 0 || !IsSafeInteger(ordinal)
            || !value.TryGetProperty("payload", out var payload) || payload.ValueKind != JsonValueKind.Object
            || !TryGetString(payload, "type", out var payloadType))
            throw new JsonlCorruptionError($"Invalid sidecar record in {description}");

        switch (payloadType)
        {
            case "task":
            {
                if (!payload.TryGetProperty("value", out var taskValue) || taskValue.ValueKind != JsonValueKind.Object
                    || !TryGetInt64(taskValue, "id", out var rawTaskId) || !IsSafeInteger(rawTaskId)
                    || !taskValue.TryGetProperty("state", out var taskState) || taskState.ValueKind != JsonValueKind.Object)
                    throw new JsonlCorruptionError($"Invalid live task record in {description}");
                var record = DeserializeRecord<TaskRecord>(taskValue, $"Invalid live task record in {description}");
                if (record.State.Status == TaskStatus.Terminal)
                    throw new JsonlCorruptionError($"Invalid live task record in {description}");
                return new JsonlSidecarRecord(Seq.From(seq), (int)ordinal, new JsonlSidecarPayload.TaskPayload(record));
            }
            case "document":
            {
                if (!TryGetInt64(payload, "id", out var id) || !IsSafeInteger(id)
                    || !payload.TryGetProperty("content", out var content))
                    throw new JsonlCorruptionError($"Invalid document record in {description}");
                return new JsonlSidecarRecord(
                    Seq.From(seq),
                    (int)ordinal,
                    new JsonlSidecarPayload.DocumentPayload(DocumentId.From(id), ParseContent(content, description)));
            }
            default:
                throw new JsonlCorruptionError($"Unknown sidecar record type in {description}");
        }
    }

    private static JsonlMainOperation ParseOperation(JsonElement element, string description)
    {
        if (element.ValueKind != JsonValueKind.Object || !TryGetString(element, "type", out var type))
            throw new JsonlCorruptionError($"Invalid write in {description}");

        switch (type)
        {
            case "conversation":
            case "entry":
            case "submission":
            {
                if (!element.TryGetProperty("value", out var value) || value.ValueKind != JsonValueKind.Object
                    || !TryGetInt64(value, "id", out var rawId) || !IsSafeInteger(rawId))
                    throw new JsonlCorruptionError($"Invalid {type} write in {description}");
                return type switch
                {
                    "conversation" => new JsonlMainOperation.OperationConversation(
                        DeserializeRecord<ConversationRecord>(value, $"Invalid {type} write in {description}")),
                    "entry" => new JsonlMainOperation.OperationEntry(
                        DeserializeRecord<EntryRecord>(value, $"Invalid {type} write in {description}")),
                    _ => new JsonlMainOperation.OperationSubmission(
                        DeserializeRecord<SubmissionRecord>(value, $"Invalid {type} write in {description}")),
                };
            }
            case "task":
            {
                if (!element.TryGetProperty("value", out var value) || value.ValueKind != JsonValueKind.Object
                    || !TryGetInt64(value, "id", out var rawTaskId) || !IsSafeInteger(rawTaskId)
                    || !value.TryGetProperty("state", out var state) || state.ValueKind != JsonValueKind.Object
                    || !TryGetString(state, "status", out var status) || status != "terminal")
                    throw new JsonlCorruptionError($"Invalid terminal task write in {description}");
                return new JsonlMainOperation.OperationTask(
                    DeserializeRecord<TaskRecord>(value, $"Invalid terminal task write in {description}"));
            }
            case "document.retire":
                return new JsonlMainOperation.DocumentRetire(DocumentId.From(ReadId(element, description, "Invalid document retirement in")));
            case "task.sidecar":
            {
                if (!TryGetInt64(element, "id", out var id) || !IsSafeInteger(id))
                    throw new JsonlCorruptionError($"Invalid task sidecar write in {description}");
                if (!TryGetInt64(element, "ordinal", out var ordinal) || ordinal < 0 || !IsSafeInteger(ordinal))
                    throw new JsonlCorruptionError($"Invalid task sidecar write in {description}");
                return new JsonlMainOperation.TaskSidecar(TaskId<object?>.From(id), (int)ordinal);
            }
            case "document.create":
            {
                if (!element.TryGetProperty("record", out var record) || record.ValueKind != JsonValueKind.Object
                    || !TryGetInt64(record, "id", out var id) || !IsSafeInteger(id))
                    throw new JsonlCorruptionError($"Invalid document creation in {description}");
                if (!TryGetInt64(element, "ordinal", out var ordinal) || ordinal < 0 || !IsSafeInteger(ordinal))
                    throw new JsonlCorruptionError($"Invalid document creation in {description}");
                return new JsonlMainOperation.DocumentCreateOp(
                    DeserializeRecord<DocumentCreate>(record, $"Invalid document creation in {description}"),
                    (int)ordinal);
            }
            case "document.change":
            {
                if (!TryGetInt64(element, "id", out var id) || !IsSafeInteger(id))
                    throw new JsonlCorruptionError($"Invalid document change in {description}");
                if (!TryGetInt64(element, "ordinal", out var ordinal) || ordinal < 0 || !IsSafeInteger(ordinal))
                    throw new JsonlCorruptionError($"Invalid document change in {description}");
                return new JsonlMainOperation.DocumentChangeOp(DocumentId.From(id), (int)ordinal);
            }
            default:
                throw new JsonlCorruptionError($"Unknown write type in {description}");
        }
    }

    private static DocumentContent ParseContent(JsonElement element, string description)
    {
        if (element.ValueKind != JsonValueKind.Object
            || !TryGetInt64(element, "version", out var version) || version < 1 || version > int.MaxValue
            || !TryGetString(element, "kind", out var kind))
            throw new JsonlCorruptionError($"Invalid document content in {description}");

        switch (kind)
        {
            case "base":
            {
                if (!element.TryGetProperty("value", out var value) || value.ValueKind != JsonValueKind.Object)
                    throw new JsonlCorruptionError($"Invalid document content in {description}");
                try
                {
                    return new DocumentContent.Base(
                        (int)version,
                        SqliteStorageJson.Deserialize<IReadOnlyDictionary<string, object?>>(value.GetRawText()));
                }
                catch (Exception error) when (error is JsonException or InvalidOperationException)
                {
                    throw new JsonlCorruptionError($"Invalid document content in {description}", error);
                }
            }
            case "delta":
            {
                if (!element.TryGetProperty("ops", out var ops) || ops.ValueKind != JsonValueKind.Array)
                    throw new JsonlCorruptionError($"Invalid document content in {description}");
                try
                {
                    return new DocumentContent.Delta(
                        (int)version,
                        SqliteStorageJson.Deserialize<IReadOnlyList<DeltaOp>>(ops.GetRawText()));
                }
                catch (Exception error) when (error is JsonException or InvalidOperationException)
                {
                    throw new JsonlCorruptionError($"Invalid document content in {description}", error);
                }
            }
            default:
                throw new JsonlCorruptionError($"Invalid document content in {description}");
        }
    }

    // ─── 原语 ────────────────────────────────────────────────────────────────

    private static JsonElement ParseJson(string text, string description)
    {
        try
        {
            using var document = JsonDocument.Parse(text);
            return document.RootElement.Clone();
        }
        catch (JsonException error)
        {
            throw new JsonlCorruptionError($"Malformed complete {description}", error);
        }
    }

    private static T DeserializeRecord<T>(JsonElement element, string message)
    {
        try
        {
            return SqliteStorageJson.Deserialize<T>(element.GetRawText());
        }
        catch (Exception error) when (error is JsonException or InvalidOperationException or NotSupportedException)
        {
            throw new JsonlCorruptionError(message, error);
        }
    }

    private static long ReadId(JsonElement element, string description, string message)
    {
        if (!TryGetInt64(element, "id", out var id) || !IsSafeInteger(id))
            throw new JsonlCorruptionError($"{message} {description}");
        return id;
    }

    private static bool TryGetInt64(JsonElement element, string name, out long value)
    {
        value = 0;
        return element.TryGetProperty(name, out var property)
               && property.ValueKind == JsonValueKind.Number
               && property.TryGetInt64(out value);
    }

    private static bool TryGetString(JsonElement element, string name, out string value)
    {
        value = string.Empty;
        if (!element.TryGetProperty(name, out var property) || property.ValueKind != JsonValueKind.String) return false;
        value = property.GetString()!;
        return true;
    }

    private static bool IsSafeInteger(long value) => value is >= -MaxSafeInteger and <= MaxSafeInteger;
}
