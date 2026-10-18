// ============================================================================
// Server-hosted session catalog — port of experimental/session-catalog.ts (4g-3a)
// ============================================================================
//
// A server-hosted Session is a directory holding `meta.json` (this catalog's metadata) and the
// worker-owned `session.sqlite`. The catalog only reads and writes `meta.json`; the storage file is
// created by the Session's worker on first open, so `sessionStoragePath` is a path helper, not a
// guarantee that the file exists.
//
// Everything here is plain filesystem + JSON, with two Node-isms worth keeping in mind:
// - `readSession` swallows *every* failure (missing file, unreadable file, malformed JSON) and
//   returns undefined, so a corrupt metadata file just makes the Session invisible.
// - `createSession` relies on a non-recursive `mkdir` to detect a duplicate id (EEXIST). .NET has no
//   non-recursive atomic mkdir, so the duplicate check is an explicit existence test (C308).

using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Pi.CodingAgent.Utils;

namespace Pi.CodingAgent.Experimental;

/// <summary>
/// One server-hosted Session. Port of the TS <c>SessionCatalogMetadata</c> interface.
/// </summary>
/// <param name="Id">The Session id, which is also its directory name.</param>
/// <param name="CreatedAt">
/// Milliseconds since the Unix epoch (TS <c>Date.now()</c>). Kept as <see cref="double"/> because TS
/// validates it with <c>typeof === "number"</c> and the durable layer types it as a plain number, so a
/// hand-edited fractional value is still accepted.
/// </param>
/// <param name="Cwd">The working directory the Session's agent runs in.</param>
/// <param name="Path">The Session directory. Workers lock it and own the storage inside it.</param>
public sealed record SessionCatalogMetadata(string Id, double CreatedAt, string Cwd, string Path);

/// <summary>Options of <see cref="SessionCatalog.CreateSessionAsync"/>.</summary>
public sealed record SessionCatalogCreateOptions
{
    /// <summary>The Session id; a random UUID when omitted.</summary>
    public string? Id { get; init; }

    /// <summary>The working directory the Session's agent runs in.</summary>
    public required string Cwd { get; init; }
}

/// <summary>Port of <c>experimental/session-catalog.ts</c>.</summary>
public static class SessionCatalog
{
    private const string MetadataFile = "meta.json";
    private const string StorageFile = "session.sqlite";

    // TS: /^[A-Za-z0-9][A-Za-z0-9._-]{0,127}$/ — no anchors beyond ^$; .NET's $ also matches before a
    // trailing newline, so the pattern uses \z to keep the same "whole string" meaning.
    private static readonly Regex SessionIdPattern =
        new(@"^[A-Za-z0-9][A-Za-z0-9._-]{0,127}\z", RegexOptions.Compiled);

    /// <summary>Whether <paramref name="id"/> is a valid Session id.</summary>
    public static bool IsSessionId(string id) => SessionIdPattern.IsMatch(id);

    /// <summary>
    /// The durable storage file of a Session. Only the Session's worker opens it.
    /// </summary>
    public static string SessionStoragePath(SessionCatalogMetadata metadata) =>
        NodePath.Join(metadata.Path, StorageFile);

    /// <summary>
    /// Every Session in <paramref name="sessionDir"/>. Entries without valid metadata are skipped.
    /// </summary>
    public static async Task<IReadOnlyList<SessionCatalogMetadata>> ListSessionsAsync(string sessionDir)
    {
        string[] names;
        try
        {
            // Node's readdir yields entry names, not paths.
            names = Directory.GetFileSystemEntries(sessionDir);
        }
        catch (DirectoryNotFoundException)
        {
            // TS: ENOENT → []. A path that exists but is not a directory (ENOTDIR) still throws there.
            return [];
        }

        var ids = names
            .Select(NodePath.Basename)
            .Where(IsSessionId)
            .ToList();

        // TS uses Promise.all, which starts every read and preserves the input order.
        var sessions = await Task.WhenAll(ids.Select(id => ReadSessionAsync(sessionDir, id)))
            .ConfigureAwait(false);
        return [.. sessions.Where(metadata => metadata is not null).Select(metadata => metadata!)];
    }

