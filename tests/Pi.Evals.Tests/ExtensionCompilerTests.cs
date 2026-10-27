using System.Runtime.CompilerServices;
using Pi.CodingAgent.Core.Extensions;
using Xunit;

namespace Pi.Evals.Tests;

public sealed class ExtensionCompilerTests
{
    [Fact]
    public async Task CompileAsync_BuildsAssemblyThatNativeLoaderCanLoad()
    {
        var agentDir = Path.Combine(Path.GetTempPath(), "pi-eval-compiler-" + Guid.NewGuid().ToString("N"));
        var extensionDir = Path.Combine(agentDir, "extensions", "sample");
        Directory.CreateDirectory(extensionDir);
        var assemblyPath = Path.Combine(agentDir, "extensions", "sample.dll");
        var loader = new AssemblyLoadContextExtensionLoader();

        try
        {
            await File.WriteAllTextAsync(
                Path.Combine(extensionDir, "Entry.cs"),
                """
                using Pi.CodingAgent.Core.Extensions;

                public sealed class SampleExtension : IExtensionEntry
                {
                    public ExtensionFactory Factory => static _ => Task.CompletedTask;
                }
                """);

            var builtPath = await ExtensionCompiler.CompileAsync(agentDir, "sample");

            Assert.Equal(assemblyPath, builtPath);
            Assert.True(File.Exists(builtPath));
            await LoadAndUnloadAsync(loader, builtPath);
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
        }
        finally
        {
            loader.Unload(assemblyPath);
            DeleteDirectoryWithRetry(agentDir);
        }
    }

    /// <summary>
    /// 删除临时目录，容忍一次卸载后仍被占用的程序集。
    /// </summary>
    /// <remarks>
    /// collectible 的 <see cref="AssemblyLoadContext"/> 卸载是异步的：Windows 上文件句柄在
    /// <c>Unload</c> 返回后还要等运行时真正释放。net9.0 的释放明显比 net10.0 慢，紧接着删目录会
    /// 稳定抛 <c>UnauthorizedAccessException: Access to the path 'sample.dll' is denied</c>
    /// （删除已加载的程序集所在目录）。这里重试几次；句柄迟迟不释放时最后一次仍照原样抛出，
    /// 免得把真正的缺陷藏起来。
    /// </remarks>
    private static void DeleteDirectoryWithRetry(string path)
    {
        const int attempts = 25;
        for (var attempt = 1; attempt <= attempts; attempt++)
        {
            if (!Directory.Exists(path))
            {
                return;
            }

            try
            {
                Directory.Delete(path, recursive: true);
                return;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                if (attempt == attempts)
                {
                    throw;
                }

                GC.Collect();
                GC.WaitForPendingFinalizers();
                Thread.Sleep(100);
            }
        }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static async Task LoadAndUnloadAsync(AssemblyLoadContextExtensionLoader loader, string assemblyPath)
    {
        Assert.NotNull(await loader.LoadAsync(assemblyPath));
        loader.Unload(assemblyPath);
    }

    [Theory]
    [InlineData("../outside")]
    [InlineData("Uppercase")]
    [InlineData("has space")]
    public async Task CompileAsync_RejectsInvalidExtensionName(string name)
    {
        var agentDir = Path.Combine(Path.GetTempPath(), "pi-eval-compiler-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(agentDir);
        try
        {
            await Assert.ThrowsAsync<ArgumentException>(() => ExtensionCompiler.CompileAsync(agentDir, name));
        }
        finally
        {
            Directory.Delete(agentDir, recursive: true);
        }
    }
}
