using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Pi.CodingAgent.Experimental;
using Xunit;

namespace Pi.CodingAgent.Tests;

/// <summary>
/// Differential tests for the experimental foundation layer (4g-3a): the server-hosted session
/// catalog (<c>experimental/session-catalog.ts</c>) and the internal-process plumbing
/// (<c>experimental/process.ts</c>). Vectors are transcribed from the TS behaviour.
/// </summary>
public sealed class ExperimentalFoundationTests : IDisposable
{
    private readonly string _root;
    private readonly string? _previousRole;

    public ExperimentalFoundationTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "pi-exp-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
        // The role tag is process-wide, so every test starts from a clean slate and restores on dispose.
        _previousRole = Environment.GetEnvironmentVariable(InternalProcess.InternalProcessEnv);
        Environment.SetEnvironmentVariable(InternalProcess.InternalProcessEnv, null);
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable(InternalProcess.InternalProcessEnv, _previousRole);
        try { Directory.Delete(_root, true); } catch { /* best effort */ }
    }

    // ---------------------------------------------------------------- session ids

    public static TheoryData<string, bool> SessionIdVectors => new()
    {
        // TS: /^[A-Za-z0-9][A-Za-z0-9._-]{0,127}$/
        { "a", true },
        { "A0", true },
        { "0", true },
        { "a.b-c_d", true },
        { "abc.DEF_123-xyz", true },
        { new string('a', 128), true },
        // The first character must be alphanumeric.
        { "-a", false },
        { ".a", false },
        { "_a", false },
        // 129 characters is one past the ceiling.
        { new string('a', 129), false },
        { "a b", false },
        { "a/b", false },
        { "a\\b", false },
        { "a:b", false },
        // JS `$` without the m flag does not match before a trailing newline, and neither does \z.
        { "a\n", false },
        { "", false },
    };

    [Theory]
    [MemberData(nameof(SessionIdVectors))]
    public void IsSessionId_MatchesTheTsPattern(string id, bool expected) =>
        Assert.Equal(expected, SessionCatalog.IsSessionId(id));

    // ---------------------------------------------------------------- create

    [Fact]
    public async Task CreateSession_WritesTheTsMetadataShape()
    {
        var sessionDir = Path.Combine(_root, "sessions");

        var metadata = await SessionCatalog.CreateSessionAsync(sessionDir, new SessionCatalogCreateOptions
        {
            Id = "s1",
            Cwd = Path.Combine(_root, "work"),
        });

        Assert.Equal("s1", metadata.Id);
        Assert.Equal(Path.Combine(sessionDir, "s1"), metadata.Path);
        Assert.Equal(Path.Combine(_root, "work"), metadata.Cwd);
        // Date.now() is whole milliseconds.
        Assert.Equal(Math.Floor(metadata.CreatedAt), metadata.CreatedAt);

        // TS: `${JSON.stringify({ createdAt, cwd }, null, "\t")}\n` — tab indent, createdAt first.
        var text = await File.ReadAllTextAsync(Path.Combine(metadata.Path, "meta.json"));
        var createdAt = metadata.CreatedAt.ToString("0", CultureInfo.InvariantCulture);
        var cwd = JsonSerializer.Serialize(metadata.Cwd);
        Assert.Equal($"{{\n\t\"createdAt\": {createdAt},\n\t\"cwd\": {cwd}\n}}\n", text);
    }

    [Fact]
    public async Task CreateSession_WithoutAnIdUsesARandomUuid()
    {
        var sessionDir = Path.Combine(_root, "sessions");

        var metadata = await SessionCatalog.CreateSessionAsync(sessionDir, new SessionCatalogCreateOptions
        {
            Cwd = _root,
        });

        Assert.True(Guid.TryParse(metadata.Id, out _));
        Assert.True(SessionCatalog.IsSessionId(metadata.Id));
        Assert.True(Directory.Exists(metadata.Path));
    }

    [Fact]
    public async Task CreateSession_RejectsAnInvalidId()
    {
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            SessionCatalog.CreateSessionAsync(_root, new SessionCatalogCreateOptions
            {
                Id = "bad id!",
                Cwd = _root,
            }));

        Assert.Equal("Invalid session ID: bad id!", error.Message);
    }

    [Fact]
    public async Task CreateSession_RejectsADuplicateId()
    {
        var options = new SessionCatalogCreateOptions { Id = "dup", Cwd = _root };
        await SessionCatalog.CreateSessionAsync(_root, options);

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            SessionCatalog.CreateSessionAsync(_root, options));

        // TS: mkdir() fails with EEXIST → `Session ${id} already exists`.
        Assert.Equal("Session dup already exists", error.Message);
    }

    // ---------------------------------------------------------------- read

    [Fact]
    public async Task ReadSession_ReturnsNullForAnInvalidId()
    {
        // Even when a directory with that name exists, an invalid id is rejected before touching disk.
        Directory.CreateDirectory(Path.Combine(_root, "bad id"));
        await File.WriteAllTextAsync(
            Path.Combine(_root, "bad id", "meta.json"),
            """{"createdAt":1,"cwd":"x"}""");

        Assert.Null(await SessionCatalog.ReadSessionAsync(_root, "bad id"));
    }

    [Fact]
    public async Task ReadSession_ReturnsNullWhenTheMetadataIsMissing()
    {
        Assert.Null(await SessionCatalog.ReadSessionAsync(_root, "missing"));
    }

    [Fact]
    public async Task ReadSession_ReturnsNullWhenTheMetadataIsNotJson()
    {
        var path = Path.Combine(_root, "broken");
        Directory.CreateDirectory(path);
        await File.WriteAllTextAsync(Path.Combine(path, "meta.json"), "not json");

        Assert.Null(await SessionCatalog.ReadSessionAsync(_root, "broken"));
    }

    [Theory]
    // TS: typeof createdAt !== "number" || typeof cwd !== "string" → undefined.
    [InlineData("""{"cwd":"/w"}""")]
    [InlineData("""{"createdAt":"1","cwd":"/w"}""")]
    [InlineData("""{"createdAt":1}""")]
    [InlineData("""{"createdAt":1,"cwd":2}""")]
    [InlineData("""{"createdAt":null,"cwd":"/w"}""")]
    // Non-objects: a bare scalar, null, and an array (which passes typeof === "object" in JS but
    // yields undefined for both fields).
    [InlineData("1")]
    [InlineData("null")]
    [InlineData("[]")]
    public async Task ReadSession_ReturnsNullForShapesTsRejects(string body)
    {
        var path = Path.Combine(_root, "shape");
        Directory.CreateDirectory(path);
        await File.WriteAllTextAsync(Path.Combine(path, "meta.json"), body);

        Assert.Null(await SessionCatalog.ReadSessionAsync(_root, "shape"));
    }

    [Fact]
    public async Task ReadSession_AcceptsAFractionalCreatedAt()
    {
        // TS only checks `typeof === "number"`, so a hand-edited 1.5 is still valid.
        var path = Path.Combine(_root, "frac");
        Directory.CreateDirectory(path);
        await File.WriteAllTextAsync(Path.Combine(path, "meta.json"), """{"createdAt":1.5,"cwd":"/w"}""");

        var metadata = await SessionCatalog.ReadSessionAsync(_root, "frac");

        Assert.NotNull(metadata);
        Assert.Equal(1.5, metadata!.CreatedAt);
        Assert.Equal("/w", metadata.Cwd);
        Assert.Equal(path, metadata.Path);
    }

    // ---------------------------------------------------------------- list

    [Fact]
    public async Task ListSessions_ReturnsEmptyForAMissingDirectory()
    {
        Assert.Empty(await SessionCatalog.ListSessionsAsync(Path.Combine(_root, "nope")));
    }

    [Fact]
    public async Task ListSessions_SkipsInvalidNamesAndBrokenMetadata()
    {
        var good = await SessionCatalog.CreateSessionAsync(_root, new SessionCatalogCreateOptions
        {
            Id = "good",
            Cwd = "/w",
        });

        // A valid id with no metadata file.
        Directory.CreateDirectory(Path.Combine(_root, "empty"));

        // A name that is not a valid id, even though its metadata is fine.
        var bad = Path.Combine(_root, "bad name");
        Directory.CreateDirectory(bad);
        await File.WriteAllTextAsync(Path.Combine(bad, "meta.json"), """{"createdAt":1,"cwd":"/w"}""");

        // A stray file at the top level.
        await File.WriteAllTextAsync(Path.Combine(_root, "stray.txt"), "x");

        var sessions = await SessionCatalog.ListSessionsAsync(_root);

        var only = Assert.Single(sessions);
        Assert.Equal(good, only);
    }

    [Fact]
    public async Task ListSessions_ReturnsEveryValidSession()
    {
        foreach (var id in new[] { "b", "a", "c" })
        {
            await SessionCatalog.CreateSessionAsync(_root, new SessionCatalogCreateOptions { Id = id, Cwd = "/w" });
        }

        var sessions = await SessionCatalog.ListSessionsAsync(_root);

        // The catalog does not sort; callers do (server.ts sorts by id then createdAt). Directory order
        // is filesystem-defined, so only the set is asserted here.
        Assert.Equal(
            ["a", "b", "c"],
            sessions.Select(session => session.Id).OrderBy(id => id, StringComparer.Ordinal));
    }

    // ---------------------------------------------------------------- storage + delete

    [Fact]
    public async Task SessionStoragePath_JoinsTheStorageFile()
    {
        var metadata = await SessionCatalog.CreateSessionAsync(_root, new SessionCatalogCreateOptions
        {
            Id = "s1",
            Cwd = "/w",
        });

        Assert.Equal(Path.Combine(metadata.Path, "session.sqlite"), SessionCatalog.SessionStoragePath(metadata));
        // The worker owns the file; the catalog must not create it.
        Assert.False(File.Exists(SessionCatalog.SessionStoragePath(metadata)));
    }

    [Fact]
    public async Task DeleteSession_RemovesTheDirectory()
    {
        var metadata = await SessionCatalog.CreateSessionAsync(_root, new SessionCatalogCreateOptions
        {
            Id = "gone",
            Cwd = "/w",
        });
        await File.WriteAllTextAsync(Path.Combine(metadata.Path, "session.sqlite"), "db");

        await SessionCatalog.DeleteSessionAsync(metadata);

        Assert.False(Directory.Exists(metadata.Path));
    }

    [Fact]
    public async Task DeleteSession_IgnoresAMissingDirectory()
    {
        var metadata = new SessionCatalogMetadata("gone", 1, "/w", Path.Combine(_root, "gone"));

        // TS: rm({ recursive: true, force: true }).
        await SessionCatalog.DeleteSessionAsync(metadata);
    }

    // ---------------------------------------------------------------- roles

    [Fact]
    public void ToWireValue_MatchesTheEnvSpelling()
    {
        Assert.Equal("coordinator", InternalProcessRole.Coordinator.ToWireValue());
        Assert.Equal("server", InternalProcessRole.Server.ToWireValue());
        Assert.Equal("session-worker", InternalProcessRole.SessionWorker.ToWireValue());
    }

    [Theory]
    [InlineData("coordinator", InternalProcessRole.Coordinator)]
    [InlineData("server", InternalProcessRole.Server)]
    [InlineData("session-worker", InternalProcessRole.SessionWorker)]
    public void TryParseRole_ReadsTheThreeWireValues(string wire, InternalProcessRole expected) =>
        Assert.Equal(expected, InternalProcess.TryParseRole(wire));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("worker")]
    [InlineData("Coordinator")]
    [InlineData(" coordinator")]
    public void TryParseRole_RejectsAnythingElse(string? wire) =>
        Assert.Null(InternalProcess.TryParseRole(wire));

    [Fact]
    public void GetInternalProcessRole_ReturnsNullWhenUnset() =>
        Assert.Null(InternalProcess.GetInternalProcessRole());

    [Fact]
    public void GetInternalProcessRole_ReadsTheTagWithoutConsumingIt()
    {
        Environment.SetEnvironmentVariable(InternalProcess.InternalProcessEnv, "server");

        Assert.Equal(InternalProcessRole.Server, InternalProcess.GetInternalProcessRole());
        Assert.Equal("server", Environment.GetEnvironmentVariable(InternalProcess.InternalProcessEnv));
    }

    [Fact]
    public void GetInternalProcessRole_ThrowsOnAnUnknownTag()
    {
        Environment.SetEnvironmentVariable(InternalProcess.InternalProcessEnv, "bogus");

        var error = Assert.Throws<InvalidOperationException>(() => InternalProcess.GetInternalProcessRole());

        Assert.Equal("Unsupported internal process role: bogus", error.Message);
    }

    [Fact]
    public void ConsumeInternalProcessRole_RemovesTheVariable()
    {
        Environment.SetEnvironmentVariable(InternalProcess.InternalProcessEnv, "session-worker");

        Assert.Equal(InternalProcessRole.SessionWorker, InternalProcess.ConsumeInternalProcessRole());
        // TS: delete process.env[...] so descendants do not inherit the role.
        Assert.Null(Environment.GetEnvironmentVariable(InternalProcess.InternalProcessEnv));
        Assert.Null(InternalProcess.GetInternalProcessRole());
    }

    [Fact]
    public void ConsumeInternalProcessRole_ReturnsNullWhenUnset() =>
        Assert.Null(InternalProcess.ConsumeInternalProcessRole());

    // ---------------------------------------------------------------- control lines

    [Fact]
    public void EncodeControlLine_IsCompactJsonPlusNewline()
    {
        var message = new JsonObject { ["kind"] = "hello", ["count"] = 2 };

        Assert.Equal("{\"kind\":\"hello\",\"count\":2}\n", InternalProcess.EncodeControlLine(message));
    }

    [Fact]
    public void EncodeControlLine_WritesNullForNull() =>
        Assert.Equal("null\n", InternalProcess.EncodeControlLine(null));

    [Fact]
    public void EncodeControlLine_CountsUtf8Bytes()
    {
        // `JSON.stringify("中")` is `"中"` — two quotes plus three UTF-8 bytes — and the trailing
        // newline makes 6. Buffer.byteLength in TS counts bytes, not characters, so a 6-byte ceiling
        // passes and a 5-byte one does not. (STJ's default encoder would escape it to \u4E2D.)
        var message = JsonValue.Create("中");

        Assert.Equal("\"中\"\n", InternalProcess.EncodeControlLine(message, 6));
        Assert.Throws<InvalidOperationException>(() => InternalProcess.EncodeControlLine(message, 5));
    }

    [Fact]
    public void EncodeControlLine_ThrowsWhenTheMessageIsTooLarge()
    {
        var message = JsonValue.Create(new string('x', 32));

        var error = Assert.Throws<InvalidOperationException>(() =>
            InternalProcess.EncodeControlLine(message, 32));

        Assert.Equal("Internal control message is too large", error.Message);
    }

    [Fact]
    public void MaxControlLineBytes_IsOneHundredTwentyEightMebibytes() =>
        Assert.Equal(128 * 1024 * 1024, InternalProcess.MaxControlLineBytes);

    [Fact]
    public void InternalProcessEnv_MatchesTheTsVariableName() =>
        Assert.Equal("__PI_INTERNAL_SPAWN", InternalProcess.InternalProcessEnv);
}
