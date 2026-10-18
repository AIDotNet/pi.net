using Pi.Ai.Models;
using Pi.CodingAgent.Cli;
using Xunit;

namespace Pi.CodingAgent.Tests;

/// <summary>
/// <c>cli/list-models.ts</c> 的纯排版层。期望值全部由 TS 实现在 Node 里跑出
/// （<c>formatTokenCount</c> 逐字抄进脚本、表格渲染同理），不是手算回抄。
/// </summary>
public class CliListModelsTests
{
    [Theory]
    [InlineData(0, "0")]
    [InlineData(1, "1")]
    [InlineData(999, "999")]
    [InlineData(1000, "1K")]
    [InlineData(1001, "1.0K")]
    [InlineData(1024, "1.0K")]
    [InlineData(1500, "1.5K")]
    [InlineData(8192, "8.2K")]
    [InlineData(32000, "32K")]
    [InlineData(64000, "64K")]
    [InlineData(65536, "65.5K")]
    [InlineData(128000, "128K")]
    [InlineData(200000, "200K")]
    [InlineData(262144, "262.1K")]
    // 999_999 落在 K 分支且四舍五入到 1000.0——TS 不会回头升级成 M，这里必须保持同样结果。
    [InlineData(999999, "1000.0K")]
    [InlineData(1000000, "1M")]
    [InlineData(1048576, "1.0M")]
    [InlineData(1234567, "1.2M")]
    // toFixed(1) 对精确可表示的 1.25 是「进位到 1.3」（ECMAScript 取较大 n）；"0.0" 同。
    [InlineData(1250000, "1.3M")]
    [InlineData(1500000, "1.5M")]
    [InlineData(2000000, "2M")]
    [InlineData(10000000, "10M")]
    public void FormatTokenCountMatchesTs(long count, string expected)
    {
        Assert.Equal(expected, ListModels.FormatTokenCount(count));
    }

    [Fact]
    public void RenderTableSortsByProviderThenModelAndPadsEveryColumn()
    {
        var models = new[]
        {
            Model("openai", "gpt-5", 400000, 128000, reasoning: true, images: true),
            Model("anthropic", "claude-sonnet-4-5", 200000, 64000, reasoning: true, images: true),
            Model("google", "gemini-2.5-flash", 1048576, 65536, reasoning: false, images: true),
            Model("local", "tiny", 8192, 1024, reasoning: false, images: false),
        };

        Assert.Equal(
            new[]
            {
                "provider   model              context  max-out  thinking  images",
                "anthropic  claude-sonnet-4-5  200K     64K      yes       yes   ",
                "google     gemini-2.5-flash   1.0M     65.5K    no        yes   ",
                "local      tiny               8.2K     1.0K     no        no    ",
                "openai     gpt-5              400K     128K     yes       yes   ",
            },
            ListModels.RenderTable(models));
    }

    [Fact]
    public void RenderTablePadsLastColumnToo()
    {
        // TS 对最后一列同样 `padEnd`，所以行尾带空格——这不是笔误。
        var lines = ListModels.RenderTable([Model("x", "y", 0, 999, reasoning: false, images: false)]);
        Assert.Equal("provider  model  context  max-out  thinking  images", lines[0]);
        Assert.Equal("x         y      0        999      no        no    ", lines[1]);
        Assert.EndsWith("no    ", lines[1], StringComparison.Ordinal);
    }

    [Fact]
    public void RenderTableSortsModelIdsWithinSameProvider()
    {
        var models = new[]
        {
            Model("openai", "gpt-5-mini", 200000, 100000, reasoning: true, images: true),
            Model("openai", "gpt-5", 400000, 128000, reasoning: true, images: true),
            Model("openai", "gpt-4.1", 1000000, 32000, reasoning: false, images: true),
        };

        var lines = ListModels.RenderTable(models);

        Assert.Equal(4, lines.Count);
        // provider 列宽取表头 "provider"（8）而非 "openai"（6），故 provider 与 model 之间是 4 个空格。
        Assert.StartsWith("openai    gpt-4.1", lines[1], StringComparison.Ordinal);
        Assert.StartsWith("openai    gpt-5 ", lines[2], StringComparison.Ordinal);
        Assert.StartsWith("openai    gpt-5-mini", lines[3], StringComparison.Ordinal);
    }

    private static ModelSpec Model(
        string provider,
        string id,
        long contextWindow,
        long maxTokens,
        bool reasoning,
        bool images) => new()
        {
            Id = id,
            Name = id,
            Api = "openai-completions",
            Provider = provider,
            BaseUrl = "https://example.invalid",
            ContextWindow = contextWindow,
            MaxTokens = maxTokens,
            Reasoning = reasoning,
            Input = images ? [ModelInput.Text, ModelInput.Image] : [ModelInput.Text],
        };
}
