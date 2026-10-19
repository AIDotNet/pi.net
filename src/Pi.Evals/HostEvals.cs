using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Pi.Ai.Types;
using Pi.Agent.Types;
using Pi.CodingAgent.Core;
using Pi.CodingAgent.Core.Extensions;
using ToolDefinition = Pi.CodingAgent.Core.Extensions.ToolDefinition;

namespace Pi.Evals;

internal sealed record HostEvalCase(string File, string FullName, string CaseId, string? DocumentationPath);

internal sealed record HostEvalObservation(
    string File,
    string FullName,
    string Provider,
    string Model,
    bool Passed,
    EvalMetrics Metrics,
    IReadOnlyList<string> Errors);

internal static class HostEvalCatalog
{
    private const string SmokeFile = "evals/smoke.eval.cs";
    private const string DocumentationAuditFile = "evals/documentation-audit.eval.cs";

    public static IReadOnlyList<HostEvalCase> GetCases(
        string repositoryRoot,
        IReadOnlyList<string>? files = null,
        string? testNamePattern = null)
    {
        var selectedFiles = files?.ToHashSet(StringComparer.Ordinal);
        var pattern = string.IsNullOrWhiteSpace(testNamePattern)
            ? null
            : new Regex(testNamePattern, RegexOptions.CultureInvariant);
        var cases = new List<HostEvalCase>();

        if (selectedFiles is null || selectedFiles.Contains(SmokeFile))
        {
            cases.Add(new HostEvalCase(
                SmokeFile,
                "Answer a basic prompt > returns the expected answer",
                "returns the expected answer",
                null));
        }

        if (selectedFiles is null || selectedFiles.Contains(DocumentationAuditFile))
        {
            var docsRoot = Path.Combine(repositoryRoot, "src", "Pi.CodingAgent", "Assets", "docs");
            if (!Directory.Exists(docsRoot))
            {
                throw new DirectoryNotFoundException($"Coding-agent documentation directory does not exist: {docsRoot}");
            }

            foreach (var path in Directory.EnumerateFiles(docsRoot, "*.md", SearchOption.AllDirectories)
                         .OrderBy(path => Path.GetRelativePath(docsRoot, path).Replace('\\', '/'), StringComparer.Ordinal))
            {
                var relativePath = Path.GetRelativePath(docsRoot, path).Replace('\\', '/');
                cases.Add(new HostEvalCase(
                    DocumentationAuditFile,
                    $"Audit documentation against implementation > {relativePath} matches the implementation",
                    $"{relativePath} matches the implementation",
                    path));
            }
        }

        if (selectedFiles is not null)
        {
            var supported = new HashSet<string>(StringComparer.Ordinal) { SmokeFile, DocumentationAuditFile };
            var unsupported = selectedFiles.Where(file => !supported.Contains(file)).ToArray();
            if (unsupported.Length > 0)
            {
                throw new ArgumentException(
                    $"Unsupported host eval file: {string.Join(", ", unsupported)}",
                    nameof(files));
            }
        }

        return cases
            .Where(evalCase => pattern is null || pattern.IsMatch(evalCase.FullName))
            .ToArray();
    }
}

internal static class HostEvalRunner
{
    private const string AuditToolName = "submit_documentation_audit";

