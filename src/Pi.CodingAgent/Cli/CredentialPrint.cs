using Pi.Ai.Auth;
using Pi.Ai.Models;
using Pi.CodingAgent.Core;

namespace Pi.CodingAgent.Cli;

// ============================================================================
// 打印单个 provider 凭据 — TS `cli/credential-print.ts`（87 行）。
//
// 这是 `pi auth api-key` / `pi auth bearer-token` 的取值核心，分支密集，逐条对齐 TS：
//   1. 先过 `validateAuthCommandArgs`（未知 flag / 位置参数 / provider 必填的闸门在那边）。
//   2. 凭据类型表来自 `listCredentials()`，用来决定「这个 provider 该不该被这个 kind 选中」。
//   3. 指定了 `--provider` 时：provider 必须存在；给了 `--model` 就必须能解析出模型。
//   4. 未指定 `--provider` 时：遍历全部 provider，**只保留有凭据的**，再按 model 解析；
//      解析出模型但带 "Using custom model id" 警告的**要跳过**（那是自造 id，不该拿来取值）。
//   5. 取值后按 kind 与凭据类型交叉过滤，最后按 0 / 1 / 多 三个分支给不同错误。
//
// 注意第 5 步的错误文案依赖 `providers[0]`——多 provider 场景下「0 个可用」时指的是
// 第一个候选 provider 的类型，不是「所有 provider 都不是 oauth」。这是 TS 的既有行为。
//
// 类型差异：
//   - `CredentialInfo["type"]` 的 `"api_key" | "oauth"` → <see cref="CredentialKind"/> 枚举（C242）。
//   - `{ ...(cond ? {minOAuthValidityMs} : {}), signal }` 的对象展开 → 先建
//     <see cref="ModelRuntimeAuthOverrides"/> 再按需写字段（MinOAuthValidityMs 为 null 即「用默认 5 分钟」）。
//   - `AbortSignal` → <see cref="CancellationToken"/>（C231）。
// ============================================================================

/// <summary>Port of <c>cli/credential-print.ts</c>.</summary>
public static class CredentialPrint
{
    /// <summary>TS <c>DEFAULT_BEARER_TOKEN_MIN_EXPIRY_MS = 30 * 60_000</c>。</summary>
    internal const long DefaultBearerTokenMinExpiryMs = 30 * 60_000;

    /// <summary>对应 TS <c>CredentialPrintKind = Exclude&lt;AuthCommandKind, "check"&gt;</c>。</summary>
    internal static bool IsPrintKind(AuthCommandKind kind) => kind != AuthCommandKind.Check;

