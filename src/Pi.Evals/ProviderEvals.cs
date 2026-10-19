using System.Text.Json;
using Pi.Ai.Models;
using Pi.Ai.Types;
using Pi.CodingAgent.Core;
using AiText = Pi.Ai.Utils.Text;

namespace Pi.Evals;

internal static class OpenAiProviderEval
{
    public static async Task<EvalTaskReport> RunAsync(EvalTask task, CancellationToken cancellationToken)
    {
        await using var server = AcmeFixtureServer.Start("openai");
        var options = EvalHarness.CreatePiDocumentationEvalHarness(new EvalHarnessOptions
        {
            Model = ModelSelection(task),
            EnvironmentOverrides = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["ACME_API_KEY"] = AcmeFixtureServer.OpenAiApiKey,
            },
            Output = context => EvalProviderOracle.InspectAsync(
                context.Session.ModelRuntime,
                AcmeFixtureServer.OpenAiProviderId,
                AcmeFixtureServer.OpenAiModelId,
                AcmeFixtureServer.OpenAiProbePrompt,
                new Dictionary<string, object?>(StringComparer.Ordinal)
                {
                    ["env"] = new Dictionary<string, string>(StringComparer.Ordinal)
                    {
                        ["ACME_API_KEY"] = AcmeFixtureServer.OpenAiApiKey,
                    },
                    ["maxTokens"] = 32,
                },
                () => server.ValidRequestReceived,
                cancellationToken),
        });

        var harness = await EvalHarness.RunAsync(
            [
                new EvalInputStep.Prompt(
                    $"Configure this running Pi installation with Acme as a provider. "
                    + "Do not create project-local configuration. Its provider ID is acme, its API is at "
                    + $"{server.OpenAiBaseUrl}, and it uses OpenAI Chat Completions. Read its API key from the "
                    + "ACME_API_KEY environment variable.\n\n"
                    + "The provider offers one model, acme-chat, shown as “Acme Chat”. It accepts text, does not "
                    + "support reasoning, has a 32,768-token context window and a 4,096-token maximum output, "
                    + "and has no usage cost."),
                new EvalInputStep.Reload(),
            ],
            options,
            cancellationToken).ConfigureAwait(false);

        server.ThrowIfFailed();
        return EvalScenarioCatalog.CreateTaskReport(
            task,
            harness,
            harness.Errors.Count == 0 && EvalProviderOracle.IsExpected(
                harness.Output,
                AcmeFixtureServer.OpenAiProviderId,
                AcmeFixtureServer.OpenAiModelId,
                "Acme Chat",
                32_768,
                4_096,
                AcmeFixtureServer.OpenAiProbeResponse,
                3,
                2)
                ? 1
                : 0);
    }

    private static EvalModelSelection ModelSelection(EvalTask task)
        => new(EvalScenarioCatalog.ModelProvider(task.Model), EvalScenarioCatalog.ModelId(task.Model));
}

internal static class CustomProviderEval
{
    public static async Task<EvalTaskReport> RunAsync(EvalTask task, CancellationToken cancellationToken)
    {
        await using var server = AcmeFixtureServer.Start("stream");
        var options = EvalHarness.CreatePiDocumentationEvalHarness(new EvalHarnessOptions
        {
            Model = new EvalModelSelection(
                EvalScenarioCatalog.ModelProvider(task.Model),
                EvalScenarioCatalog.ModelId(task.Model)),
            WorkspaceFiles = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["fixtures/acme-stream-api.json"] = AcmeFixtureServer.StreamApiDocumentation,
            },
            EnvironmentOverrides = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["ACME_STREAM_API_KEY"] = AcmeFixtureServer.StreamApiKey,
            },
            Output = context => EvalProviderOracle.InspectAsync(
                context.Session.ModelRuntime,
                AcmeFixtureServer.StreamProviderId,
                AcmeFixtureServer.StreamModelId,
                AcmeFixtureServer.StreamProbePrompt,
                new Dictionary<string, object?>(StringComparer.Ordinal)
                {
                    ["env"] = new Dictionary<string, string>(StringComparer.Ordinal)
                    {
                        ["ACME_STREAM_API_KEY"] = AcmeFixtureServer.StreamApiKey,
                    },
                    ["maxTokens"] = 32,
                },
                () => server.ValidRequestReceived,
                cancellationToken),
        });

        var harness = await EvalHarness.RunAsync(
            [
                new EvalInputStep.Prompt(
                    $"Configure this running Pi installation with Acme Stream as a provider. "
                    + "Do not create a project package or modify project source. Its provider ID is acme-stream, "
                    + $"its API is at {server.Origin}, and its API documentation is in "
                    + "./fixtures/acme-stream-api.json. Read its credential from the "
                    + "ACME_STREAM_API_KEY environment variable.\n\n"
                    + "It offers one model, acme-stream-chat, shown as “Acme Stream Chat”. The model accepts text, "
                    + "does not support reasoning, has a 16,384-token context window and a 2,048-token maximum "
                    + "output, and has no usage cost."),
                new EvalInputStep.Reload(),
            ],
            options,
            cancellationToken).ConfigureAwait(false);

        server.ThrowIfFailed();
        return EvalScenarioCatalog.CreateTaskReport(
            task,
            harness,
            harness.Errors.Count == 0 && EvalProviderOracle.IsExpected(
                harness.Output,
                AcmeFixtureServer.StreamProviderId,
                AcmeFixtureServer.StreamModelId,
                "Acme Stream Chat",
                16_384,
                2_048,
                AcmeFixtureServer.StreamProbeResponse,
                4,
                3)
                ? 1
                : 0);
    }
}

