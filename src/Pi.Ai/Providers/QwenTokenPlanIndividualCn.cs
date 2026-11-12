using Pi.Ai.Api;
using Pi.Ai.Auth;
using Pi.Ai.Models;

namespace Pi.Ai.Providers;

/// <summary>
/// Qwen Token Plan Individual (China endpoint) provider。上游 1.5.1 新增（#7989），
/// 对应 TS <c>providers/qwen-token-plan-individual-cn.ts</c>：
/// 在 CN 端点上复用 Individual 的 allowlist，凭据取自 <c>QWEN_TOKEN_PLAN_CN_API_KEY</c>。
/// </summary>
public static class QwenTokenPlanIndividualCn
{
    public static IProvider Provider() => ProviderFactory.Create(new CreateProviderOptions
    {
        Id = "qwen-token-plan-individual-cn",
        Name = "Qwen Token Plan Individual CN",
        BaseUrl = "https://token-plan.cn-beijing.maas.aliyuncs.com/compatible-mode/v1",
        Auth = new ProviderAuth { ApiKey = new EnvApiKeyAuth("Qwen Token Plan Individual CN API key", ["QWEN_TOKEN_PLAN_CN_API_KEY"]) },
        Models = BuiltinCatalog.All("qwen-token-plan-individual-cn"),
        Api = LazyApis.OpenAiCompletionsApi(),
    });
}
