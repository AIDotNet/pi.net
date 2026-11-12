using System.Text;
using System.Text.Json.Nodes;
using Pi.CodingAgent.Core.Tools;
using Xunit;

namespace Pi.CodingAgent.Tests;

/// <summary>
/// 上游 v1.5.1 #8544（PR #11751）：edit 工具原来把整篇按一种行尾重写，会改掉未触碰行的
/// CRLF / 孤立 CR。修复后匹配仍在 LF 归一化文本上进行，但写盘时用
/// <see cref="EditDiff.ApplyReplacementsToOriginal"/> 把替换映射回原始字节就地拼接，
/// 未改动区间连行尾一起保留。
/// 逐字对照 <c>packages/coding-agent/test/tools.test.ts</c> 的 #8544 用例（Buffer.equals 校验写出字节）。
/// 去掉修复（旧 RestoreLineEndings）时这些用例失败。
/// </summary>
public sealed class EditLineEndingsTests
{
    private static EditDiff.TextReplacement R(int index, int length, string newText) => new(index, length, newText);

    /// <summary>模拟 edit 主流程：归一化 → 在归一化文本上定位替换 → 映射回原始字节。</summary>
    private static string WriteViaEditPipeline(string original, string oldText, string newText)
    {
        var normalized = EditDiff.NormalizeToLf(original);
        // 归一化只把 CRLF→LF、孤立 CR→LF（长度不变）；定位在归一化文本上的替换。
        var index = normalized.IndexOf(oldText, StringComparison.Ordinal);
        Assert.True(index >= 0, $"oldText '{oldText}' not found in normalized content");
        var replacements = new List<EditDiff.TextReplacement> { R(index, oldText.Length, newText) };
        return original == normalized
            ? EditDiff.ApplyReplacements(normalized, replacements)
            : EditDiff.ApplyReplacementsToOriginal(original, replacements);
    }

    // 混合换行：LF 在前，未触碰的 CRLF 行必须保留
    [Fact]
    public void MixedLfFirst_KeepsCrlfOnUntouchedLines()
    {
        // tools.test.ts: "a\nb\r\nX = 1\r\n" edit X = 1 -> X = 2
        var written = WriteViaEditPipeline("a\nb\r\nX = 1\r\n", "X = 1", "X = 2");
        Assert.Equal("a\nb\r\nX = 2\r\n", written);
    }

    // 混合换行：CRLF 在前，未触碰的 LF 行必须保留
    [Fact]
    public void MixedCrlfFirst_KeepsLfOnUntouchedLine()
    {
        // tools.test.ts: "a\r\nb\nc" edit c -> C  =>  "a\r\nb\nC"
        var written = WriteViaEditPipeline("a\r\nb\nc", "c", "C");
        Assert.Equal("a\r\nb\nC", written);
    }

    // 孤立 CR 在未触碰行里是内容，不得被改成 LF
    [Fact]
    public void LoneCrInUntouchedLine_IsPreserved()
    {
        // tools.test.ts: "a\rb\nX = 1\n" edit X = 1 -> X = 2  =>  "a\rb\nX = 2\n"
        var written = WriteViaEditPipeline("a\rb\nX = 1\n", "X = 1", "X = 2");
        Assert.Equal("a\rb\nX = 2\n", written);
    }

    // 纯 LF / 纯 CRLF：行为与旧实现一致（回归保护）
    [Fact]
    public void PureLf_UnchangedBehaviour()
    {
        var written = WriteViaEditPipeline("a\nb\nX = 1\n", "X = 1", "X = 2\nY = 3");
        Assert.Equal("a\nb\nX = 2\nY = 3\n", written);
    }

    [Fact]
    public void PureCrlf_InsertedLinesUseCrlf()
    {
        // 纯 CRLF 文件插入新行，新行的换行用所在行的行尾（CRLF）。
        var written = WriteViaEditPipeline("a\r\nX = 1\r\n", "X = 1", "X = 2\r\nY = 3".Replace("\r\n", "\n"));
        Assert.Equal("a\r\nX = 2\r\nY = 3\r\n", written);
    }

