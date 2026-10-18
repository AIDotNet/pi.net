using Pi.CodingAgent.Cli;
using Pi.CodingAgent.Core;
using Pi.CodingAgent.Utils;
using Xunit;

namespace Pi.CodingAgent.Tests;

/// <summary>
/// <c>cli/project-trust.ts</c>：上下文形状（尤其 <c>mode</c> 的 interactive → tui 映射）
/// 与「没有 UI / 非交互模式一律不弹窗」的四个闸门。
/// </summary>
public class CliProjectTrustUiTests
{
    [Fact]
    public void InteractiveModeIsReportedAsTui()
    {
        var context = Create(AppModes.Interactive, hasUI: true);

        Assert.Equal("tui", context.Mode);
        Assert.Equal("/work", context.Cwd);
        Assert.True(context.HasUI);
    }

    [Theory]
    [InlineData(AppModes.Print)]
    [InlineData(AppModes.Json)]
    [InlineData(AppModes.Rpc)]
    public void NonInteractiveModesPassThroughUnchanged(string mode)
    {
        Assert.Equal(mode, Create(mode, hasUI: false).Mode);
    }

    [Fact]
    public async Task NoUiReturnsDefaultsWithoutPrompting()
    {
        var context = Create(AppModes.Interactive, hasUI: false);

        Assert.Null(await context.Ui.Select("title", ["a", "b"]));
        Assert.False(await context.Ui.Confirm("title", "message"));
        Assert.Null(await context.Ui.Input("title", "placeholder"));
    }

    [Fact]
    public async Task NonInteractiveModeReturnsDefaultsEvenWithUi()
    {
        // hasUI 为真但 mode 不是 interactive：TS 的第二道闸门同样拦下来。
        var context = Create(AppModes.Rpc, hasUI: true);

        Assert.Null(await context.Ui.Select("title", ["a"]));
        Assert.False(await context.Ui.Confirm("title", "message"));
        Assert.Null(await context.Ui.Input("title"));
    }

    [Fact]
    public void NotifyIsSilentInInteractiveMode()
    {
        var context = Create(AppModes.Interactive, hasUI: true);
        var (writer, original) = CaptureErrorWithColour();
        try
        {
            context.Ui.Notify("hello", "error");
        }
        finally
        {
            Console.SetError(original);
            Chalk.EnabledOverride = null;
        }

        Assert.Equal("", writer.ToString());
    }

    [Theory]
    [InlineData("error", "\u001b[31m")]
    [InlineData("warning", "\u001b[33m")]
    [InlineData("info", "\u001b[36m")]
    [InlineData(null, "\u001b[36m")]
    public void NotifyPrintsInNonInteractiveModesWithTypeColour(string? type, string expectedPrefix)
    {
        var context = Create(AppModes.Print, hasUI: false);
        var (writer, original) = CaptureErrorWithColour();
        try
        {
            context.Ui.Notify("boom", type);
        }
        finally
        {
            Console.SetError(original);
            Chalk.EnabledOverride = null;
        }

        Assert.Equal($"{expectedPrefix}boom\u001b[39m{Environment.NewLine}", writer.ToString());
    }

    private static Core.Extensions.ProjectTrustContext Create(string mode, bool hasUI) =>
        ProjectTrustUi.CreateProjectTrustContext(new ProjectTrustUiOptions
        {
            Cwd = "/work",
            Mode = mode,
            SettingsManager = SettingsManager.InMemory(new Settings()),
            HasUI = hasUI,
        });

    private static (StringWriter Text, TextWriter Original) CaptureError()
    {
        var original = Console.Error;
        var writer = new StringWriter();
        Console.SetError(writer);
        return (writer, original);
    }

    /// <summary>
    /// 同上，并强制打开 chalk（重定向输出时 chalk 默认判定为不支持颜色）。
    /// 调用方负责在 finally 里把 <see cref="Chalk.EnabledOverride"/> 还原成 null。
    /// </summary>
    private static (StringWriter Text, TextWriter Original) CaptureErrorWithColour()
    {
        Chalk.EnabledOverride = true;
        return CaptureError();
    }
}
