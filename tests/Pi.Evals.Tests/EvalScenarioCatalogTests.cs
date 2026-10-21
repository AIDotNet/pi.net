using System.Text.Json;
using Xunit;

namespace Pi.Evals.Tests;

public sealed class EvalScenarioCatalogTests
{
    [Fact]
    public void GetDocumentationCases_ListsEveryScenarioWithStableDiscoveryNames()
    {
        var cases = EvalScenarioCatalog.GetDocumentationCases();

        Assert.Equal(5, cases.Count);
        Assert.All(cases, evalCase =>
        {
            Assert.StartsWith("evals/", evalCase.File, StringComparison.Ordinal);
            Assert.EndsWith(".docs.eval.cs", evalCase.File, StringComparison.Ordinal);
            Assert.Equal($"{evalCase.EvalSet} > {evalCase.CaseId}", evalCase.FullName);
        });
    }

    [Fact]
    public void GetDocumentationCases_TreatsAnEmptyFileListAsNoFilter()
    {
        Assert.Equal(5, EvalScenarioCatalog.GetDocumentationCases([]).Count);
    }

    [Fact]
    public void GetDocumentationCases_AppliesFileAndNameFilters()
    {
        var cases = EvalScenarioCatalog.GetDocumentationCases(
            [@"evals\extensions.docs.eval.cs"],
            "Create and use");

        var selected = Assert.Single(cases);
        Assert.Equal("evals/extensions.docs.eval.cs", selected.File);
        Assert.Equal("Create and use a tool extension > creates, reloads, and invokes the extension", selected.FullName);
    }

    [Fact]
    public void GetDocumentationCases_MatchesTheVitestStyleFullName()
    {
        var cases = EvalScenarioCatalog.GetDocumentationCases(
            ["evals/models.docs.eval.cs"],
            "^Add model to existing provider adds the model without replacing existing models$");

        var selected = Assert.Single(cases);
        Assert.Equal(
            "Add model to existing provider adds the model without replacing existing models",
            selected.TestName);
        Assert.Equal(
            "Add model to existing provider > adds the model without replacing existing models",
            selected.FullName);
    }

    [Fact]
    public void GetDocumentationCases_ReturnsNoCasesForAnUnknownFile()
    {
        Assert.Empty(EvalScenarioCatalog.GetDocumentationCases(["evals/unknown.docs.eval.cs"]));
    }

    [Fact]
    public void CreateTaskReport_UsesNativeReporterAssertionName()
    {
        var task = TaskFixture();
        var report = EvalScenarioCatalog.CreateTaskReport(
            task,
            new EvalHarnessResult
            {
                Provider = "fixture",
                Model = "model",
                Output = JsonSerializer.SerializeToElement<object?>(null, EvalJson.Options),
            },
            1);

        var assertion = Assert.Single(report.Assertions);
        var result = Assert.Single(report.Cases);
        Assert.Equal("Example workflow handles the case", assertion.FullName);
        Assert.Equal(assertion.FullName, result.FullName);
    }

    [Fact]
    public void FailedTaskReport_UsesNativeReporterAssertionName()
    {
        var report = EvalScenarioCatalog.FailedTaskReport(TaskFixture(), new InvalidOperationException("failure"));

        var assertion = Assert.Single(report.Assertions);
        var result = Assert.Single(report.Cases);
        Assert.Equal("Example workflow handles the case", assertion.FullName);
        Assert.Equal(assertion.FullName, result.FullName);
    }

    [Fact]
    public async Task GeneratedTaskReport_CanBeReadAsScoredObservation()
    {
        var task = TaskFixture();
        var report = EvalScenarioCatalog.CreateTaskReport(
            task,
            new EvalHarnessResult
            {
                Provider = "fixture",
                Model = "model",
                Output = JsonSerializer.SerializeToElement<object?>(null, EvalJson.Options),
            },
            1);
        var artifactDirectory = Path.Combine(Path.GetTempPath(), $"pi-eval-catalog-{Guid.NewGuid():N}");
        Directory.CreateDirectory(artifactDirectory);

        try
        {
            var json = JsonSerializer.Serialize(report, EvalJson.Options);
            var deserialized = JsonSerializer.Deserialize<EvalTaskReport>(json, EvalJson.Options);
            Assert.NotNull(deserialized);

            var observation = await EvalReport.ReadTaskObservationAsync(task, deserialized, artifactDirectory);

            Assert.Equal(EvalOutcome.Scored, observation.Outcome);
            Assert.Equal(1, observation.Score);
        }
        finally
        {
            Directory.Delete(artifactDirectory, recursive: true);
        }
    }

    private static EvalTask TaskFixture() => new()
    {
        File = "evals/example.docs.eval.cs",
        FullName = "Example workflow > handles the case",
        EvalSet = "Example workflow",
        CaseId = "handles the case",
        Variant = DocumentationVariant.WithDocs,
        Model = "fixture/model",
        RunNumber = 1,
    };
}
