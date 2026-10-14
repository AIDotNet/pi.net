// ============================================================================
// Tests for modes/interactive/theme/theme-json.ts
// ============================================================================
//
// TS validates with typebox (`Compile(ThemeJsonSchema).Check/Errors`); the port re-implements the
// schema by hand over JsonNode (divergence C194). What is pinned here is therefore the accept/reject
// set, the exact framing of the thrown message, and the sorted "Missing required color tokens"
// section. The wording of the individual "Other errors" lines is the port's own — typebox's message
// table is not reproducible — so those assertions only match the port's own strings.

using System.Text.Json.Nodes;
using Pi.CodingAgent.Modes.Interactive;
using Xunit;

namespace Pi.CodingAgent.Tests;

public sealed class ThemeJsonTests
{
    // ---------------------------------------------------------------------------------------------
    // Fixtures
    // ---------------------------------------------------------------------------------------------

    /// <summary>Every token the schema requires, plus the optional ones, all with a valid color.</summary>
    private static string FullColorsJson()
    {
        var tokens = ThemeJson.RequiredTokens
            .Concat(ThemeColors.Optional)
            .Concat(ThemeBackgrounds.Optional);
        return "{" + string.Join(", ", tokens.Select(token => $"\"{token}\": \"#101010\"")) + "}";
    }

    private static JsonNode FullTheme(string name = "test") =>
        JsonNode.Parse($"{{\"name\": \"{name}\", \"colors\": {FullColorsJson()}}}")!;

    private static string Message(string label, JsonNode? json) =>
        Assert.Throws<InvalidOperationException>(() => ThemeJson.ValidateThemeJson(label, json)).Message;

    // ---------------------------------------------------------------------------------------------
    // Token sets
    // ---------------------------------------------------------------------------------------------

    [Fact]
    public void RequiredTokens_AreTheUnionMinusTheOptionalOnes()
    {
        // 49 foreground tokens with 4 optional, 7 background tokens with 1 optional.
        Assert.Equal(49, ThemeColors.All.Count);
        Assert.Equal(7, ThemeBackgrounds.All.Count);
        Assert.Equal(51, ThemeJson.RequiredTokens.Count);
        Assert.Equal(
            [.. ThemeColors.All.Where(token => !ThemeColors.Optional.Contains(token)),
             .. ThemeBackgrounds.All.Where(token => !ThemeBackgrounds.Optional.Contains(token))],
            ThemeJson.RequiredTokens);
    }

    [Fact]
    public void RequiredTokens_ExcludeExactlyTheOptionalTokens()
    {
        Assert.DoesNotContain(ThemeColors.ScrollbarTrack, ThemeJson.RequiredTokens);
        Assert.DoesNotContain(ThemeColors.ScrollbarThumb, ThemeJson.RequiredTokens);
        Assert.DoesNotContain(ThemeColors.ThinkingMax, ThemeJson.RequiredTokens);
        Assert.DoesNotContain(ThemeColors.SearchMatchText, ThemeJson.RequiredTokens);
        Assert.DoesNotContain(ThemeBackgrounds.SearchMatchBg, ThemeJson.RequiredTokens);
        Assert.Contains(ThemeColors.Text, ThemeJson.RequiredTokens);
        Assert.Contains(ThemeBackgrounds.SelectedBg, ThemeJson.RequiredTokens);
    }

    [Fact]
    public void TokenPredicates_SeparateTheSlots()
    {
        Assert.True(ThemeTokens.IsColor(ThemeColors.Text));
        Assert.False(ThemeTokens.IsColor(ThemeBackgrounds.SelectedBg));
        Assert.True(ThemeTokens.IsBackground(ThemeBackgrounds.SelectedBg));
        Assert.False(ThemeTokens.IsBackground(ThemeColors.Text));
        Assert.True(ThemeTokens.IsToken(ThemeColors.Text));
        Assert.True(ThemeTokens.IsToken(ThemeBackgrounds.SelectedBg));
        Assert.False(ThemeTokens.IsToken("notAToken"));
        Assert.Equal(56, ThemeTokens.All.Count);
    }

