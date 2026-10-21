using System.Text.Json;

namespace Pi.Evals;

public enum DocumentationVariant
{
    WithoutDocs,
    WithDocs,
}

public sealed record DiscoveredEvalCase
{
    public required string File { get; init; }

    public required string FullName { get; init; }

    public required string EvalSet { get; init; }

    public required string CaseId { get; init; }

    /// <summary>
    /// vitest 风格的完整测试名（套件与用例以空格连接）。上游把 <c>--testNamePattern</c>
    /// 交给 vitest 匹配这个名字，而发现结果里的 <see cref="FullName"/> 用 <c> &gt; </c> 连接。
    /// </summary>
    public string TestName => $"{EvalSet} {CaseId}";
}

public sealed record EvalTask
{
    public required string File { get; init; }

    public required string FullName { get; init; }

    public required string EvalSet { get; init; }

    public required string CaseId { get; init; }

    public required DocumentationVariant Variant { get; init; }

    public required string Model { get; init; }

    public required long RunNumber { get; init; }
}

public static class EvalPlan
{
    public const long MaxSafeInteger = 9_007_199_254_740_991;

    public static IReadOnlyList<DocumentationVariant> DocumentationVariants { get; } =
        [DocumentationVariant.WithoutDocs, DocumentationVariant.WithDocs];

    public static IReadOnlyList<DiscoveredEvalCase> ParseDiscoveredCases(JsonElement value)
    {
        if (value.ValueKind != JsonValueKind.Array)
        {
            throw new ArgumentException("Discovered eval cases must be an array.", nameof(value));
        }

        var identities = new HashSet<(string EvalSet, string CaseId)>();
        var cases = new List<DiscoveredEvalCase>();
        foreach (var item in value.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object
                || !item.TryGetProperty("name", out var nameValue)
                || nameValue.ValueKind != JsonValueKind.String
                || !item.TryGetProperty("file", out var fileValue)
                || fileValue.ValueKind != JsonValueKind.String)
            {
                throw new ArgumentException("Discovered eval case is invalid.", nameof(value));
            }

            var fullName = nameValue.GetString()!;
            var parts = fullName.Split(" > ", StringSplitOptions.None);
            if (parts.Length != 2 || string.IsNullOrWhiteSpace(parts[0]) || string.IsNullOrWhiteSpace(parts[1]))
            {
                throw new ArgumentException(
                    $"Documentation eval must use \"<eval set> > <case>\": {fullName}",
                    nameof(value));
            }

            if (!identities.Add((parts[0], parts[1])))
            {
                throw new ArgumentException($"Duplicate eval case identity: {fullName}", nameof(value));
            }

            cases.Add(new DiscoveredEvalCase
            {
                File = fileValue.GetString()!,
                FullName = fullName,
                EvalSet = parts[0],
                CaseId = parts[1],
            });
        }

        return cases;
    }

    public static IReadOnlyList<EvalTask> CreateTaskPlan(
        IReadOnlyList<DiscoveredEvalCase> cases,
        string model,
        long runsPerVariant)
    {
        if (!model.Contains('/') || model.StartsWith('/') || model.EndsWith('/'))
        {
            throw new ArgumentException("Model identity must contain a provider and model.", nameof(model));
        }

        if (runsPerVariant < 1 || runsPerVariant > MaxSafeInteger)
        {
            throw new ArgumentOutOfRangeException(
                nameof(runsPerVariant),
                "Runs per variant must be a positive integer.");
        }

        if (cases.Count > 0 && runsPerVariant > Array.MaxLength / (2L * cases.Count))
        {
            throw new ArgumentOutOfRangeException(
                nameof(runsPerVariant),
                "The requested plan exceeds the maximum in-memory task count.");
        }

        var taskCount = (long)cases.Count * 2 * runsPerVariant;
        if (taskCount > Array.MaxLength)
        {
            throw new ArgumentOutOfRangeException(
                nameof(runsPerVariant),
                "The requested plan exceeds the maximum in-memory task count.");
        }

        var tasks = new List<EvalTask>((int)taskCount);
        foreach (var evalCase in cases)
        {
            for (long runNumber = 1; runNumber <= runsPerVariant; runNumber++)
            {
                DocumentationVariant[] variants = runNumber % 2 == 1
                    ? [DocumentationVariant.WithoutDocs, DocumentationVariant.WithDocs]
                    : [DocumentationVariant.WithDocs, DocumentationVariant.WithoutDocs];
                foreach (var variant in variants)
                {
                    tasks.Add(new EvalTask
                    {
                        File = evalCase.File,
                        FullName = evalCase.FullName,
                        EvalSet = evalCase.EvalSet,
                        CaseId = evalCase.CaseId,
                        Variant = variant,
                        Model = model,
                        RunNumber = runNumber,
                    });
                }
            }
        }

        return tasks;
    }
}
