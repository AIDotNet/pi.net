using System.Text.Json;
using System.Text.RegularExpressions;

namespace Pi.Evals;

public static class EvalScenarioCatalog
{
    private static readonly DiscoveredEvalCase[] DocumentationCases =
    [
        Case(
            "evals/models.docs.eval.cs",
            "Add model to existing provider",
            "adds the model without replacing existing models"),
        Case(
            "evals/extensions.docs.eval.cs",
            "Create and use a tool extension",
            "creates, reloads, and invokes the extension"),
        Case(
            "evals/custom-provider.docs.eval.cs",
            "Add custom streaming provider",
            "implements a provider from an API fixture"),
        Case(
            "evals/openai-provider.docs.eval.cs",
            "Add OpenAI-compatible provider",
            "configures a provider that works through Pi"),
        Case(
            "evals/tui.docs.eval.cs",
            "Customize the interactive context footer",
            "replaces numeric context usage with a progress bar"),
    ];

    public static IReadOnlyList<DiscoveredEvalCase> GetDocumentationCases(
        IReadOnlyList<string>? files = null,
        string? testNamePattern = null)
    {
        // 与 TS 一致：未指定文件或空列表表示全部用例，而不是零匹配。
        var selectedFiles = files is { Count: > 0 }
            ? files.Select(NormalizeFile).ToHashSet(StringComparer.Ordinal)
            : null;
        var pattern = string.IsNullOrWhiteSpace(testNamePattern)
            ? null
            : new Regex(testNamePattern, RegexOptions.CultureInvariant);

        return DocumentationCases
            .Where(evalCase => selectedFiles is null || selectedFiles.Contains(evalCase.File))
            .Where(evalCase => pattern is null || pattern.IsMatch(evalCase.TestName))
            .Select(evalCase => evalCase with { })
            .ToArray();
    }

    public static IReadOnlyList<DiscoveredEvalCase> ParseDiscovery(JsonElement value)
        => EvalPlan.ParseDiscoveredCases(value);

    public static string NormalizeFile(string file)
    {
        var normalized = file.Replace('\\', '/');
        while (normalized.StartsWith("./", StringComparison.Ordinal))
        {
            normalized = normalized[2..];
        }

        if (normalized.StartsWith("/", StringComparison.Ordinal)
            || normalized.Split('/').Any(segment => segment is "" or "." or ".."))
        {
            throw new ArgumentException($"Eval file must be a normalized relative path: {file}", nameof(file));
        }

        return normalized;
    }

    public static EvalTaskReport FailedTaskReport(EvalTask task, Exception error)
        => new()
        {
            Assertions =
            [
                new EvalTaskAssertion
                {
                    FullName = AssertionFullName(task),
                    Status = "failed",
                },
            ],
            Cases =
            [
                new EvalTaskCaseResult
                {
                    FullName = AssertionFullName(task),
                    Status = "failed",
                    HarnessRun = new EvalTaskHarnessRun
                    {
                        Provider = ModelProvider(task.Model),
                        Model = ModelId(task.Model),
                        Errors = [error.ToString()],
                    },
                },
            ],
        };

    public static EvalTaskReport CreateTaskReport(
        EvalTask task,
        EvalHarnessResult harness,
        double? score)
    {
        if (score is { } value && (!double.IsFinite(value) || value < 0 || value > 1))
        {
            throw new ArgumentOutOfRangeException(nameof(score), "Eval score must be between 0 and 1.");
        }

        var status = harness.Errors.Count == 0 ? "passed" : "failed";
        var fullName = AssertionFullName(task);
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
                    Output = harness.Output,
                    HarnessRun = new EvalTaskHarnessRun
                    {
                        Provider = harness.Provider,
                        Model = harness.Model,
                        Metrics = harness.Metrics,
                        Errors = harness.Errors,
                        Events = harness.Events,
                        Metadata = harness.Metadata,
                        SessionJsonl = harness.SessionJsonl,
                    },
                },
            ],
        };
    }

    public static async Task<EvalTaskReport> RunDocumentationTaskAsync(
        EvalTask task,
        CancellationToken cancellationToken = default)
    {
        try
        {
            return task.File switch
            {
                "evals/models.docs.eval.cs" => await ModelConfigEval.RunAsync(task, cancellationToken)
                    .ConfigureAwait(false),
                "evals/extensions.docs.eval.cs" => await ExtensionCreationEval.RunAsync(task, cancellationToken)
                    .ConfigureAwait(false),
                "evals/custom-provider.docs.eval.cs" => await CustomProviderEval.RunAsync(task, cancellationToken)
                    .ConfigureAwait(false),
                "evals/openai-provider.docs.eval.cs" => await OpenAiProviderEval.RunAsync(task, cancellationToken)
                    .ConfigureAwait(false),
                "evals/tui.docs.eval.cs" => await TuiFooterEval.RunAsync(task, cancellationToken)
                    .ConfigureAwait(false),
                _ => throw new ArgumentException($"Unknown documentation eval file: {task.File}", nameof(task)),
            };
        }
        catch (Exception error)
        {
            return FailedTaskReport(task, error);
        }
    }

    public static string ModelProvider(string model)
    {
        var separator = model.IndexOf('/');
        return separator > 0 ? model[..separator] : model;
    }

    public static string ModelId(string model)
    {
        var separator = model.IndexOf('/');
        return separator >= 0 ? model[(separator + 1)..] : string.Empty;
    }

    private static DiscoveredEvalCase Case(string file, string evalSet, string caseId)
        => new()
        {
            File = file,
            FullName = $"{evalSet} > {caseId}",
            EvalSet = evalSet,
            CaseId = caseId,
        };

    private static string AssertionFullName(EvalTask task)
        => $"{task.EvalSet} {task.CaseId}";
}
