// ============================================================================
// Session export — port of core/session-export.ts (4e-2d)
// ============================================================================
//
// Serialize the current branch (and optional export-only trailing entries) as JSONL, and write it
// to a file. The TS `{ ...entry, parentId }` spread maps to JsonObject indexer assignment, which
// preserves the property position when "parentId" already exists and appends at the end otherwise —
// exactly the JS object-literal behaviour. A null parentId must be written as an explicit JSON null
// (TS always emits the field), hence JsonValue.Create rather than the indexer, which removes the
// property on null.

using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Pi.CodingAgent.Utils;

namespace Pi.CodingAgent.Core;

/// <summary>Export-only entry factory: (parentId, timestamp) → entries. TS <c>TrailingEntries</c>.</summary>
public delegate IReadOnlyList<object> TrailingEntriesFactory(string? parentId, string timestamp);

/// <summary>Port of <c>core/session-export.ts</c>.</summary>
public static class SessionExport
{
    private static string NowIso() =>
        DateTimeOffset.UtcNow.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture);

    private static JsonObject ToJsonObject(object entry)
    {
        JsonNode? node = entry switch
        {
            JsonObject jsonObject => jsonObject.DeepClone(),
            FileEntry fileEntry => JsonSerializer.SerializeToNode(
                fileEntry, typeof(FileEntry), SessionEntries.JsonOptions),
            _ => JsonSerializer.SerializeToNode(entry, entry.GetType(), SessionEntries.JsonOptions),
        };
        return node as JsonObject
            ?? throw new InvalidOperationException("session entry did not serialize to a JSON object");
    }

    /// <summary>Serialize the current branch and optional export-only entries as JSONL.</summary>
    public static string SerializeSessionBranch(
        SessionManager sessionManager,
        TrailingEntriesFactory? createTrailingEntries = null)
    {
        var timestamp = NowIso();
        var header = ToJsonObject(new SessionHeader
        {
            Version = SessionEntries.CurrentVersion,
            Id = sessionManager.GetSessionId(),
            Timestamp = timestamp,
            Cwd = sessionManager.GetCwd(),
        });

        var entries = new List<JsonObject> { header };
        string? parentId = null;
        foreach (var entry in sessionManager.GetBranch())
        {
            var serialized = ToJsonObject(entry);
            // TS spreads parentId last: an existing key keeps its position with a new value; a
            // missing one is appended. A null value must stay an explicit JSON null — the indexer
            // removes the property on null, so null is (re)added via Add.
            if (parentId is null)
            {
                if (serialized.ContainsKey("parentId"))
                {
                    serialized.Remove("parentId");
                }

                serialized.Add("parentId", null);
            }
            else
            {
                serialized["parentId"] = JsonValue.Create(parentId);
            }

            entries.Add(serialized);
            parentId = entry.Id;
        }

        if (createTrailingEntries is not null)
        {
            foreach (var trailing in createTrailingEntries(parentId, timestamp))
            {
                entries.Add(ToJsonObject(trailing));
            }
        }

        return string.Join("\n", entries.Select(entry => entry.ToJsonString())) + "\n";
    }

    /// <summary>Write the current session branch and optional export-only entries as JSONL.</summary>
    public static string ExportSessionToJsonl(
        SessionManager sessionManager,
        string? outputPath = null,
        TrailingEntriesFactory? createTrailingEntries = null)
    {
        var filePath = Paths.ResolvePath(
            outputPath ?? $"session-{NowIso().Replace(":", "-").Replace(".", "-")}.jsonl");
        var dir = NodePath.Dirname(filePath);
        if (!Directory.Exists(dir))
        {
            Directory.CreateDirectory(dir);
        }

        File.WriteAllText(filePath, SerializeSessionBranch(sessionManager, createTrailingEntries), new UTF8Encoding(false));
        return filePath;
    }
}
