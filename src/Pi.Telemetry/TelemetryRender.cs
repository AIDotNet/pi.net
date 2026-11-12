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
        // Map semantics of the TS original: a repeated id keeps its first position and its last value.
        var order = new List<string>();
        foreach (var span in spans)
        {
            if (!byId.ContainsKey(span.Id)) order.Add(span.Id);
            byId[span.Id] = span;
        }

        var roots = new List<StoredSpan>();
        var children = new Dictionary<string, List<StoredSpan>>(StringComparer.Ordinal);
        // Stable sort: ties keep the map's insertion order, like Array.prototype.sort in current V8.
        foreach (var span in order.Select(id => byId[id]).OrderBy(span => span.StartedMs))
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
                // 上游 1.5.1（telemetry/render.ts）：错误名与消息也可能来自别的进程的 carrier。
                if (status.Detail is { } detail)
                {
                    error.Append($" {EscapeControl(detail.Name)}: {EscapeControl(detail.Message)}");
                }
            }
            lines.Add(
                $"{indent}{EscapeControl(span.Name)}{FormatAttributes(span.Attributes)} " +
                $"({span.EndedMs - span.StartedMs}ms){error}");
            foreach (var storedEvent in span.Events)
            {
                lines.Add(
                    $"{indent}  · {EscapeControl(storedEvent.Name)}{FormatAttributes(storedEvent.Attributes)} (+{storedEvent.TimeMs}ms)");
            }
            foreach (var link in span.Links)
            {
                lines.Add(
                    $"{indent}  → link trace {EscapeControl(link.TraceId)} span {EscapeControl(link.SpanId)}{FormatAttributes(link.Attributes)}");
            }
            if (!children.TryGetValue(span.Id, out var descendants)) continue;
            for (var index = descendants.Count - 1; index >= 0; index--)
            {
                pending.Push((descendants[index], depth + 1));
            }
        }
        return string.Join("\n", lines);
    }

    /// <summary>
    /// C0 / C1 控制字符与 DEL 写成 <c>\uXXXX</c>。span 名、事件名、属性键、错误名/消息、link 的
    /// trace/span id 都可能来自另一进程的 carrier，不能原样送进终端或查看器（上游 1.5.1 telemetry）。
    /// </summary>
    private static string EscapeControl(string text)
    {
        var result = new StringBuilder(text.Length);
        foreach (var ch in text)
        {
            if (ch <= '\u001f' || (ch >= '\u007f' && ch <= '\u009f'))
            {
                result.Append("\\u").Append(((int)ch).ToString("x4", CultureInfo.InvariantCulture));
            }
            else
            {
                result.Append(ch);
            }
        }
        return result.ToString();
    }

    private static string FormatAttributes(SpanAttributes? attributes)
    {
        if (attributes is null || attributes.Count == 0) return string.Empty;
        var text = new StringBuilder();
        foreach (var (key, value) in attributes)
        {
            if (value is null) continue;
            text.Append(' ').Append(EscapeControl(key)).Append('=').Append(JsonSerializer.Serialize(value, value.GetType()));
        }
        return text.ToString();
    }
}
