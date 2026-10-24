using Pi.Ai.Api;
using Pi.Ai.Auth;
using Pi.Ai.Auth.OAuth;
using Pi.Ai.Models;
using Pi.Ai.Types;

namespace Pi.Ai.Providers;

/// <summary>
/// OpenAI provider。对应 TS <c>providers/openai.ts</c> 的 <c>openaiProvider()</c>。
/// </summary>
public static class OpenAi
{
    public static IProvider Provider() => ProviderFactory.Create(new CreateProviderOptions
    {
        Id = "openai",
        Name = "OpenAI",
        BaseUrl = "https://api.openai.com/v1",
        Auth = new ProviderAuth
        {
            ApiKey = new EnvApiKeyAuth("OpenAI API key", ["OPENAI_API_KEY"]),
            OAuth = new LazyOAuthAuth("OpenAI (ChatGPT subscription)", OAuthFlows.LoadOpenAIChatGPT,
                isSubscription: true, loginLabel: "Sign in with ChatGPT"),
        },
        Models = BuiltinCatalog.All("openai"),
        Api = LazyApis.OpenAiResponsesApi(),
        Classifiers = new Dictionary<string, ProviderClassifier>
        {
            [OpenAiDecisions.ApiName] = LazyApis.OpenAiDecisionsApi(),
        },
        // Sign in with ChatGPT 的 token 只能到 Responses API；Decisions API 拒绝它们。
        FilterAllModels = static (models, credential) => credential is Credential.OAuth
            ? [.. models.Where(model => model.Type != ModelType.Classifier)]
            : models,
    });
}
