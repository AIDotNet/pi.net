using Pi.CodingAgent.Core;
using Xunit;

namespace Pi.CodingAgent.Tests;

/// <summary>
/// 系统提示词的固定文案。上游 v1.5.0 收紧了默认前缀、工具段与文档段（<c>fix(coding-agent):
/// tighten system prompt</c>）；这里逐句钉住，避免后续对齐时被静默改回。
/// </summary>
public class SystemPromptTests
{
    private static IReadOnlyDictionary<string, string> DefaultSections() =>
        SystemPrompt.BuildSystemPromptSections(new BuildSystemPromptOptions
        {
            Cwd = Path.GetTempPath(),
            SelectedTools = ["read"],
        });

    [Fact]
    public void PreambleMatchesUpstream1_5_0()
    {
        var sections = DefaultSections();
        Assert.Equal(
            "You are a coding assistant running inside pi, a coding agent harness. " +
            "Help users inspect files, run commands, and modify code.",
            sections["preamble"]);
    }

    [Fact]
    public void ToolsSectionKeepsTheShortTail()
    {
        var sections = DefaultSections();
        Assert.Contains("(none)\n\nAdditional tools may be available.\n</tools>", sections["tools"], StringComparison.Ordinal);
        Assert.DoesNotContain("In addition to the tools above", sections["tools"], StringComparison.Ordinal);
    }

    [Fact]
    public void DocsSectionUsesHarnessWordingAndOneCrossReferenceRule()
    {
        var sections = DefaultSections();
        var docs = sections["docs"];
        Assert.StartsWith(
            "<docs>\nPi documentation (read only when the user asks about the harness itself, its SDK, extensions, themes, skills, or TUI):",
            docs,
            StringComparison.Ordinal);
        Assert.EndsWith(
            "- Before implementing harness changes, read the relevant docs and examples in full, following related cross-references.\n</docs>",
            docs,
            StringComparison.Ordinal);
        // 1.5.0 把原先两条「读全文档」的规则合并成上面一条。
        Assert.DoesNotContain("Always read pi .md files completely", docs, StringComparison.Ordinal);
        Assert.DoesNotContain("When working on pi topics", docs, StringComparison.Ordinal);
        // 端口专属文档（dotnet-extensions.md）必须保留在扩展条目里。
        Assert.Contains("docs/dotnet-extensions.md", docs, StringComparison.Ordinal);
    }
}
