using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Pi.CodingAgent;
using Pi.CodingAgent.Core;
using Pi.CodingAgent.Core.Extensions;
using Pi.CodingAgent.Modes.Interactive;
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
            GetToolRenderers = _ => new ToolRenderers
            {
                RenderResult = (_, _, _, _) => component,
            },
            Theme = MinimalTheme(),
            Cwd = "/tmp",
        });

        Assert.Equal(
            """<div class="ansi-line"><span style="color:#800000">one</span></div><div class="ansi-line">two</div>""",
            renderer.RenderResult("id", "custom", Array.Empty<object>(), null, false)?.Expanded);
    }

    /// <summary>
    /// A theme with the tokens the constructor fills in for its optional slots, which is all the
    /// renderer under test needs.
    /// </summary>
    private static Theme MinimalTheme() => new(
        new Dictionary<string, ThemeJsonColorValue>(StringComparer.Ordinal)
        {
            [ThemeColors.Text] = "#ffffff",
            [ThemeColors.Muted] = "#cccccc",
            [ThemeColors.ThinkingXhigh] = "#dddddd",
        },
        new Dictionary<string, ThemeJsonColorValue>(StringComparer.Ordinal)
        {
            [ThemeBackgrounds.SelectedBg] = "#111111",
        },
        TerminalColorMode.Truecolor,
        new ThemeOptions { Name = "export-html-test", Appearance = ThemeAppearance.Dark });

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

    [Fact]
    public void TemplateJs_UsesCjkFriendlyEmphasisTokenizer()
    {
        // Upstream #3353 / #10154: `**中文**。` rendered literal asterisks without the CJK-aware
        // `emStrong` tokenizer.
        var js = File.ReadAllText(Path.Combine(TemplateDir(), "template.js"));
        Assert.Contains("markedCjkFriendly().tokenizer.emStrong", js);

        var html = File.ReadAllText(Path.Combine(TemplateDir(), "template.html"));
        Assert.Contains("<script>{{MARKED_CJK_FRIENDLY_JS}}</script>", html);
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
        Assert.DoesNotContain("{{MARKED_CJK_FRIENDLY_JS}}", html);
        Assert.Contains("markedCjkFriendly", html, StringComparison.Ordinal);
        Assert.Contains("<html", html, StringComparison.OrdinalIgnoreCase);
    }

    // ---- bug-report redaction ---------------------------------------------
    // 期望值由 tools/bug-report-redaction-reference.mjs（TS 原函数逐字抄录）对拍得出。

    [Fact]
    public void RedactUrl_LeavesOrdinaryUrlsUnchanged()
        => Assert.Equal("https://example.com/path?q=1", BugReport.RedactUrl("https://example.com/path?q=1"));

    [Fact]
    public void RedactUrl_RedactsSecretQueryParams()
        // TS 的 searchParams.set 对值做百分号编码。
        => Assert.Equal(
            "https://api.example.com/v1?api_key=%3Credacted%3E&q=ok",
            BugReport.RedactUrl("https://api.example.com/v1?api_key=secret&q=ok"));

    [Fact]
    public void RedactUrl_StripsUserInfo()
        => Assert.Equal("https://example.com/path?x=1", BugReport.RedactUrl("https://user:pass@example.com/path?x=1"));

    [Fact]
    public void RedactUrl_KeepsTheTrailingSlashWhenStrippingUserInfo()
        => Assert.Equal("https://example.com/", BugReport.RedactUrl("https://tok@example.com/"));

    [Fact]
    public void RedactUrl_HandlesTheNestedSchemeForm()
        => Assert.Equal(
            "proxy:https://example.com/x?token=%3Credacted%3E&q=1",
            BugReport.RedactUrl("proxy:https://user:pass@example.com/x?token=t&q=1"));

    [Fact]
    public void RedactUrl_MatchesCamelCaseQueryKeys()
        => Assert.Equal(
            "https://x.test/?apiKey=%3Credacted%3E&keep=1",
            BugReport.RedactUrl("https://x.test/?apiKey=abc&keep=1"));

    [Fact]
    public void RedactUrl_LeavesNonUrlsAlone()
        => Assert.Equal("not a url", BugReport.RedactUrl("not a url"));

    [Fact]
    public void RedactUrl_RedactsEverySecretQueryParam()
        => Assert.Equal(
            "https://x.test/?token=%3Credacted%3E&secret=%3Credacted%3E&password=%3Credacted%3E&q=1",
            BugReport.RedactUrl("https://x.test/?token=a&secret=b&password=c&q=1"));

    [Fact]
    public void RedactUrl_LeavesNonSecretKeysThatStartWithASecretWord()
        => Assert.Equal("https://x.test/?tokenizer=abc", BugReport.RedactUrl("https://x.test/?tokenizer=abc"));

    [Theory]
    [InlineData("apiKey", true)]
    [InlineData("x-api-key", true)]
    [InlineData("apikey", true)]
    [InlineData("API_KEY", true)]
    [InlineData("accessToken", true)]
    [InlineData("refreshToken", true)]
    [InlineData("passwd", true)]
    [InlineData("cookie", true)]
    [InlineData("authorization", true)]
    [InlineData("my-token-value", true)]
    [InlineData("tokenizer", false)]
    [InlineData("secretx", false)]
    [InlineData("privateKey", false)]   // TS 的 SENSITIVE_KEY 没有裸 "key"
    [InlineData("model", false)]
    public void IsSensitiveKey_MatchesTheTsRegex(string key, bool expected)
        => Assert.Equal(expected, BugReport.IsSensitiveKey(key));

    [Fact]
    public void RedactJsonValue_RedactsSecretKeysAndNestedUrls()
    {
        var node = JsonNode.Parse("""{"apiKey":"sk-live","url":"https://x.test?token=abc","keep":1}""")!;
        var redacted = BugReport.RedactJsonValue(node)!.AsObject();
        Assert.Equal("<redacted>", redacted["apiKey"]!.GetValue<string>());
        Assert.Equal("https://x.test/?token=%3Credacted%3E", redacted["url"]!.GetValue<string>());
        Assert.Equal(1, redacted["keep"]!.GetValue<int>());
    }

    [Fact]
    public void RedactJsonValue_WalksNestedObjectsAndArrays()
    {
        var node = JsonNode.Parse("""{"a":{"api_key":"k","nested":[{"token":"t","ok":"https://y.test"}]}}""")!;
        var redacted = BugReport.RedactJsonValue(node)!.AsObject();
        var inner = redacted["a"]!.AsObject();
        Assert.Equal("<redacted>", inner["api_key"]!.GetValue<string>());
        var item = inner["nested"]!.AsArray()[0]!.AsObject();
        Assert.Equal("<redacted>", item["token"]!.GetValue<string>());
        Assert.Equal("https://y.test", item["ok"]!.GetValue<string>());
    }

    [Fact]
    public void RedactJsonValue_KeepsNullValues()
    {
        var node = JsonNode.Parse("""{"token":null,"other":"x"}""")!;
        var redacted = BugReport.RedactJsonValue(node)!.AsObject();
        Assert.Null(redacted["token"]);
        Assert.Equal("x", redacted["other"]!.GetValue<string>());
    }
}
