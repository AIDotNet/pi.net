using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Pi.CodingAgent.Core;

namespace Pi.CodingAgent.Experimental.Vacation;

/// <summary>One Vacation database location, exclusively locked until the runtime closes.</summary>
public sealed class VacationSessionLocation : IAsyncDisposable
{
    private FileStream? _lock;

    internal VacationSessionLocation(
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

/// <summary>Selects a new or most-recent Vacation session and prevents concurrent opens.</summary>
public static partial class VacationSessions
{
    private const int LockRetries = 12;
    private static readonly TimeSpan LockRetryDelay = TimeSpan.FromSeconds(1);

    [GeneratedRegex(@"\A[0-9]{13}-[0-9a-f-]{36}\z", RegexOptions.CultureInvariant)]
    private static partial Regex SessionDirectoryName();

    public static async Task<VacationSessionLocation> SelectAsync(
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
            "vacation-sessions",
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
                    : throw new InvalidOperationException($"No vacation session exists for {cwd}");
        }
        else
        {
            var timestamp = (timeProvider ?? TimeProvider.System).GetUtcNow().ToUnixTimeMilliseconds()
                .ToString("D13", System.Globalization.CultureInfo.InvariantCulture);
            directory = Path.Combine(root, $"{timestamp}-{Guid.NewGuid():D}");
            Directory.CreateDirectory(directory);
            created = true;
        }

        var lockPath = Path.Combine(directory, ".vacation.lock");
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
                return new VacationSessionLocation(
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
