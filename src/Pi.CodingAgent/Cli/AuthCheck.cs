using Pi.Ai.Auth;
using Pi.CodingAgent.Core;

namespace Pi.CodingAgent.Cli;

// ============================================================================
// `pi auth check` — TS `cli/auth-check.ts`（73 行）。
//
// 判定「这个 provider 现在能不能用」，输出一个三态 + 原因的结构化结果（不打印、不抛错，
// 除了「连 provider 都解析不出来」那几种）。分支顺序即 TS 的顺序：
//   1. `validateAuthCommandArgs(args, "check")` 拿 provider/model；
//   2. 给了 model 就先解析出模型，用**模型自带的 provider** 覆盖 cliProvider；
//   3. provider 仍为空 → 抛错（不是返回 not_ready）；
//   4. 目录加载有错 → invalid / invalid_state（先于 provider 存在性检查）；
//   5. provider 不存在 → not_ready / provider_not_found；
//   6. checkAuth 为空 → not_ready / credentials_not_configured；
//   7. 要求 refresh 且 getAuth 拿不到 → 同上；
//   8. 否则 ready + authType；
//   9. checkAuth 抛错 → invalid / invalid_state。
//
// 与 TS 的差异：
//   - `status` / `reason` / `authType` 的字符串联合 → 枚举（C242）；序列化由调用方决定。
//   - `options: { refresh } = { refresh: false }` → <see cref="AuthCheckOptions.Refresh"/> 默认 false。
//   - `CredentialStore` → <see cref="ICredentialStore"/>（C231 系列既有映射）。
//   - `credential?.type === "oauth"` → <see cref="Credential.Kind"/> == <see cref="CredentialKind.OAuth"/>。
// ============================================================================

/// <summary>对应 TS <c>AuthCheckStatus</c>。</summary>
public enum AuthCheckStatus
{
    Ready,
    NotReady,
    Invalid,
}

/// <summary>对应 TS <c>AuthCheckReason</c>。</summary>
public enum AuthCheckReason
{
    ProviderNotFound,
    CredentialsNotConfigured,
    CredentialNotAvailable,
    InvalidState,
}

/// <summary>对应 TS <c>AuthCheckResult</c>（<c>reason</c>/<c>authType</c> 为可选键）。</summary>
public sealed record AuthCheckResult
{
    public required AuthCheckStatus Status { get; init; }

    public required string Provider { get; init; }

    public AuthCheckReason? Reason { get; init; }

    public CredentialKind? AuthType { get; init; }
}

/// <summary>对应 TS <c>checkProviderAuth</c> 的 <c>options</c>。</summary>
public sealed record AuthCheckOptions
{
    public bool Refresh { get; init; }
}

/// <summary>Port of <c>cli/auth-check.ts</c>.</summary>
public static class ProviderAuthCheck
{
    /// <summary>对应 TS <c>checkProviderAuth</c>。</summary>
    public static async Task<AuthCheckResult> CheckProviderAuthAsync(
        Args args,
        ModelRuntime modelRuntime,
        AuthCheckOptions? options = null)
    {
        var refresh = options?.Refresh ?? false;
        var target = AuthCommands.ValidateAuthCommandArgs(args, AuthCommandKind.Check);
        var provider = target.Provider;
        var cliModel = target.Model;

        if (!string.IsNullOrEmpty(cliModel))
        {
            var resolved = ModelResolver.ResolveCliModel(new ResolveCliModelOptions
            {
                CliProvider = provider,
                CliModel = cliModel,
                Runtime = modelRuntime,
            });
            if (resolved.Error is not null || resolved.Model is null)
            {
                throw new AuthCommandError(resolved.Error ?? $"Unable to resolve model \"{cliModel}\"");
            }

            provider = resolved.Model.Provider;
        }

        if (string.IsNullOrEmpty(provider))
        {
            throw new AuthCommandError("Unable to resolve an auth provider");
        }

        if (modelRuntime.GetError() is not null)
        {
            return new AuthCheckResult
            {
                Status = AuthCheckStatus.Invalid,
                Provider = provider,
                Reason = AuthCheckReason.InvalidState,
            };
        }

        if (modelRuntime.GetProvider(provider) is null)
        {
            return new AuthCheckResult
            {
                Status = AuthCheckStatus.NotReady,
                Provider = provider,
                Reason = AuthCheckReason.ProviderNotFound,
            };
        }

        try
        {
            var auth = await modelRuntime.CheckAuthAsync(provider).ConfigureAwait(false);
            if (auth is null)
            {
                return new AuthCheckResult
                {
                    Status = AuthCheckStatus.NotReady,
                    Provider = provider,
                    Reason = AuthCheckReason.CredentialsNotConfigured,
                };
            }

            if (refresh && await modelRuntime.GetAuthAsync(provider).ConfigureAwait(false) is null)
            {
                return new AuthCheckResult
                {
                    Status = AuthCheckStatus.NotReady,
                    Provider = provider,
                    Reason = AuthCheckReason.CredentialsNotConfigured,
                };
            }

            return new AuthCheckResult
            {
                Status = AuthCheckStatus.Ready,
                Provider = provider,
                AuthType = auth.Type,
            };
        }
        catch
        {
            // TS 是裸 catch：checkAuth/getAuth 的任何异常都折算成 invalid_state。
            return new AuthCheckResult
            {
                Status = AuthCheckStatus.Invalid,
                Provider = provider,
                Reason = AuthCheckReason.InvalidState,
            };
        }
    }

    /// <summary>
    /// 对应 TS <c>getProviderCredential</c>：不要求 refresh 且存的是 oauth 时直接读存储，
    /// 否则走运行时鉴权（可能触发刷新）。
    /// </summary>
    public static async Task<string?> GetProviderCredentialAsync(
        string providerId,
        ModelRuntime modelRuntime,
        ICredentialStore credentials,
        AuthCheckOptions options)
    {
        var credential = await credentials.ReadAsync(providerId).ConfigureAwait(false);
        if (!options.Refresh && credential is Credential.OAuth oauth)
        {
            return oauth.Access;
        }

        return AuthCredentials.GetAuthCredential(await modelRuntime.GetAuthAsync(providerId).ConfigureAwait(false));
    }

    /// <summary>
    /// 对应 TS <c>createAuthCheckModelRuntime</c>：内存模型库 + 不联网 + 不预刷新，
    /// 保证 `auth check` 只反映本地状态。
    /// </summary>
    public static Task<ModelRuntime> CreateAuthCheckModelRuntimeAsync(ICredentialStore credentials) =>
        ModelRuntime.CreateAsync(new CreateModelRuntimeOptions
        {
            Credentials = credentials,
            ModelsStore = new InMemoryCodingAgentModelsStore(),
            AllowModelNetwork = false,
            RefreshOnCreate = false,
        });
}
