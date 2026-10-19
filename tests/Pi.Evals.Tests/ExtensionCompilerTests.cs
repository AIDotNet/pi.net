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
            if (Directory.Exists(agentDir))
            {
                Directory.Delete(agentDir, recursive: true);
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
