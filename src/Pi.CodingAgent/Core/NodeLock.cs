using Pi.CodingAgent.Utils;

namespace Pi.CodingAgent.Core;

/// <summary>Raised when another holder owns the lock. Carries the Node code <c>ELOCKED</c>.</summary>
public sealed class LockHeldException : NodeIoException
{
    public LockHeldException(string lockPath, Exception? innerException = null)
        : base($"Lock file is already being held: {lockPath}", "ELOCKED", innerException)
    {
    }
}

/// <summary>
/// Port of the parts of <c>proper-lockfile</c> the coding agent uses (<c>lockSync</c> / <c>lock</c>).
/// </summary>
/// <remarks>
/// <para>
/// <c>proper-lockfile</c> uses an <b>exclusive directory</b> (<c>mkdir &lt;file&gt;.lock</c>) as the lock
/// artifact. .NET has no exclusive directory creation, so the port uses an <b>exclusive file</b>
/// (<c>FileMode.CreateNew</c>), which is atomic on every supported platform. The observable difference is
/// the artifact kind (file rather than directory) at <c>&lt;file&gt;.lock</c>; the mutual exclusion, the
/// <c>ELOCKED</c> code, the release-on-dispose behavior and the stale takeover are the same.
/// </para>
/// <para>
/// Like <c>proper-lockfile</c>, the lock is expressed by the artifact's <b>existence</b>, not by an open
/// handle, and the holder refreshes its mtime so a long-running holder is not mistaken for a stale one.
/// <c>onCompromised</c> fires when the artifact disappears or is replaced by another holder.
/// </para>
/// </remarks>
public static class NodeLock
{
    /// <summary>
    /// Windows 上「刚删除的锁文件仍处于 delete-pending」这类瞬时创建失败的重试上限。
    /// 单独设上限，让真正的权限错误快速失败，而不是耗完整个等待窗口（上游 #10801）。
    /// </summary>
    private const int MaxPermissionRetries = 5;

    /// <summary>Windows 上锁产物被另一个句柄（杀毒、索引器、另一个 pi 探锁）拦住导致的创建失败。</summary>
    private static bool IsTransientLockFailure(Exception? failure)
        => OperatingSystem.IsWindows() && failure is UnauthorizedAccessException;

    /// <summary>Acquire the lock at <paramref name="path"/> synchronously, retrying like the callers do.</summary>
    public static IDisposable AcquireSync(string path, int maxAttempts = 10, int delayMs = 20)
    {
        var lockPath = path + ".lock";
        Exception? lastError = null;
        for (var attempt = 1; attempt <= maxAttempts; attempt++)
        {
            try
            {
                return new SyncLock(lockPath);
            }
            catch (LockHeldException error)
            {
                lastError = error;
                if (attempt == maxAttempts) throw;
                Thread.Sleep(delayMs);
            }
        }

        throw lastError ?? new LockHeldException(lockPath);
    }

