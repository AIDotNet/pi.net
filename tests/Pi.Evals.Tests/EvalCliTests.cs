using Xunit;

namespace Pi.Evals.Tests;

public sealed class EvalCliTests
{
    [Fact]
    public void ParseDocumentation_UsesEnvironmentDefaultsAndNormalizesFilePaths()
    {
        var options = EvalCli.ParseDocumentation(
            [@".\evals\models.docs.eval.cs", "--runs-per-variant", "3", "-t", "adds the model"],
            new Dictionary<string, string?>(StringComparer.Ordinal)
            {
                ["PI_PROVIDER"] = " fixture ",
                ["PI_MODEL"] = " model ",
            });

        Assert.Equal("fixture", options.Provider);
        Assert.Equal("model", options.Model);
        Assert.Equal(3, options.RunsPerVariant);
        Assert.Equal(["evals/models.docs.eval.cs"], options.Files);
        Assert.Equal("adds the model", options.TestNamePattern);
    }

    [Fact]
    public void ParseDocumentation_RequiresBothCliModelOptionsTogether()
    {
        var error = Assert.Throws<ArgumentException>(
            () => EvalCli.ParseDocumentation(
                ["--provider", "fixture"],
                new Dictionary<string, string?>(StringComparer.Ordinal)));

        Assert.Contains("requires both --provider and --model", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ParseDocumentation_RejectsIncompleteEnvironmentModelSelection()
    {
        var error = Assert.Throws<ArgumentException>(
            () => EvalCli.ParseDocumentation(
                [],
                new Dictionary<string, string?>(StringComparer.Ordinal)
                {
                    ["PI_PROVIDER"] = "fixture",
                }));

        Assert.Contains("Set both PI_PROVIDER and PI_MODEL", error.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("0")]
    [InlineData("-1")]
    [InlineData("1.5")]
    [InlineData("9007199254740992")]
    public void ParseDocumentation_RejectsInvalidRunCounts(string value)
    {
        Assert.Throws<ArgumentException>(
            () => EvalCli.ParseDocumentation(["--runs-per-variant", value]));
    }

    [Fact]
    public void ParseHost_RejectsDocumentationFilesAndDocumentationOnlyOptions()
    {
        Assert.Throws<ArgumentException>(
            () => EvalCli.ParseHost(["evals/models.docs.eval.cs"]));
        Assert.Throws<ArgumentException>(
            () => EvalCli.ParseHost(["--runs-per-variant", "2"]));
    }

    [Fact]
    public void ParseHost_AcceptsHostEvalFiles()
    {
        var options = EvalCli.ParseHost(
            ["evals/smoke.eval.cs", "--provider", "fixture", "--model", "model"]);

        Assert.Equal(["evals/smoke.eval.cs"], options.Files);
        Assert.Equal("fixture", options.Provider);
        Assert.Equal("model", options.Model);
    }
}
