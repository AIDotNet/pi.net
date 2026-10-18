using Pi.Ai.Auth;
using Pi.CodingAgent.Cli;
using Pi.CodingAgent.Core;
using Xunit;

namespace Pi.CodingAgent.Tests;

/// <summary>
/// <c>cli/auth-check.ts</c> 的判定顺序。用「内存模型库 + 不联网 + 不预刷新」的运行时
/// （正是 <see cref="ProviderAuthCheck.CreateAuthCheckModelRuntimeAsync"/> 的配置）跑真实分支，
/// 不打桩 <c>ModelRuntime</c> 本身。
/// </summary>
public class CliAuthCheckTests
{
    [Fact]
    public async Task RequiresProviderOrModel()
    {
        var runtime = await CreateRuntimeAsync();
        var error = await Assert.ThrowsAsync<AuthCommandError>(
            () => ProviderAuthCheck.CheckProviderAuthAsync(new Args(), runtime));

        Assert.Equal("Auth checks require --provider <provider> or --model <model>", error.Message);
    }

    [Fact]
    public async Task RejectsUnknownFlagsBeforeTouchingTheRuntime()
    {
        var runtime = await CreateRuntimeAsync();
        var args = new Args { Provider = "openai" };
        args.UnknownFlags["bogus"] = true;

        var error = await Assert.ThrowsAsync<AuthCommandError>(
            () => ProviderAuthCheck.CheckProviderAuthAsync(args, runtime));

        Assert.Equal("Unknown option --bogus for \"auth check\".", error.Message);
    }

    [Fact]
    public async Task UnknownProviderReportsProviderNotFound()
    {
        var runtime = await CreateRuntimeAsync();
        var result = await ProviderAuthCheck.CheckProviderAuthAsync(new Args { Provider = "no-such-provider" }, runtime);

        Assert.Equal(AuthCheckStatus.NotReady, result.Status);
        Assert.Equal("no-such-provider", result.Provider);
        Assert.Equal(AuthCheckReason.ProviderNotFound, result.Reason);
        Assert.Null(result.AuthType);
    }

    [Fact]
    public async Task UnresolvableModelThrows()
    {
        var runtime = await CreateRuntimeAsync();
        var error = await Assert.ThrowsAsync<AuthCommandError>(
            () => ProviderAuthCheck.CheckProviderAuthAsync(new Args { Model = "definitely-not-a-model" }, runtime));

        Assert.False(string.IsNullOrWhiteSpace(error.Message));
    }

    // 说明：`ready` / `credentials_not_configured` 这两条分支取决于**当前进程环境**
    // （内置 provider 会读 ANTHROPIC_API_KEY 之类的环境变量），在测试里无法稳定复现，
    // 故此处只钉不依赖环境的四条：参数校验、未知 provider、模型解析失败、存储读值。
    // 环境相关的鉴权判定本身由 4e 期的 auth 测试覆盖。

    [Fact]
    public async Task StoredOAuthCredentialIsReturnedWithoutRefresh()
    {
        var store = new MemoryCredentialStore();
        store.Seed("demo", new Credential.OAuth("refresh-token", "access-token", 0));
        var runtime = await ProviderAuthCheck.CreateAuthCheckModelRuntimeAsync(store);

        var value = await ProviderAuthCheck.GetProviderCredentialAsync(
            "demo", runtime, store, new AuthCheckOptions { Refresh = false });

        Assert.Equal("access-token", value);
    }

    private static Task<ModelRuntime> CreateRuntimeAsync() =>
        ProviderAuthCheck.CreateAuthCheckModelRuntimeAsync(new MemoryCredentialStore());

    private sealed class MemoryCredentialStore : ICredentialStore
    {
        private readonly Dictionary<string, Credential> _entries = new(StringComparer.Ordinal);

        public void Seed(string providerId, Credential credential) => _entries[providerId] = credential;

        public Task<Credential?> ReadAsync(string providerId, CancellationToken cancellationToken = default) =>
            Task.FromResult(_entries.GetValueOrDefault(providerId));

        public Task<IReadOnlyList<CredentialInfo>> ListAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<CredentialInfo>>(
                [.. _entries.Select(entry => new CredentialInfo(entry.Key, entry.Value.Kind))]);

        public Task<Credential?> ModifyAsync(
            string providerId,
            Func<Credential?, Task<Credential?>> fn,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task DeleteAsync(string providerId, CancellationToken cancellationToken = default)
        {
            _entries.Remove(providerId);
            return Task.CompletedTask;
        }
    }
}