    /// <summary>
    /// Acquire the lock asynchronously with the exponential backoff and stale takeover the auth store uses.
    /// The returned delegate releases the lock.
    /// </summary>
    /// <param name="maxWaitMs">
    /// Total wait before the lock is reported as held (proper-lockfile's <c>retries.maxRetryTime</c>).
    /// Defaults to <paramref name="staleMs"/>, the behaviour the earlier call sites rely on.
    /// </param>
    /// <param name="fixedRetryMs">
    /// A constant retry delay instead of the exponential backoff (proper-lockfile's
    /// <c>minTimeout == maxTimeout</c> with <c>factor: 1</c>).
    /// </param>
    public static async Task<Func<Task>> AcquireAsync(
        string path,
        CancellationToken cancellationToken,
        Action<Exception>? onCompromised = null,
        int staleMs = 30_000,
        int maxDelayMs = 2_000,
        int? maxWaitMs = null,
        int? fixedRetryMs = null)
    {
        var lockPath = path + ".lock";
        var deadline = Environment.TickCount64 + (maxWaitMs ?? staleMs);
        var retry = 0;
        var permissionRetries = 0;
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var handle = TryAcquire(lockPath, staleMs, onCompromised, out var acquireFailure);
            if (handle is not null)
            {
                if (cancellationToken.IsCancellationRequested)
                {
                    await handle.ReleaseAsync().ConfigureAwait(false);
                    cancellationToken.ThrowIfCancellationRequested();
                }
                return handle.ReleaseAsync;
            }

            cancellationToken.ThrowIfCancellationRequested();
            var remainingMs = deadline - Environment.TickCount64;
            if (remainingMs <= 0) throw new LockHeldException(lockPath, acquireFailure);
            if (IsTransientLockFailure(acquireFailure) && ++permissionRetries > MaxPermissionRetries)
            {
                throw new LockHeldException(lockPath, acquireFailure);
            }
            int delayMs;
            if (fixedRetryMs is int fixedMs)
            {
                delayMs = (int)Math.Min(fixedMs, remainingMs);
            }
            else
            {
                var baseDelayMs = Math.Min(10 * Math.Pow(2, retry), maxDelayMs / 2.0);
                retry++;
                delayMs = (int)Math.Min(Math.Round(baseDelayMs * (1 + Random.Shared.NextDouble())), remainingMs);
            }

            await Task.Delay(delayMs, cancellationToken).ConfigureAwait(false);
        }
    }

    private static AsyncLock? TryAcquire(
        string lockPath, int staleMs, Action<Exception>? onCompromised, out Exception? failure)
    {
        failure = null;
        var directory = Path.GetDirectoryName(lockPath);
        if (!string.IsNullOrEmpty(directory))
        {
            try
            {
                Directory.CreateDirectory(directory);
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
                // 目录创建失败也可能是 delete-pending 的瞬时状态，交给调用方的重试上限处理。
                failure = error;
                return null;
            }
        }

        if (TryCreateExclusive(lockPath, out failure))
        {
            return new AsyncLock(lockPath, staleMs, onCompromised);
        }

        // Take over an artifact older than the stale window, like proper-lockfile's `stale` option.
        try
        {
            var info = new FileInfo(lockPath);
            if (!info.Exists)
            {
                return TryCreateExclusive(lockPath, out _) ? new AsyncLock(lockPath, staleMs, onCompromised) : null;
            }
            var ageMs = DateTime.UtcNow - info.LastWriteTimeUtc;
            if (ageMs.TotalMilliseconds <= staleMs) return null;
            File.Delete(lockPath);
        }
        catch (IOException)
        {
            return null;
        }

        return TryCreateExclusive(lockPath, out _) ? new AsyncLock(lockPath, staleMs, onCompromised) : null;
    }

    private static bool TryCreateExclusive(string lockPath, out Exception? failure)
    {
        try
        {
            using var stream = new FileStream(lockPath, FileMode.CreateNew, FileAccess.Write, FileShare.Read);
            failure = null;
            return true;
        }
        catch (IOException error)
        {
            failure = error;
            return false;
        }
        catch (UnauthorizedAccessException error)
        {
            failure = error;
            return false;
        }
    }

    private sealed class SyncLock : IDisposable
    {
        private readonly string _lockPath;

        public SyncLock(string lockPath)
        {
            _lockPath = lockPath;
            var directory = Path.GetDirectoryName(lockPath);
            if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
            if (!TryCreateExclusive(lockPath, out var failure))
            {
                throw new LockHeldException(lockPath, failure);
            }
        }

        public void Dispose()
        {
            try
            {
                File.Delete(_lockPath);
            }
            catch (IOException)
            {
                // Releasing is best-effort; a failed delete leaves a stale artifact the next holder takes over.
            }
        }
    }

    private sealed class AsyncLock
    {
        private readonly string _lockPath;
        private readonly string _token = Guid.NewGuid().ToString("N");
        private readonly Action<Exception>? _onCompromised;
        private readonly CancellationTokenSource _refreshStop = new();

        public AsyncLock(string lockPath, int staleMs, Action<Exception>? onCompromised)
        {
            _lockPath = lockPath;
            _onCompromised = onCompromised;
            File.WriteAllText(lockPath, _token);
            _ = RefreshLoopAsync(staleMs, _refreshStop.Token);
        }

        private async Task RefreshLoopAsync(int staleMs, CancellationToken cancellationToken)
        {
            var interval = Math.Max(1, staleMs / 3);
            try
            {
                while (!cancellationToken.IsCancellationRequested)
                {
                    await Task.Delay(interval, cancellationToken).ConfigureAwait(false);
                    try
                    {
                        var current = File.Exists(_lockPath) ? File.ReadAllText(_lockPath) : null;
                        if (current != _token)
                        {
                            _onCompromised?.Invoke(
                                new IOException($"Lock file {_lockPath} was taken over by another process"));
                            return;
                        }
                        File.SetLastWriteTimeUtc(_lockPath, DateTime.UtcNow);
                    }
                    catch (IOException error)
                    {
                        _onCompromised?.Invoke(error);
                        return;
                    }
                }
            }
            catch (OperationCanceledException)
            {
                // Normal shutdown.
            }
        }

        public Task ReleaseAsync()
        {
            _refreshStop.Cancel();
            try
            {
                if (File.Exists(_lockPath) && File.ReadAllText(_lockPath) == _token) File.Delete(_lockPath);
            }
            catch (IOException)
            {
                // Best-effort release.
            }
            _refreshStop.Dispose();
            return Task.CompletedTask;
        }
    }
}
