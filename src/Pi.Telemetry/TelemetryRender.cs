using System.Globalization;
using System.Text;
using System.Text.Json;

namespace Pi.Telemetry;

/// <summary>
/// Renders stored spans as a chronological forest, treating spans whose parent is absent from the
/// input as roots. Mirrors TS <c>renderTrace</c> (render.ts).
/// </summary>
/// <remarks>
/// Output line shapes (two spaces of indent per depth):
/// <list type="bullet">
/// <item><c>name key="value" (12ms)</c>, with <c> error Name: message</c> appended for error statuses.</item>
/// <item><c>  · event (+3ms)</c> for each event, indented one level deeper than its span.</item>
/// <item><c>  → link trace T span S</c> for each link.</item>
/// </list>
/// Attribute values are JSON-encoded, matching TS <c>JSON.stringify</c>.
/// </remarks>
public static class TelemetryRender
{
    public static string RenderTrace(IReadOnlyList<StoredSpan> spans)
    {
        var byId = new Dictionary<string, StoredSpan>(StringComparer.Ordinal);
        foreach (var span in spans) byId[span.Id] = span;

        var roots = new List<StoredSpan>();
        var children = new Dictionary<string, List<StoredSpan>>(StringComparer.Ordinal);
        // Stable sort: ties keep input order, like Array.prototype.sort in current V8.
        foreach (var span in spans
            .Where(span => byId.ContainsKey(span.Id))
            .OrderBy(span => span.StartedMs))
        {
            if (span.ParentId is null || !byId.ContainsKey(span.ParentId))
            {
                roots.Add(span);
                continue;
            }
            if (!children.TryGetValue(span.ParentId, out var siblings))
            {
                siblings = [];
                children[span.ParentId] = siblings;
            }
            siblings.Add(span);
        }

        var lines = new List<string>();
        var pending = new Stack<(StoredSpan Span, int Depth)>();
        for (var index = roots.Count - 1; index >= 0; index--) pending.Push((roots[index], 0));
        while (pending.Count > 0)
        {
            var (span, depth) = pending.Pop();
            var indent = new string(' ', depth * 2);
            var error = new StringBuilder();
            if (span.Status is SpanStatus.ErrorStatus status)
            {
                error.Append(" error");
                if (status.Detail is { } detail) error.Append($" {detail.Name}: {detail.Message}");
            }
            lines.Add(
                $"{indent}{span.Name}{FormatAttributes(span.Attributes)} " +
                $"({span.EndedMs - span.StartedMs}ms){error}");
            foreach (var storedEvent in span.Events)
            {
                lines.Add(
                    $"{indent}  · {storedEvent.Name}{FormatAttributes(storedEvent.Attributes)} (+{storedEvent.TimeMs}ms)");
            }
            foreach (var link in span.Links)
            {
                lines.Add(
                    $"{indent}  → link trace {link.TraceId} span {link.SpanId}{FormatAttributes(link.Attributes)}");
            }
            if (!children.TryGetValue(span.Id, out var descendants)) continue;
            for (var index = descendants.Count - 1; index >= 0; index--)
            {
                pending.Push((descendants[index], depth + 1));
            }
        }
        return string.Join("\n", lines);
    }

    private static string FormatAttributes(SpanAttributes? attributes)
    {
        if (attributes is null || attributes.Count == 0) return string.Empty;
        var text = new StringBuilder();
        foreach (var (key, value) in attributes)
        {
            if (value is null) continue;
            text.Append(' ').Append(key).Append('=').Append(JsonSerializer.Serialize(value, value.GetType()));
        }
        return text.ToString();
    }
}
