using Pi.Ai.Auth;
using Pi.CodingAgent.Core;
using Xunit;

namespace Pi.CodingAgent.Tests;

/// <summary>
/// 上游 1.5.1 #11692：进程退出前等待进行中的凭据写入（OAuth 刷新）落盘，否则一次性 refresh
/// token 已用掉、新 token 却没保存，登录就丢了。此处在 RuntimeCredentials 层验证
/// <c>SettledAsync</c> 会等待一个尚未完成的 <c>ModifyAsync</c>，且写入失败也能正常结算。
/// </summary>
public class RuntimeCredentialsSettleTests
{
    private sealed class GatedCredentialStore : ICredentialStore
    {
        private readonly TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _started = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task Started => _started.Task;
        public void Release() => _release.TrySetResult();

        public Task<Credential?> ReadAsync(string providerId, CancellationToken cancellationToken = default)
            => Task.FromResult<Credential?>(null);

        public Task<IReadOnlyList<CredentialInfo>> ListAsync(CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<CredentialInfo>>([]);

        public async Task<Credential?> ModifyAsync(string providerId, Func<Credential?, Task<Credential?>> fn,
            CancellationToken cancellationToken = default)
        {
            _started.TrySetResult();
            await _release.Task.ConfigureAwait(false);
            return await fn(null).ConfigureAwait(false);
        }

        public Task DeleteAsync(string providerId, CancellationToken cancellationToken = default)
            => Task.CompletedTask;
    }

    private sealed class FailingCredentialStore : ICredentialStore
    {
        public Task<Credential?> ReadAsync(string providerId, CancellationToken cancellationToken = default)
            => Task.FromResult<Credential?>(null);
        public Task<IReadOnlyList<CredentialInfo>> ListAsync(CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<CredentialInfo>>([]);
        public Task<Credential?> ModifyAsync(string providerId, Func<Credential?, Task<Credential?>> fn,
            CancellationToken cancellationToken = default)
            => Task.FromException<Credential?>(new IOException("disk failure"));
        public Task DeleteAsync(string providerId, CancellationToken cancellationToken = default)
            => Task.CompletedTask;
    }

    [Fact]
    public async Task SettledAsync_WaitsForInFlightWrite()
    {
        var store = new GatedCredentialStore();
        var credentials = new RuntimeCredentials(store);

        // 启动一次写入但先不放行，模拟后台 OAuth 刷新正在进行、token endpoint 已用掉旧 refresh token。
        var write = credentials.ModifyAsync("anthropic", _ =>
            Task.FromResult<Credential?>(new Credential.ApiKey("new-access")));
        await store.Started;

        var settled = credentials.SettledAsync();
        await Task.Yield();
        Assert.False(settled.IsCompleted, "settled 必须等这次写入落盘，不能在写入完成前返回");

        store.Release();
        await write;
        await settled; // 不应挂起
        Assert.True(settled.IsCompletedSuccessfully);
    }

    [Fact]
    public async Task SettledAsync_FailedWriteStillSettles()
    {
        var credentials = new RuntimeCredentials(new FailingCredentialStore());
        var write = credentials.ModifyAsync("anthropic", _ =>
            Task.FromResult<Credential?>(new Credential.ApiKey("x")));
        await Assert.ThrowsAsync<IOException>(() => write);
        // 写入失败也必须结算，otherwise dispose 会永久挂起。
        await credentials.SettledAsync();
    }

    [Fact]
    public async Task SettledAsync_WithNoWrites_CompletesImmediately()
    {
        var credentials = new RuntimeCredentials(new GatedCredentialStore());
        await credentials.SettledAsync();
    }
}