internal static class EvalProviderOracle
{
    public static async Task<JsonElement> InspectAsync(
        ModelRuntime runtime,
        string providerId,
        string modelId,
        string prompt,
        IReadOnlyDictionary<string, object?> options,
        Func<bool> validRequestReceived,
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

        var model = runtime.GetModel(providerId, modelId);
        if (model is null)
        {
            return EvalScenarioHelpers.ToJson(new
            {
                result = new { error = $"Model {providerId}/{modelId} is unavailable after reload." },
            });
        }

        AssistantMessage response;
        try
        {
            response = await runtime.CompleteSimpleAsync(
                model,
                [Messages.UserText(prompt)],
                options,
                cancellationToken).ConfigureAwait(false);
        }
        catch (Exception error)
        {
            return EvalScenarioHelpers.ToJson(new { result = new { error = error.Message } });
        }

        if (response.StopReason == StopReason.Error)
        {
            return EvalScenarioHelpers.ToJson(new
            {
                result = new { error = response.ErrorMessage ?? "Provider request failed." },
            });
        }

        var usage = EvalScenarioHelpers.Usage(response);
        return EvalScenarioHelpers.ToJson(new
        {
            result = new
            {
                validRequestReceived = validRequestReceived(),
                model = EvalScenarioHelpers.ModelFields(model),
                response = new
                {
                    text = AiText.ContentText(response.Content),
                    stopReason = EvalScenarioHelpers.StopReasonText(response.StopReason),
                    inputTokens = usage?.Input ?? 0,
                    outputTokens = usage?.Output ?? 0,
                },
            },
        });
    }

    public static bool IsExpected(
        JsonElement output,
        string providerId,
        string modelId,
        string name,
        long contextWindow,
        long maxTokens,
        string expectedText,
        long expectedInputTokens,
        long expectedOutputTokens)
    {
        if (output.ValueKind != JsonValueKind.Object
            || !output.TryGetProperty("result", out var result)
            || result.ValueKind != JsonValueKind.Object
            || !EvalScenarioHelpers.HasJsonBoolean(result, "validRequestReceived", true)
            || !result.TryGetProperty("model", out var model)
            || !result.TryGetProperty("response", out var response))
        {
            return false;
        }

        return ModelMatches(model, providerId, modelId, name, contextWindow, maxTokens)
            && EvalScenarioHelpers.HasJsonString(response, "text", expectedText)
            && EvalScenarioHelpers.HasJsonString(response, "stopReason", "stop")
            && HasJsonInteger(response, "inputTokens", expectedInputTokens)
            && HasJsonInteger(response, "outputTokens", expectedOutputTokens);
    }

    private static bool ModelMatches(
        JsonElement model,
        string providerId,
        string modelId,
        string name,
        long contextWindow,
        long maxTokens)
        => EvalScenarioHelpers.HasJsonString(model, "id", modelId)
            && EvalScenarioHelpers.HasJsonString(model, "name", name)
            && EvalScenarioHelpers.HasJsonString(model, "provider", providerId)
            && EvalScenarioHelpers.HasJsonBoolean(model, "reasoning", false)
            && HasJsonInteger(model, "contextWindow", contextWindow)
            && HasJsonInteger(model, "maxTokens", maxTokens)
            && model.TryGetProperty("input", out var input)
            && input.ValueKind == JsonValueKind.Array
            && input.GetArrayLength() == 1
            && input[0].ValueKind == JsonValueKind.String
            && input[0].GetString() == "text"
            && model.TryGetProperty("cost", out var cost)
            && new[] { "input", "output", "cacheRead", "cacheWrite" }
                .All(key => cost.TryGetProperty(key, out var value)
                    && value.ValueKind == JsonValueKind.Number
                    && value.GetDouble() == 0);

    private static bool HasJsonInteger(JsonElement value, string name, long expected)
        => value.TryGetProperty(name, out var property)
            && property.ValueKind == JsonValueKind.Number
            && property.TryGetInt64(out var actual)
            && actual == expected;
}
