using System.Text;
using Pi.Ai.Types;
using Pi.CodingAgent.Core.Tools;
using Pi.CodingAgent.Utils;
using Pi.CodingAgent.Utils.Image;

namespace Pi.CodingAgent.Cli;

// ============================================================================
// `@file` 参数处理 — TS `cli/file-processor.ts`（88 行）。
//
// 把每个 `@path` 展开成「拼进首条消息的文本片段」+「图像附件」，规则逐条对齐 TS：
//   1. 路径先过 `resolveReadPath`（`~` 展开 + macOS 截图的 Unicode 空格/NFD 变体回退），
//      再 `resolve` 成绝对路径；**写进 `<file name="…">` 的是绝对路径**。
//   2. 文件不存在 → 红字报错 + `exit(1)`（不是抛异常）。
//   3. 空文件（size === 0）**静默跳过**，连 `<file>` 片段都不产生。
//   4. 图片：读出字节 → `processImage`；失败时把 message 塞进 `<file>` 片段继续下一个文件，
//      **不中断**；成功则推附件，并按有无 hints 决定片段内容。
//   5. 文本：`stripBom` 后包成 `<file name="…">\n<content>\n</file>\n`；读失败 → 红字 + `exit(1)`。
//
// 与 TS 的差异：
//   - `process.exit(1)` 走可注入的 <see cref="ProcessFileOptions.Exit"/>（默认
//     <see cref="Environment.Exit(int)"/>），与 `RpcMode` 的既有做法一致——否则这两条
//     分支无法测试。
//   - `process.cwd()` 走 <see cref="ProcessFileOptions.Cwd"/>（默认
//     <see cref="Environment.CurrentDirectory"/>）。
//   - `readFile(path, "utf-8")` → `File.ReadAllTextAsync`：两侧对非法字节都替换为 U+FFFD，
//     且都会先剥 BOM，故 `stripBom` 在多数路径上是幂等的空操作（保留它对齐 TS）。
// ============================================================================

/// <summary>对应 TS <c>ProcessedFiles</c>。</summary>
public sealed record ProcessedFiles(string Text, IReadOnlyList<ImageContent> Images);

/// <summary>对应 TS <c>ProcessFileOptions</c>（外加两个可注入的进程钩子）。</summary>
public sealed record ProcessFileOptions
{
    /// <summary>是否自动缩放图片。默认 <see langword="true"/>。</summary>
    public bool? AutoResizeImages { get; init; }

    /// <summary>工作目录。TS 用 <c>process.cwd()</c>；测试注入隔离目录。</summary>
    public string? Cwd { get; init; }

    /// <summary>退出钩子。TS 用 <c>process.exit</c>；测试注入以断言退出码。</summary>
    public Action<int>? Exit { get; init; }
}

/// <summary>Port of <c>cli/file-processor.ts</c>.</summary>
public static class FileProcessor
{
    /// <summary>对应 TS <c>processFileArguments</c>。</summary>
    public static async Task<ProcessedFiles> ProcessFileArgumentsAsync(
        IReadOnlyList<string> fileArgs,
        ProcessFileOptions? options = null)
    {
        var autoResizeImages = options?.AutoResizeImages ?? true;
        var cwd = options?.Cwd ?? Environment.CurrentDirectory;
        var exit = options?.Exit ?? (code => Environment.Exit(code));
        var text = new StringBuilder();
        var images = new List<ImageContent>();

        foreach (var fileArg in fileArgs)
        {
            var absolutePath = Path.GetFullPath(ToolPathUtils.ResolveReadPath(fileArg, cwd));

            if (!File.Exists(absolutePath) && !Directory.Exists(absolutePath))
            {
                Console.Error.WriteLine(Chalk.Red($"Error: File not found: {absolutePath}"));
                exit(1);
                return new ProcessedFiles(text.ToString(), images);
            }

            // node `stat().size` 对目录在 Windows 上是 0（Linux 上是 4096）。这里统一按 0 处理，
            // 即目录与空文件一样被跳过——对齐 Windows 上 TS 的行为，也避免 Linux 上后续
            // 打开目录读 MIME 时的 EISDIR 崩溃（TS 在 Linux 上确实会崩）。
            var size = File.Exists(absolutePath) ? new FileInfo(absolutePath).Length : 0;
            if (size == 0)
            {
                continue;
            }

            var mimeType = await Mime.DetectSupportedImageMimeTypeFromFileAsync(absolutePath).ConfigureAwait(false);

            if (mimeType is not null)
            {
                var content = await File.ReadAllBytesAsync(absolutePath).ConfigureAwait(false);
                var processed = await ImageProcess
                    .ProcessImageAsync(content, mimeType, new ProcessImageOptions { AutoResizeImages = autoResizeImages })
                    .ConfigureAwait(false);

                if (processed is ProcessImageResult.Fail fail)
                {
                    text.Append($"<file name=\"{absolutePath}\">{fail.Message}</file>\n");
                    continue;
                }

                var ok = (ProcessImageResult.Ok)processed;
                images.Add(new ImageContent(ok.Data, ok.MimeType));

                // 有 hint 就把 hint 逐行拼进片段，否则留一个空片段作为「这里有附件」的锚点。
                text.Append(ok.Hints.Count > 0
                    ? $"<file name=\"{absolutePath}\">{string.Join("\n", ok.Hints)}</file>\n"
                    : $"<file name=\"{absolutePath}\"></file>\n");
            }
            else
            {
                string raw;
                try
                {
                    raw = await File.ReadAllTextAsync(absolutePath, Encoding.UTF8).ConfigureAwait(false);
                }
                catch (Exception error)
                {
                    Console.Error.WriteLine(
                        Chalk.Red($"Error: Could not read file {absolutePath}: {error.Message}"));
                    exit(1);
                    return new ProcessedFiles(text.ToString(), images);
                }

                text.Append($"<file name=\"{absolutePath}\">\n{Text.StripBom(raw)}\n</file>\n");
            }
        }

        return new ProcessedFiles(text.ToString(), images);
    }
}
