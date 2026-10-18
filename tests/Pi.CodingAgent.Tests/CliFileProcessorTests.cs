using Pi.CodingAgent.Cli;
using Xunit;

namespace Pi.CodingAgent.Tests;

/// <summary>
/// <c>cli/file-processor.ts</c> 的端到端行为（真实临时目录 + 真实 MIME 嗅探）。
/// 期望片段格式取自 TS 原文的模板字符串，逐字对齐。
/// </summary>
public class CliFileProcessorTests : IDisposable
{
    private readonly string _dir = Path.Combine(
        Path.GetTempPath(), "pi-cli-fp-" + Guid.NewGuid().ToString("N"));

    public CliFileProcessorTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try
        {
            Directory.Delete(_dir, recursive: true);
        }
        catch
        {
            // 清理失败不影响断言。
        }
    }

    [Fact]
    public async Task WrapsTextFileInFileTag()
    {
        var path = Write("hello.txt", "hello");
        var result = await FileProcessor.ProcessFileArgumentsAsync([path], Options());

        Assert.Equal($"<file name=\"{path}\">\nhello\n</file>\n", result.Text);
        Assert.Empty(result.Images);
    }

    [Fact]
    public async Task KeepsFileTrailingNewlineSoTheTagGetsABlankLine()
    {
        // TS 的模板是 `<file name="…">\n${content}\n</file>\n`——content 自带尾换行时，
        // 关闭标签前会多出一个空行。这不是 bug，别顺手 trim。
        var path = Write("nl.txt", "hello\n");
        var result = await FileProcessor.ProcessFileArgumentsAsync([path], Options());

        Assert.Equal($"<file name=\"{path}\">\nhello\n\n</file>\n", result.Text);
    }

    [Fact]
    public async Task SkipsEmptyFilesSilently()
    {
        var path = Write("empty.txt", "");
        var result = await FileProcessor.ProcessFileArgumentsAsync([path], Options());

        Assert.Equal("", result.Text);
        Assert.Empty(result.Images);
    }

    [Fact]
    public async Task StripsUtf8Bom()
    {
        var path = Path.Combine(_dir, "bom.txt");
        await File.WriteAllBytesAsync(path, [0xef, 0xbb, 0xbf, (byte)'h', (byte)'i']);

        var result = await FileProcessor.ProcessFileArgumentsAsync([path], Options());

        Assert.Equal($"<file name=\"{path}\">\nhi\n</file>\n", result.Text);
    }

    [Fact]
    public async Task ConcatenatesMultipleFilesInArgumentOrder()
    {
        var first = Write("a.txt", "A");
        var second = Write("b.txt", "B");

        var result = await FileProcessor.ProcessFileArgumentsAsync([first, second], Options());

        Assert.Equal(
            $"<file name=\"{first}\">\nA\n</file>\n<file name=\"{second}\">\nB\n</file>\n",
            result.Text);
    }

    [Fact]
    public async Task MissingFileReportsErrorAndExitsWithOne()
    {
        var missing = Path.Combine(_dir, "nope.txt");
        var exitCodes = new List<int>();
        var stderr = CaptureError();

        ProcessedFiles result;
        try
        {
            result = await FileProcessor.ProcessFileArgumentsAsync(
                [missing], Options() with { Exit = exitCodes.Add });
        }
        finally
        {
            Console.SetError(stderr.Original);
        }

        Assert.Equal(new[] { 1 }, exitCodes);
        Assert.Equal("", result.Text);
        Assert.Contains($"Error: File not found: {missing}", stderr.Text.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task ResolvesRelativePathsAgainstInjectedCwd()
    {
        Write("rel.txt", "x");
        var result = await FileProcessor.ProcessFileArgumentsAsync(
            ["rel.txt"], Options() with { Cwd = _dir });

        var absolute = Path.Combine(_dir, "rel.txt");
        Assert.Equal($"<file name=\"{absolute}\">\nx\n</file>\n", result.Text);
    }

    [Fact]
    public async Task PngBecomesAnAttachmentPlusEmptyFileTag()
    {
        // 2x2 PNG：已在受支持格式内、远低于体积上限，故 processImage 不产生任何 hint，
        // 片段就退化成空的 `<file>` 标签（那是「这里有个附件」的锚点）。
        var path = Path.Combine(_dir, "tiny.png");
        await File.WriteAllBytesAsync(path, ImageTestSupport.Bytes(ImageTestSupport.TinyPng));

        var result = await FileProcessor.ProcessFileArgumentsAsync([path], Options());

        Assert.Single(result.Images);
        Assert.Equal("image/png", result.Images[0].MimeType);
        Assert.False(string.IsNullOrEmpty(result.Images[0].Data));
        Assert.Equal($"<file name=\"{path}\"></file>\n", result.Text);
    }

    [Fact]
    public async Task DisablingAutoResizeStillProducesAnAttachment()
    {
        var path = Path.Combine(_dir, "tiny2.png");
        await File.WriteAllBytesAsync(path, ImageTestSupport.Bytes(ImageTestSupport.TinyPng));

        var result = await FileProcessor.ProcessFileArgumentsAsync(
            [path], Options() with { AutoResizeImages = false });

        Assert.Single(result.Images);
        Assert.Equal($"<file name=\"{path}\"></file>\n", result.Text);
    }

    private ProcessFileOptions Options() => new() { Cwd = _dir };

    private string Write(string name, string content)
    {
        var path = Path.Combine(_dir, name);
        File.WriteAllText(path, content);
        return path;
    }

    private static (StringWriter Text, TextWriter Original) CaptureError()
    {
        var original = Console.Error;
        var writer = new StringWriter();
        Console.SetError(writer);
        return (writer, original);
    }
}
