using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Encodings.Web;
using Pi.CodingAgent.Core;

namespace Pi.Evals;

internal static class DocumentationEvalRunner
{
    private static readonly JsonSerializerOptions IdentityJsonOptions = new()
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    public static async Task<int> RunAsync(
        EvalCliOptions options,
        string repositoryRoot,
        CancellationToken cancellationToken = default)
    {
        var selection = EvalCli.RequireModel(options);
        var modelIdentity = $"{selection.Provider}/{selection.Id}";
        var authPath = EvalDockerRunner.RequireEvalAuthFile(selection.Provider);
        var catalogCases = EvalScenarioCatalog.GetDocumentationCases(options.Files, options.TestNamePattern);
        if (catalogCases.Count == 0)
        {
            throw new InvalidOperationException("No documentation eval cases matched the selection.");
        }

        var files = (options.Files.Count > 0
                ? options.Files
                : EvalScenarioCatalog.GetDocumentationCases()
                    .Select(evalCase => evalCase.File)
                    .Distinct(StringComparer.Ordinal)
                    .ToArray())
            .OrderBy(file => file, StringComparer.Ordinal)
            .ToArray();
        var artifactDirectory = CreateArtifactDirectory(repositoryRoot);
        var images = await EvalDockerRunner.BuildImagesAsync(repositoryRoot, cancellationToken)
            .ConfigureAwait(false);
        var docker = new EvalDockerContext(
            repositoryRoot,
            images,
            authPath,
            selection.Provider,
            selection.Id,
            options.RunsPerVariant);

        var discoveries = new List<IReadOnlyList<DiscoveredEvalCase>>(EvalPlan.DocumentationVariants.Count);
        foreach (var variant in EvalPlan.DocumentationVariants)
        {
            var directory = Path.Combine(artifactDirectory, "discovery", VariantText(variant));
            var arguments = new List<string> { "--discover", "--project", "docs" };
            arguments.AddRange(files);
            AddNamePattern(arguments, options.TestNamePattern);
            var exitCode = await EvalDockerRunner.RunContainerAsync(
                docker,
                variant,
                directory,
                arguments,
                cancellationToken).ConfigureAwait(false);
            if (exitCode != 0)
            {
                throw new InvalidOperationException($"{VariantText(variant)} eval discovery failed with exit code {exitCode}.");
            }

            var discoveryPath = Path.Combine(directory, "discovered-tests.json");
            EnsureRegularFile(discoveryPath);
            using var discoveryDocument = JsonDocument.Parse(
                await File.ReadAllTextAsync(discoveryPath, cancellationToken).ConfigureAwait(false));
            discoveries.Add(ValidateDiscovery(
                EvalScenarioCatalog.ParseDiscovery(discoveryDocument.RootElement),
                files));
        }

        CompareDiscovery(discoveries[0], discoveries[1]);
        var cases = discoveries[0];
        if (cases.Count == 0)
        {
            throw new InvalidOperationException("No documentation eval cases matched the selection.");
        }

        var tasks = EvalPlan.CreateTaskPlan(cases, modelIdentity, options.RunsPerVariant);
        var protocol = CreateProtocol(selection, options.RunsPerVariant, images, files, cases, tasks);
        var protocolText = JsonSerializer.Serialize(protocol, EvalJson.Options);
        var protocolDigest = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(protocolText)));
        await WriteJsonAsync(
            Path.Combine(artifactDirectory, "protocol.json"),
            new
            {
                protocol.SchemaVersion,
                protocol.Model,
                protocol.RunsPerVariant,
                protocol.Images,
                protocol.Files,
                protocol.Cases,
                protocol.Tasks,
                ProtocolDigest = protocolDigest,
            },
            cancellationToken).ConfigureAwait(false);
        await WriteJsonAsync(
            Path.Combine(artifactDirectory, "expected-runs.json"),
            tasks,
            cancellationToken).ConfigureAwait(false);

        var observations = new List<EvalObservation>(tasks.Count);
        for (var index = 0; index < tasks.Count; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var task = tasks[index];
            Console.WriteLine(
                $"\n[{index + 1}/{tasks.Count}] {task.EvalSet} > {task.CaseId} | "
                + $"{VariantText(task.Variant)} | {task.Model} | run {task.RunNumber}");

            var taskDirectory = Path.Combine(artifactDirectory, "tasks", TaskDirectoryName(task));
            var taskArguments = new List<string> { "--project", "docs", task.File };
            var exactName = $"{task.EvalSet} {task.CaseId}";
            AddNamePattern(taskArguments, $"^{RegexEscape(exactName)}$");
            var exitCode = await EvalDockerRunner.RunContainerAsync(
                docker,
                task.Variant,
                taskDirectory,
                taskArguments,
                cancellationToken).ConfigureAwait(false);

            var reportPath = Path.Combine(taskDirectory, "vitest.json");
            EvalObservation observation;
            if (File.Exists(reportPath))
            {
                EnsureRegularFile(reportPath);
                var taskReport = JsonSerializer.Deserialize<EvalTaskReport>(
                    await File.ReadAllTextAsync(reportPath, cancellationToken).ConfigureAwait(false),
                    EvalJson.Options)
                    ?? throw new InvalidDataException($"Task report is empty: {reportPath}");
                observation = await EvalReport.ReadTaskObservationAsync(
                    task,
                    taskReport,
                    artifactDirectory,
                    cancellationToken).ConfigureAwait(false);
            }
            else
            {
                Console.Error.WriteLine(
                    $"  No task report was produced (container exit code {exitCode}).");
                observation = EvalReport.ErroredObservation(task);
            }

            observations.Add(observation);
            await File.WriteAllTextAsync(
                Path.Combine(artifactDirectory, "observations.jsonl"),
                string.Join(
                    Environment.NewLine,
                    observations.Select(item => JsonSerializer.Serialize(item, EvalJson.Options)))
                    + Environment.NewLine,
                cancellationToken).ConfigureAwait(false);
        }

        var expectedRuns = tasks.Select(task => new EvalRunIdentity
        {
            EvalSet = task.EvalSet,
            CaseId = task.CaseId,
            Variant = task.Variant,
            Model = task.Model,
            RunNumber = task.RunNumber,
        }).ToArray();
        var report = EvalReport.SummarizeEvalObservations(protocolDigest, expectedRuns, observations);
        var reportText = EvalReport.FormatEvalComparisonReport(report);
        await WriteJsonAsync(Path.Combine(artifactDirectory, "report.json"), report, cancellationToken)
            .ConfigureAwait(false);
        await File.WriteAllTextAsync(
            Path.Combine(artifactDirectory, "report.txt"),
            reportText + Environment.NewLine,
            cancellationToken).ConfigureAwait(false);

        Console.WriteLine($"\n{reportText}");
        Console.WriteLine($"\nArtifacts: {artifactDirectory}");
        return report.BlockedPairs.Count > 0 ? 1 : 0;
    }

    private static DocumentationEvalProtocol CreateProtocol(
        EvalModelSelection selection,
        long runsPerVariant,
        EvalDockerImages images,
        IReadOnlyList<string> files,
        IReadOnlyList<DiscoveredEvalCase> cases,
        IReadOnlyList<EvalTask> tasks)
        => new()
        {
            Model = $"{selection.Provider}/{selection.Id}",
            RunsPerVariant = runsPerVariant,
            Images = new Dictionary<string, EvalDockerImage>(StringComparer.Ordinal)
            {
                ["without_docs"] = images.WithoutDocs,
                ["with_docs"] = images.WithDocs,
            },
            Files = files,
            Cases = cases.Select(evalCase => new DocumentationEvalProtocolCase(
                evalCase.EvalSet,
                evalCase.CaseId,
                evalCase.File)).ToArray(),
            Tasks = tasks,
        };

    private static IReadOnlyList<DiscoveredEvalCase> ValidateDiscovery(
        IReadOnlyList<DiscoveredEvalCase> cases,
        IReadOnlyList<string> requestedFiles)
    {
        var validated = new List<DiscoveredEvalCase>(cases.Count);
        foreach (var evalCase in cases)
        {
            var file = EvalScenarioCatalog.NormalizeFile(evalCase.File);
            if (!requestedFiles.Contains(file, StringComparer.Ordinal))
            {
                throw new InvalidDataException(
                    $"Container discovery returned an unrequested eval file: {file}");
            }

            var known = EvalScenarioCatalog.GetDocumentationCases([file])
                .SingleOrDefault(candidate =>
                    candidate.FullName == evalCase.FullName
                    && candidate.EvalSet == evalCase.EvalSet
                    && candidate.CaseId == evalCase.CaseId);
            if (known is null)
            {
                throw new InvalidDataException(
                    $"Container discovery returned an unknown eval case: {evalCase.FullName} ({file})");
            }

            validated.Add(known);
        }

        return validated;
    }

    private static void CompareDiscovery(
        IReadOnlyList<DiscoveredEvalCase> withoutDocs,
        IReadOnlyList<DiscoveredEvalCase> withDocs)
    {
        var left = withoutDocs
            .Select(evalCase => (evalCase.FullName, evalCase.File))
            .OrderBy(value => value.FullName, StringComparer.Ordinal)
            .ThenBy(value => value.File, StringComparer.Ordinal)
            .ToArray();
        var right = withDocs
            .Select(evalCase => (evalCase.FullName, evalCase.File))
            .OrderBy(value => value.FullName, StringComparer.Ordinal)
            .ThenBy(value => value.File, StringComparer.Ordinal)
            .ToArray();
        if (!left.SequenceEqual(right))
        {
            throw new InvalidOperationException("Documentation variants discovered different eval cases.");
        }
    }

    private static void AddNamePattern(List<string> arguments, string? pattern)
    {
        if (pattern is null) return;
        arguments.Add("--testNamePattern");
        arguments.Add(pattern);
    }

    private static string RegexEscape(string value)
        => System.Text.RegularExpressions.Regex.Escape(value);

    private static string TaskDirectoryName(EvalTask task)
    {
        var identity = JsonSerializer.Serialize(
            new object?[]
            {
                task.EvalSet,
                task.CaseId,
                VariantText(task.Variant),
                task.Model,
                task.RunNumber,
            },
            IdentityJsonOptions);
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(identity)));
    }

    private static string VariantText(DocumentationVariant variant)
        => variant == DocumentationVariant.WithDocs ? "with_docs" : "without_docs";

    private static string CreateArtifactDirectory(string repositoryRoot)
    {
        var timestamp = DateTimeOffset.UtcNow.ToString("yyyy-MM-ddTHH-mm-ss.fffZ", System.Globalization.CultureInfo.InvariantCulture);
        var directory = Path.Combine(
            repositoryRoot,
            "src",
            "Pi.Evals",
            ".eval",
            $"{timestamp}_{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        return directory;
    }

    private static void EnsureRegularFile(string path)
    {
        if (!File.Exists(path))
        {
            throw new FileNotFoundException("Expected eval artifact does not exist.", path);
        }

        var attributes = File.GetAttributes(path);
        if ((attributes & (FileAttributes.Directory | FileAttributes.ReparsePoint)) != 0)
        {
            throw new InvalidDataException($"Eval artifact must be a regular file: {path}");
        }
    }

    private static async Task WriteJsonAsync<T>(
        string path,
        T value,
        CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(
            path,
            FileMode.CreateNew,
            FileAccess.Write,
            FileShare.Read,
            bufferSize: 16 * 1024,
            FileOptions.Asynchronous);
        await JsonSerializer.SerializeAsync(stream, value, EvalJson.Options, cancellationToken).ConfigureAwait(false);
        await stream.WriteAsync(Encoding.UTF8.GetBytes(Environment.NewLine), cancellationToken).ConfigureAwait(false);
    }

    private sealed record DocumentationEvalProtocol
    {
        public int SchemaVersion { get; init; } = 1;

        public required string Model { get; init; }

        public required long RunsPerVariant { get; init; }

        public required IReadOnlyDictionary<string, EvalDockerImage> Images { get; init; }

        public required IReadOnlyList<string> Files { get; init; }

        public required IReadOnlyList<DocumentationEvalProtocolCase> Cases { get; init; }

        public required IReadOnlyList<EvalTask> Tasks { get; init; }
    }

    private sealed record DocumentationEvalProtocolCase(string EvalSet, string CaseId, string File);
}
