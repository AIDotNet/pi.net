// ============================================================================
// 嵌套工作树里的项目上下文文件去重。
//
// 上游 f1b2e77f5（#10681）：去重比较的是上下文文件**自己的 realpath**，于是工作树里
// 符号链接到主仓库那份的 AGENTS.md 会被当成「主仓库那份」而一起跳过——工作树反而一份都不加载。
// 改为比较「走过的目录的 realpath + 文件名」。
// ============================================================================

using Pi.CodingAgent.Core;
using Xunit;

namespace Pi.CodingAgent.Tests;

public sealed class ResourceLoaderWorktreeTests : IDisposable
{
    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "pi-worktree-context-" + Guid.NewGuid().ToString("N")[..8]);

    private readonly string _agentDir;

    public ResourceLoaderWorktreeTests()
    {
        _agentDir = Path.Combine(_root, "agent");
        Directory.CreateDirectory(_agentDir);
    }

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

    /// <summary>
    /// 搭一个链接工作树的骨架（不需要 git）：<c>main/.git/worktrees/feat</c> 里有 <c>HEAD</c> 与指回主
    /// <c>.git</c> 的 <c>commondir</c>，工作树自己的 <c>.git</c> 是内容为 <c>gitdir:</c> 的**文件**。
    /// </summary>
    private static (string Outer, string Main, string Worktree, string WorktreeSrc) NestedWorktree(string root)
    {
        var outer = Path.Combine(root, "outer");
        var main = Path.Combine(outer, "main");
        var worktree = Path.Combine(main, "worktrees", "feat");
        var worktreeSrc = Path.Combine(worktree, "src");
        Directory.CreateDirectory(worktreeSrc);

        var gitDir = Path.Combine(main, ".git", "worktrees", "feat");
        Directory.CreateDirectory(gitDir);
        File.WriteAllText(Path.Combine(main, ".git", "HEAD"), "ref: refs/heads/main\n");
        File.WriteAllText(Path.Combine(gitDir, "HEAD"), "ref: refs/heads/feat\n");
        File.WriteAllText(Path.Combine(gitDir, "commondir"), "../..");
        File.WriteAllText(Path.Combine(worktree, ".git"), $"gitdir: {gitDir}\n");
        return (outer, main, worktree, worktreeSrc);
    }

    /// <summary>建符号链接；Windows 上没开开发者模式会失败，此时跳过用例（上游在 win32 上同样跳过）。</summary>
    private static void RequireSymlink(string path, string target)
    {
        try
        {
            File.CreateSymbolicLink(path, target);
        }
        catch (Exception error) when (error is UnauthorizedAccessException or IOException)
        {
            Assert.Skip($"Symlinks are unavailable here: {error.Message}");
        }
    }

    [Fact]
    public void SkipsTheMainReposDuplicateWhenTheWorktreeHasItsOwnContext()
    {
        var (_, main, worktree, worktreeSrc) = NestedWorktree(_root);
        File.WriteAllText(Path.Combine(main, "AGENTS.md"), "main repo instructions");
        File.WriteAllText(Path.Combine(worktree, "AGENTS.md"), "worktree instructions");

        var files = DefaultResourceLoader.LoadProjectContextFiles(worktreeSrc, _agentDir);

        Assert.Equal(["worktree instructions"], files.Select(file => file.Content));
    }

    /// <summary>工作树里的 AGENTS.md 符号链接到主仓库那份时，加载的是工作树这条路径（上游 #10681）。</summary>
    [Fact]
    public void LoadsTheWorktreesContextOnceWhenItSymlinksToTheMainReposFile()
    {
        var (_, main, worktree, worktreeSrc) = NestedWorktree(_root);
        File.WriteAllText(Path.Combine(main, "AGENTS.md"), "main repo instructions");
        RequireSymlink(Path.Combine(worktree, "AGENTS.md"), Path.Combine(main, "AGENTS.md"));

        var files = DefaultResourceLoader.LoadProjectContextFiles(worktreeSrc, _agentDir);

        Assert.Equal([Path.Combine(worktree, "AGENTS.md")], files.Select(file => file.Path));
    }

    /// <summary>两边都链接到同一个文件时仍只加载工作树那条：主仓库那份被判为重复。</summary>
    [Fact]
    public void SkipsTheMainReposDuplicateWhenBothSymlinkToOneFile()
    {
        var (outer, main, worktree, worktreeSrc) = NestedWorktree(_root);
        var shared = Path.Combine(outer, "shared-agents.md");
        File.WriteAllText(shared, "shared instructions");
        RequireSymlink(Path.Combine(main, "AGENTS.md"), shared);
        RequireSymlink(Path.Combine(worktree, "AGENTS.md"), shared);

        var files = DefaultResourceLoader.LoadProjectContextFiles(worktreeSrc, _agentDir);

        Assert.Equal([Path.Combine(worktree, "AGENTS.md")], files.Select(file => file.Path));
    }

    /// <summary>工作树根没有上下文时照常继承主仓库的（回归保护：修复不能把主仓库那份也丢掉）。</summary>
    [Fact]
    public void StillInheritsTheMainReposContextWhenTheWorktreeRootHasNone()
    {
        var (_, main, _, worktreeSrc) = NestedWorktree(_root);
        File.WriteAllText(Path.Combine(main, "AGENTS.md"), "main repo instructions");

        var files = DefaultResourceLoader.LoadProjectContextFiles(worktreeSrc, _agentDir);

        Assert.Equal(["main repo instructions"], files.Select(file => file.Content));
    }

    // ─── 判定本身 ───────────────────────────────────────────────────────────────
    // 真实现场是「工作树的 AGENTS.md 符号链接到主仓库那份」：文件 realpath 就等于被遮蔽的路径。
    // 建符号链接需要权限，所以这里用 `..` 造出同样的形状——路径规范化后正好是主仓库那份——
    // 直接锁住「判定看目录 + 文件名，不看文件自己的 realpath」这一点。

    /// <summary>
    /// 走过的目录是工作树，而文件的规范路径落在主仓库（符号链接的情形），不算被遮蔽（上游 #10681）。
    /// </summary>
    [Fact]
    public void AWorktreeContextFileThatResolvesIntoTheMainRepoIsNotShadowed()
    {
        var (_, main, worktree, _) = NestedWorktree(_root);
        var mainFile = Path.Combine(main, "AGENTS.md");
        File.WriteAllText(mainFile, "main repo instructions");
        // worktree/../.. == main，因此这个路径的规范形式正是主仓库那份。
        var resolvesToMainRepo = Path.Combine(worktree, "..", "..", "AGENTS.md");

        Assert.False(DefaultResourceLoader.IsShadowedContextFile(worktree, resolvesToMainRepo, mainFile));
    }

    /// <summary>主仓库目录里那个同名文件就是被遮蔽的那份。</summary>
    [Fact]
    public void TheMainReposOwnContextFileIsShadowed()
    {
        var (_, main, _, _) = NestedWorktree(_root);
        var mainFile = Path.Combine(main, "AGENTS.md");
        File.WriteAllText(mainFile, "main repo instructions");

        Assert.True(DefaultResourceLoader.IsShadowedContextFile(main, mainFile, mainFile));
    }
}
