using System.Text;
using System.Text.Json;
using Pi.CodingAgent.Core.Tools;
using Xunit;

namespace Pi.CodingAgent.Tests;

/// <summary>
/// 上游 v1.5.1 #11807（rg --glob 锚定在 path / 搜 "." / formatPath 解析相对路径）与
/// #9276（上下文行由 rg 的 context 事件给出、恰好 limit 条不误报 limit、字节上限后继续计数、
/// UTF-16/base64 bytes 解码、孤立 CR 与 CRLF 处理）的回归用例。
/// 逐字对照 <c>packages/coding-agent/test/tools.test.ts</c>；本机无 rg，故以纯叶子
/// <see cref="GrepRgEventProcessor"/> / <see cref="GrepTool.BuildRgArgs"/> / TryParseRgEvent
/// 直接注入事件流，去掉修复即失败。
/// </summary>
public sealed class GrepRgEventsTests
{
    private const int MaxBytes = 50 * 1024;

    private static GrepRgEventProcessor NewProcessor(int context, int limit, string? pathPrefix = null)
        => new(filePath => pathPrefix ?? System.IO.Path.GetFileName(filePath), context, limit, MaxBytes);

    private static GrepRgEvent Match(int line, string text, string file = "f.txt") =>
        new(true, file, line, text);

    private static GrepRgEvent Context(int line, string text, string file = "f.txt") =>
        new(false, file, line, text);

    // ------------------------------------------------------------------ #11807 args / cwd / formatPath

    [Fact]
    public void DirectorySearch_UsesDotAsTargetAndContextFlag()
    {
        var args = GrepTool.BuildRgArgs(false, false, "migrations/main/*.js", "hello", "/tmp/g/repo", 2, isDirectory: true);
        Assert.Contains("--context", args);
        Assert.Equal("2", args[args.IndexOf("--context") + 1]);
        // 目录搜索：搜 "." 而非绝对 searchPath（glob 才能锚定在 path）。
        Assert.Equal(".", args[^1]);
        Assert.Equal("hello", args[^2]);
    }

    [Fact]
    public void FileSearch_KeepsAbsoluteTargetAndNoContextFlag()
    {
        var args = GrepTool.BuildRgArgs(false, false, null, "x", "/tmp/g/repo/a.js", 0, isDirectory: false);
        Assert.DoesNotContain("--context", args);
        Assert.Equal("/tmp/g/repo/a.js", args[^1]);
    }

    [Fact]
    public void RgWorkingDirectory_AnchorsGlobAtSearchPathForDirectories()
    {
        Assert.Equal("/tmp/g/repo", GrepTool.RgWorkingDirectory("/tmp/g", "/tmp/g/repo", isDirectory: true));
        Assert.Equal("/tmp/g", GrepTool.RgWorkingDirectory("/tmp/g", "/tmp/g/repo", isDirectory: false));
    }

    [Fact]
    public void FormatRgPath_ResolvesDotRelativeOutputAgainstSearchPath()
    {
        // rg 在 searchPath 内搜 "." 时输出 "./migrations/main/a.js"；解析后相对 path 输出。
        var p = GrepTool.FormatRgPath("/tmp/g/repo", "./migrations/main/a.js", isDirectory: true);
        Assert.Equal("migrations/main/a.js", p.Replace('\\', '/'));

        // 超出 searchPath 的路径回落为文件名。
        var outside = GrepTool.FormatRgPath("/tmp/g/repo", "../other/x.js", isDirectory: true);
        Assert.Equal("x.js", outside);
    }

    // ------------------------------------------------------------------ #9276 event stream

    [Fact]
    public void ContextLines_ComeFromRgEvents_AndFormatWithDash()
    {
        // 对照 tools.test.ts "respect global limit and include context lines"：before / match / after。
        var processor = NewProcessor(context: 1, limit: 1);
        processor.Process(Context(1, "before\n", "context.txt"));
        processor.Process(Match(2, "match one\n", "context.txt"));
        processor.Process(Context(3, "after\n", "context.txt"));

        var (output, details) = processor.Finalize();
        Assert.Contains("context.txt-1- before", output);
        Assert.Contains("context.txt:2: match one", output);
        Assert.Contains("context.txt-3- after", output);
        Assert.Null(details); // 恰好 1 条、无额外 match → 不应误报 limit（#9276/#11423）
    }

    [Fact]
    public void OverlappingContextIsPrintedOnce_AndLastMatchTrailingContextIsKept()
    {
        // tools.test.ts #9276：a / match one / b / match two / match three / c / match four，context=1 limit=2。
        var processor = NewProcessor(context: 1, limit: 2);
        processor.Process(Context(1, "a\n", "rg-context.txt"));
        processor.Process(Match(2, "match one\n", "rg-context.txt"));
        processor.Process(Context(3, "b\n", "rg-context.txt"));
        processor.Process(Match(4, "match two\n", "rg-context.txt"));
        // 最后一条允许 match 的尾部上下文窗口里，另一条 match 也当上下文呈现。
        processor.Process(Match(5, "match three\n", "rg-context.txt"));

        var (output, _) = processor.Finalize();
        var block = output.Split("\n\n")[0];
        Assert.Equal(
            string.Join("\n",
                "rg-context.txt-1- a",
                "rg-context.txt:2: match one",
                "rg-context.txt-3- b",
                "rg-context.txt:4: match two",
                "rg-context.txt-5- match three"),
            block);
    }

