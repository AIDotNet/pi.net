using Pi.Chord.Services;

namespace Pi.CodingAgent.Experimental;

/// <summary>
/// 把 durable 的精确帧 watch 适配回 chord 的只读复制状态。
/// 上游在 1.2.0 删除了实验性 server/client 栈（<c>7472cda0d</c>），因此 <c>documentState()</c> /
/// <c>viewState()</c> / <c>taskGraph()</c> 的移除在上游没有对应的改写可以照搬；端口保留这套栈，
/// 于是在这里用 watch 重建它需要的 <see cref="IReplicatedState{T}"/>。
/// </summary>
public static class WatchState
{
    /// <summary>把一个 watch 附加为只读复制状态；处置该状态会停止 watch。</summary>
    public static AttachedReplicatedState<T> Attach<T>(IWatchHandle<T> watch, Action<Exception>? onError = null)
        => ReplicatedStateAttachments.AttachReplicatedStateSource(
            new WatchStateSource<T>(watch),
            new ReplicatedStateSourceOptions { OnError = onError });

    /// <summary>watch 驱动的授权源：快照取自 watch 的当前修订，帧按投递顺序递增游标。</summary>
    private sealed class WatchStateSource<T>(IWatchHandle<T> watch) : IReplicatedStateSource<T>
    {
        private long _cursor;
        private bool _attached;

        public ReplicatedStateSourceSnapshot<T> Snapshot => new(watch.Value, _cursor);

        public IReplicatedStateSourceAttachment<T> Attach()
        {
            if (_attached)
            {
                throw new InvalidOperationException("A watch backs exactly one replicated state");
            }

            _attached = true;
            return new Attachment(this, watch);
        }

        private sealed class Attachment(WatchStateSource<T> source, IWatchHandle<T> watch)
            : IReplicatedStateSourceAttachment<T>
        {
            private bool _disposed;

            public void Activate(Action<ReplicatedStateSourceFrame<T>> receive)
            {
                if (_disposed) throw new InvalidOperationException("State attachment is disposed");
                watch.Start((value, ops, context) =>
                {
                    if (!_disposed)
                    {
                        receive(new ReplicatedStateSourceFrame<T>(value, ops, ++source._cursor, context));
                    }

                    return Task.CompletedTask;
                });
            }

            public void Dispose()
            {
                if (_disposed) return;
                _disposed = true;
                // watch 以 stopped 结束；其 detach 同时移除底层的提交/关闭订阅。
                _ = watch.Stop();
            }
        }
    }
}