    /// <summary>One Session by id, or <see langword="null"/> when it does not exist.</summary>
    public static async Task<SessionCatalogMetadata?> ReadSessionAsync(string sessionDir, string id)
    {
        if (!IsSessionId(id))
        {
            return null;
        }

        var path = NodePath.Join(sessionDir, id);
        string text;
        try
        {
            text = await File.ReadAllTextAsync(NodePath.Join(path, MetadataFile)).ConfigureAwait(false);
        }
        catch (Exception)
        {
            // TS catches everything here, including unreadable files and ENOENT.
            return null;
        }

        JsonNode? value;
        try
        {
            value = JsonNode.Parse(text);
        }
        catch (JsonException)
        {
            return null;
        }

        // TS: typeof value !== "object" || value === null — arrays pass that test but then yield
        // undefined for both fields, so treating them as "not a metadata object" is equivalent.
        if (value is not JsonObject metadata)
        {
            return null;
        }

        if (!TryReadNumber(metadata, "createdAt", out var createdAt))
        {
            return null;
        }

        if (metadata["cwd"] is not JsonValue cwdNode || !cwdNode.TryGetValue<string>(out var cwd))
        {
            return null;
        }

        return new SessionCatalogMetadata(id, createdAt, cwd, path);
    }

    /// <summary>
    /// Create an empty Session. Its worker creates the storage on first open.
    /// </summary>
    public static async Task<SessionCatalogMetadata> CreateSessionAsync(
        string sessionDir,
        SessionCatalogCreateOptions options)
    {
        var id = options.Id ?? Guid.NewGuid().ToString();
        if (!IsSessionId(id))
        {
            throw new InvalidOperationException($"Invalid session ID: {id}");
        }

        var path = NodePath.Join(sessionDir, id);
        Directory.CreateDirectory(sessionDir);

        // TS: `mkdir(path)` without `recursive` fails with EEXIST when the Session already exists.
        // .NET's CreateDirectory is idempotent, so the duplicate check has to be explicit (C308).
        if (Directory.Exists(path) || File.Exists(path))
        {
            throw new InvalidOperationException($"Session {id} already exists");
        }

        Directory.CreateDirectory(path);

        var createdAt = (double)DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var metadata = new SessionCatalogMetadata(id, createdAt, options.Cwd, path);
        await File.WriteAllTextAsync(NodePath.Join(path, MetadataFile), SerializeMetadata(createdAt, options.Cwd))
            .ConfigureAwait(false);
        return metadata;
    }

    /// <summary>Delete a Session directory. Its worker must be closed first.</summary>
    public static Task DeleteSessionAsync(SessionCatalogMetadata metadata)
    {
        // TS: rm({ recursive: true, force: true }) — a missing directory is not an error.
        try
        {
            if (Directory.Exists(metadata.Path))
            {
                Directory.Delete(metadata.Path, recursive: true);
            }
        }
        catch (DirectoryNotFoundException)
        {
        }

        return Task.CompletedTask;
    }

    /// <summary>
    /// TS: `${JSON.stringify({ createdAt, cwd }, null, "\t")}\n` — key order is createdAt then cwd,
    /// one tab per level, and a trailing newline.
    /// </summary>
    private static string SerializeMetadata(double createdAt, string cwd)
    {
        var json = new JsonObject
        {
            // JSON.stringify writes 1712345678901, not 1.7123456789E+12.
            ["createdAt"] = JsonValue.Create(createdAt),
            ["cwd"] = JsonValue.Create(cwd),
        };
        var options = new JsonSerializerOptions
        {
            WriteIndented = true,
            // .NET 9+ defaults the writer's line break to Environment.NewLine; TS always writes "\n".
            NewLine = "\n",
            IndentCharacter = '\t',
            IndentSize = 1,
            Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        };
        return $"{json.ToJsonString(options)}\n";
    }

    /// <summary>TS <c>typeof value === "number"</c>, which only JSON numbers satisfy.</summary>
    private static bool TryReadNumber(JsonObject metadata, string key, out double value)
    {
        value = 0;
        if (metadata[key] is not JsonValue node || node.GetValueKind() != JsonValueKind.Number)
        {
            return false;
        }

        return node.TryGetValue(out value);
    }
}
