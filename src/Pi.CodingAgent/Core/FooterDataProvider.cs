using Pi.CodingAgent.Core.Extensions.Types;
using Pi.CodingAgent.Utils;

namespace Pi.CodingAgent.Core;

/// <summary>对应 TS <c>GitPaths</c>。</summary>
public sealed record GitPaths
{
    public required string RepoDir { get; init; }

    public required string CommonGitDir { get; init; }

    public required string HeadPath { get; init; }
}

/// <summary>
/// Git 分支与扩展状态。对应 TS <c>core/footer-data-provider.ts</c>。
/// 4e-3d-1：FindGitPaths、同步读分支、扩展 status、cwd 切换；HEAD 监视用 FileSystemWatcher。
/// </summary>
public sealed class FooterDataProvider : IReadonlyFooterDataProvider, IDisposable
{
    private const int WatchDebounceMs = 500;
    private readonly Dictionary<string, string> _extensionStatuses = new(StringComparer.Ordinal);
    private readonly HashSet<Action> _branchChangeCallbacks = [];
    private string _cwd;
    private GitPaths? _gitPaths;
    private string? _cachedBranch;
    private bool _hasCachedBranch;
    private FileSystemWatcher? _headWatcher;
    private FileSystemWatcher? _reftableWatcher;
    private Timer? _refreshTimer;
    private int _availableProviderCount;
    private bool _disposed;

    public FooterDataProvider(string cwd)
    {
        _cwd = cwd;
        _gitPaths = FindGitPaths(cwd);
        SetupGitWatcher();
    }

    /// <summary>对应 TS <c>findGitPaths</c>。</summary>
    public static GitPaths? FindGitPaths(string cwd)
    {
        var dir = Path.GetFullPath(cwd);
        while (true)
        {
            var gitPath = Path.Combine(dir, ".git");
            if (File.Exists(gitPath))
            {
                try
                {
                    var content = File.ReadAllText(gitPath).Trim();
                    if (content.StartsWith("gitdir: ", StringComparison.Ordinal))
                    {
                        var gitDir = Path.GetFullPath(Path.Combine(dir, content["gitdir: ".Length..].Trim()));
                        var headPath = Path.Combine(gitDir, "HEAD");
                        if (!File.Exists(headPath))
                        {
                            return null;
                        }

                        var commonDirPath = Path.Combine(gitDir, "commondir");
                        var commonGitDir = File.Exists(commonDirPath)
                            ? Path.GetFullPath(Path.Combine(gitDir, File.ReadAllText(commonDirPath).Trim()))
                            : gitDir;
                        return new GitPaths { RepoDir = dir, CommonGitDir = commonGitDir, HeadPath = headPath };
                    }
                }
                catch (IOException)
                {
                    return null;
                }
            }
            else if (Directory.Exists(gitPath))
            {
                var headPath = Path.Combine(gitPath, "HEAD");
                if (!File.Exists(headPath))
                {
                    return null;
                }

                return new GitPaths { RepoDir = dir, CommonGitDir = gitPath, HeadPath = headPath };
            }

            var parent = Path.GetDirectoryName(dir);
            if (string.IsNullOrEmpty(parent) || parent == dir)
            {
                return null;
            }

            dir = parent;
        }
    }

    public string? GetGitBranch()
    {
        if (!_hasCachedBranch)
        {
            _cachedBranch = ResolveGitBranchSync();
            _hasCachedBranch = true;
        }

        return _cachedBranch;
    }

    public IReadOnlyDictionary<string, string> GetExtensionStatuses() => _extensionStatuses;

    public Action OnBranchChange(Action callback)
    {
        _branchChangeCallbacks.Add(callback);
        return () => _branchChangeCallbacks.Remove(callback);
    }

    public void SetExtensionStatus(string key, string? text)
    {
        if (text is null)
        {
            _extensionStatuses.Remove(key);
        }
        else
        {
            _extensionStatuses[key] = text;
        }
    }

    public void ClearExtensionStatuses() => _extensionStatuses.Clear();

