using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Pi.Evals;

public enum EvalOutcome
{
    Scored,
    Unscored,
    Skipped,
    Pending,
    Errored,
}

public record EvalRunIdentity
{
    public required string EvalSet { get; init; }

    public required string CaseId { get; init; }

    public required DocumentationVariant Variant { get; init; }

    public required string Model { get; init; }

    public required long RunNumber { get; init; }
}

public sealed record EvalMetrics
{
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public double? InputTokens { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public double? OutputTokens { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public double? CacheReadTokens { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public double? CacheWriteTokens { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public double? TotalTokens { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public double? ToolCalls { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public double? TotalMs { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public double? EstimatedCostUsd { get; init; }
}

public sealed record EvalObservation : EvalRunIdentity
{
    public required EvalOutcome Outcome { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public double? Score { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public double? InputTokens { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public double? OutputTokens { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public double? CacheReadTokens { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public double? CacheWriteTokens { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public double? TotalTokens { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public double? ToolCalls { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public double? TotalMs { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public double? EstimatedCostUsd { get; init; }
}

public sealed record EvalTaskAssertion
{
    public required string FullName { get; init; }

    public required string Status { get; init; }
}

public sealed record EvalTaskCaseResult
{
    public required string FullName { get; init; }

    public required string Status { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public double? Score { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public JsonElement? Output { get; init; }

    public EvalTaskHarnessRun? HarnessRun { get; init; }
}

public sealed record EvalTaskHarnessRun
{
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Provider { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Model { get; init; }

    public EvalMetrics Metrics { get; init; } = new();

    public IReadOnlyList<string> Errors { get; init; } = [];

    public IReadOnlyList<EvalTranscriptEvent> Events { get; init; } = [];

    public IReadOnlyDictionary<string, string> Metadata { get; init; } =
        new Dictionary<string, string>(StringComparer.Ordinal);

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? SessionJsonl { get; init; }
}

public sealed record EvalTaskReport
{
    public IReadOnlyList<EvalTaskAssertion> Assertions { get; init; } = [];

    public IReadOnlyList<EvalTaskCaseResult> Cases { get; init; } = [];
}

public sealed record PairedMetricSummary
{
    public required int EligiblePairs { get; init; }

    public double? ControlMean { get; init; }

    public double? TreatmentMean { get; init; }

    public double? MeanDelta { get; init; }
}

public sealed record EvalSetComparison
{
    public required string EvalSet { get; init; }

    public required int TotalPairs { get; init; }

    public required int EligiblePairs { get; init; }

    public required int BlockedPairs { get; init; }

    public double? ControlPassRate { get; init; }

    public double? TreatmentPassRate { get; init; }

    public double? Lift { get; init; }

    public IReadOnlyList<string> Flags { get; init; } = [];

    public required PairedMetricSummary TotalTokens { get; init; }

    public required PairedMetricSummary ToolCalls { get; init; }

    public required PairedMetricSummary TotalMs { get; init; }

    public required PairedMetricSummary EstimatedCostUsd { get; init; }
}

public sealed record BlockedPair
{
    public required string EvalSet { get; init; }

    public required string CaseId { get; init; }

    public required string Model { get; init; }

    public required long RunNumber { get; init; }

    public IReadOnlyList<string> Reasons { get; init; } = [];
}

public sealed record OperationalMetricTotal
{
    public required int AvailableRuns { get; init; }

    public double? Total { get; init; }
}

public sealed record VariantTotals
{
    public required DocumentationVariant Variant { get; init; }

    public required int Runs { get; init; }

    public required OperationalMetricTotal InputTokens { get; init; }

    public required OperationalMetricTotal OutputTokens { get; init; }

    public required OperationalMetricTotal CacheReadTokens { get; init; }

    public required OperationalMetricTotal CacheWriteTokens { get; init; }

    public required OperationalMetricTotal TotalTokens { get; init; }

    public required OperationalMetricTotal ToolCalls { get; init; }

    public required OperationalMetricTotal TotalMs { get; init; }

    public required OperationalMetricTotal EstimatedCostUsd { get; init; }
}

public sealed record EvalComparisonReport
{
    public int SchemaVersion { get; init; } = 3;

    public required string ProtocolDigest { get; init; }

    public DocumentationVariant Control { get; init; } = DocumentationVariant.WithoutDocs;

    public DocumentationVariant Treatment { get; init; } = DocumentationVariant.WithDocs;

    public IReadOnlyList<EvalSetComparison> Comparisons { get; init; } = [];

    public IReadOnlyList<BlockedPair> BlockedPairs { get; init; } = [];

    public IReadOnlyList<VariantTotals> OperationalTotals { get; init; } = [];
}

public static class EvalReport
{
    public const string PiSessionSnapshotArtifact = "piSessionJsonl";

    private static readonly JsonSerializerOptions IdentityJsonOptions = new()
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private static readonly DocumentationVariant[] Variants =
        [DocumentationVariant.WithoutDocs, DocumentationVariant.WithDocs];

    private readonly record struct PairIdentity(string EvalSet, string CaseId, string Model, long RunNumber);

    private sealed class PairGroup
    {
        public required PairIdentity Identity { get; init; }

        public Dictionary<DocumentationVariant, int> Expected { get; } = [];

        public Dictionary<DocumentationVariant, List<EvalObservation>> Observations { get; } = [];
    }

    private sealed record ScoredPair(EvalObservation Control, EvalObservation Treatment);

    public static EvalOutcome? ClassifyCaseStatus(string status)
    {
        if (status == "failed") return EvalOutcome.Errored;
        if (status is "skipped" or "todo" or "disabled") return EvalOutcome.Skipped;
        if (status == "pending") return EvalOutcome.Pending;
        return null;
    }

    public static EvalObservation ErroredObservation(EvalTask task)
        => Identity(task) with { Outcome = EvalOutcome.Errored };

    public static async Task<EvalObservation> ReadTaskObservationAsync(
        EvalTask task,
        EvalTaskReport report,
        string artifactDirectory,
        CancellationToken cancellationToken = default)
    {
        var identity = Identity(task);
        if (report.Assertions.Count != 1) return identity with { Outcome = EvalOutcome.Errored };
        var assertion = report.Assertions[0];
        if (assertion.FullName != $"{task.EvalSet} {task.CaseId}")
        {
            return identity with { Outcome = EvalOutcome.Errored };
        }

        var statusOutcome = ClassifyCaseStatus(assertion.Status);
        if (statusOutcome is EvalOutcome.Skipped or EvalOutcome.Pending)
        {
            return identity with { Outcome = statusOutcome.Value };
        }

        if (report.Cases.Count != 1) return identity with { Outcome = EvalOutcome.Errored };
        var caseResult = report.Cases[0];
        if (caseResult.FullName != assertion.FullName || caseResult.Status != assertion.Status)
        {
            return identity with { Outcome = EvalOutcome.Errored };
        }

        var run = caseResult.HarnessRun;
        if (run is null) return identity with { Outcome = EvalOutcome.Errored };
        if (run.SessionJsonl is not null)
        {
            await PersistSessionAsync(task, run.SessionJsonl, artifactDirectory, cancellationToken)
                .ConfigureAwait(false);
        }

        var actualModel = !string.IsNullOrEmpty(run.Provider) && !string.IsNullOrEmpty(run.Model)
            ? $"{run.Provider}/{run.Model}"
            : null;
        if (actualModel != task.Model) return identity with { Outcome = EvalOutcome.Errored };

        EvalMetrics metrics;
        try
        {
            metrics = ValidateMetrics(run.Metrics);
        }
        catch (ArgumentOutOfRangeException)
        {
            return identity with { Outcome = EvalOutcome.Errored };
        }

        if (statusOutcome == EvalOutcome.Errored || run.Errors.Count > 0)
        {
            return WithMetrics(identity, metrics) with { Outcome = EvalOutcome.Errored };
        }

        double? score;
        try
        {
            score = ValidateScore(caseResult.Score);
        }
        catch (ArgumentOutOfRangeException)
        {
            return WithMetrics(identity, metrics) with { Outcome = EvalOutcome.Errored };
        }

        return score is null
            ? WithMetrics(identity, metrics) with { Outcome = EvalOutcome.Unscored }
            : WithMetrics(identity, metrics) with { Outcome = EvalOutcome.Scored, Score = score };
    }

    public static EvalComparisonReport SummarizeEvalObservations(
        string protocolDigest,
        IReadOnlyList<EvalRunIdentity> expectedRuns,
        IReadOnlyList<EvalObservation> observations)
    {
        var groups = GroupPairs(expectedRuns, observations);
        var blockedPairs = new List<BlockedPair>();
        var pairsByEvalSet = new Dictionary<string, List<ScoredPair>>(StringComparer.Ordinal);
        var totalsByEvalSet = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var group in groups)
        {
            totalsByEvalSet[group.Identity.EvalSet] = totalsByEvalSet.GetValueOrDefault(group.Identity.EvalSet) + 1;
            var (pair, blocked) = ResolvePair(group);
            if (blocked is not null) blockedPairs.Add(blocked);
            if (pair is not null)
            {
                if (!pairsByEvalSet.TryGetValue(group.Identity.EvalSet, out var pairs))
                {
                    pairs = [];
                    pairsByEvalSet[group.Identity.EvalSet] = pairs;
                }

                pairs.Add(pair);
            }
        }

        var comparisons = totalsByEvalSet
            .OrderBy(entry => entry.Key, StringComparer.Ordinal)
            .Select(entry =>
            {
                var (evalSet, totalPairs) = entry;
                var pairs = pairsByEvalSet.GetValueOrDefault(evalSet) ?? [];
                var blockedPairCount = totalPairs - pairs.Count;
                var publishHeadline = blockedPairCount == 0 && pairs.Count > 0;
                double? controlPassRate = publishHeadline
                    ? pairs.Count(pair => pair.Control.Score >= 1) / (double)pairs.Count
                    : null;
                double? treatmentPassRate = publishHeadline
                    ? pairs.Count(pair => pair.Treatment.Score >= 1) / (double)pairs.Count
                    : null;
                return new EvalSetComparison
                {
                    EvalSet = evalSet,
                    TotalPairs = totalPairs,
                    EligiblePairs = pairs.Count,
                    BlockedPairs = blockedPairCount,
                    ControlPassRate = controlPassRate,
                    TreatmentPassRate = treatmentPassRate,
                    Lift = controlPassRate is null || treatmentPassRate is null
                        ? null
                        : Difference(treatmentPassRate.Value, controlPassRate.Value),
                    Flags = ComparisonFlags(pairs, controlPassRate, treatmentPassRate),
                    TotalTokens = SummarizeMetric(pairs, observation => observation.TotalTokens),
                    ToolCalls = SummarizeMetric(pairs, observation => observation.ToolCalls),
                    TotalMs = SummarizeMetric(pairs, observation => observation.TotalMs),
                    EstimatedCostUsd = SummarizeMetric(pairs, observation => observation.EstimatedCostUsd),
                };
            })
            .ToList();

        return new EvalComparisonReport
        {
            ProtocolDigest = protocolDigest,
            Comparisons = comparisons,
            BlockedPairs = blockedPairs,
            OperationalTotals =
            [
                VariantTotalsFor(observations, DocumentationVariant.WithoutDocs),
                VariantTotalsFor(observations, DocumentationVariant.WithDocs),
            ],
        };
    }

    public static string FormatEvalComparisonReport(EvalComparisonReport report)
    {
        if (report.Comparisons.Count == 0) return string.Empty;
        var lines = new List<string> { "Documentation Eval Comparisons" };
        foreach (var comparison in report.Comparisons)
        {
            lines.Add($"  {comparison.EvalSet}");
            lines.Add($"         Pairs  {comparison.EligiblePairs}/{comparison.TotalPairs} eligible");
            if (comparison.Lift is null)
            {
                lines.Add(comparison.BlockedPairs > 0
                    ? "     Pass rate  withheld because pairs are blocked"
                    : "     Pass rate  unavailable");
            }
            else
            {
                lines.Add(
                    $"     Pass rate  {Signed(comparison.Lift.Value * 100, 1)} pp (with {Percentage(comparison.TreatmentPassRate)}, without {Percentage(comparison.ControlPassRate)})");
            }

            if (comparison.Flags.Count > 0) lines.Add($"         Flags  {string.Join(", ", comparison.Flags)}");
            lines.Add(PairedMetric("Tokens", comparison.TotalTokens));
            lines.Add(PairedMetric("Tools", comparison.ToolCalls));
            lines.Add(PairedMetric("Latency", comparison.TotalMs, "ms"));
            var cost = comparison.EstimatedCostUsd;
            if (cost.MeanDelta is null || cost.ControlMean is null || cost.TreatmentMean is null)
            {
                lines.Add("     Est. cost  unavailable");
            }
            else
            {
                var sign = cost.MeanDelta.Value >= 0 ? "+" : "-";
                lines.Add(
                    $"     Est. cost  {sign}${Math.Abs(cost.MeanDelta.Value).ToString("F4", CultureInfo.InvariantCulture)} (with ${cost.TreatmentMean.Value.ToString("F4", CultureInfo.InvariantCulture)}, without ${cost.ControlMean.Value.ToString("F4", CultureInfo.InvariantCulture)}, {cost.EligiblePairs} pairs)");
            }
        }

        lines.Add("  Operational totals");
        foreach (var totals in report.OperationalTotals)
        {
            var tokens = OperationalMetric(totals.TotalTokens, totals.Runs,
                total => $"{FormatNumber(total)} tokens");
            var tools = OperationalMetric(totals.ToolCalls, totals.Runs,
                total => $"{FormatNumber(total)} tools");
            var latency = OperationalMetric(totals.TotalMs, totals.Runs,
                total => $"{(total / 1000).ToString("F2", CultureInfo.InvariantCulture)}s");
            var cost = OperationalMetric(totals.EstimatedCostUsd, totals.Runs,
                total => $"${total.ToString("F4", CultureInfo.InvariantCulture)} cost");
            lines.Add(
                $"    {VariantText(totals.Variant)}: {totals.Runs} runs, {tokens}, {tools}, {latency}, {cost}");
        }

        if (report.BlockedPairs.Count > 0)
        {
            lines.Add("  Blocked pairs");
            foreach (var blocked in report.BlockedPairs)
            {
                lines.Add(
                    $"    {blocked.EvalSet}/{blocked.CaseId}/{blocked.Model}/run-{blocked.RunNumber}: {string.Join("; ", blocked.Reasons)}");
            }
        }

        return string.Join('\n', lines);
    }

    public static string VariantText(DocumentationVariant variant) => variant switch
    {
        DocumentationVariant.WithoutDocs => "without_docs",
        DocumentationVariant.WithDocs => "with_docs",
        _ => throw new ArgumentOutOfRangeException(nameof(variant)),
    };

    private static EvalObservation Identity(EvalTask task) => new()
    {
        EvalSet = task.EvalSet,
        CaseId = task.CaseId,
        Variant = task.Variant,
        Model = task.Model,
        RunNumber = task.RunNumber,
        Outcome = EvalOutcome.Errored,
    };

    private static EvalMetrics ValidateMetrics(EvalMetrics metrics)
    {
        ValidateMetric(metrics.InputTokens, nameof(metrics.InputTokens));
        ValidateMetric(metrics.OutputTokens, nameof(metrics.OutputTokens));
        ValidateMetric(metrics.CacheReadTokens, nameof(metrics.CacheReadTokens));
        ValidateMetric(metrics.CacheWriteTokens, nameof(metrics.CacheWriteTokens));
        ValidateMetric(metrics.TotalTokens, nameof(metrics.TotalTokens));
        ValidateMetric(metrics.ToolCalls, nameof(metrics.ToolCalls));
        ValidateMetric(metrics.TotalMs, nameof(metrics.TotalMs));
        ValidateMetric(metrics.EstimatedCostUsd, nameof(metrics.EstimatedCostUsd));
        return metrics;
    }

    private static void ValidateMetric(double? value, string name)
    {
        if (value is { } metric && (!double.IsFinite(metric) || metric < 0))
        {
            throw new ArgumentOutOfRangeException(name, "Metric must be a finite non-negative number.");
        }
    }

    private static double? ValidateScore(double? value)
    {
        if (value is { } score && (!double.IsFinite(score) || score < 0 || score > 1))
        {
            throw new ArgumentOutOfRangeException(nameof(value), "Eval score must be between 0 and 1.");
        }

        return value;
    }

    private static EvalObservation WithMetrics(EvalRunIdentity identity, EvalMetrics metrics) => new()
    {
        EvalSet = identity.EvalSet,
        CaseId = identity.CaseId,
        Variant = identity.Variant,
        Model = identity.Model,
        RunNumber = identity.RunNumber,
        InputTokens = metrics.InputTokens,
        OutputTokens = metrics.OutputTokens,
        CacheReadTokens = metrics.CacheReadTokens,
        CacheWriteTokens = metrics.CacheWriteTokens,
        TotalTokens = metrics.TotalTokens,
        ToolCalls = metrics.ToolCalls,
        TotalMs = metrics.TotalMs,
        EstimatedCostUsd = metrics.EstimatedCostUsd,
        Outcome = EvalOutcome.Errored,
    };

    private static async Task PersistSessionAsync(
        EvalTask task,
        string session,
        string artifactDirectory,
        CancellationToken cancellationToken)
    {
        var identity = JsonSerializer.Serialize(
            new object?[] { task.EvalSet, task.CaseId, VariantText(task.Variant), task.Model, task.RunNumber },
            IdentityJsonOptions);
        var hash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(identity)));
        var directory = Path.Combine(
            artifactDirectory,
            VariantText(task.Variant),
            "sessions",
            hash);
        Directory.CreateDirectory(directory);
        await File.WriteAllTextAsync(
            Path.Combine(directory, "session.jsonl"),
            session,
            cancellationToken).ConfigureAwait(false);
    }

    private static List<PairGroup> GroupPairs(
        IReadOnlyList<EvalRunIdentity> expectedRuns,
        IReadOnlyList<EvalObservation> observations)
    {
        var groups = new Dictionary<PairIdentity, PairGroup>();
        PairGroup GetGroup(EvalRunIdentity identity)
        {
            var key = new PairIdentity(identity.EvalSet, identity.CaseId, identity.Model, identity.RunNumber);
            if (groups.TryGetValue(key, out var existing)) return existing;
            var group = new PairGroup { Identity = key };
            groups.Add(key, group);
            return group;
        }

        foreach (var expected in expectedRuns)
        {
            var group = GetGroup(expected);
            group.Expected[expected.Variant] = group.Expected.GetValueOrDefault(expected.Variant) + 1;
        }

        foreach (var observation in observations)
        {
            var group = GetGroup(observation);
            if (!group.Observations.TryGetValue(observation.Variant, out var runs))
            {
                runs = [];
                group.Observations[observation.Variant] = runs;
            }

            runs.Add(observation);
        }

        return groups.Values
            .OrderBy(group => group.Identity.EvalSet, StringComparer.Ordinal)
            .ThenBy(group => group.Identity.CaseId, StringComparer.Ordinal)
            .ThenBy(group => group.Identity.Model, StringComparer.Ordinal)
            .ThenBy(group => group.Identity.RunNumber)
            .ToList();
    }

    private static (ScoredPair? Pair, BlockedPair? Blocked) ResolvePair(PairGroup group)
    {
        var reasons = new List<string>();
        foreach (var variant in Variants)
        {
            var expected = group.Expected.GetValueOrDefault(variant);
            var observed = group.Observations.GetValueOrDefault(variant) ?? [];
            if (expected != 1) reasons.Add($"{VariantText(variant)}: design expected 1 run, found {expected}");
            if (observed.Count != expected)
            {
                reasons.Add(
                    $"{VariantText(variant)}: expected {expected} observation{(expected == 1 ? string.Empty : "s")}, found {observed.Count}");
            }

            if (expected == 1 && observed.Count == 1 && observed[0].Outcome != EvalOutcome.Scored)
            {
                reasons.Add($"{VariantText(variant)}: {OutcomeText(observed[0].Outcome)}");
            }
        }

        if (reasons.Count > 0)
        {
            return (null, new BlockedPair
            {
                EvalSet = group.Identity.EvalSet,
                CaseId = group.Identity.CaseId,
                Model = group.Identity.Model,
                RunNumber = group.Identity.RunNumber,
                Reasons = reasons,
            });
        }

        return (
            new ScoredPair(
                group.Observations[DocumentationVariant.WithoutDocs][0],
                group.Observations[DocumentationVariant.WithDocs][0]),
            null);
    }

    private static string OutcomeText(EvalOutcome outcome) => outcome switch
    {
        EvalOutcome.Scored => "scored",
        EvalOutcome.Unscored => "unscored",
        EvalOutcome.Skipped => "skipped",
        EvalOutcome.Pending => "pending",
        EvalOutcome.Errored => "errored",
        _ => throw new ArgumentOutOfRangeException(nameof(outcome)),
    };

    private static PairedMetricSummary SummarizeMetric(
        IReadOnlyList<ScoredPair> pairs,
        Func<EvalObservation, double?> select)
    {
        var control = new List<double>();
        var treatment = new List<double>();
        foreach (var pair in pairs)
        {
            var controlValue = select(pair.Control);
            var treatmentValue = select(pair.Treatment);
            if (controlValue is null || treatmentValue is null) continue;
            control.Add(controlValue.Value);
            treatment.Add(treatmentValue.Value);
        }

        var controlMean = Mean(control);
        var treatmentMean = Mean(treatment);
        return new PairedMetricSummary
        {
            EligiblePairs = control.Count,
            ControlMean = controlMean,
            TreatmentMean = treatmentMean,
            MeanDelta = controlMean is null || treatmentMean is null
                ? null
                : Difference(treatmentMean.Value, controlMean.Value),
        };
    }

    private static OperationalMetricTotal OperationalTotal(
        IReadOnlyList<EvalObservation> runs,
        Func<EvalObservation, double?> select)
    {
        var values = runs.Select(select).Where(value => value is not null).Select(value => value!.Value).ToList();
        return new OperationalMetricTotal
        {
            AvailableRuns = values.Count,
            Total = values.Count == 0 ? null : values.Sum(),
        };
    }

    private static VariantTotals VariantTotalsFor(
        IReadOnlyList<EvalObservation> observations,
        DocumentationVariant variant)
    {
        var runs = observations.Where(observation => observation.Variant == variant).ToList();
        return new VariantTotals
        {
            Variant = variant,
            Runs = runs.Count,
            InputTokens = OperationalTotal(runs, observation => observation.InputTokens),
            OutputTokens = OperationalTotal(runs, observation => observation.OutputTokens),
            CacheReadTokens = OperationalTotal(runs, observation => observation.CacheReadTokens),
            CacheWriteTokens = OperationalTotal(runs, observation => observation.CacheWriteTokens),
            TotalTokens = OperationalTotal(runs, observation => observation.TotalTokens),
            ToolCalls = OperationalTotal(runs, observation => observation.ToolCalls),
            TotalMs = OperationalTotal(runs, observation => observation.TotalMs),
            EstimatedCostUsd = OperationalTotal(runs, observation => observation.EstimatedCostUsd),
        };
    }

    private static IReadOnlyList<string> ComparisonFlags(
        IReadOnlyList<ScoredPair> pairs,
        double? controlPassRate,
        double? treatmentPassRate)
    {
        var flags = new List<string>();
        if (controlPassRate is not null && treatmentPassRate is not null)
        {
            if (controlPassRate == treatmentPassRate) flags.Add("no-lift");
            if (treatmentPassRate < controlPassRate) flags.Add("negative-delta");
            if (controlPassRate == 1) flags.Add("control-saturated");
            if (treatmentPassRate == 1) flags.Add("treatment-saturated");
        }

        var outcomes = new Dictionary<(string CaseId, DocumentationVariant Variant), HashSet<bool>>();
        foreach (var pair in pairs)
        {
            AddOutcome(pair.Control);
            AddOutcome(pair.Treatment);
        }

        if (outcomes.Values.Any(values => values.Count > 1)) flags.Add("flaky");
        return flags;

        void AddOutcome(EvalObservation observation)
        {
            var key = (observation.CaseId, observation.Variant);
            if (!outcomes.TryGetValue(key, out var values))
            {
                values = [];
                outcomes[key] = values;
            }

            values.Add(observation.Score.GetValueOrDefault() >= 1);
        }
    }

    private static double? Mean(IReadOnlyList<double> values)
        => values.Count == 0 ? null : values.Sum() / values.Count;

    private static double Difference(double treatment, double control)
    {
        var value = treatment - control;
        var text = value.ToString("G15", CultureInfo.InvariantCulture);
        return double.Parse(text, NumberStyles.Float, CultureInfo.InvariantCulture);
    }

    private static string Percentage(double? value)
        => value is null ? "unavailable" : $"{(value.Value * 100).ToString("F1", CultureInfo.InvariantCulture)}%";

    private static string Signed(double value, int digits)
    {
        var formatted = value.ToString($"F{digits}", CultureInfo.InvariantCulture);
        return value >= 0 ? $"+{formatted}" : formatted;
    }

    private static string PairedMetric(string label, PairedMetricSummary metric, string unit = "")
    {
        if (metric.MeanDelta is null || metric.ControlMean is null || metric.TreatmentMean is null)
        {
            return $"    {label.PadLeft(10)}  unavailable";
        }

        return
            $"    {label.PadLeft(10)}  {Signed(metric.MeanDelta.Value, 1)}{unit} (with {metric.TreatmentMean.Value.ToString("F1", CultureInfo.InvariantCulture)}{unit}, without {metric.ControlMean.Value.ToString("F1", CultureInfo.InvariantCulture)}{unit}, {metric.EligiblePairs} pairs)";
    }

    private static string OperationalMetric(
        OperationalMetricTotal metric,
        int runs,
        Func<double, string> format)
    {
        if (metric.Total is null) return $"unavailable (0/{runs} measured)";
        var coverage = metric.AvailableRuns == runs ? string.Empty : $" ({metric.AvailableRuns}/{runs} measured)";
        return $"{format(metric.Total.Value)}{coverage}";
    }

    private static string FormatNumber(double value)
        => value.ToString("G", CultureInfo.InvariantCulture);
}
