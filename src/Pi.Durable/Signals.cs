using System.Text.Json.Nodes;
using Pi.Durable.Types;
using TaskStatus = Pi.Durable.Types.TaskStatus;

namespace Pi.Durable.Harness;

/// <summary>一次等待信号的键：信号名 + 调用方给的键。对应 TS <c>SignalKey</c>。</summary>
public sealed record SignalKey(string Signal, string Key);

/// <summary>一种信号：类型化的 JSON 值在键下投递。对应 TS <c>SignalToken&lt;T&gt;</c>。</summary>
public sealed class SignalToken<T>
{
    internal SignalToken(string name, int version, Func<T, bool>? validate, Func<object?, long, T>? migrate)
    {
        Name = name;
        Version = version;
        Validate = validate;
        Migrate = migrate;
    }

    public string Name { get; }

    public int Version { get; }

    internal Func<T, bool>? Validate { get; }

    internal Func<object?, long, T>? Migrate { get; }

    /// <summary>该信号下某个键的等待键；空键拒绝。对应 TS <c>key()</c>。</summary>
    public SignalKey Key(string key)
        => string.IsNullOrEmpty(key)
            ? throw new InvalidOperationException($"Signal {Name} key must be non-empty")
            : new SignalKey(Name, key);
}

/// <summary>一个键的状态：已投递的值，或因为睡眠到期而作废。对应 TS <c>SignalState&lt;T&gt;</c>。</summary>
public abstract record SignalState<T>
{
    private SignalState() { }

    public sealed record Value(T Item) : SignalState<T>;

    public sealed record Expired : SignalState<T>;
}

/// <summary>一次投递的结果。对应 TS <c>SignalResult</c>。</summary>
public enum SignalResult
{
    Delivered,
    Duplicate,
    Invalid,
    Closed,
    NotFound,
}

/// <summary>一次投递：结果，以及状态被改写时要写回的任务记录。对应 TS <c>deliverSignal()</c> 的返回值。</summary>
public sealed record SignalDelivery(SignalResult Result, TaskRecord? Record = null);

/// <summary>
/// 信号系统的纯逻辑层：定义、投递、读取，以及睡眠在信号上的判定。
/// 对应 TS <c>packages/durable/src/signals.ts</c>。值与其它 memo 一样存放在任务的
/// <see cref="TaskRecord.Memos"/> 里，键为 <c>pi.signal.&lt;name&gt;/&lt;key&gt;</c>。
/// </summary>
public static class SignalStore
{
    /// <summary>
    /// 定义一个信号。版本必须是正整数，名字不能为空且不能含 <c>/</c>。
    /// 可选的 <paramref name="validate"/> / <paramref name="migrate"/> 对应上游的 schema 校验与迁移回调。
    /// </summary>
    public static SignalToken<T> Define<T>(
        string name, int version,
        Func<T, bool>? validate = null,
        Func<object?, long, T>? migrate = null)
    {
        if (string.IsNullOrEmpty(name) || name.Contains('/'))
            throw new InvalidOperationException($"Signal name {name} must be non-empty, without \"/\"");
        if (version < 1)
            throw new InvalidOperationException($"Signal {name} version must be a positive integer");
        return new SignalToken<T>(name, version, validate, migrate);
    }

    private static string MemoName(string signal, string key) => $"pi.signal.{signal}/{key}";

    /// <summary>
    /// 把值投递到任务的键上：任务不存在 → NotFound；已结束、正在收尾或带中止标记 → Closed；
    /// 值不合法 → Invalid；键已有值 → Duplicate；睡眠已到期且正等这个键 → Closed（并把键标记为
    /// expired）；否则写入值并返回带新 memo 的记录。对应 TS <c>deliverSignal()</c>。
    /// </summary>
    public static SignalDelivery Deliver<T>(
        TaskRecord? record, SignalToken<T> token, string key, T value, long now)
    {
        if (record is null) return new SignalDelivery(SignalResult.NotFound);
        var state = record.State;
        if (state.Status == TaskStatus.Terminal || state.Status == TaskStatus.Completing || record.AbortRequested)
            return new SignalDelivery(SignalResult.Closed);
        if (string.IsNullOrEmpty(key) || token.Validate is { } validate && !validate(value))
            return new SignalDelivery(SignalResult.Invalid);

        var name = MemoName(token.Name, key);
        var stored = MemoOf(record.Memos, name);
        if (stored is not null)
            return new SignalDelivery(IsExpired(stored) ? SignalResult.Closed : SignalResult.Duplicate);

        if (state.Status == TaskStatus.Sleeping
            && state.Until is { } until && until <= now
            && state.Signals?.Any(signal => signal.Signal == token.Name && signal.Key == key) == true)
        {
            return new SignalDelivery(SignalResult.Closed, WithMemo(record, name, ExpiredMemo()));
        }

        var memo = new Dictionary<string, object?>
        {
            ["version"] = token.Version,
            ["value"] = value,
        };
        return new SignalDelivery(SignalResult.Delivered, WithMemo(record, name, memo));
    }