    public int GetAvailableProviderCount() => _availableProviderCount;

    public void SetAvailableProviderCount(int count) => _availableProviderCount = count;

    public void SetCwd(string cwd)
    {
        if (_cwd == cwd)
        {
            return;
        }

        _cwd = cwd;
        CancelRefresh();
        ClearGitWatchers();
        _hasCachedBranch = false;
        _cachedBranch = null;
        _gitPaths = FindGitPaths(cwd);
        SetupGitWatcher();
        NotifyBranchChange();
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        CancelRefresh();
        ClearGitWatchers();
        _branchChangeCallbacks.Clear();
    }

    private void NotifyBranchChange()
    {
        foreach (var callback in _branchChangeCallbacks.ToArray())
        {
            callback();
        }
    }

    private string? ResolveGitBranchSync()
    {
        try
        {
            if (_gitPaths is null)
            {
                return null;
            }

            var content = File.ReadAllText(_gitPaths.HeadPath).Trim();
            if (content.StartsWith("ref: refs/heads/", StringComparison.Ordinal))
            {
                var branch = content["ref: refs/heads/".Length..];
                return branch == ".invalid" ? ResolveBranchWithGitSync(_gitPaths.RepoDir) ?? "detached" : branch;
            }

            return "detached";
        }
        catch (IOException)
        {
            return null;
        }
    }

    private static string? ResolveBranchWithGitSync(string repoDir)
    {
        var result = ChildProcess.SpawnSync(
            "git",
            ["--no-optional-locks", "symbolic-ref", "--quiet", "--short", "HEAD"],
            new SpawnSyncOptions { Cwd = repoDir });
        if (result.Status is not 0)
        {
            return null;
        }

        var branch = result.Stdout.Trim();
        return branch.Length == 0 ? null : branch;
    }

    private void SetupGitWatcher()
    {
        ClearGitWatchers();
        if (_gitPaths is null)
        {
            return;
        }

        try
        {
            var dir = Path.GetDirectoryName(_gitPaths.HeadPath);
            if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir))
            {
                return;
            }

            _headWatcher = new FileSystemWatcher(dir)
            {
                Filter = "HEAD",
                NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName | NotifyFilters.Size,
                EnableRaisingEvents = true,
            };
            _headWatcher.Changed += (_, _) => ScheduleRefresh();
            _headWatcher.Created += (_, _) => ScheduleRefresh();
            _headWatcher.Renamed += (_, _) => ScheduleRefresh();
        }
        catch (IOException)
        {
        }

        var reftableDir = Path.Combine(_gitPaths.CommonGitDir, "reftable");
        if (!Directory.Exists(reftableDir))
        {
            return;
        }

        try
        {
            _reftableWatcher = new FileSystemWatcher(reftableDir)
            {
                NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName | NotifyFilters.Size,
                EnableRaisingEvents = true,
            };
            _reftableWatcher.Changed += (_, _) => ScheduleRefresh();
            _reftableWatcher.Created += (_, _) => ScheduleRefresh();
        }
        catch (IOException)
        {
        }
    }

    private void ScheduleRefresh()
    {
        if (_disposed || _refreshTimer is not null)
        {
            return;
        }

        _refreshTimer = new Timer(_ =>
        {
            _refreshTimer = null;
            if (_disposed)
            {
                return;
            }

            var next = ResolveGitBranchSync();
            if (_hasCachedBranch && _cachedBranch != next)
            {
                _cachedBranch = next;
                NotifyBranchChange();
                return;
            }

            _cachedBranch = next;
            _hasCachedBranch = true;
        }, null, WatchDebounceMs, Timeout.Infinite);
    }

    private void CancelRefresh()
    {
        _refreshTimer?.Dispose();
        _refreshTimer = null;
    }

    private void ClearGitWatchers()
    {
        _headWatcher?.Dispose();
        _headWatcher = null;
        _reftableWatcher?.Dispose();
        _reftableWatcher = null;
    }
}