    public static async Task<int> RunAsync(
        EvalCliOptions options,
        string repositoryRoot,
        CancellationToken cancellationToken = default)
    {
        var model = EvalCli.RequireModel(options);
        var cases = HostEvalCatalog.GetCases(repositoryRoot, options.Files, options.TestNamePattern);
        if (cases.Count == 0)
        {
            throw new InvalidOperationException("No host eval cases matched the selection.");
        }

        var artifactDirectory = CreateArtifactDirectory(repositoryRoot, "host");
        var observations = new List<HostEvalObservation>(cases.Count);
        for (var index = 0; index < cases.Count; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var evalCase = cases[index];
            Console.WriteLine($"[{index + 1}/{cases.Count}] {evalCase.FullName}");

            var result = evalCase.File switch
            {
                "evals/smoke.eval.cs" => await RunSmokeAsync(model, cancellationToken).ConfigureAwait(false),
                "evals/documentation-audit.eval.cs" => await RunDocumentationAuditAsync(
                    model,
                    repositoryRoot,
                    evalCase.DocumentationPath
                        ?? throw new InvalidOperationException("Documentation audit case has no page path."),
                    evalCase.FullName,
                    cancellationToken).ConfigureAwait(false),
                _ => throw new ArgumentException($"Unknown host eval file: {evalCase.File}", nameof(options)),
            };

            var observation = new HostEvalObservation(
                evalCase.File,
                evalCase.FullName,
                result.Provider,
                result.Model,
                result.Passed,
                result.Metrics,
                result.Errors);
            observations.Add(observation);
            Console.WriteLine($"  {(observation.Passed ? "passed" : "failed")}");
            foreach (var error in observation.Errors)
            {
                Console.Error.WriteLine($"  {error}");
            }

            await File.WriteAllLinesAsync(
                Path.Combine(artifactDirectory, "observations.jsonl"),
                observations.Select(observation => JsonSerializer.Serialize(observation, EvalJson.Options)),
                cancellationToken).ConfigureAwait(false);
        }

        Console.WriteLine($"Host eval artifacts: {artifactDirectory}");
        return observations.All(observation => observation.Passed) ? 0 : 1;
    }

    private static async Task<HostEvalObservation> RunSmokeAsync(
        EvalModelSelection model,
        CancellationToken cancellationToken)
    {
        var harness = await EvalHarness.RunAsync(
            [new EvalInputStep.Prompt("What's the capital of France? Respond with only the city name.")],
            new EvalHarnessOptions
            {
                Model = model,
                NoTools = NoToolsMode.All,
            },
            cancellationToken).ConfigureAwait(false);

        var response = harness.Output.ValueKind == JsonValueKind.String
            ? harness.Output.GetString()
            : null;
        var errors = harness.Errors.ToList();
        if (!string.Equals(response?.Trim(), "Paris", StringComparison.Ordinal))
        {
            errors.Add($"Expected the exact answer Paris; received {response ?? "no assistant text"}.");
        }

        if (harness.Provider != model.Provider || harness.Model != model.Id)
        {
            errors.Add($"Harness selected {harness.Provider}/{harness.Model} instead of {model.Provider}/{model.Id}.");
        }

        if (harness.Metrics.TotalTokens is not > 0)
        {
            errors.Add("Smoke eval produced no token usage.");
        }

        return new HostEvalObservation(
            "evals/smoke.eval.cs",
            "Answer a basic prompt > returns the expected answer",
            harness.Provider,
            harness.Model,
            errors.Count == 0,
            harness.Metrics,
            errors);
    }

    private static async Task<HostEvalObservation> RunDocumentationAuditAsync(
        EvalModelSelection model,
        string repositoryRoot,
        string documentationPath,
        string fullName,
        CancellationToken cancellationToken)
    {
        var tool = CreateAuditTool();
        var harness = await EvalHarness.RunAsync(
            [
                new EvalInputStep.Prompt(
                    $"Audit this Pi documentation page against the repository implementation.\n\n"
                    + $"Documentation page: {documentationPath}\n"
                    + $"Repository root: {repositoryRoot}\n\n"
                    + "Read the complete page and the relevant implementation. Report a mismatch only for a "
                    + "clear, user-visible contradiction between an explicit documentation claim and actual "
                    + "runtime behavior. Follow the runtime path; names, comments, types, isolated helpers, and "
                    + "tests are not sufficient evidence by themselves.\n\n"
                    + "Missing internal detail, ambiguous wording, hypothetical misuse, and undocumented edge "
                    + "cases are not mismatches. If the evidence is not decisive, report inconclusive. Otherwise "
                    + "report match.\n\n"
                    + "For a mismatch, quote the claim, cite the implementation path and symbol, and state the "
                    + "concrete behavior a user would observe.\n\n"
                    + $"Call {AuditToolName} exactly once as your final action. Do not return prose."),
            ],
            new EvalHarnessOptions
            {
                Model = model,
                Tools = ["read", "grep", "find", "ls", AuditToolName],
                CustomTools = [tool],
            },
            cancellationToken).ConfigureAwait(false);

        var errors = harness.Errors.ToList();
        var auditFailure = GetAuditFailure(harness.Events);
        if (auditFailure is not null)
        {
            errors.Add(auditFailure);
        }

        return new HostEvalObservation(
            "evals/documentation-audit.eval.cs",
            fullName,
            harness.Provider,
            harness.Model,
            errors.Count == 0,
            harness.Metrics,
            errors);
    }

