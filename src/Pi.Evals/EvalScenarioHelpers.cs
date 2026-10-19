using System.Text.Json;
using Pi.Ai.Models;
using Pi.Ai.Types;

namespace Pi.Evals;

internal sealed record EvalModelCost(
    double Input,
    double Output,
    double CacheRead,
    double CacheWrite);

internal sealed record EvalModelFields(
    string Id,
    string Name,
    string Provider,
    bool Reasoning,
    IReadOnlyList<string> Input,
    EvalModelCost Cost,
    long ContextWindow,
    long MaxTokens);

internal static class EvalScenarioHelpers
{
    public static JsonElement ToJson<T>(T value)
        => JsonSerializer.SerializeToElement(value, EvalJson.Options);

    public static EvalModelFields ModelFields(ModelSpec model)
        => new(
            model.Id,
            model.Name,
            model.Provider,
            model.Reasoning,
            model.Input.ToArray(),
            new EvalModelCost(
                model.Cost?.Input ?? 0,
                model.Cost?.Output ?? 0,
                model.Cost?.CacheRead ?? 0,
                model.Cost?.CacheWrite ?? 0),
            model.ContextWindow,
            model.MaxTokens);

    public static bool HasJsonBoolean(JsonElement value, string name, bool expected)
        => value.ValueKind == JsonValueKind.Object
            && value.TryGetProperty(name, out var property)
            && property.ValueKind is JsonValueKind.True or JsonValueKind.False
            && property.GetBoolean() == expected;

    public static bool HasJsonString(JsonElement value, string name, string expected)
        => value.ValueKind == JsonValueKind.Object
            && value.TryGetProperty(name, out var property)
            && property.ValueKind == JsonValueKind.String
            && string.Equals(property.GetString(), expected, StringComparison.Ordinal);

    public static bool HasToolCall(
        IReadOnlyList<EvalTranscriptEvent> events,
        string name,
        string argumentName,
        string argumentValue)
        => events.Any(@event =>
            @event.Type == "tool_call"
            && @event.Name == name
            && @event.Arguments is { ValueKind: JsonValueKind.Object } arguments
            && arguments.TryGetProperty(argumentName, out var value)
            && value.ValueKind == JsonValueKind.String
            && value.GetString() == argumentValue);

    public static string StopReasonText(StopReason reason)
        => reason switch
        {
            StopReason.Stop => "stop",
            StopReason.Length => "length",
            StopReason.ToolUse => "toolUse",
            StopReason.Aborted => "aborted",
            StopReason.Error => "error",
            StopReason.Deferred => "deferred",
            _ => throw new ArgumentOutOfRangeException(nameof(reason), reason, "Unknown stop reason."),
        };

    public static (long Input, long Output)? Usage(AssistantMessage message)
        => message.UsageStats is { } usage ? (usage.Input, usage.Output) : null;
}