    [Theory]
    [InlineData("dark", true)]
    [InlineData("light", true)]
    [InlineData("Dark", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void ThemeAppearance_IsValid(string? value, bool expected) =>
        Assert.Equal(expected, ThemeAppearance.IsValid(value));

    // ---------------------------------------------------------------------------------------------
    // Accepting
    // ---------------------------------------------------------------------------------------------

    [Fact]
    public void ValidateThemeJson_AcceptsAFullDocumentAndReadsItBack()
    {
        var json = JsonNode.Parse(
            """
            {
              "$schema": "https://example.test/theme-schema.json",
              "name": "custom",
              "appearance": "light",
              "vars": { "brand": "#112233", "alias": "brand" },
              "colors": { "accent": "brand", "text": "", "border": 7 },
              "export": { "pageBg": "#000000", "cardBg": 24 }
            }
            """)!;
        var colors = new JsonObject();
        foreach (var token in ThemeJson.RequiredTokens)
        {
            colors[token] = "#101010";
        }
        colors["accent"] = "brand";
        colors["text"] = "";
        colors["border"] = 7;
        ((JsonObject)json)["colors"] = colors;

        var validated = ThemeJson.ValidateThemeJson("custom", json);

        Assert.Equal("https://example.test/theme-schema.json", validated.Schema);
        Assert.Equal("custom", validated.Name);
        Assert.Equal(ThemeAppearance.Light, validated.Appearance);
        Assert.Equal(2, validated.Vars!.Count);
        Assert.True(validated.Colors["accent"].IsText);
        Assert.Equal("brand", validated.Colors["accent"].Text);
        Assert.Equal("", validated.Colors["text"].Text);
        Assert.Equal(7, validated.Colors["border"].Index);
        Assert.Equal("#000000", validated.Export!.PageBg!.Value.Text);
        Assert.Equal(24, validated.Export.CardBg!.Value.Index);
        Assert.Null(validated.Export.InfoBg);
    }

    [Fact]
    public void ValidateThemeJson_AcceptsAnEmptyStringAsTheTerminalDefault()
    {
        var json = (JsonObject)FullTheme();
        ((JsonObject)json["colors"]!)["text"] = "";
        Assert.Equal("", ThemeJson.ValidateThemeJson("test", json).Colors["text"].Text);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(255)]
    public void ValidateThemeJson_AcceptsPaletteIndicesInRange(int index)
    {
        var json = (JsonObject)FullTheme();
        ((JsonObject)json["colors"]!)["accent"] = index;
        Assert.Equal(index, ThemeJson.ValidateThemeJson("test", json).Colors["accent"].Index);
    }

    [Fact]
    public void ValidateThemeJson_IgnoresUnknownColorKeys()
    {
        // The schema does not set additionalProperties: false, so an unknown key is not an error.
        var json = (JsonObject)FullTheme();
        ((JsonObject)json["colors"]!)["notAToken"] = "#ffffff";
        Assert.Equal("test", ThemeJson.ValidateThemeJson("test", json).Name);
    }

    // ---------------------------------------------------------------------------------------------
    // Rejecting: framing
    // ---------------------------------------------------------------------------------------------

    [Fact]
    public void ValidateThemeJson_RejectsANonObject()
    {
        // TS builds the message as `Invalid theme "x":\n` and appends `\n\nOther errors:\n…`, so a
        // document with other errors but no missing colors carries three newlines after the colon.
        // The port reproduces that quirk rather than tidying it.
        var message = Message("thing", JsonNode.Parse("[]"));
        Assert.Equal(
            "Invalid theme \"thing\":\n\n\nOther errors:\n  - /: Expected an object with a \"colors\" map.",
            message);
        Assert.DoesNotContain("Missing required color tokens", message);
    }

    [Fact]
    public void ValidateThemeJson_RejectsNull()
    {
        var message = Message("thing", null);
        Assert.Contains("Invalid theme \"thing\":", message);
        Assert.Contains("  - /: Expected an object with a \"colors\" map.", message);
    }

    [Fact]
    public void ValidateThemeJson_ListsEveryMissingTokenInOrdinalOrder()
    {
        var message = Message("thing", JsonNode.Parse("{\"name\": \"thing\"}"));
        var expected = string.Join(
            "\n",
            ThemeJson.RequiredTokens.OrderBy(token => token, StringComparer.Ordinal).Select(token => $"  - {token}"));
        Assert.Contains(expected, message);
        Assert.Contains("  - /: Expected required property colors", message);
    }

    [Fact]
    public void ValidateThemeJson_SortsMissingTokensByOrdinalNotByTheTokenList()
    {
        var json = (JsonObject)FullTheme();
        var colors = (JsonObject)json["colors"]!;
        colors.Remove("text");
        colors.Remove("accent");

        var message = Message("thing", json);
        var missingSection = message[..message.IndexOf("\n\nPlease add", StringComparison.Ordinal)];
        Assert.EndsWith("  - accent\n  - text", missingSection);
    }

    [Fact]
    public void ValidateThemeJson_ClosesTheMissingSectionWithTheReferenceLines()
    {
        var message = Message("thing", JsonNode.Parse("{\"name\": \"thing\", \"colors\": {\"text\": \"#000000\"}}"));
        Assert.DoesNotContain("Other errors", message);
        Assert.EndsWith(
            "\n\nPlease add these colors to your theme's \"colors\" object."
            + "\nSee the built-in themes (dark.json, light.json) for reference values.",
            message);
    }

    [Fact]
    public void ValidateThemeJson_EmitsTheMissingSectionBeforeTheOtherErrors()
    {
        var message = Message("thing", JsonNode.Parse("{\"name\": \"thing\"}"));
        Assert.Contains("Please add these colors to your theme's \"colors\" object.", message);
        Assert.EndsWith("Other errors:\n  - /: Expected required property colors", message);
    }

    [Fact]
    public void ValidateThemeJson_OmitsTheMissingSectionWhenNothingIsMissing()
    {
        var json = (JsonObject)FullTheme();
        json.Remove("name");

        var message = Message("thing", json);
        Assert.DoesNotContain("Missing required color tokens", message);
        Assert.Contains("Other errors:\n  - /: Expected required property name", message);
    }

    // ---------------------------------------------------------------------------------------------
    // Rejecting: per-field errors
    // ---------------------------------------------------------------------------------------------

    [Fact]
    public void ValidateThemeJson_RejectsColorsThatAreNotAnObject()
    {
        var message = Message("thing", JsonNode.Parse("{\"name\": \"thing\", \"colors\": []}"));
        Assert.Contains("  - /colors: Expected an object", message);
        Assert.Contains("Missing required color tokens", message);
    }

    [Theory]
    [InlineData("true")]
    [InlineData("256")]
    [InlineData("-1")]
    [InlineData("1.5")]
    public void ValidateThemeJson_RejectsInvalidColorValues(string value)
    {
        var json = (JsonObject)FullTheme();
        ((JsonObject)json["colors"]!)["accent"] = JsonNode.Parse(value);
        Assert.Contains(
            "  - /colors/accent: Expected a color string or an integer between 0 and 255",
            Message("thing", json));
    }

    [Fact]
    public void ValidateThemeJson_AcceptsAnyStringAsAColorValue()
    {
        // The schema cannot tell a hex string from a var reference, so any string passes validation;
        // resolution happens later, in ResolveVarRefs.
        var json = (JsonObject)FullTheme();
        ((JsonObject)json["colors"]!)["accent"] = "#ffffff";
        Assert.Equal("#ffffff", ThemeJson.ValidateThemeJson("thing", json).Colors["accent"].Text);
    }

    [Fact]
    public void ValidateThemeJson_RejectsANullColorValue()
    {
        var json = (JsonObject)FullTheme();
        ((JsonObject)json["colors"]!)["accent"] = null;
        Assert.Contains(
            "  - /colors/accent: Expected a color string or an integer between 0 and 255",
            Message("thing", json));
    }

    [Fact]
    public void ValidateThemeJson_RejectsAMissingName()
    {
        var json = (JsonObject)FullTheme();
        json.Remove("name");
        Assert.Contains("  - /: Expected required property name", Message("thing", json));
    }

    [Fact]
    public void ValidateThemeJson_RejectsANonStringName()
    {
        var json = (JsonObject)FullTheme();
        json["name"] = 7;
        Assert.Contains("  - /name: Expected a string", Message("thing", json));
    }

    [Theory]
    [InlineData("\"Dark\"")]
    [InlineData("\"\"")]
    [InlineData("\"system\"")]
    [InlineData("7")]
    [InlineData("true")]
    public void ValidateThemeJson_RejectsAnUnknownAppearance(string appearance)
    {
        var json = (JsonObject)FullTheme();
        json["appearance"] = JsonNode.Parse(appearance);
        Assert.Contains("  - /appearance: Expected \"dark\" or \"light\"", Message("thing", json));
    }

    [Theory]
    [InlineData("\"dark\"")]
    [InlineData("\"light\"")]
    public void ValidateThemeJson_AcceptsAKnownAppearance(string appearance)
    {
        var json = (JsonObject)FullTheme();
        json["appearance"] = JsonNode.Parse(appearance);
        Assert.Equal(appearance.Trim('"'), ThemeJson.ValidateThemeJson("thing", json).Appearance);
    }

    [Fact]
    public void ValidateThemeJson_RejectsVarsThatAreNotAnObject()
    {
        var json = (JsonObject)FullTheme();
        json["vars"] = JsonNode.Parse("[]");
        Assert.Contains("  - /vars: Expected an object", Message("thing", json));
    }

    [Fact]
    public void ValidateThemeJson_RejectsAnInvalidVarValue()
    {
        var json = (JsonObject)FullTheme();
        json["vars"] = JsonNode.Parse("{\"brand\": true}");
        Assert.Contains(
            "  - /vars/brand: Expected a color string or an integer between 0 and 255",
            Message("thing", json));
    }

    [Fact]
    public void ValidateThemeJson_RejectsExportThatIsNotAnObject()
    {
        var json = (JsonObject)FullTheme();
        json["export"] = JsonNode.Parse("[]");
        Assert.Contains("  - /export: Expected an object", Message("thing", json));
    }

    [Theory]
    [InlineData("pageBg")]
    [InlineData("cardBg")]
    [InlineData("infoBg")]
    public void ValidateThemeJson_RejectsAnInvalidExportValue(string key)
    {
        var json = (JsonObject)FullTheme();
        json["export"] = JsonNode.Parse($"{{\"{key}\": true}}");
        Assert.Contains(
            $"  - /export/{key}: Expected a color string or an integer between 0 and 255",
            Message("thing", json));
    }

    [Fact]
    public void ValidateThemeJson_AcceptsAnEmptyExportObject()
    {
        var json = (JsonObject)FullTheme();
        json["export"] = JsonNode.Parse("{}");
        var export = ThemeJson.ValidateThemeJson("thing", json).Export;
        Assert.NotNull(export);
        Assert.Null(export!.PageBg);
        Assert.Null(export.CardBg);
        Assert.Null(export.InfoBg);
    }

    // ---------------------------------------------------------------------------------------------
    // Rejecting: the reserved name character
    // ---------------------------------------------------------------------------------------------

    [Fact]
    public void ValidateThemeJson_RejectsANameWithASlash()
    {
        var message = Assert.Throws<InvalidOperationException>(
            () => ThemeJson.ValidateThemeJson("thing", FullTheme("light/dark"))).Message;
        Assert.Equal(
            "Invalid theme name \"light/dark\": theme names cannot contain \"/\" because it is reserved "
            + "for automatic light/dark theme settings.",
            message);
    }

    [Fact]
    public void ValidateThemeJson_ReportsColorErrorsBeforeTheNameCheck()
    {
        // The slash check runs only after the schema passes, so a document with both problems reports
        // the colors first.
        var json = (JsonObject)FullTheme("light/dark");
        ((JsonObject)json["colors"]!).Remove("text");
        Assert.Contains("Missing required color tokens", Message("thing", json));
    }

    // ---------------------------------------------------------------------------------------------
    // The label
    // ---------------------------------------------------------------------------------------------

    [Fact]
    public void ValidateThemeJson_UsesTheLabelVerbatim()
    {
        Assert.StartsWith(
            "Invalid theme \"/tmp/my theme.json\":",
            Message("/tmp/my theme.json", JsonNode.Parse("{}")));
    }
}
