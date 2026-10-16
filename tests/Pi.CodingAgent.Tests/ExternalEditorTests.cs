// ============================================================================
// Tests for modes/interactive/external-editor.ts
// Vectors transcribed from TS test/external-editor.test.ts, plus real-spawn
// cases locking the shell wiring and the spawn-error path.
// ============================================================================

using Pi.CodingAgent.Modes.Interactive;
using Xunit;
using static Pi.CodingAgent.Utils.ProcessInfo;

namespace Pi.CodingAgent.Tests;

public sealed class ExternalEditorTests : IDisposable
{
    private readonly Func<string, IReadOnlyList<string>, string, Task<int?>>? _previousOverride =
        ExternalEditor.SpawnOverride;

    public void Dispose()
    {
        ExternalEditor.SpawnOverride = _previousOverride;
        GC.SuppressFinalize(this);
    }

    // ------------------------------------------------------------------ upstream test 1

    [Fact]
    public async Task EditsPromptInsidePrivateTemporaryDirectory()
    {
        string? capturedContent = null;
        string? capturedFilePath = null;
        string? capturedDirectory = null;
        string[]? capturedEntries = null;
        ExternalEditor.SpawnOverride = (_, _, filePath) =>
        {
            capturedFilePath = filePath;
            capturedDirectory = Path.GetDirectoryName(filePath);
            capturedContent = File.ReadAllText(filePath);
            capturedEntries = Directory.GetFiles(Path.GetDirectoryName(filePath)!)
                .Select(Path.GetFileName)
                .OfType<string>()
                .ToArray();
            File.WriteAllText(filePath, "edited");
            return Task.FromResult<int?>(0);
        };

        var result = await ExternalEditor.EditInExternalEditorAsync(
            new ExternalEditorOptions { Command = "fake-editor", Content = "original" });

        var complete = Assert.IsType<ExternalEditorResult.Complete>(result);
        Assert.Equal("edited", complete.Content);
        Assert.Equal("original", capturedContent);
        Assert.NotNull(capturedFilePath);
        Assert.Equal("prompt.md", Path.GetFileName(capturedFilePath));
        Assert.NotNull(capturedDirectory);
        // The editor sees exactly one file in its private directory.
        Assert.NotNull(capturedEntries);
        Assert.Equal(["prompt.md"], capturedEntries);
        // The directory lives directly under the temp root with the pi-editor- prefix.
        Assert.Equal(
            Path.GetTempPath(),
            Path.GetDirectoryName(capturedDirectory)!.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar);
        Assert.Matches(@"^pi-editor-.+$", Path.GetFileName(capturedDirectory));
        // The temp directory is removed afterwards.
        Assert.False(Directory.Exists(capturedDirectory));
    }

    // ------------------------------------------------------------------ upstream test 2

    [Fact]
    public async Task KeepsOriginalContentWhenEditorExitsUnsuccessfully()
    {
        string? capturedDirectory = null;
        ExternalEditor.SpawnOverride = (_, _, filePath) =>
        {
            capturedDirectory = Path.GetDirectoryName(filePath);
            return Task.FromResult<int?>(1);
        };

        var result = await ExternalEditor.EditInExternalEditorAsync(
            new ExternalEditorOptions { Command = "fake-editor --fail", Content = "original" });

        Assert.IsType<ExternalEditorResult.Failed>(result);
        Assert.False(Directory.Exists(capturedDirectory));
    }

    // ------------------------------------------------------------------ upstream test 3

    [Fact]
    public async Task ReturnsEmptyContentWhenEditorClearsPrompt()
    {
        ExternalEditor.SpawnOverride = (_, _, filePath) =>
        {
            File.WriteAllText(filePath, "");
            return Task.FromResult<int?>(0);
        };

        var result = await ExternalEditor.EditInExternalEditorAsync(
            new ExternalEditorOptions { Command = "fake-editor --empty", Content = "original" });

        var complete = Assert.IsType<ExternalEditorResult.Complete>(result);
        Assert.Equal("", complete.Content);
    }

    // ------------------------------------------------------------------ port: newline trimming

    [Fact]
    public async Task TrimsExactlyOneTrailingNewline()
    {
        ExternalEditor.SpawnOverride = (_, _, filePath) =>
        {
            File.WriteAllText(filePath, "edited\n");
            return Task.FromResult<int?>(0);
        };

        var result = await ExternalEditor.EditInExternalEditorAsync(
            new ExternalEditorOptions { Command = "fake-editor", Content = "original" });

        Assert.Equal("edited", Assert.IsType<ExternalEditorResult.Complete>(result).Content);
    }

    // ------------------------------------------------------------------ port: real spawn wiring

    [Fact]
    public async Task RealSpawnCompletesThroughThePlatformShell()
    {
        // editor command "cmd /c exit 0" — the appended prompt path becomes a harmless extra
        // argument to the nested cmd, which still exits 0.
        var command = Platform == "win32" ? "cmd /c exit 0" : "true";
        var result = await ExternalEditor.EditInExternalEditorAsync(
            new ExternalEditorOptions { Command = command, Content = "original" });

        Assert.Equal("original", Assert.IsType<ExternalEditorResult.Complete>(result).Content);
    }

    [Fact]
    public async Task RealSpawnFailureFailsTheEdit()
    {
        var command = Platform == "win32" ? "cmd /c exit 3" : "false";
        var result = await ExternalEditor.EditInExternalEditorAsync(
            new ExternalEditorOptions { Command = command, Content = "original" });

        Assert.IsType<ExternalEditorResult.Failed>(result);
    }

    [Fact]
    public async Task SpawnErrorResolvesToFailure()
    {
        // TS: `child.on("error", () => resolve(null))` — an unspawnable editor yields `failed`.
        var result = await ExternalEditor.EditInExternalEditorAsync(
            new ExternalEditorOptions
            {
                Command = "definitely-not-a-real-editor-binary-xyz",
                Content = "original",
            });

        Assert.IsType<ExternalEditorResult.Failed>(result);
    }
}
