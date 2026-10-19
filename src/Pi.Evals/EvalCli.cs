using System.Globalization;

namespace Pi.Evals;

internal sealed record EvalCliOptions
{
    public string? Provider { get; init; }

    public string? Model { get; init; }

    public long RunsPerVariant { get; init; } = 1;

    public IReadOnlyList<string> Files { get; init; } = [];

    public string? TestNamePattern { get; init; }
}

internal static class EvalCli
{
    private static readonly HashSet<string> ModelOptions =
        new(StringComparer.Ordinal) { "--provider", "--model" };

    public static EvalCliOptions ParseDocumentation(
        IReadOnlyList<string> args,
        IReadOnlyDictionary<string, string?>? environment = null)
        => Parse(args, documentation: true, environment);

    public static EvalCliOptions ParseHost(
        IReadOnlyList<string> args,
        IReadOnlyDictionary<string, string?>? environment = null)
        => Parse(args, documentation: false, environment);

    private static EvalCliOptions Parse(
        IReadOnlyList<string> args,
        bool documentation,
        IReadOnlyDictionary<string, string?>? environment)
    {
        environment ??= EvalHarness.CurrentEnvironment();
        string? provider = null;
        string? model = null;
        string? runsText = null;
        string? testNamePattern = null;
        var cliSelectedModel = false;
        var files = new List<string>();

        for (var index = 0; index < args.Count; index++)
        {
            var argument = args[index];
            var suffix = documentation ? ".docs.eval.cs" : ".eval.cs";
            if (argument.EndsWith(suffix, StringComparison.Ordinal))
            {
                if (!documentation && argument.EndsWith(".docs.eval.cs", StringComparison.Ordinal))
                {
                    throw new ArgumentException($"Documentation eval is not a host eval: {argument}");
                }

                files.Add(EvalScenarioCatalog.NormalizeFile(argument));
                continue;
            }

            if (argument is "-t" or "--testNamePattern")
            {
                testNamePattern = RequiredValue(args, ref index, argument);
                continue;
            }

            if (argument.StartsWith("--testNamePattern=", StringComparison.Ordinal))
            {
                testNamePattern = argument["--testNamePattern=".Length..];
                if (string.IsNullOrWhiteSpace(testNamePattern))
                {
                    throw new ArgumentException("Missing value for --testNamePattern.");
                }

                continue;
            }

            var equalsIndex = argument.IndexOf('=');
            var option = equalsIndex < 0 ? argument : argument[..equalsIndex];
            if (ModelOptions.Contains(option))
            {
                var value = equalsIndex < 0
                    ? RequiredValue(args, ref index, option)
                    : argument[(equalsIndex + 1)..];
                if (string.IsNullOrWhiteSpace(value))
                {
                    throw new ArgumentException($"Missing value for {option}.");
                }

                if (option == "--provider") provider = value.Trim();
                else model = value.Trim();
                cliSelectedModel = true;
                continue;
            }

            if (documentation && option == "--runs-per-variant")
            {
                runsText = equalsIndex < 0
                    ? RequiredValue(args, ref index, option)
                    : argument[(equalsIndex + 1)..];
                if (string.IsNullOrWhiteSpace(runsText))
                {
                    throw new ArgumentException("Missing value for --runs-per-variant.");
                }

                continue;
            }

            throw new ArgumentException($"Unsupported eval argument: {argument}");
        }

        if (cliSelectedModel)
        {
            if (string.IsNullOrEmpty(provider) || string.IsNullOrEmpty(model))
            {
                throw new ArgumentException("CLI model selection requires both --provider and --model.");
            }
        }
        else
        {
            provider = Normalize(environment.GetValueOrDefault("PI_PROVIDER"));
            model = Normalize(environment.GetValueOrDefault("PI_MODEL"));
            if ((provider is null) != (model is null))
            {
                throw new ArgumentException("Set both PI_PROVIDER and PI_MODEL, or neither.");
            }
        }

        var configuredRuns = Normalize(runsText)
            ?? (documentation ? Normalize(environment.GetValueOrDefault("PI_EVAL_RUNS_PER_VARIANT")) : null);
        var runsPerVariant = configuredRuns is null
            ? 1
            : ParseRunsPerVariant(configuredRuns);

        return new EvalCliOptions
        {
            Provider = provider,
            Model = model,
            RunsPerVariant = runsPerVariant,
            Files = files,
            TestNamePattern = testNamePattern,
        };
    }

    public static EvalModelSelection RequireModel(EvalCliOptions options)
    {
        if (string.IsNullOrWhiteSpace(options.Provider) || string.IsNullOrWhiteSpace(options.Model))
        {
            throw new InvalidOperationException(
                "Set PI_PROVIDER and PI_MODEL, or pass both --provider and --model.");
        }

        return new EvalModelSelection(options.Provider, options.Model);
    }

    private static string RequiredValue(IReadOnlyList<string> args, ref int index, string option)
    {
        if (index + 1 >= args.Count || args[index + 1].StartsWith("-", StringComparison.Ordinal))
        {
            throw new ArgumentException($"Missing value for {option}.");
        }

        return args[++index];
    }

    private static string? Normalize(string? value)
        => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static long ParseRunsPerVariant(string text)
    {
        if (!long.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var runs)
            || runs < 1
            || runs > EvalPlan.MaxSafeInteger)
        {
            throw new ArgumentException("Runs per variant must be a positive integer.");
        }

        return runs;
    }
}
