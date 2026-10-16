// ============================================================================
// External editor hand-off: write the prompt into a private temp directory,
// launch the user's editor on it, read the result back.
// Port of modes/interactive/external-editor.ts
// ============================================================================

using System.ComponentModel;
using System.Diagnostics;
using System.Text;
using Pi.CodingAgent.Utils;

namespace Pi.CodingAgent.Modes.Interactive;

/// <summary>TS <c>ExternalEditorOptions</c>.</summary>
public sealed record ExternalEditorOptions
{
    public required string Command { get; init; }

    public required string Content { get; init; }
}

/// <summary>
/// TS <c>ExternalEditorResult = { status: "complete"; content: string } | { status: "failed" }</c>
/// as the established abstract-record + sealed-variants shape.
/// </summary>
public abstract record ExternalEditorResult
{
    public sealed record Complete(string Content) : ExternalEditorResult;

    public sealed record Failed : ExternalEditorResult;
}

/// <summary>Port of <c>editInExternalEditor</c>.</summary>
public static class ExternalEditor
{
    private static Func<string, IReadOnlyList<string>, string, Task<int?>>? _spawnOverride;

    /// <summary>
    /// Replace the child-process launch. The TS tests drive a real fixture editor through the real
    /// spawn; the port keeps real-spawn cases for the shell wiring and exposes this seam for the
    /// file-lifecycle cases (same rationale as <see cref="Utils.OpenBrowser.OpenOverride"/>, C232).
    /// The seam receives the resolved editor command, its argument list, and the prompt file path;
    /// returning <c>null</c> models a spawn error (TS <c>child.on("error")</c>).
    /// </summary>
    internal static Func<string, IReadOnlyList<string>, string, Task<int?>>? SpawnOverride
    {
        get => _spawnOverride;
        set => _spawnOverride = value;
    }

    /// <summary>Port of <c>editInExternalEditor</c>.</summary>
    public static async Task<ExternalEditorResult> EditInExternalEditorAsync(ExternalEditorOptions options)
    {
        var directory = Directory.CreateTempSubdirectory("pi-editor-");
        var filePath = Path.Combine(directory.FullName, "prompt.md");
        try
        {
            // TS writeFileSync(filePath, content, "utf-8") — UTF-8 without a BOM.
            File.WriteAllText(filePath, options.Content, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));

            // TS: `const [editor, ...editorArgs] = options.command.split(" ")`.
            var parts = options.Command.Split(' ');
            var editor = parts[0];
            var editorArgs = parts.Skip(1).ToList();
            Console.Out.Write($"Launching external editor: {options.Command}\nPi will resume when the editor exits.\n");

            var exitCode = _spawnOverride is { } spawn
                ? await spawn(editor, editorArgs, filePath).ConfigureAwait(false)
                : await SpawnAsync(editor, editorArgs, filePath).ConfigureAwait(false);

            // TS: `exitCode !== 0` — a spawn error resolves null, which also counts as failure.
            if (exitCode != 0)
            {
                return new ExternalEditorResult.Failed();
            }

            var text = Text.StripBom(File.ReadAllText(filePath, Encoding.UTF8));
            // TS `.replace(/\n$/, "")` — removes at most one trailing newline.
            if (text.EndsWith('\n'))
            {
                text = text[..^1];
            }

            return new ExternalEditorResult.Complete(text);
        }
        finally
        {
            // TS `rmSync(directory, { recursive: true, force: true })` in a bare catch — best effort.
            try
            {
                directory.Delete(recursive: true);
            }
            catch (Exception)
            {
                // Cleanup is best effort.
            }
        }
    }

    /// <summary>
    /// The real child launch. TS uses <c>spawn(editor, [...editorArgs, filePath], { stdio: "inherit",
    /// shell: win32 })</c> and deliberately avoids <c>spawnSync</c>: on Windows a synchronous child
    /// keeps Node/libuv's console input read active after the parent pauses stdin, racing vim/nvim
    /// for the console input buffer. The C# port launches the child asynchronously for the same
    /// reason and lets it inherit the console handles (no redirection) — TS <c>stdio: "inherit"</c>.
    /// </summary>
    private static async Task<int?> SpawnAsync(string editor, IReadOnlyList<string> editorArgs, string filePath)
    {
        var startInfo = BuildStartInfo(editor, editorArgs, filePath);
        using var child = new Process { StartInfo = startInfo };
        try
        {
            child.Start();
        }
        catch (Exception e) when (e is Win32Exception or InvalidOperationException)
        {
            // TS `child.on("error", () => resolve(null))`.
            return null;
        }

        await child.WaitForExitAsync().ConfigureAwait(false);
        // TS `child.on("close", code)` — null for signal deaths; .NET has no signal analogue,
        // so a completed child always yields its integer exit code.
        return child.ExitCode;
    }

    private static ProcessStartInfo BuildStartInfo(string editor, IReadOnlyList<string> editorArgs, string filePath)
    {
        if (ProcessInfo.Platform != "win32")
        {
            var direct = new ProcessStartInfo(editor) { UseShellExecute = false };
            foreach (var arg in editorArgs)
            {
                direct.ArgumentList.Add(arg);
            }

            direct.ArgumentList.Add(filePath);
            return direct;
        }

        // TS `shell: true` on win32: Node builds `cmd.exe /d /s /c "<command>"` by joining the
        // arguments with spaces and quoting the ones that contain whitespace or quotes.
        var all = new List<string>(editorArgs.Count + 2) { editor };
        all.AddRange(editorArgs);
        all.Add(filePath);
        var shell = new ProcessStartInfo("cmd.exe") { UseShellExecute = false };
        shell.ArgumentList.Add("/d");
        shell.ArgumentList.Add("/s");
        shell.ArgumentList.Add("/c");
        shell.ArgumentList.Add(string.Join(" ", all.Select(QuoteForCmd)));
        return shell;
    }

    private static string QuoteForCmd(string arg) =>
        arg.Length > 0 && !arg.Any(c => c is ' ' or '\t' or '"')
            ? arg
            : $"\"{arg.Replace("\"", "\\\"")}\"";
}
