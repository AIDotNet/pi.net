using Pi.Ai.Types;
using Pi.CodingAgent.Core.Extensions;
using AiText = Pi.Ai.Utils.Text;

namespace Pi.Evals;

internal static class ExtensionCreationEval
{
    private const string ToolName = "hello";
    private const string ToolResultText = "Hello, Bob!";

    public static async Task<EvalTaskReport> RunAsync(EvalTask task, CancellationToken cancellationToken)
    {
        var options = EvalHarness.CreatePiDocumentationEvalHarness(new EvalHarnessOptions
        {
            Model = new EvalModelSelection(
                EvalScenarioCatalog.ModelProvider(task.Model),
                EvalScenarioCatalog.ModelId(task.Model)),
            Tools = [.. EvalHarness.DocumentationTools, "compile_dotnet_extension"],
            EnableExtensionCompiler = true,
            Output = context =>
            {
                var extensions = context.Session.ResourceLoader.GetExtensions();
                var toolResult = context.Session.Messages
                    .OfType<ToolResultMessage>()
                    .Reverse()
                    .FirstOrDefault(message => message.ToolName == ToolName && !message.IsError);
                var output = new
                {
                    response = context.Response,
                    extensionErrors = extensions.Errors.Select(error => error.Error).ToArray(),
                    toolLoaded = extensions.Extensions.Any(extension => extension.Tools.ContainsKey(ToolName)),
                    toolResult = toolResult is null ? null : AiText.ContentText(toolResult.Content),
                };
                return Task.FromResult(EvalScenarioHelpers.ToJson(output));
            },
        });

        var harness = await EvalHarness.RunAsync(
            [
                new EvalInputStep.Prompt(
                    "Configure this running Pi installation with a compiled .NET extension containing a "
                    + "hello tool that takes a name and returns a greeting. Do not create project source. "
                    + "Use the read-only Pi documentation for the C# extension entry and tool contracts. "
                    + "For example, passing Bob should return `Hello, Bob!`."),
                new EvalInputStep.Reload(),
                new EvalInputStep.Prompt(
                    "Use the hello tool to greet Bob. Respond with exactly the tool's greeting and nothing else."),
            ],
            options,
            cancellationToken).ConfigureAwait(false);

        var score = harness.Errors.Count == 0
            && IsExpectedOutput(harness.Output)
            && EvalScenarioHelpers.HasToolCall(harness.Events, ToolName, "name", "Bob");
        return EvalScenarioCatalog.CreateTaskReport(task, harness, score ? 1 : 0);
    }

    private static bool IsExpectedOutput(System.Text.Json.JsonElement output)
    {
        if (output.ValueKind != System.Text.Json.JsonValueKind.Object
            || !EvalScenarioHelpers.HasJsonString(output, "response", ToolResultText)
            || !EvalScenarioHelpers.HasJsonBoolean(output, "toolLoaded", true)
            || !EvalScenarioHelpers.HasJsonString(output, "toolResult", ToolResultText)
            || !output.TryGetProperty("extensionErrors", out var errors))
        {
            return false;
        }

        return errors.ValueKind == System.Text.Json.JsonValueKind.Array
            && errors.GetArrayLength() == 0;
    }
}
