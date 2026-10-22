using System.Text.Json.Nodes;
using Pi.CodingAgent;
using Xunit;

namespace Pi.CodingAgent.Tests;

/// <summary>
/// The configuration schemas upstream publishes at <c>pi.dev</c> (upstream <c>7f9e1198f</c> /
/// <c>96abe9a5a</c>). They ship as assets so editors can validate and complete
/// <c>settings.json</c>, <c>models.json</c>, <c>keybindings.json</c>, and theme files. Nothing at
/// runtime reads them, so what these tests pin is artifact consistency: the canonical <c>$id</c>s,
/// the bundled themes' <c>$schema</c>, and that the theme schema and the themes agree on roles.
/// </summary>
public sealed class ConfigurationSchemaTests
{
    private static string SchemasDir => Path.Combine(AppContext.BaseDirectory, "Assets", "schemas");

    private static JsonNode Load(string path) => JsonNode.Parse(File.ReadAllText(path))!;

    [Theory]
    [InlineData("settings")]
    [InlineData("models")]
    [InlineData("theme")]
    [InlineData("keybindings")]
    public void SchemaArtifactsAreShippedWithTheirCanonicalIds(string name)
    {
        var path = Path.Combine(SchemasDir, $"{name}.schema.json");
        Assert.True(File.Exists(path), $"missing {path}");

        var schema = Load(path);
        Assert.Equal("https://json-schema.org/draft/2020-12/schema", schema["$schema"]!.GetValue<string>());
        Assert.Equal($"https://pi.dev/schemas/{name}.schema.json", schema["$id"]!.GetValue<string>());
        Assert.Equal("object", schema["type"]!.GetValue<string>());
    }

    [Fact]
    public void BundledThemesPointAtThePublishedThemeSchema()
    {
        foreach (var name in new[] { "dark", "light" })
        {
            var theme = Load(Path.Combine(Config.GetThemesDir(), $"{name}.json"));
            Assert.Equal("https://pi.dev/schemas/theme.schema.json", theme["$schema"]!.GetValue<string>());
        }
    }

    [Fact]
    public void ThemeSchemaAndBundledThemesDescribeTheSameColorRoles()
    {
        var schema = Load(Path.Combine(SchemasDir, "theme.schema.json"));
        var required = Strings(schema["required"]);
        Assert.Contains("name", required);
        Assert.Contains("colors", required);

        var colorsSchema = schema["properties"]!["colors"]!;
        var requiredColors = Strings(colorsSchema["required"]);
        var describedColors = colorsSchema["properties"]!.AsObject().Select(kv => kv.Key).ToHashSet(StringComparer.Ordinal);
        Assert.NotEmpty(requiredColors);

        foreach (var name in new[] { "dark", "light" })
        {
            var colors = Load(Path.Combine(Config.GetThemesDir(), $"{name}.json"))["colors"]!.AsObject();
            foreach (var color in requiredColors)
            {
                Assert.True(colors.ContainsKey(color), $"{name}.json is missing required color '{color}'");
            }
            // Every role a bundled theme assigns must be described by the schema, otherwise editors
            // flag the files Pi itself ships as invalid.
            foreach (var color in colors.Select(kv => kv.Key))
            {
                Assert.True(describedColors.Contains(color), $"theme.schema.json does not describe color '{color}'");
            }
        }
    }

    [Fact]
    public void SettingsSchemaCoversTheTranscriptAndTerminalSettingsThePortImplements()
    {
        var properties = Load(Path.Combine(SchemasDir, "settings.schema.json"))["properties"]!.AsObject();
        string[] keys =
        [
            "editorPaddingX",
            "outputPad",
            "autocompleteMaxVisible",
            "showHardwareCursor",
            "fullscreenCopyOnSelect",
            "fullscreenWheelScrollLines",
            "terminal",
            "markdown",
            "sessionShareDestination",
        ];
        foreach (var key in keys)
        {
            Assert.True(properties.ContainsKey(key), $"settings.schema.json is missing '{key}'");
        }
    }

    [Fact]
    public void KeybindingsSchemaAllowsEveryKeyIdThePortParses()
    {
        var properties = Load(Path.Combine(SchemasDir, "keybindings.schema.json"))["properties"]!.AsObject();
        // Action ids the schema enumerates are the same identifiers the port registers; sample a few
        // that landed in different batches so a stale artifact is caught.
        foreach (var id in new[] { "app.interrupt", "app.session.tree", "tui.altScreen.search", "tui.editor.undo" })
        {
            Assert.True(properties.ContainsKey(id), $"keybindings.schema.json is missing '{id}'");
        }
        // Free-form ids stay allowed.
        Assert.NotNull(Load(Path.Combine(SchemasDir, "keybindings.schema.json"))["additionalProperties"]);
    }

    private static string[] Strings(JsonNode? node) =>
        node is JsonArray array ? array.Select(n => n!.GetValue<string>()).ToArray() : [];
}
