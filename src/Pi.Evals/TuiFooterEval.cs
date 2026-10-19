using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using Pi.Ai.Models;
using Pi.Ai.Types;
using Pi.CodingAgent.Core;
using Pi.CodingAgent.Core.Extensions;
using Pi.CodingAgent.Modes.Interactive;
using Pi.Tui;

namespace Pi.Evals;

internal static partial class TuiFooterEval
{
    private const long ContextWindow = 272_000;
    private static readonly Fixture[] Fixtures =
    [
        new(42.2, "████░░░░░░ 42%"),
        new(65, "███████░░░ 65%"),
        new(120, "██████████ 100%"),
    ];

    private static readonly Regex BuiltInContextPattern = new(
        @"\d+(?:\.\d+)?%/272k(?: \(auto\))?",
        RegexOptions.CultureInvariant);

    private static readonly Regex AnsiSequences = new(
        @"\x1B\[[0-?]*[ -/]*[@-~]|\x1B\][^\x07]*(?:\x07|\x1B\\)",
        RegexOptions.CultureInvariant);

    private static readonly MethodInfo InitMethod = typeof(InteractiveMode).GetMethod(
        "InitAsync",
        BindingFlags.Instance | BindingFlags.NonPublic)
        ?? throw new InvalidOperationException("InteractiveMode.InitAsync was not found.");

    public static async Task<EvalTaskReport> RunAsync(EvalTask task, CancellationToken cancellationToken)
    {
        var options = EvalHarness.CreatePiDocumentationEvalHarness(new EvalHarnessOptions
        {
            Model = new EvalModelSelection(
                EvalScenarioCatalog.ModelProvider(task.Model),
                EvalScenarioCatalog.ModelId(task.Model)),
            Tools = [.. EvalHarness.DocumentationTools, "compile_dotnet_extension"],
            EnableExtensionCompiler = true,
            EnvironmentOverrides = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["PI_OFFLINE"] = "1",
            },
            Output = context => InspectAsync(context.Session, context.Services, context.CancellationToken),
        });

        var harness = await EvalHarness.RunAsync(
            [
                new EvalInputStep.Prompt(
                    "Change the Pi footer by replacing the built-in context information, for example "
                    + "`42.2%/272k (auto)`, with a ten-cell progress bar. It should show "
                    + "`████░░░░░░ 42%` for 42.2%, `███████░░░ 65%` for 65%, and "
                    + "`██████████ 100%` for 120%. Clamp percentages to 0–100 and round the filled "
                    + "cell count to the nearest integer. Preserve the rest of the built-in footer. "
                    + "Implement this as a compiled .NET extension using the documented footer API."),
                new EvalInputStep.Reload(),
            ],
            options,
            cancellationToken).ConfigureAwait(false);

