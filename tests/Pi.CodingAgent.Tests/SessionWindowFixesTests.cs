using Pi.Ai.Types;
using Pi.CodingAgent.Core;
using Pi.CodingAgent.Core.Extensions;
using Xunit;

namespace Pi.CodingAgent.Tests;

/// <summary>
/// The 1.5.0 window's session-side fixes: an entry that failed to reach the file must not stay in
/// memory (TS <c>807a5c051</c>), a saved "Do not trust" must win over auto-trust (TS <c>31ade0e0d</c>),
/// a saved trust decision must be found across casing (TS <c>1be312b46</c>), and reordering the same
/// active tools must not reorder the declared loadout (TS <c>b9e3eef6c</c>).
/// </summary>
public sealed class SessionWindowFixesTests : IDisposable
{
    private readonly string _root;

    public SessionWindowFixesTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "pi-window-fixes-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_root))
            {
                // Clear read-only attributes a failing-write test may have left behind.
                foreach (var file in Directory.EnumerateFiles(_root, "*", SearchOption.AllDirectories))
                {
                    File.SetAttributes(file, FileAttributes.Normal);
                }

                Directory.Delete(_root, recursive: true);
            }
        }
        catch (Exception)
        {
            // Best effort: a locked file must not fail the suite.
        }
    }

    private static UserMessage Text(string text) => new(MessageContent.From(text), 1);

    /// <summary>A context with no UI: the prompt branch is never reached, so the UI slice is inert.</summary>
    private ProjectTrustContext TrustContext() => new()
    {
        Cwd = _root,
        Mode = AppModes.Print,
        HasUI = false,
        Ui = new NullTrustUi(),
    };

    private sealed class NullTrustUi : IProjectTrustUiContext
    {
        public Task<string?> Select(string title, IReadOnlyList<string> options, ExtensionUIDialogOptions? opts = null) =>
            Task.FromResult<string?>(null);

        public Task<bool> Confirm(string title, string message, ExtensionUIDialogOptions? opts = null) =>
            Task.FromResult(false);

        public Task<string?> Input(string title, string? placeholder = null, ExtensionUIDialogOptions? opts = null) =>
            Task.FromResult<string?>(null);

        public void Notify(string message, string? type = null) { }
    }

    // ---- append rollback ----------------------------------------------------------------

    [Fact]
    public void AppendEntry_KeepsMemoryInLineWithTheFile_WhenTheWriteFails()
    {
        var sessionFile = Path.Combine(_root, "session.jsonl");
        var manager = SessionManager.Open(sessionFile, _root, _root);

        var firstId = manager.AppendMessage(Text("first"));
        Assert.Equal(firstId, manager.GetLeafId());
        var entriesBeforeFailure = manager.GetEntries().Count;

        // Make the file read-only so the append cannot be persisted.
        File.SetAttributes(sessionFile, FileAttributes.ReadOnly);
        try
        {
            Assert.ThrowsAny<Exception>(() => manager.AppendMessage(Text("second")));

            // The failed entry must not become the leaf nor stay in memory.
            Assert.Equal(firstId, manager.GetLeafId());
            Assert.Equal(entriesBeforeFailure, manager.GetEntries().Count);
        }
        finally
        {
            File.SetAttributes(sessionFile, FileAttributes.Normal);
        }

        // The next entry chains to the one actually on disk.
        var thirdId = manager.AppendMessage(Text("third"));
        var reopened = SessionManager.Open(sessionFile, _root, _root);
        var branch = reopened.GetBranch(thirdId);
        Assert.Contains(branch, entry => entry.Id == firstId);
        Assert.Contains(branch, entry => entry.Id == thirdId);
        Assert.DoesNotContain(branch, entry => entry is SessionMessageEntry { Message: UserMessage message }
            && message.Content is MessageContent.Text { Value: "second" });
    }

    // ---- project trust ------------------------------------------------------------------

    [Fact]
    public async Task ResolveProjectTrusted_HonorsSavedDistrustWithoutTrustRequiringResources()
    {
        // The directory holds no .pi resources, so the old code auto-trusted it and ignored trust.json.
        var store = new ProjectTrustStore(_root);
        store.Set(_root, false);

        var trusted = await ProjectTrust.ResolveProjectTrustedAsync(new ResolveProjectTrustedOptions
        {
            Cwd = _root,
            TrustStore = store,
            ProjectTrustContext = TrustContext(),
        });

        Assert.False(trusted);
    }

    [Fact]
    public async Task ResolveProjectTrusted_AutoTrustsWhenNothingIsSaved()
    {
        var store = new ProjectTrustStore(_root);

        var trusted = await ProjectTrust.ResolveProjectTrustedAsync(new ResolveProjectTrustedOptions
        {
            Cwd = _root,
            TrustStore = store,
            ProjectTrustContext = TrustContext(),
        });

        Assert.True(trusted);
    }

    [Fact]
    public void TrustStore_FindsDecisionSavedWithOtherCasing()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var store = new ProjectTrustStore(_root);
        store.Set(_root, false);

        var upperCased = _root.ToUpperInvariant();
        Assert.False(store.Get(upperCased));
    }

    [Fact]
    public void TrustStore_ReplacesDecisionSavedWithOtherCasing()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var store = new ProjectTrustStore(_root);
        store.Set(_root, false);
        store.Set(_root.ToUpperInvariant(), true);

        // One key for the location, not two.
        var keys = TrustManager.FindTrustFileKeysForTest(
            TrustManager.ReadTrustFile(store.TrustPath), _root);
        Assert.Single(keys);
        Assert.True(store.Get(_root));
    }

}
