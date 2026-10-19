using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Xunit;

namespace Pi.Evals.Tests;

public sealed class ReportTests
{
    private const string Session = "{\"type\":\"session\"}\n";

    [Theory]
    [InlineData("skipped", EvalOutcome.Skipped)]
    [InlineData("todo", EvalOutcome.Skipped)]
    [InlineData("disabled", EvalOutcome.Skipped)]
    [InlineData("pending", EvalOutcome.Pending)]
    [InlineData("failed", EvalOutcome.Errored)]
    [InlineData("passed", null)]
    public void ClassifyCaseStatus_MapsVitestStatuses(string status, EvalOutcome? expected)
        => Assert.Equal(expected, EvalReport.ClassifyCaseStatus(status));

    [Theory]
    [InlineData("skipped", EvalOutcome.Skipped)]
    [InlineData("pending", EvalOutcome.Pending)]
    public async Task ReadTaskObservation_PreservesUnrunStatus(string status, EvalOutcome expected)
    {
        var task = TaskFixture();
        var report = new EvalTaskReport
        {
            Assertions = [new EvalTaskAssertion { FullName = "Example workflow handles the case", Status = status }],
        };
        var directory = NewTemporaryDirectory();
        try
        {
            var observation = await EvalReport.ReadTaskObservationAsync(task, report, directory);
            Assert.Equal(expected, observation.Outcome);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task ReadTaskObservation_PreservesMetricsFromFailedPartialRunAndPersistsSession()
    {
        var task = TaskFixture();
        var report = TaskReport("failed", run: HarnessRun(errors: ["Prompt verification failed"]));
        var directory = NewTemporaryDirectory();
        try
        {
            var observation = await EvalReport.ReadTaskObservationAsync(task, report, directory);

            Assert.Equal(
                new EvalObservation
                {
                    EvalSet = task.EvalSet,
                    CaseId = task.CaseId,
                    Variant = task.Variant,
                    Model = task.Model,
                    RunNumber = task.RunNumber,
                    Outcome = EvalOutcome.Errored,
                    InputTokens = 10,
                    OutputTokens = 5,
                    CacheReadTokens = 2,
                    CacheWriteTokens = 3,
                    TotalTokens = 15,
                    ToolCalls = 1,
                    TotalMs = 1234,
                    EstimatedCostUsd = 0.01,
                },
                observation);
            Assert.Equal(Session, await ReadPersistedSessionAsync(directory, task));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task ReadTaskObservation_RejectsPassedReportWithoutHarnessRun()
    {
        var task = TaskFixture();
        var report = TaskReport("passed", run: null);

        var observation = await EvalReport.ReadTaskObservationAsync(task, report, NewTemporaryDirectory());

        Assert.Equal(EvalOutcome.Errored, observation.Outcome);
    }

    [Fact]
    public async Task ReadTaskObservation_ScoresZeroAndPersistsSession()
    {
        var task = TaskFixture();
        var report = TaskReport("passed", score: 0, run: HarnessRun());
        var directory = NewTemporaryDirectory();
        try
        {
            var observation = await EvalReport.ReadTaskObservationAsync(task, report, directory);
            Assert.Equal(EvalOutcome.Scored, observation.Outcome);
            Assert.Equal(0, observation.Score);
            Assert.Equal(Session, await ReadPersistedSessionAsync(directory, task));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task ReadTaskObservation_RecordsUnscoredWhenCompletedWithoutScore()
    {
        var observation = await EvalReport.ReadTaskObservationAsync(
            TaskFixture(),
            TaskReport("passed", score: null, run: HarnessRun()),
            NewTemporaryDirectory());

        Assert.Equal(EvalOutcome.Unscored, observation.Outcome);
        Assert.Null(observation.Score);
    }

    [Fact]
    public async Task ReadTaskObservation_RejectsMismatchedModelAndRunErrors()
    {
        var task = TaskFixture();
        var mismatched = TaskReport("passed", run: HarnessRun(model: "other"));
        var errored = TaskReport("passed", run: HarnessRun(errors: ["boom"]));

        Assert.Equal(
            EvalOutcome.Errored,
            (await EvalReport.ReadTaskObservationAsync(task, mismatched, NewTemporaryDirectory())).Outcome);
        var erroredObservation =
            await EvalReport.ReadTaskObservationAsync(task, errored, NewTemporaryDirectory());
        Assert.Equal(EvalOutcome.Errored, erroredObservation.Outcome);
        Assert.Equal(15, erroredObservation.TotalTokens);
    }

    [Fact]
    public async Task ReadTaskObservation_RejectsInvalidMetricsAndScores()
    {
        var task = TaskFixture();
        var invalidMetrics = TaskReport(
            "passed",
            run: HarnessRun(metrics: new EvalMetrics { InputTokens = double.NaN }));
        var invalidScore = TaskReport("passed", score: 1.1, run: HarnessRun());

        var invalidMetricObservation =
            await EvalReport.ReadTaskObservationAsync(task, invalidMetrics, NewTemporaryDirectory());
        var invalidScoreObservation =
            await EvalReport.ReadTaskObservationAsync(task, invalidScore, NewTemporaryDirectory());

        Assert.Equal(EvalOutcome.Errored, invalidMetricObservation.Outcome);
        Assert.Null(invalidMetricObservation.InputTokens);
        Assert.Equal(EvalOutcome.Errored, invalidScoreObservation.Outcome);
        Assert.Equal(15, invalidScoreObservation.TotalTokens);
    }

    [Fact]
    public void SummarizeEvalObservations_ComputesPairedLiftAndEfficiencyDeltas()
    {
        var expected = ExpectedFor([1, 2]);
        var observations = new[]
        {
            Scored(DocumentationVariant.WithoutDocs, 1, 0, totalTokens: 100, toolCalls: 3, totalMs: 1000),
            Scored(DocumentationVariant.WithDocs, 1, 1, totalTokens: 120, toolCalls: 2, totalMs: 800),
            Scored(DocumentationVariant.WithoutDocs, 2, 1, totalTokens: 200),
            Scored(DocumentationVariant.WithDocs, 2, 1, totalTokens: 180),
        };

        var report = EvalReport.SummarizeEvalObservations("digest", expected, observations);
        var comparison = Assert.Single(report.Comparisons);

        Assert.Equal(2, comparison.TotalPairs);
        Assert.Equal(2, comparison.EligiblePairs);
        Assert.Equal(0.5, comparison.ControlPassRate);
        Assert.Equal(1, comparison.TreatmentPassRate);
        Assert.Equal(0.5, comparison.Lift);
        Assert.Equal(new PairedMetricSummary
        {
            EligiblePairs = 2,
            ControlMean = 150,
            TreatmentMean = 150,
            MeanDelta = 0,
        }, comparison.TotalTokens);
        Assert.Equal(2.5, comparison.ToolCalls.ControlMean);
        Assert.Equal(2, comparison.ToolCalls.TreatmentMean);
        Assert.Equal(-0.5, comparison.ToolCalls.MeanDelta);
    }

    [Fact]
    public void SummarizeEvalObservations_BlocksIncompletePairsAndRetainsOperationalTotals()
    {
        var observations = new[]
        {
            Scored(DocumentationVariant.WithoutDocs, 1, 0),
            Errored(DocumentationVariant.WithDocs, 1, totalTokens: 120),
            Scored(DocumentationVariant.WithoutDocs, 2, 1, totalTokens: 200),
        };

        var report = EvalReport.SummarizeEvalObservations("digest", ExpectedFor([1, 2]), observations);
        var comparison = Assert.Single(report.Comparisons);

        Assert.Equal(0, comparison.EligiblePairs);
        Assert.Equal(2, comparison.BlockedPairs);
        Assert.Null(comparison.Lift);
        Assert.Equal(
            ["with_docs: errored"],
            report.BlockedPairs[0].Reasons);
        Assert.Equal(
            ["with_docs: expected 1 observation, found 0"],
            report.BlockedPairs[1].Reasons);
        Assert.Equal(300, report.OperationalTotals[0].TotalTokens.Total);
        Assert.Equal(2, report.OperationalTotals[0].TotalTokens.AvailableRuns);
    }

    [Fact]
    public void SummarizeEvalObservations_BlocksDuplicatesAndKeepsMissingMetricsDistinctFromZero()
    {
        var withoutTokens = Scored(DocumentationVariant.WithoutDocs, 1, 1) with { TotalTokens = null };
        var report = EvalReport.SummarizeEvalObservations(
            "digest",
            ExpectedFor([1]),
            [
                withoutTokens,
                withoutTokens,
                Scored(DocumentationVariant.WithDocs, 1, 1, totalTokens: 0),
            ]);

        Assert.Equal(
            ["without_docs: expected 1 observation, found 2"],
            report.BlockedPairs[0].Reasons);
        Assert.Equal(new OperationalMetricTotal { AvailableRuns = 0, Total = null },
            report.OperationalTotals[0].TotalTokens);
        Assert.Equal(new OperationalMetricTotal { AvailableRuns = 1, Total = 0 },
            report.OperationalTotals[1].TotalTokens);
    }

    [Fact]
    public void SummarizeEvalObservations_FlagsFlakyAndSaturatedComparisons()
    {
        var observations = new[]
        {
            Scored(DocumentationVariant.WithoutDocs, 1, 0),
            Scored(DocumentationVariant.WithDocs, 1, 1),
            Scored(DocumentationVariant.WithoutDocs, 2, 1),
            Scored(DocumentationVariant.WithDocs, 2, 1),
        };

        var comparison = Assert.Single(
            EvalReport.SummarizeEvalObservations("digest", ExpectedFor([1, 2]), observations).Comparisons);

        Assert.Contains("flaky", comparison.Flags);
        Assert.Contains("treatment-saturated", comparison.Flags);
    }

    [Fact]
    public void FormatEvalComparisonReport_FormatsBlockedComparisonsAndOperationalTotals()
    {
        var report = EvalReport.SummarizeEvalObservations(
            "digest",
            ExpectedFor([1, 2]),
            [Scored(DocumentationVariant.WithoutDocs, 1, 1), Scored(DocumentationVariant.WithDocs, 1, 1)]);

        var formatted = EvalReport.FormatEvalComparisonReport(report);

        Assert.Contains("Documentation Eval Comparisons", formatted, StringComparison.Ordinal);
        Assert.Contains("Pass rate  withheld because pairs are blocked", formatted, StringComparison.Ordinal);
        Assert.Contains("without_docs: 1 runs", formatted, StringComparison.Ordinal);
    }

    [Fact]
    public void EvalObservationJson_OmitsUndefinedStyleOptionalProperties()
    {
        var observation = Errored(DocumentationVariant.WithoutDocs, 1);
        var json = JsonSerializer.Serialize(observation, EvalJson.Options);

        Assert.DoesNotContain("\"score\"", json, StringComparison.Ordinal);
        Assert.DoesNotContain("\"inputTokens\"", json, StringComparison.Ordinal);
        Assert.Contains("\"variant\":\"without_docs\"", json, StringComparison.Ordinal);
        Assert.Contains("\"outcome\":\"errored\"", json, StringComparison.Ordinal);
    }

    private static EvalTask TaskFixture() => new()
    {
        File = "evals/example.docs.eval.cs",
        FullName = "Example workflow > handles the case",
        EvalSet = "Example workflow",
        CaseId = "handles the case",
        Variant = DocumentationVariant.WithoutDocs,
        Model = "fixture/model",
        RunNumber = 1,
    };

    private static EvalTaskReport TaskReport(string status, double? score = 0.5, EvalTaskHarnessRun? run = null)
    {
        const string fullName = "Example workflow handles the case";
        return new EvalTaskReport
        {
            Assertions = [new EvalTaskAssertion { FullName = fullName, Status = status }],
            Cases =
            [
                new EvalTaskCaseResult
                {
                    FullName = fullName,
                    Status = status,
                    Score = score,
                    HarnessRun = run,
                },
            ],
        };
    }

    private static EvalTaskHarnessRun HarnessRun(
        string model = "model",
        IReadOnlyList<string>? errors = null,
        EvalMetrics? metrics = null)
        => new()
        {
            Provider = "fixture",
            Model = model,
            Errors = errors ?? [],
            Metrics = metrics ?? new EvalMetrics
            {
                InputTokens = 10,
                OutputTokens = 5,
                CacheReadTokens = 2,
                CacheWriteTokens = 3,
                TotalTokens = 15,
                ToolCalls = 1,
                TotalMs = 1234,
                EstimatedCostUsd = 0.01,
            },
            SessionJsonl = Session,
        };

    private static string NewTemporaryDirectory()
    {
        var path = Path.Combine(Path.GetTempPath(), $"pi-eval-report-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(path);
        return path;
    }

    private static async Task<string> ReadPersistedSessionAsync(string directory, EvalTask task)
    {
        var identity = JsonSerializer.Serialize(
            new object?[] { task.EvalSet, task.CaseId, "without_docs", task.Model, task.RunNumber },
            new JsonSerializerOptions { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping });
        var hash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(identity)));
        return await File.ReadAllTextAsync(
            Path.Combine(directory, "without_docs", "sessions", hash, "session.jsonl"));
    }

    private static IReadOnlyList<EvalRunIdentity> ExpectedFor(IReadOnlyList<long> runNumbers)
        => runNumbers
            .SelectMany(runNumber => EvalPlan.DocumentationVariants.Select(variant => new EvalRunIdentity
            {
                EvalSet = "tool access",
                CaseId = "create",
                Model = "fixture/model",
                RunNumber = runNumber,
                Variant = variant,
            }))
            .ToList();

    private static EvalObservation Scored(
        DocumentationVariant variant,
        long runNumber,
        double score,
        double? totalTokens = 100,
        double? toolCalls = 2,
        double? totalMs = 1000)
        => new()
        {
            EvalSet = "tool access",
            CaseId = "create",
            Variant = variant,
            Model = "fixture/model",
            RunNumber = runNumber,
            Outcome = EvalOutcome.Scored,
            Score = score,
            InputTokens = totalTokens,
            OutputTokens = totalTokens,
            TotalTokens = totalTokens,
            ToolCalls = toolCalls,
            TotalMs = totalMs,
            EstimatedCostUsd = 0.01,
        };

    private static EvalObservation Errored(
        DocumentationVariant variant,
        long runNumber,
        double? totalTokens = null)
        => new()
        {
            EvalSet = "tool access",
            CaseId = "create",
            Variant = variant,
            Model = "fixture/model",
            RunNumber = runNumber,
            Outcome = EvalOutcome.Errored,
            TotalTokens = totalTokens,
        };
}
