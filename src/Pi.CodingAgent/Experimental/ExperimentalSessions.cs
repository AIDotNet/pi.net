using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Pi.CodingAgent.Core;

namespace Pi.CodingAgent.Experimental;

/// <summary>
/// 实验宿主的一类会话目录。<c>experimental/durable/sessions.ts</c> 与 <c>experimental/vacation/sessions.ts</c>
/// 是同一份代码，只有目录名与「没有会话」的文案不同；这里把差异收成一个值。
/// </summary>
public sealed record ExperimentalSessionKind(string DirectoryName, string Label)
{
    /// <summary><c>~/.pi/agent/experimental/durable-sessions</c>。</summary>
    public static ExperimentalSessionKind Durable { get; } = new("durable-sessions", "durable");

    /// <summary><c>~/.pi/agent/experimental/vacation-sessions</c>。</summary>
    public static ExperimentalSessionKind Vacation { get; } = new("vacation-sessions", "vacation");

    /// <summary>进程独占用的锁文件，放在会话目录内。</summary>
    public string LockFileName => $".{Label}.lock";
}

/// <summary>一个实验会话的数据库位置，独占锁定直到运行时关闭。</summary>
public sealed class ExperimentalSessionLocation : IAsyncDisposable
{
    private FileStream? _lock;

    internal ExperimentalSessionLocation(
        string id, string directory, string database, string cwd, bool created, FileStream sessionLock)
    {
        Id = id;
        Directory = directory;
        Database = database;
        Cwd = cwd;
        Created = created;
        _lock = sessionLock;
    }

    public string Id { get; }

    public string Directory { get; }

    public string Database { get; }

    public string Cwd { get; }

    public bool Created { get; }

    public ValueTask DisposeAsync()
        => Interlocked.Exchange(ref _lock, null)?.DisposeAsync() ?? ValueTask.CompletedTask;
}

/// <summary>
/// 为 <paramref name="cwdInput"/> 选一个会话目录：新建，或（<c>--continue</c>）取最新的一个，并阻止并发打开。
/// 对应 TS <c>selectSession</c>。
/// </summary>
public static partial class ExperimentalSessions
{
    private const int LockRetries = 12;
    private static readonly TimeSpan LockRetryDelay = TimeSpan.FromSeconds(1);

    /// <summary>会话目录名：13 位毫秒时间戳 + UUID。</summary>
    [GeneratedRegex(@"\A[0-9]{13}-[0-9a-f-]{36}\z", RegexOptions.CultureInvariant)]
    private static partial Regex SessionDirectoryName();

    public static async Task<ExperimentalSessionLocation> SelectAsync(
        ExperimentalSessionKind kind,
        string cwdInput,
        bool continueSession,
        string? agentDirectory = null,
        TimeProvider? timeProvider = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(cwdInput);
        var cwd = ResolveRealDirectory(cwdInput);
        var cwdHash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(cwd)))[..24];
        var root = Path.Combine(
            Path.GetFullPath(agentDirectory ?? Config.GetAgentDir()),
            "experimental",
            kind.DirectoryName,
            cwdHash);
        Directory.CreateDirectory(root);

        string directory;
        var created = false;
        if (continueSession)
        {
            directory = Directory.EnumerateDirectories(root)
                .Select(Path.GetFileName)
                .Where(name => name is not null && SessionDirectoryName().IsMatch(name))
                .OrderBy(name => name, StringComparer.Ordinal)
                .LastOrDefault() is { } newest
                    ? Path.Combine(root, newest)
                    : throw new InvalidOperationException($"No {kind.Label} session exists for {cwd}");
        }
        else
        {
            var timestamp = (timeProvider ?? TimeProvider.System).GetUtcNow().ToUnixTimeMilliseconds()
                .ToString("D13", System.Globalization.CultureInfo.InvariantCulture);
            directory = Path.Combine(root, $"{timestamp}-{Guid.NewGuid():D}");
            Directory.CreateDirectory(directory);
            created = true;
        }

        var lockPath = Path.Combine(directory, kind.LockFileName);
        IOException? lastLockError = null;
        for (var attempt = 0; attempt <= LockRetries; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                var sessionLock = new FileStream(
                    lockPath,
                    FileMode.OpenOrCreate,
                    FileAccess.ReadWrite,
                    FileShare.None,
                    bufferSize: 1,
                    FileOptions.Asynchronous);
                return new ExperimentalSessionLocation(
                    Path.GetFileName(directory),
                    directory,
                    Path.Combine(directory, "session.sqlite"),
                    cwd,
                    created,
                    sessionLock);
            }
            catch (IOException error)
            {
                lastLockError = error;
                if (attempt == LockRetries) break;
                await Task.Delay(LockRetryDelay, cancellationToken).ConfigureAwait(false);
            }
            catch (UnauthorizedAccessException error)
            {
                throw AlreadyOpen(directory, error);
            }
        }

        throw AlreadyOpen(directory, lastLockError);
    }

    private static InvalidOperationException AlreadyOpen(string directory, Exception? cause)
        => new($"Session is already open in another process: {directory}", cause);

    /// <summary>
    /// <c>fs.realpath</c> 的等价物：逐段解析符号链接。.NET 没有一次性 realpath，
    /// 而 Node 的 <c>realpath</c> 是逐段解析的，逐段做才能得到同样的结果。
    /// </summary>
    private static string ResolveRealDirectory(string path)
    {
        var fullPath = Path.GetFullPath(path);
        if (!System.IO.Directory.Exists(fullPath))
            throw new DirectoryNotFoundException($"Working directory does not exist: {fullPath}");

        var root = Path.GetPathRoot(fullPath)
            ?? throw new IOException($"Could not resolve the root of working directory: {fullPath}");
        var current = root;
        foreach (var component in Path.GetRelativePath(root, fullPath)
                     .Split([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
                         StringSplitOptions.RemoveEmptyEntries))
        {
            current = Path.Combine(current, component);
            var target = new DirectoryInfo(current).ResolveLinkTarget(returnFinalTarget: true);
            if (target is not null) current = target.FullName;
        }

        return Path.GetFullPath(current);
    }
}