    // 拼接缝：保留的 CR 与新 LF 相遇，补一个 LF 使其各自成为独立行尾，写出的文件归一化后 = diff 描述的内容
    [Fact]
    public void Seam_InsertStartingWithLfAfterKeptCr_AddsLf()
    {
        // tools.test.ts: "a\rb\nc" edit b -> "\nB"  =>  written "a\r\n\nB\nc"（归一 "a\n\nB\nc"）
        var written = WriteViaEditPipeline("a\rb\nc", "b", "\nB");
        Assert.Equal("a\r\n\nB\nc", written);
        Assert.Equal("a\n\nB\nc", EditDiff.NormalizeToLf(written));
    }

    [Fact]
    public void Seam_DeletionBetweenCrAndLf_AddsLf()
    {
        // tools.test.ts: "a\rb\nc" edit b -> ""  =>  written "a\r\n\nc"（归一 "a\n\nc"）
        var written = WriteViaEditPipeline("a\rb\nc", "b", "");
        Assert.Equal("a\r\n\nc", written);
        Assert.Equal("a\n\nc", EditDiff.NormalizeToLf(written));
    }

    [Fact]
    public void Seam_InsertedCrBeforeKeptLf_NormalizesToDiffedContent()
    {
        // tools.test.ts: applyReplacementsToOriginal("a\rb\nc", [{2,1,"\r"}]) 归一后 "a\n\n\nc"
        var written = EditDiff.ApplyReplacementsToOriginal("a\rb\nc", [R(2, 1, "\r")]);
        Assert.Equal("a\n\n\nc", EditDiff.NormalizeToLf(written));
    }

    // 经典 Mac（全 CR，无 LF）：插入行用 CR 行尾
    [Fact]
    public void ClassicMac_NoLf_InsertedLinesUseCr()
    {
        // 无 LF 时 getLineEndingAt 返回 "\r"，新文本里的 \n 换成 \r。
        var written = WriteViaEditPipeline("a\rX = 1\r", "X = 1", "X = 2\nY = 3");
        Assert.Equal("a\rX = 2\rY = 3\r", written);
        Assert.Equal("a\nX = 2\nY = 3\n", EditDiff.NormalizeToLf(written));
    }

    // 直接叶子：无 CR 的归一化文本等价
    [Fact]
    public void ApplyReplacementsToOriginal_NoCr_EqualsApplyReplacements()
    {
        var original = "a\nb\n";
        var reps = new List<EditDiff.TextReplacement> { R(2, 1, "B") };
        Assert.Equal(
            EditDiff.ApplyReplacements(original, reps),
            EditDiff.ApplyReplacementsToOriginal(original, reps));
    }

    // ---------- 端到端：真正经 EditTool.Execute 写盘，校验未改动行的行尾字节 ----------

    private static async Task<string> RunFullEditTool(string root, string fileContent, string oldText, string newText)
    {
        var path = Path.Combine(root, "mixed.txt");
        File.WriteAllText(path, fileContent, new UTF8Encoding(false));
        var tool = EditTool.CreateEditToolDefinition(root);
        var edits = new JsonArray
        {
            new JsonObject { ["oldText"] = oldText, ["newText"] = newText },
        };
        await tool.Execute("call-1", new Dictionary<string, object?>
        {
            ["path"] = path,
            ["edits"] = edits,
        }, CancellationToken.None, null, null).ConfigureAwait(false);
        return File.ReadAllText(path, new UTF8Encoding(false));
    }

    [Fact]
    public async Task FullEditTool_MixedLfFirst_KeepsUntouchedCrlfBytes()
    {
        var root = Path.Combine(Path.GetTempPath(), "pi-edit-le-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var written = await RunFullEditTool(root, "a\nb\r\nX = 1\r\n", "X = 1", "X = 2");
            Assert.Equal("a\nb\r\nX = 2\r\n", written);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task FullEditTool_LoneCrInUntouchedLine_IsPreserved()
    {
        var root = Path.Combine(Path.GetTempPath(), "pi-edit-le-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var written = await RunFullEditTool(root, "a\rb\nX = 1\n", "X = 1", "X = 2");
            Assert.Equal("a\rb\nX = 2\n", written);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }
}
