using Pi.Chord.Context;

namespace Pi.Chord.Services;

/// <summary>keyed 实例目录条目。对应 TS <c>InstanceDirectoryEntry</c>。</summary>
public interface IInstanceDirectoryEntry
{
    string Key { get; }

    long Generation { get; }

    object Service { get; }

    void Deactivate();
}

/// <summary>
/// 拥有 keyed 实例生命周期与可取消的观察任务。对应 TS <c>InstanceDirectory</c>（services/instances.ts）。
/// </summary>
public sealed class InstanceDirectory<TEntry> where TEntry : IInstanceDirectoryEntry
{
    private sealed class EntryReferenceComparer : IEqualityComparer<TEntry>
    {
        public static EntryReferenceComparer Instance { get; } = new();

        public bool Equals(TEntry? x, TEntry? y) => ReferenceEquals(x, y);

        public int GetHashCode(TEntry entry) => System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(entry);
    }

    private sealed record ObservationTask(Context.Context Context, Action Cancel);

    private readonly Dictionary<string, TEntry> _entries = [];
    private readonly HashSet<Observer> _observers = [];
    private readonly Action<Exception> _reportError;
    private bool _ready;
    private bool _disposed;

    /// <summary>ready=false 时 insert 的实例先挂起，ready() 后统一启动观察。</summary>
    public InstanceDirectory(bool ready, Action<Exception> reportError)
    {
        _ready = ready;
        _reportError = reportError;
    }

    public int ObserverCount => _observers.Count;

    public IEnumerable<TEntry> Values => _entries.Values;

    public TEntry? Get(string key) => _entries.GetValueOrDefault(key);

    /// <summary>插入实例（键重复拒绝）；ready 时立即通知观察者。</summary>
    public void Insert(TEntry entry)
    {
        AssertActive();
        if (_entries.ContainsKey(entry.Key))
            throw new InvalidOperationException($"Keyed service already has a live instance with key {entry.Key}");
        _entries[entry.Key] = entry;
        if (_ready) StartAll(entry);
    }

    /// <summary>替换实例：同键旧代移除（代数必须不同）。</summary>
    public void Replace(TEntry entry)
    {
        AssertActive();
        if (_entries.TryGetValue(entry.Key, out var previous))
        {
            if (previous.Generation == entry.Generation)
                throw new InvalidOperationException("Keyed service repeated a live generation");
            RemoveInternal(previous);
        }
        _entries[entry.Key] = entry;
        if (_ready) StartAll(entry);
    }

    /// <summary>移除实例（仅当仍为当前条目）。</summary>
    public void Remove(TEntry entry)
    {
        if (_entries.GetValueOrDefault(entry.Key)?.Equals(entry) != true
            && !ReferenceEquals(_entries.GetValueOrDefault(entry.Key), entry))
        {
            return;
        }
        RemoveInternal(entry);
    }

    /// <summary>激活：挂起的实例全部通知观察者。</summary>
    public void Ready()
    {
        AssertActive();
        if (_ready) return;
        _ready = true;
        foreach (var entry in _entries.Values) StartAll(entry);
    }

    /// <summary>复位：停止观察并移除全部实例（dispose 语义不变）。</summary>
    public void Reset()
    {
        if (_disposed) return;
        _ready = false;
        foreach (var entry in _entries.Values.ToList()) RemoveInternal(entry);
    }

    /// <summary>观察每个存活实例（ready 时现有实例立即通知）。返回退订。</summary>
    public IDisposable Observe(Func<object, Context.Context, Task?> handler)
    {
        AssertActive();
        var observer = new Observer(handler);
        _observers.Add(observer);
        if (_ready)
        {
            foreach (var entry in _entries.Values) Start(observer, entry);
        }
        return new Unsubscription(() =>
        {
            if (observer.Closed) return;
            observer.Closed = true;
            foreach (var task in observer.Tasks.Values) task.Cancel();
            observer.Tasks.Clear();
            _observers.Remove(observer);
        });
    }

    /// <summary>停止观察并逐个 deactivate 实例。</summary>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        foreach (var observer in _observers)
        {
            observer.Closed = true;
            foreach (var task in observer.Tasks.Values) task.Cancel();
            observer.Tasks.Clear();
        }
        _observers.Clear();
        foreach (var entry in _entries.Values.ToList()) entry.Deactivate();
        _entries.Clear();
    }

    private void RemoveInternal(TEntry entry)
    {
        if (!ReferenceEquals(_entries.GetValueOrDefault(entry.Key), entry)) return;
        _entries.Remove(entry.Key);
        entry.Deactivate();
        foreach (var observer in _observers)
        {
            if (observer.Tasks.Remove(entry, out var task)) task.Cancel();
        }
    }

    private void StartAll(TEntry entry)
    {
        foreach (var observer in _observers) Start(observer, entry);
    }

    private void Start(Observer observer, TEntry entry)
    {
        if (observer.Closed || observer.Tasks.ContainsKey(entry)) return;
        var (context, cancel) = ContextSignals.WithCancel(Context.Context.Background);
        observer.Tasks.Add(entry, new ObservationTask(context, cancel));
        try
        {
            var pending = observer.Handler(entry.Service, context);
            if (pending is not null)
            {
                _ = pending.ContinueWith(
                    completed =>
                    {
                        if (completed.IsFaulted && !observer.Closed
                            && !(context.AbortSignal?.IsCancellationRequested ?? false))
                        {
                            _reportError(completed.Exception!.GetBaseException());
                        }
                    }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
            }
        }
        catch (Exception error)
        {
            if (!(context.AbortSignal?.IsCancellationRequested ?? false)) _reportError(error);
        }
    }

    private void AssertActive()
    {
        if (_disposed) throw new ObjectDisposedException(nameof(InstanceDirectory<TEntry>));
    }

    private sealed class Observer(Func<object, Context.Context, Task?> handler)
    {
        public Func<object, Context.Context, Task?> Handler { get; } = handler;

        public Dictionary<TEntry, ObservationTask> Tasks { get; } = new(EntryReferenceComparer.Instance);

        public bool Closed { get; set; }
    }

    private sealed class Unsubscription(Action dispose) : IDisposable
    {
        public void Dispose() => dispose();
    }
}
