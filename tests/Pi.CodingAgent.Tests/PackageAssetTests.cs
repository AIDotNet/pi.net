using Pi.CodingAgent;
using Xunit;

namespace Pi.CodingAgent.Tests;

public sealed class PackageAssetTests
{
    [Fact]
    public void RuntimePackageIncludesMetadataDocumentationAndExamples()
    {
        Assert.True(File.Exists(Config.GetPackageJsonPath()));
        Assert.True(File.Exists(Config.GetReadmePath()));
        Assert.True(File.Exists(Config.GetChangelogPath()));
        Assert.True(File.Exists(Path.Combine(Config.GetDocsPath(), "providers.md")));
        Assert.NotEmpty(Directory.EnumerateFiles(Config.GetExamplesPath(), "*", SearchOption.AllDirectories));
    }
}
