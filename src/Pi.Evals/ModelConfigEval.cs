using Pi.Ai.Models;
using Pi.CodingAgent.Core;

namespace Pi.Evals;

internal static class ModelConfigEval
{
    private const string ProviderId = "openai";
    private const string ModelId = "fixture-chat";
    private const string ModelName = "Fixture Chat";

    public static async Task<EvalTaskReport> RunAsync(EvalTask task, CancellationToken cancellationToken)
    {
        var harness = await EvalHarness.RunAsync(
            [
                new EvalInputStep.Prompt(
                    $"Configure this running Pi installation with a new `{ProviderId}/{ModelId}` model. "
                    + "Do not create project-local configuration. Show it as “Fixture Chat”. It accepts text, "
                    + "supports reasoning, has a 32,768-token context window and a 4,096-token maximum output, "
                    + "and has no usage cost."),
                new EvalInputStep.Reload(),
            ],
            DocumentationOptions(task, context => InspectAsync(context.Session.ModelRuntime, cancellationToken)),
            cancellationToken).ConfigureAwait(false);

        var score = harness.Errors.Count == 0
            && IsExpectedOutput(harness.Output);
        return EvalScenarioCatalog.CreateTaskReport(task, harness, score ? 1 : 0);
    }

    private static EvalHarnessOptions DocumentationOptions(
        EvalTask task,
        Func<EvalHarnessContext, Task<System.Text.Json.JsonElement>> output)
        => EvalHarness.CreatePiDocumentationEvalHarness(new EvalHarnessOptions
        {
            Model = new EvalModelSelection(
                EvalScenarioCatalog.ModelProvider(task.Model),
                EvalScenarioCatalog.ModelId(task.Model)),
            Output = output,
        });

    private static async Task<System.Text.Json.JsonElement> InspectAsync(
        ModelRuntime runtime,
        CancellationToken cancellationToken)
    {
        await runtime.RefreshAsync(new ModelsRefreshOptions
        {
            AllowNetwork = false,
            Signal = cancellationToken,
        }).ConfigureAwait(false);

        var configurationError = runtime.GetError();
        if (configurationError is not null)
        {
            return EvalScenarioHelpers.ToJson(new { result = new { error = configurationError } });
        }

        var model = runtime.GetModel(ProviderId, ModelId);
        if (model is null)
        {
            return EvalScenarioHelpers.ToJson(new
            {
                result = new { error = $"Model {ProviderId}/{ModelId} is unavailable after reload." },
            });
        }

        var pristineRuntime = await ModelRuntime.CreateAsync(new CreateModelRuntimeOptions
        {
            ModelsPathDisabled = true,
            AllowModelNetwork = false,
            RefreshOnCreate = false,
        }).ConfigureAwait(false);
        var pristineProvider = pristineRuntime.GetProvider(ProviderId);
        if (pristineProvider is null)
        {
            return EvalScenarioHelpers.ToJson(new
            {
                result = new { error = $"Built-in provider {ProviderId} is unavailable." },
            });
        }

        var existingModelIds = pristineProvider.GetModels().Select(candidate => candidate.Id).ToArray();
        if (existingModelIds.Length == 0)
        {
            return EvalScenarioHelpers.ToJson(new
            {
                result = new { error = $"Built-in provider {ProviderId} has no models." },
            });
        }

        var existingModelsPreserved = existingModelIds.All(
            id => runtime.GetModel(ProviderId, id) is not null);
        return EvalScenarioHelpers.ToJson(new
        {
            result = new
            {
                model = EvalScenarioHelpers.ModelFields(model),
                existingModelsPreserved,
            },
        });
    }

    private static bool IsExpectedOutput(System.Text.Json.JsonElement output)
    {
        if (output.ValueKind != System.Text.Json.JsonValueKind.Object
            || !output.TryGetProperty("result", out var result)
            || result.ValueKind != System.Text.Json.JsonValueKind.Object
            || !result.TryGetProperty("model", out var model)
            || model.ValueKind != System.Text.Json.JsonValueKind.Object
            || !result.TryGetProperty("existingModelsPreserved", out var preserved)
            || preserved.ValueKind != System.Text.Json.JsonValueKind.True)
        {
            return false;
        }

        return EvalScenarioHelpers.HasJsonString(model, "id", ModelId)
            && EvalScenarioHelpers.HasJsonString(model, "name", ModelName)
            && EvalScenarioHelpers.HasJsonString(model, "provider", ProviderId)
            && EvalScenarioHelpers.HasJsonBoolean(model, "reasoning", true)
            && model.TryGetProperty("contextWindow", out var contextWindow)
            && contextWindow.TryGetInt64(out var window)
            && window == 32_768
            && model.TryGetProperty("maxTokens", out var maxTokens)
            && maxTokens.TryGetInt64(out var maximum)
            && maximum == 4_096
            && model.TryGetProperty("input", out var input)
            && input.ValueKind == System.Text.Json.JsonValueKind.Array
            && input.GetArrayLength() == 1
            && input[0].ValueKind == System.Text.Json.JsonValueKind.String
            && input[0].GetString() == "text"
            && model.TryGetProperty("cost", out var cost)
            && new[] { "input", "output", "cacheRead", "cacheWrite" }
                .All(name => cost.TryGetProperty(name, out var value)
                    && value.ValueKind == System.Text.Json.JsonValueKind.Number
                    && value.GetDouble() == 0);
    }
}