    /// <summary>
    /// 解析出一个可打印的凭据值。对应 TS <c>resolveCredentialForPrint</c>。
    /// </summary>
    /// <remarks>
    /// 故意走 <see cref="ModelRuntime.GetAuthAsync(string, ModelRuntimeAuthOverrides?)"/>：它会通过正常的
    /// 请求鉴权路径刷新并落盘「剩余不足五分钟」的 OAuth 凭据，于是打印出来的值总是可用的。
    /// </remarks>
    public static async Task<string> ResolveCredentialForPrintAsync(
        Args args,
        ModelRuntime modelRuntime,
        AuthCommandKind kind,
        long? minExpiryMs = null,
        CancellationToken signal = default)
    {
        var target = AuthCommands.ValidateAuthCommandArgs(args, kind);
        var cliProvider = target.Provider;
        var cliModel = target.Model;

        var credentialTypes = new Dictionary<string, CredentialKind>(StringComparer.Ordinal);
        foreach (var credential in await modelRuntime.ListCredentialsAsync(signal).ConfigureAwait(false))
        {
            credentialTypes[credential.ProviderId] = credential.Type;
        }

        var providers = new List<(string Id, ModelSpec? Model)>();
        if (!string.IsNullOrEmpty(cliProvider))
        {
            var provider = modelRuntime.GetProvider(cliProvider);
            if (provider is null)
            {
                throw new AuthCommandError(
                    $"Unknown provider \"{cliProvider}\". Use --list-models to see available providers.");
            }

            if (!string.IsNullOrEmpty(cliModel))
            {
                var resolved = ModelResolver.ResolveCliModel(new ResolveCliModelOptions
                {
                    CliProvider = provider.Id,
                    CliModel = cliModel,
                    Runtime = modelRuntime,
                });
                if (resolved.Error is not null || resolved.Model is null)
                {
                    throw new AuthCommandError(resolved.Error ?? "Unable to resolve the requested provider/model");
                }

                providers.Add((provider.Id, resolved.Model));
            }
            else
            {
                providers.Add((provider.Id, null));
            }
        }
        else
        {
            // TS 此处对 `cliModel!` 用了非空断言：没 provider 时 model 必然存在（validateAuthCommandArgs 保证）。
            var modelId = cliModel!;
            foreach (var provider in modelRuntime.GetProviders())
            {
                if (!credentialTypes.ContainsKey(provider.Id))
                {
                    continue;
                }

                var resolved = ModelResolver.ResolveCliModel(new ResolveCliModelOptions
                {
                    CliProvider = provider.Id,
                    CliModel = modelId,
                    Runtime = modelRuntime,
                });
                if (resolved.Model is not null
                    && resolved.Error is null
                    && resolved.Warning?.Contains("Using custom model id", StringComparison.Ordinal) != true)
                {
                    providers.Add((provider.Id, resolved.Model));
                }
            }

            if (providers.Count == 0)
            {
                throw new AuthCommandError(
                    $"Model \"{cliModel}\" not found. Use --list-models to see available models.");
            }
        }

        var credentials = new List<(string ProviderId, string Value)>();
        foreach (var provider in providers)
        {
            credentialTypes.TryGetValue(provider.Id, out var type);
            var hasType = credentialTypes.ContainsKey(provider.Id);
            if (kind == AuthCommandKind.ApiKey && hasType && type == CredentialKind.OAuth)
            {
                continue;
            }

            if (kind == AuthCommandKind.BearerToken && (!hasType || type != CredentialKind.OAuth))
            {
                continue;
            }

            var authOptions = new ModelRuntimeAuthOverrides
            {
                // TS 只在 bearer_token 时写 minOAuthValidityMs；其余 kind 保持「用默认」。
                MinOAuthValidityMs = kind == AuthCommandKind.BearerToken
                    ? minExpiryMs ?? DefaultBearerTokenMinExpiryMs
                    : null,
                Signal = signal,
            };
            var auth = provider.Model is not null
                ? await modelRuntime.GetAuthAsync(provider.Model, authOptions).ConfigureAwait(false)
                : await modelRuntime.GetAuthAsync(provider.Id, authOptions).ConfigureAwait(false);
            var value = AuthCredentials.GetAuthCredential(auth);
            if (!string.IsNullOrEmpty(value))
            {
                credentials.Add((provider.Id, value));
            }
        }

        if (credentials.Count == 1)
        {
            return credentials[0].Value;
        }

        if (credentials.Count == 0)
        {
            // TS `providers[0]?.id` + `credentialTypes.get(id)`：只看**第一个候选** provider 的类型。
            var providerId = providers.Count > 0 ? providers[0].Id : null;
            CredentialKind? firstType = providerId is not null && credentialTypes.TryGetValue(providerId, out var known)
                ? known
                : null;
            var explicitProvider = !string.IsNullOrEmpty(cliProvider);

            if (explicitProvider && kind == AuthCommandKind.ApiKey && firstType == CredentialKind.OAuth)
            {
                throw new AuthCommandError($"Provider \"{providerId}\" is configured with OAuth, not an API key");
            }

            if (explicitProvider && kind == AuthCommandKind.BearerToken && firstType != CredentialKind.OAuth)
            {
                throw new AuthCommandError($"Provider \"{providerId}\" is not configured with an OAuth bearer token");
            }

            throw new AuthCommandError(
                $"No usable {(kind == AuthCommandKind.ApiKey ? "API key" : "OAuth bearer token")} is configured");
        }

        throw new AuthCommandError(
            $"Multiple configured providers matched ({string.Join(", ", credentials.Select(c => c.ProviderId))}). Specify --provider.");
    }
}