    [Fact]
    public void ExactlyLimitMatches_NoLimitNotice_OverLimitReportsNotice()
    {
        // tools.test.ts "should not report the match limit when exactly limit matches exist"（context=0 分支）。
        var exact = NewProcessor(context: 0, limit: 2);
        exact.Process(Match(1, "match one\n", "exact-limit.txt"));
        exact.Process(Match(3, "match two\n", "exact-limit.txt"));
        var (exactOutput, exactDetails) = exact.Finalize();
        Assert.DoesNotContain("limit reached", exactOutput);
        Assert.Null(exactDetails);

        var over = NewProcessor(context: 0, limit: 1);
        over.Process(Match(1, "match one\n", "exact-limit.txt"));
        over.Process(Match(3, "match two\n", "exact-limit.txt")); // 又一条 match 证明截断
        var (overOutput, overDetails) = over.Finalize();
        Assert.Contains("[1 matches limit reached. Use limit=2 for more, or refine pattern]", overOutput);
        Assert.NotNull(overDetails);
        Assert.Equal(1, overDetails!.MatchLimitReached);
    }

    [Fact]
    public void LoneCrInsideLineIsContent_OnlyTrailingTerminatorStripped()
    {
        var processor = NewProcessor(context: 0, limit: 10);
        // "one\rtwo needle\r\n" → 行尾 CRLF 去掉，行内孤立 CR 保留（对照 mixed-cr.txt）。
        processor.Process(Match(1, "one\rtwo needle\r\n", "mixed-cr.txt"));
        var (output, _) = processor.Finalize();
        Assert.Equal("mixed-cr.txt:1: one\rtwo needle", output);
    }

    [Fact]
    public void ByteCap_StopsStoringButKeepsCounting()
    {
        // tools.test.ts "should keep counting matches past the byte cap"：
        // 200 行每行 400+ 字节，limit=150 → 仍报 150 matches 且 truncation.totalLines=150。
        var line = "match " + new string('x', 400);
        var processor = NewProcessor(context: 0, limit: 150);
        for (var i = 1; i <= 150; i++)
        {
            processor.Process(Match(i, line + "\n", "byte-cap.txt"));
        }
        processor.Process(Match(151, line + "\n", "byte-cap.txt")); // 证明截断

        var (output, details) = processor.Finalize();
        Assert.Contains("150 matches limit reached", output);
        Assert.Contains($"{Truncate.FormatSize(MaxBytes)} limit reached", output);
        Assert.NotNull(details);
        Assert.Equal(150, details!.MatchLimitReached);
        // 上游逐字：limit=150，第 151 条证明截断但不在尾部上下文窗口内、不 push，故 totalLines=150。
        Assert.Equal(150, details!.Truncation!.TotalLines);
        Assert.True(processor.OutputLines.Count < 151, "超出字节上限后必须停止存储");
    }

    // ------------------------------------------------------------------ rg JSON 解析 / base64 bytes

    private static string RgEvent(string type, string filePath, int lineNumber, string lineText, bool asBytes = false)
    {
        var path = asBytes
            ? new Dictionary<string, object?> { ["bytes"] = Convert.ToBase64String(Encoding.UTF8.GetBytes(filePath)) }
            : new Dictionary<string, object?> { ["text"] = filePath };
        var lines = asBytes
            ? new Dictionary<string, object?> { ["bytes"] = Convert.ToBase64String(Encoding.UTF8.GetBytes(lineText)) }
            : new Dictionary<string, object?> { ["text"] = lineText };
        return JsonSerializer.Serialize(new Dictionary<string, object?>
        {
            ["type"] = type,
            ["data"] = new Dictionary<string, object?>
            {
                ["path"] = path,
                ["lines"] = lines,
                ["line_number"] = lineNumber,
            },
        });
    }

    [Fact]
    public void TryParseRgEvent_ParsesMatchAndContext_AndDecodesBase64Bytes()
    {
        // UTF-16 / 非 UTF-8 行由 rg 以 bytes(base64) 给出（#11423），解码后仍是正确的中文文本。
        Assert.True(GrepRgEventProcessor.TryParseRgEvent(
            RgEvent("match", "utf16.txt", 2, "needle 中文\n", asBytes: true), out var match));
        Assert.True(match.IsMatch);
        Assert.Equal("utf16.txt", match.FilePath);
        Assert.Equal(2, match.LineNumber);
        Assert.Equal("needle 中文\n", match.LineText);

        Assert.True(GrepRgEventProcessor.TryParseRgEvent(
            RgEvent("context", "utf16.txt", 1, "before\n"), out var context));
        Assert.False(context.IsMatch);
        Assert.Equal("utf16.txt", context.FilePath);
        Assert.Equal("before\n", context.LineText);
    }

    [Fact]
    public void TryParseRgEvent_IgnoresNonMatchContextAndMalformedLines()
    {
        Assert.False(GrepRgEventProcessor.TryParseRgEvent("{\"type\":\"begin\",\"data\":{}}", out _));
        Assert.False(GrepRgEventProcessor.TryParseRgEvent("not json", out _));
        Assert.False(GrepRgEventProcessor.TryParseRgEvent("{\"type\":\"summary\",\"data\":{}}", out _));
        Assert.False(GrepRgEventProcessor.TryParseRgEvent("", out _));
    }

    [Fact]
    public void DecodeRgText_HandlesTextBytesAndBareString()
    {
        using var text = JsonDocument.Parse("{\"text\":\"hi\"}");
        Assert.Equal("hi", GrepRgEventProcessor.DecodeRgText(text.RootElement));

        using var bytes = JsonDocument.Parse("{\"bytes\":\"" + Convert.ToBase64String(Encoding.UTF8.GetBytes("中文")) + "\"}");
        Assert.Equal("中文", GrepRgEventProcessor.DecodeRgText(bytes.RootElement));

        using var bare = JsonDocument.Parse("\"plain\"");
        Assert.Equal("plain", GrepRgEventProcessor.DecodeRgText(bare.RootElement));
    }
}
