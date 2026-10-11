using Pi.CodingAgent.Core;
using Xunit;

namespace Pi.CodingAgent.Tests;

public sealed class FooterDataProviderTests : IDisposable
{
    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "pi-footer-" + Guid.NewGuid().ToString("N")[..8]);

    public FooterDataProviderTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_root))
            {
                Directory.Delete(_root, recursive: true);
            }
        }
        catch (IOException)
        {
        }
    }

    [Fact]
    public void FindGitPaths_ReadsRegularRepo()
    {
        var repo = Path.Combine(_root, "repo");
        var git = Path.Combine(repo, ".git");
        Directory.CreateDirectory(git);
        File.WriteAllText(Path.Combine(git, "HEAD"), "ref: refs/heads/main\n");
        var paths = FooterDataProvider.FindGitPaths(repo);
        Assert.NotNull(paths);
        Assert.Equal(Path.GetFullPath(repo), paths!.RepoDir);
        Assert.Equal(Path.GetFullPath(git), paths.CommonGitDir);
        Assert.Equal(Path.GetFullPath(Path.Combine(git, "HEAD")), paths.HeadPath);
    }

    [Fact]
    public void FindGitPaths_ReadsWorktreeGitdirFile()
    {
        var main = Path.Combine(_root, "main");
        var worktree = Path.Combine(_root, "work");
        var gitDir = Path.Combine(main, ".git", "worktrees", "work");
        Directory.CreateDirectory(gitDir);
        Directory.CreateDirectory(worktree);
        File.WriteAllText(Path.Combine(gitDir, "HEAD"), "ref: refs/heads/feature\n");
        File.WriteAllText(Path.Combine(gitDir, "commondir"), "../..\n");
        File.WriteAllText(Path.Combine(worktree, ".git"), $"gitdir: {gitDir}\n");
        var paths = FooterDataProvider.FindGitPaths(worktree);
        Assert.NotNull(paths);
        Assert.Equal(Path.GetFullPath(worktree), paths!.RepoDir);
        Assert.Equal(Path.GetFullPath(Path.Combine(main, ".git")), paths.CommonGitDir);
    }

    [Fact]
    public void GetGitBranch_ReadsHeadRef()
    {
        var repo = Path.Combine(_root, "branch");
        Directory.CreateDirectory(Path.Combine(repo, ".git"));
        File.WriteAllText(Path.Combine(repo, ".git", "HEAD"), "ref: refs/heads/dev\n");
        using var provider = new FooterDataProvider(repo);
        Assert.Equal("dev", provider.GetGitBranch());
    }

    [Fact]
    public void GetGitBranch_DetachedHead()
    {
        var repo = Path.Combine(_root, "detach");
        Directory.CreateDirectory(Path.Combine(repo, ".git"));
        File.WriteAllText(Path.Combine(repo, ".git", "HEAD"), "abc123def456\n");
        using var provider = new FooterDataProvider(repo);
        Assert.Equal("detached", provider.GetGitBranch());
    }

    [Fact]
    public void ExtensionStatus_SetAndClear()
    {
        using var provider = new FooterDataProvider(_root);
        provider.SetExtensionStatus("mcp", "ready");
        Assert.Equal("ready", provider.GetExtensionStatuses()["mcp"]);
        provider.SetExtensionStatus("mcp", null);
        Assert.Empty(provider.GetExtensionStatuses());
        provider.SetExtensionStatus("a", "1");
        provider.ClearExtensionStatuses();
        Assert.Empty(provider.GetExtensionStatuses());
    }

    [Fact]
    public void SetCwd_NotifiesBranchChange()
    {
        var first = Path.Combine(_root, "a");
        var second = Path.Combine(_root, "b");
        Directory.CreateDirectory(Path.Combine(first, ".git"));
        Directory.CreateDirectory(Path.Combine(second, ".git"));
        File.WriteAllText(Path.Combine(first, ".git", "HEAD"), "ref: refs/heads/one\n");
        File.WriteAllText(Path.Combine(second, ".git", "HEAD"), "ref: refs/heads/two\n");
        using var provider = new FooterDataProvider(first);
        Assert.Equal("one", provider.GetGitBranch());
        var notified = 0;
        provider.OnBranchChange(() => notified++);
        provider.SetCwd(second);
        Assert.True(notified >= 1);
        Assert.Equal("two", provider.GetGitBranch());
    }
}
