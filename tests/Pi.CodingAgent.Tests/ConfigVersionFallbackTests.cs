using System.Reflection;
using System.Reflection.Emit;
using System.Text.Json;
using Xunit;

namespace Pi.CodingAgent.Tests;

/// <summary>
/// <c>Config.Version</c> 在没有可读 <c>package.json</c> 时的程序集回退（差异 C304）。
/// </summary>
/// <remarks>
/// 这条回退路径现实中并不冷门：<c>Assets/package.json</c> 是 <c>Pack="false"</c>，不会进 nupkg，
/// 所以把 Pi.CodingAgent 当库装进来的宿主**只会**走这里。上游 TS 是从模块自身目录读
/// <c>package.json</c>，<c>VERSION</c> 永远是 pi 包的版本、与宿主无关；本用例守住同一语义。
/// </remarks>
public sealed class ConfigVersionFallbackTests
{
    [Theory]
    [InlineData("1.4.2+dd67c74a1b2c3d4e5f60718293a4b5c6d7e8f901", "1.4.2")]
    [InlineData("1.4.2", "1.4.2")]
    [InlineData("1.4.2-rc.1+abcdef0", "1.4.2-rc.1")]
    [InlineData("1.4.2-beta.3", "1.4.2-beta.3")]
    public void StripsBuildMetadataButKeepsPrerelease(string informational, string expected) =>
        Assert.Equal(expected, Config.ReadAssemblyVersionFrom(FakeAssembly(informational)));

    [Fact]
    public void FallsBackToTheCodingAgentAssemblyRatherThanTheEntryAssembly()
    {
        var entry = Assembly.GetEntryAssembly();
        Assert.NotNull(entry);

        // 前提：本测试项目的 <Version> 被刻意设成 0.0.1（见 csproj），与 Pi.CodingAgent 错开。
        // 少了这个前提，两种实现在这里给出同一个值，断言就失效了。
        Assert.NotEqual(Expect(entry), Expect(typeof(Config).Assembly));

        Assert.Equal(Expect(typeof(Config).Assembly), Config.ReadAssemblyVersion());
    }

    [Fact]
    public void ReadsTheAssemblyItIsGiven()
    {
        // 与本包无关的程序集：返回值必须跟着入参走，而不是固定读 Config 所在的程序集。
        var foreign = typeof(JsonDocument).Assembly;
        Assert.NotEqual(Expect(typeof(Config).Assembly), Expect(foreign));

        Assert.Equal(Expect(foreign), Config.ReadAssemblyVersionFrom(foreign));
    }

    [Fact]
    public void RealAssemblyDropsTheCommitSuffix()
    {
        var informational = typeof(Config).Assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()!.InformationalVersion;

        // 前提：SDK 生成的 InformationalVersion 带 "+<sha>"，否则本用例测不到剥离逻辑。
        Assert.Contains("+", informational);

        var actual = Config.ReadAssemblyVersionFrom(typeof(Config).Assembly);

        Assert.Equal(informational[..informational.IndexOf('+')], actual);
        Assert.DoesNotContain("+", actual);
    }

    /// <summary>只存在于内存里的程序集，用来把指定的 InformationalVersion 喂给被测代码。</summary>
    private static Assembly FakeAssembly(string informational)
    {
        var builder = AssemblyBuilder.DefineDynamicAssembly(
            new AssemblyName($"Pi.FakeVersion.{Guid.NewGuid():N}"),
            AssemblyBuilderAccess.Run);
        var constructor = typeof(AssemblyInformationalVersionAttribute).GetConstructor([typeof(string)])!;
        builder.SetCustomAttribute(new CustomAttributeBuilder(constructor, [informational]));
        return builder;
    }

    /// <summary>期望值：InformationalVersion 去掉 build metadata，缺失则退回三段程序集版本。</summary>
    private static string Expect(Assembly assembly)
    {
        var informational = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        if (!string.IsNullOrEmpty(informational))
        {
            var plus = informational.IndexOf('+');
            return plus >= 0 ? informational[..plus] : informational;
        }

        var version = assembly.GetName().Version;
        return version is null ? "0.0.0" : $"{version.Major}.{version.Minor}.{version.Build}";
    }
}
