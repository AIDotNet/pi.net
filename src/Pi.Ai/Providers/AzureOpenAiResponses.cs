using Pi.Ai.Api;
using Pi.Ai.Auth;
using Pi.Ai.Models;

namespace Pi.Ai.Providers;

/// <summary>
/// Azure OpenAI provider。对应 TS <c>providers/azure.ts</c> 的 <c>azureProvider()</c>：
/// 部署名/端点由模型与选项决定，故无静态 baseUrl。
/// </summary>
/// <remarks>
/// provider id 是 <c>azure</c>。上游从 <c>azure-openai-responses</c> 改成了 <c>azure</c>
/// （引用改名的那版 CHANGELOG 明确要求同步 <c>auth.json</c> / <c>models.json</c> /
/// <c>settings.json</c> 里的 provider key）；<c>azure-openai-responses</c> 现在只是 api 名。
/// </remarks>
public static class AzureOpenAiResponses
{
    public static IProvider Provider() => ProviderFactory.Create(new CreateProviderOptions
    {
        Id = "azure",
        Name = "Azure OpenAI",
        Auth = new ProviderAuth { ApiKey = new EnvApiKeyAuth("Azure OpenAI API key", ["AZURE_OPENAI_API_KEY"]) },
        Models = BuiltinCatalog.All("azure"),
        Api = LazyApis.AzureOpenAiResponsesApi(),
    });
}