    private static ToolDefinition CreateAuditTool()
        => new()
        {
            Name = AuditToolName,
            Label = "Submit documentation audit",
            Description = "Submit the final verdict after completing the documentation investigation.",
            PromptSnippet = "Submit the final documentation audit as validated structured data",
            Parameters = new ToolSchema(new Dictionary<string, object?>
            {
                ["type"] = "object",
                ["properties"] = new Dictionary<string, object?>
                {
                    ["verdict"] = new Dictionary<string, object?>
                    {
                        ["type"] = "string",
                        ["enum"] = new[] { "match", "mismatch", "inconclusive" },
                    },
                    ["explanation"] = new Dictionary<string, object?>
                    {
                        ["type"] = "string",
                        ["minLength"] = 1,
                        ["maxLength"] = 2000,
                    },
                },
                ["required"] = new[] { "verdict", "explanation" },
                ["additionalProperties"] = false,
            }),
            ConstrainedSampling = new JsonObject
            {
                ["type"] = "json_schema",
                ["strict"] = "prefer",
            },
            Execute = (_, arguments, _, _, _) =>
            {
                var verdict = GetString(arguments, "verdict");
                var explanation = GetString(arguments, "explanation");
                if (verdict is not ("match" or "mismatch" or "inconclusive")
                    || explanation.Length is < 1 or > 2000)
                {
                    throw new ArgumentException("Documentation audit result does not match its schema.");
                }

                return Task.FromResult(new AgentToolResult(
                    [new TextContent("Documentation audit submitted.")],
                    Details: new { verdict, explanation },
                    Terminate: true));
            },
        };

    internal static string? GetAuditFailure(IReadOnlyList<EvalTranscriptEvent> events)
    {
        var calls = events.Where(@event =>
            @event.Type == "tool_call" && @event.Name == AuditToolName).ToArray();
        if (calls.Length != 1
            || calls[0].Arguments is not { ValueKind: JsonValueKind.Object } arguments
            || !arguments.TryGetProperty("verdict", out var verdictElement)
            || verdictElement.ValueKind != JsonValueKind.String
            || verdictElement.GetString() is not ("match" or "mismatch" or "inconclusive")
            || !arguments.TryGetProperty("explanation", out var explanation)
            || explanation.ValueKind != JsonValueKind.String
            || string.IsNullOrWhiteSpace(explanation.GetString())
            || explanation.GetString()!.Length > 2000)
        {
            return "Documentation audit did not submit exactly one valid final verdict.";
        }

        var results = events.Where(@event =>
            @event.Type == "tool_result" && @event.ToolCallId == calls[0].Id).ToArray();
        if (results.Length != 1 || results[0].Error is not null)
        {
            return "Documentation audit submission did not complete successfully.";
        }

        var verdict = verdictElement.GetString()!;
        return verdict == "match"
            ? null
            : $"Documentation audit verdict was '{verdict}': {explanation.GetString()}";
    }

    private static string GetString(IReadOnlyDictionary<string, object?> arguments, string name)
    {
        if (!arguments.TryGetValue(name, out var value))
        {
            throw new ArgumentException($"Missing required argument: {name}");
        }

        return value switch
        {
            string text => text,
            JsonElement { ValueKind: JsonValueKind.String } element => element.GetString()!,
            _ => throw new ArgumentException($"Argument {name} must be a string."),
        };
    }

    private static string CreateArtifactDirectory(string repositoryRoot, string kind)
    {
        var directory = Path.Combine(
            repositoryRoot,
            "src",
            "Pi.Evals",
            ".eval",
            $"{kind}_{DateTimeOffset.UtcNow:yyyy-MM-ddTHH-mm-ss}_{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        return directory;
    }
}
