using System.Text.Json;
using Xunit;

namespace Pi.Evals.Tests;

public sealed class PlanTests
{
    [Fact]
    public void ParseDiscoveredCases_DerivesStableIdentityFromVitestNames()
    {
        using var document = JsonDocument.Parse(
            """[{"name":"Add model > adds the model","file":"evals/models.docs.eval.cs"}]""");

        Assert.Equal(
            [
                new DiscoveredEvalCase
                {
                    FullName = "Add model > adds the model",
                    EvalSet = "Add model",
                    CaseId = "adds the model",
                    File = "evals/models.docs.eval.cs",
                },
            ],
            EvalPlan.ParseDiscoveredCases(document.RootElement));
    }

    [Fact]
    public void ParseDiscoveredCases_RejectsAmbiguousAndDuplicateIdentities()
    {
        using var ambiguous = JsonDocument.Parse("""[{"name":"adds the model","file":"model.ts"}]""");
        using var duplicate = JsonDocument.Parse(
            """[{"name":"Add model > adds the model","file":"model.ts"},{"name":"Add model > adds the model","file":"model.ts"}]""");

        var ambiguousError = Assert.Throws<ArgumentException>(
            () => EvalPlan.ParseDiscoveredCases(ambiguous.RootElement));
        var duplicateError = Assert.Throws<ArgumentException>(
            () => EvalPlan.ParseDiscoveredCases(duplicate.RootElement));

        Assert.Contains("<eval set> > <case>", ambiguousError.Message, StringComparison.Ordinal);
        Assert.Contains("Duplicate eval case identity", duplicateError.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void CreateTaskPlan_AlternatesVariantOrderByRunNumber()
    {
        var evalCase = new DiscoveredEvalCase
        {
            FullName = "Add model > adds the model",
            EvalSet = "Add model",
            CaseId = "adds the model",
            File = "evals/models.docs.eval.cs",
        };

        var tasks = EvalPlan.CreateTaskPlan([evalCase], "fixture/model", 2);

        Assert.Equal(4, tasks.Count);
        Assert.Equal(
            [
                (DocumentationVariant.WithoutDocs, 1L),
                (DocumentationVariant.WithDocs, 1L),
                (DocumentationVariant.WithDocs, 2L),
                (DocumentationVariant.WithoutDocs, 2L),
            ],
            tasks.Select(task => (task.Variant, task.RunNumber)));
    }

    [Theory]
    [InlineData("model", 1)]
    [InlineData("/provider/model", 1)]
    [InlineData("provider/", 1)]
    [InlineData("provider/model", 0)]
    [InlineData("provider/model", -1)]
    public void CreateTaskPlan_RejectsInvalidModelIdentitiesAndRepetitions(string model, long runsPerVariant)
    {
        var evalCase = new DiscoveredEvalCase
        {
            FullName = "Eval > case",
            EvalSet = "Eval",
            CaseId = "case",
            File = "evals/example.docs.eval.cs",
        };

        Assert.ThrowsAny<ArgumentException>(() => EvalPlan.CreateTaskPlan([evalCase], model, runsPerVariant));
    }

    [Fact]
    public void CreateTaskPlan_RejectsPlansThatCannotFitInMemory()
    {
        var evalCase = new DiscoveredEvalCase
        {
            FullName = "Eval > case",
            EvalSet = "Eval",
            CaseId = "case",
            File = "evals/example.docs.eval.ts",
        };

        Assert.Throws<ArgumentOutOfRangeException>(
            () => EvalPlan.CreateTaskPlan([evalCase], "provider/model", EvalPlan.MaxSafeInteger));
    }
}
