using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Pi.CodingAgent;
using Pi.CodingAgent.Core;
using Pi.Tui;
using Xunit;

namespace Pi.CodingAgent.Tests;

public sealed class ExportHtmlBugReportTests
{
    private static string TemplateDir()
    {
        var bundled = Path.Combine(AppContext.BaseDirectory, "Assets", "export-html");
        if (File.Exists(Path.Combine(bundled, "template.html")))
        {
            return bundled;
        }

        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            var candidate = Path.Combine(dir.FullName, "src", "Pi.CodingAgent", "Assets", "export-html");
            if (File.Exists(Path.Combine(candidate, "template.html")))
            {
                return candidate;
            }
        }

        throw new DirectoryNotFoundException("export-html templates");
    }

    // ---- ansi-to-html / whitespace ----------------------------------------

    [Fact]
    public void AnsiLinesToHtml_DoesNotInsertSourceWhitespaceBetweenLines()
        => Assert.Equal(
            """<div class="ansi-line">one</div><div class="ansi-line">two</div>""",
            AnsiToHtml.LinesToHtml(["one", "two"]));

    [Fact]
    public void TemplateCss_PreservesWhitespaceForToolOutputLines()
    {
        var css = File.ReadAllText(Path.Combine(TemplateDir(), "template.css"));
        Assert.Matches(
            new Regex(@"\.output-preview > div:not\(\.expand-hint\),\s*\.output-full > div:not\(\.expand-hint\) \{[\s\S]*?white-space:\s*pre-wrap;"),
            css);
        Assert.Matches(new Regex(@"\.ansi-line\s*\{[\s\S]*?white-space:\s*pre;"), css);
        Assert.DoesNotMatch(
            new Regex(@"\.output-preview,\s*\.output-full\s*\{[\s\S]*?white-space:\s*pre-wrap;"),
            css);
    }

    [Fact]
    public void ToolHtmlRenderer_TrimsTuiSpacingLines()
    {
        var component = new ScriptedComponent(["", "\u001b[31mone\u001b[0m", "two", ""]);
        var renderer = ToolHtmlRendererFactory.Create(new ToolHtmlRendererDeps
        {
            GetToolRenderers = _ => new ToolHtmlRenderers
            {
                RenderResult = (_, _, _, _) => component,
            },
            Theme = new object(),
            Cwd = "/tmp",
        });

        Assert.Equal(
            """<div class="ansi-line"><span style="color:#800000">one</span></div><div class="ansi-line">two</div>""",
            renderer.RenderResult("id", "custom", Array.Empty<object>(), null, false)?.Expanded);
    }

    private sealed class ScriptedComponent(string[] lines) : IComponent
    {
        public string[] Render(int width) => lines;
    }

    // ---- template.js XSS / skill-block ------------------------------------

    [Fact]
    public void TemplateJs_DefinesEscapeHtml()
    {
        var js = File.ReadAllText(Path.Combine(TemplateDir(), "template.js"));
        Assert.Contains("function escapeHtml(text)", js);
    }

    [Fact]
    public void TemplateJs_EscapesToolCallArguments()
    {
        var js = File.ReadAllText(Path.Combine(TemplateDir(), "template.js"));
        Assert.Contains("escapeHtml(JSON.stringify(args, null, 2))", js);
    }

    [Fact]
    public void TemplateJs_EscapesSkillNames()
    {
        var js = File.ReadAllText(Path.Combine(TemplateDir(), "template.js"));
        Assert.Contains("function parseSkillBlock(text)", js);
        Assert.Contains("escapeHtml(skillBlock.name)", js);
        Assert.Contains("skill-invocation-label", js);
        Assert.Contains("tree-role-skill", js);
    }

    [Fact]
    public void TemplateJs_DoesNotDoubleEscapeAnsiHtml()
    {
        var js = File.ReadAllText(Path.Combine(TemplateDir(), "template.js"));
        Assert.DoesNotContain("escapeHtml(resultHtml.expanded)", js);
        Assert.DoesNotContain("escapeHtml(resultHtml.collapsed)", js);
        Assert.DoesNotContain("escapeHtml(callHtml)", js);
    }

    // ---- generateHtml smoke -----------------------------------------------

    [Fact]
    public void GenerateHtml_ReplacesTemplatePlaceholders()
    {
        var session = SessionManager.InMemory("/tmp");
        var html = ExportHtml.GenerateHtml(session, templateDir: TemplateDir());
        Assert.DoesNotContain("{{CSS}}", html);
        Assert.DoesNotContain("{{JS}}", html);
        Assert.DoesNotContain("{{SESSION_DATA}}", html);
        Assert.DoesNotContain("{{MARKED_JS}}", html);
        Assert.Contains("<html", html, StringComparison.OrdinalIgnoreCase);
    }

    // ---- bug-report redaction ---------------------------------------------

    [Fact]
    public void RedactUrl_LeavesOrdinaryUrlsUnchanged()
        => Assert.Equal("https://example.com/path?q=1", BugReport.RedactUrl("https://example.com/path?q=1"));

    [Fact]
    public void RedactUrl_RedactsSecretQueryParams()
        => Assert.Equal(
            "https://api.example.com/v1?api_key=***&q=ok",
            BugReport.RedactUrl("https://api.example.com/v1?api_key=secret&q=ok"));

    [Fact]
    public void RedactJsonValue_RedactsSecretKeysAndNestedUrls()
    {
        var node = JsonNode.Parse("""{"apiKey":"sk-live","url":"https://x.test?token=abc","keep":1}""")!;
        var redacted = BugReport.RedactJsonValue(node)!.AsObject();
        Assert.Equal("***", redacted["apiKey"]!.GetValue<string>());
        Assert.Equal("https://x.test?token=***", redacted["url"]!.GetValue<string>());
        Assert.Equal(1, redacted["keep"]!.GetValue<int>());
    }
}