    /// <summary>
    /// 读取一个键的已提交状态；没有投递过为 null。存储版本高于 token 抛错；
    /// 旧版本按 <c>migrate</c> 迁移并校验。对应 TS <c>readSignal()</c>。
    /// </summary>
    public static SignalState<T>? Read<T>(
        IReadOnlyDictionary<string, object?>? memos, SignalToken<T> token, string key)
    {
        var stored = MemoOf(memos, MemoName(token.Name, key));
        if (stored is null) return null;
        if (IsExpired(stored)) return new SignalState<T>.Expired();
        var version = stored.TryGetValue("version", out var rawVersion) && rawVersion is not null
            ? Convert.ToInt32(rawVersion)
            : 1;
        if (version == token.Version)
            return new SignalState<T>.Value(ConvertValue<T>(stored["value"]));
        if (version > token.Version)
            throw new InvalidOperationException(
                $"Signal {token.Name} value has version {version}, newer than {token.Version}");
        if (token.Migrate is null)
            throw new InvalidOperationException($"Signal {token.Name} has no migration from version {version}");
        var migrated = token.Migrate(stored["value"], version);
        if (token.Validate is { } validate && !validate(migrated))
            throw new InvalidOperationException(
                $"Signal {token.Name} migration from version {version} returned an invalid value");
        return new SignalState<T>.Value(migrated);
    }

    /// <summary>
    /// 校验一次睡眠：必须等一个时刻或至少一个信号；不能等已经作废或已被唤醒消耗的键。
    /// 对应 TS <c>checkSleep()</c>。
    /// </summary>
    public static void CheckSleep(TaskState state, IReadOnlyDictionary<string, object?>? memos, long task)
    {
        if (state.Until is null && (state.Signals is null || state.Signals.Count == 0))
            throw new InvalidOperationException($"Task {task} cannot sleep without until or signals");
        foreach (var signal in state.Signals ?? [])
        {
            var stored = MemoOf(memos, MemoName(signal.Signal, signal.Key));
            if (stored is null) continue;
            if (IsExpired(stored))
                throw new InvalidOperationException(
                    $"Task {task} cannot sleep on signal {signal.Signal} key {signal.Key}: it expired");
            if (stored.TryGetValue("consumed", out var consumed) && consumed is true)
                throw new InvalidOperationException(
                    $"Task {task} cannot sleep on signal {signal.Signal} key {signal.Key}: it already woke a wait");
        }
    }

    /// <summary>睡眠现在能醒否：列出的键有未被唤醒使用的值，或到点。对应 TS <c>sleepDue()</c>。</summary>
    public static bool SleepDue(TaskState state, IReadOnlyDictionary<string, object?>? memos, long now)
        => ReadyKey(state, memos) is not null || state.Until is { } until && until <= now;

    /// <summary>
    /// 现在能醒时的唤醒原因与本次唤醒要写的 memo：第一个有值的键标记 consumed；
    /// 否则是到点，把仍为空的列出键标记 expired。对应 TS <c>sleepWake()</c>。
    /// </summary>
    public static (string Reason, SignalKey? Key, Dictionary<string, object?> Memos)? SleepWake(
        TaskState state, IReadOnlyDictionary<string, object?>? memos, long now)
    {
        var ready = ReadyKey(state, memos);
        if (ready is not null)
        {
            var name = MemoName(ready.Signal, ready.Key);
            var consumed = new Dictionary<string, object?>(MemoOf(memos, name)!) { ["consumed"] = true };
            return ("signal", ready, new Dictionary<string, object?> { [name] = consumed });
        }

        if (state.Until is not { } until || until > now) return null;
        if (state.Signals is null || state.Signals.Count == 0) return ("time", null, []);
        var expired = new Dictionary<string, object?>();
        foreach (var signal in state.Signals)
        {
            var name = MemoName(signal.Signal, signal.Key);
            if (MemoOf(memos, name) is null) expired[name] = ExpiredMemo();
        }
        return ("expired", null, expired);
    }

    /// <summary>存储里的 JSON 树还原成 <typeparamref name="T"/>；已经是目标类型时直接返回。</summary>
    private static T ConvertValue<T>(object? value)
    {
        if (value is T typed) return typed;
        var element = System.Text.Json.JsonSerializer.SerializeToElement(value, JsonTrees.WireOptions);
        return System.Text.Json.JsonSerializer.Deserialize<T>(element, JsonTrees.WireOptions)
            ?? throw new InvalidOperationException("signal value could not be read");
    }

    private static SignalKey? ReadyKey(TaskState state, IReadOnlyDictionary<string, object?>? memos)
    {
        foreach (var signal in state.Signals ?? [])
        {
            var stored = MemoOf(memos, MemoName(signal.Signal, signal.Key));
            if (stored is null || IsExpired(stored)) continue;
            if (stored.TryGetValue("consumed", out var consumed) && consumed is true) continue;
            return signal;
        }
        return null;
    }

    private static Dictionary<string, object?> ExpiredMemo() => new() { ["expired"] = true };

    private static bool IsExpired(Dictionary<string, object?> memo)
        => memo.TryGetValue("expired", out var expired) && expired is true;

    private static Dictionary<string, object?>? MemoOf(
        IReadOnlyDictionary<string, object?>? memos, string name)
        => memos is not null && memos.TryGetValue(name, out var value)
            ? value as Dictionary<string, object?>
            : null;

    private static TaskRecord WithMemo(TaskRecord record, string name, Dictionary<string, object?> memo)
    {
        var memos = record.Memos is { } existing
            ? new Dictionary<string, object?>(existing)
            : new Dictionary<string, object?>();
        memos[name] = memo;
        return record with { Memos = memos };
    }
}