        return EvalScenarioCatalog.CreateTaskReport(
            task,
            harness,
            harness.Errors.Count == 0 && IsExpectedOutput(harness.Output) ? 1 : 0);
    }

    private static async Task<System.Text.Json.JsonElement> InspectAsync(
        AgentSession sourceSession,
        AgentSessionServices services,
        CancellationToken cancellationToken)
    {
        var activeModel = sourceSession.Model
            ?? throw new InvalidOperationException("TUI footer oracle requires an active model.");
        var model = activeModel with { ContextWindow = ContextWindow };
        var builtInStatusLine = (string?)null;
        var observations = new List<Observation>(Fixtures.Length);

        foreach (var (fixture, index) in Fixtures.Select((fixture, index) => (fixture, index)))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var sessionManager = SessionManager.InMemory(services.Cwd);
            sessionManager.AppendMessage(CreateContextMessage(model, fixture.Percent));
            var created = await AgentSessionServicesBuilder.CreateAgentSessionFromServicesAsync(
                new CreateAgentSessionFromServicesOptions
                {
                    Services = services,
                    SessionManager = sessionManager,
                    Model = model,
                    ThinkingLevel = ThinkingLevel.High,
                    NoTools = NoToolsMode.All,
                },
                cancellationToken: cancellationToken).ConfigureAwait(false);

            var runtime = new AgentSessionRuntime(created.Session, services, FailRuntimeReplacement);
            var terminal = new RecordingTerminal();
            var mode = new InteractiveMode(runtime, new InteractiveModeOptions
            {
                Terminal = terminal,
                TuiMode = TuiMode.Regular,
            });
            try
            {
                var initialize = (Task?)InitMethod.Invoke(mode, null)
                    ?? throw new InvalidOperationException("InteractiveMode.InitAsync returned no task.");
                await initialize.ConfigureAwait(false);

                var frames = terminal.GetRenders();
                if (frames.Count == 0)
                {
                    throw new InvalidOperationException("InteractiveMode did not render a terminal frame.");
                }

                if (index == 0)
                {
                    builtInStatusLine = frames
                        .SelectMany(Lines)
                        .FirstOrDefault(line => BuiltInContextPattern.IsMatch(line));
                }

                observations.Add(new Observation(
                    fixture.Percent,
                    StripAnsi(frames[^1]),
                    created.Session.GetContextUsage()));
            }
            finally
            {
                mode.Stop();
                await runtime.DisposeAsync().ConfigureAwait(false);
            }
        }

        return EvalScenarioHelpers.ToJson(new TuiFooterOutput(
            builtInStatusLine,
            observations,
            sourceSession.ResourceLoader.GetExtensions().Errors
                .Select(error => error.Error)
                .ToArray()));
    }

    private static AssistantMessage CreateContextMessage(ModelSpec model, double percent)
    {
        var tokens = (long)Math.Round(ContextWindow * percent / 100, MidpointRounding.AwayFromZero);
        return new AssistantMessage(
            [new TextContent("Context usage fixture")],
            StopReason.Stop,
            UsageStats: new Usage(tokens, 0)
            {
                TotalTokens = tokens,
                Cost = UsageCost.Zero,
            },
            Model: model.Id,
            Api: model.Api,
            Provider: model.Provider,
            Timestamp: DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
    }

    private static Task<CreateAgentSessionRuntimeResult> FailRuntimeReplacement(
        CreateAgentSessionRuntimeOptions options)
        => Task.FromException<CreateAgentSessionRuntimeResult>(
            new NotSupportedException("TUI footer oracle does not replace sessions."));

    private static IReadOnlyList<string> Lines(string value)
        => value.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
            .Where(line => !string.IsNullOrWhiteSpace(line))
            .ToArray();

    private static string StripAnsi(string value)
        => AnsiSequences.Replace(value, string.Empty);

    private static IReadOnlyList<string> ExtractRenders(string output)
    {
        const string startMarker = "\x1b[?2026h";
        const string endMarker = "\x1b[?2026l";
        var renders = new List<string>();
        var offset = 0;
        while (true)
        {
            var start = output.IndexOf(startMarker, offset, StringComparison.Ordinal);
            if (start < 0)
            {
                return renders;
            }

            var contentStart = start + startMarker.Length;
            var end = output.IndexOf(endMarker, contentStart, StringComparison.Ordinal);
            if (end < 0)
            {
                throw new InvalidOperationException("TUI emitted an incomplete synchronized render.");
            }

            renders.Add(output[contentStart..end]);
            offset = end + endMarker.Length;
        }
    }

    private static bool IsExpectedOutput(System.Text.Json.JsonElement output)
    {
        if (output.ValueKind != System.Text.Json.JsonValueKind.Object
            || !output.TryGetProperty("builtInStatusLine", out var builtIn)
            || builtIn.ValueKind != System.Text.Json.JsonValueKind.String
            || !BuiltInContextPattern.IsMatch(builtIn.GetString() ?? string.Empty)
            || !output.TryGetProperty("extensionErrors", out var errors)
            || errors.ValueKind != System.Text.Json.JsonValueKind.Array
            || errors.GetArrayLength() != 0
            || !output.TryGetProperty("observations", out var observations)
            || observations.ValueKind != System.Text.Json.JsonValueKind.Array
            || observations.GetArrayLength() != Fixtures.Length)
        {
            return false;
        }

        var observed = observations.EnumerateArray().ToArray();
        for (var index = 0; index < Fixtures.Length; index++)
        {
            var expected = Fixtures[index];
            var actual = observed[index];
            if (!actual.TryGetProperty("percent", out var percent)
                || !percent.TryGetDouble(out var actualPercent)
                || actualPercent != expected.Percent
                || !actual.TryGetProperty("renderedOutput", out var rendered)
                || rendered.ValueKind != System.Text.Json.JsonValueKind.String
                || !actual.TryGetProperty("contextUsage", out var contextUsage)
                || contextUsage.ValueKind != System.Text.Json.JsonValueKind.Object
                || !contextUsage.TryGetProperty("contextWindow", out var window)
                || !window.TryGetDouble(out var actualWindow)
                || actualWindow != ContextWindow
                || !contextUsage.TryGetProperty("percent", out var usagePercent)
                || !usagePercent.TryGetDouble(out var actualUsagePercent)
                || Math.Round(actualUsagePercent, 1, MidpointRounding.AwayFromZero) != expected.Percent)
            {
                return false;
            }

            var renderedLines = Lines(rendered.GetString() ?? string.Empty);
            var statusLine = renderedLines.FirstOrDefault(line =>
                line.Contains(expected.ExpectedBar, StringComparison.Ordinal)
                || BuiltInContextPattern.IsMatch(line));
            if (statusLine is null
                || !statusLine.Contains(expected.ExpectedBar, StringComparison.Ordinal)
                || BuiltInContextPattern.IsMatch(statusLine)
                || Fixtures.Any(other => other != expected
                    && statusLine.Contains(other.ExpectedBar, StringComparison.Ordinal)))
            {
                return false;
            }
        }

        return true;
    }

    private sealed record Fixture(double Percent, string ExpectedBar);

    private sealed record Observation(
        double Percent,
        string RenderedOutput,
        ContextUsage? ContextUsage);

    private sealed record TuiFooterOutput(
        string? BuiltInStatusLine,
        IReadOnlyList<Observation> Observations,
        IReadOnlyList<string> ExtensionErrors);

    private sealed class RecordingTerminal : ITerminal
    {
        private readonly StringBuilder _writes = new();

        public int Columns => 100;

        public int Rows => 30;

        public bool KittyProtocolActive => true;

        public void Start(Action<string> onInput, Action onResize)
        {
        }

        public void Stop()
        {
        }

        public Task DrainInputAsync(int maxMs = 1000, int idleMs = 50) => Task.CompletedTask;

        public void Write(string data) => _writes.Append(data);

        public void MoveBy(int lines)
        {
        }

        public void HideCursor()
        {
        }

        public void ShowCursor()
        {
        }

        public void ClearLine()
        {
        }

        public void ClearFromCursor()
        {
        }

        public void ClearScreen()
        {
        }

        public void SetTitle(string title)
        {
        }

        public void SetProgress(bool active)
        {
        }

        public IReadOnlyList<string> GetRenders()
        {
            var output = _writes.ToString();
            var renders = ExtractRenders(output);
            return renders.Count > 0 ? renders.Select(StripAnsi).ToArray() : [StripAnsi(output)];
        }
    }
}
