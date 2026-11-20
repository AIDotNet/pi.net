using Pi.Ai.Api;
using Pi.Ai.Auth;
using Pi.Ai.Models;

namespace Pi.Ai.Providers;

/// <summary>
/// Qwen Token Plan provider。对应 TS <c>providers/qwen-token-plan.ts</c> 的 <c>qwenTokenPlanProvider()</c>。
/// </summary>
public static class QwenTokenPlan
{
    public static IProvider Provider() => ProviderFactory.Create(new CreateProviderOptions
    {
        Id = "qwen-token-plan",
        Name = "Qwen Token Plan Team",
        BaseUrl = "https://token-plan.maas.qwencloudapi.com/compatible-mode/v1",
        Auth = new ProviderAuth
        {
            ApiKey = new EnvApiKeyAuth(
                "Qwen Token Plan Team API key",
                ["QWEN_TOKEN_PLAN_TEAM_API_KEY", "QWEN_TOKEN_PLAN_API_KEY"]),
        },
        Models = BuiltinCatalog.All("qwen-token-plan"),
        Api = LazyApis.OpenAiCompletionsApi(),
    });
}
