namespace Pi.Evals;

internal static class Program
{
    public static async Task<int> Main(string[] args)
    {
        using var cancellation = new CancellationTokenSource();
        ConsoleCancelEventHandler cancelHandler = (_, eventArgs) =>
        {
            eventArgs.Cancel = true;
            cancellation.Cancel();
        };
        Console.CancelKeyPress += cancelHandler;

        try
        {
            return await RunAsync(args, cancellation.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
            Console.Error.WriteLine("Eval run cancelled.");
            return 130;
        }
        catch (Exception error)
        {
            Console.Error.WriteLine($"Eval runner failed: {error}");
            return 1;
        }
        finally
        {
            Console.CancelKeyPress -= cancelHandler;
        }
    }

    private static Task<int> RunAsync(string[] args, CancellationToken cancellationToken)
    {
        if (args.Length == 0)
        {
            return RunDocumentationAsync([], cancellationToken);
        }

        if (args[0] is "-h" or "--help" or "help")
        {
            PrintHelp();
            return Task.FromResult(0);
        }

        if (args[0] == "container-task")
        {
            return EvalContainerSupervisor.RunAsync(args[1..], cancellationToken);
        }

        if (args[0] == "worker")
        {
            return EvalContainerSupervisor.RunWorkerAsync(args[1..], cancellationToken);
        }

        if (args[0] == "docs")
        {
            return RunDocumentationAsync(args[1..], cancellationToken);
        }

        if (args[0] == "host")
        {
            return RunHostAsync(args[1..], cancellationToken);
        }

        if (args[0] == "all")
        {
            return RunAllAsync(args[1..], cancellationToken);
        }

        if (args[0].StartsWith("-", StringComparison.Ordinal)
            || args[0].EndsWith(".docs.eval.cs", StringComparison.Ordinal)
            || args[0] is "-t" or "--testNamePattern")
        {
            return RunDocumentationAsync(args, cancellationToken);
        }

        throw new ArgumentException($"Unknown eval command: {args[0]}. Use --help for usage.");
    }

    private static async Task<int> RunDocumentationAsync(
        IReadOnlyList<string> args,
        CancellationToken cancellationToken)
    {
        var options = EvalCli.ParseDocumentation(args);
        var repositoryRoot = EvalDockerRunner.FindRepositoryRoot();
        return await DocumentationEvalRunner.RunAsync(options, repositoryRoot, cancellationToken)
            .ConfigureAwait(false);
    }

    private static async Task<int> RunHostAsync(
        IReadOnlyList<string> args,
        CancellationToken cancellationToken)
    {
        var options = EvalCli.ParseHost(args);
        var repositoryRoot = EvalDockerRunner.FindRepositoryRoot();
        return await HostEvalRunner.RunAsync(options, repositoryRoot, cancellationToken)
            .ConfigureAwait(false);
    }

    private static async Task<int> RunAllAsync(
        IReadOnlyList<string> args,
        CancellationToken cancellationToken)
    {
        var documentationOptions = EvalCli.ParseDocumentation(args);
        var hostOptions = documentationOptions with
        {
            Files = [],
            TestNamePattern = null,
        };
        var repositoryRoot = EvalDockerRunner.FindRepositoryRoot();
        var hostExitCode = await HostEvalRunner.RunAsync(hostOptions, repositoryRoot, cancellationToken)
            .ConfigureAwait(false);
        var documentationExitCode = await DocumentationEvalRunner.RunAsync(
            documentationOptions,
            repositoryRoot,
            cancellationToken).ConfigureAwait(false);
        return hostExitCode == 0 && documentationExitCode == 0 ? 0 : 1;
    }

    private static void PrintHelp()
    {
        Console.WriteLine(
            """
            Pi .NET eval runner

            Usage:
              Pi.Evals [docs] [evals/*.docs.eval.cs] [options]
              Pi.Evals host [evals/*.eval.cs] [options]
              Pi.Evals all [documentation options]

            Model selection:
              --provider <id> --model <id>
              or set PI_PROVIDER and PI_MODEL

            Documentation eval options:
              --runs-per-variant <positive integer>
              PI_EVAL_RUNS_PER_VARIANT=<positive integer>
              -t, --testNamePattern <regular expression>

            Host evals run on the current machine. Documentation evals build isolated Docker images.
            Run from the Pi.NET repository root. See src/Pi.Evals/README.md for details.
            """);
    }
}
