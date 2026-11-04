using Pi.Ai.Models;
using Pi.Ai.Types;
using Pi.CodingAgent.Core;
using Pi.CodingAgent.Modes.Interactive;
using Pi.Tui;
using Pi.CodingAgent.Utils;
using Xunit;

namespace Pi.CodingAgent.Tests;

/// <summary>
/// 全屏标题栏的模型 scope 提示（`7391dbb0f`，上游 interactive-mode-model-scope.test.ts）：
/// 全屏把提示挂到头部而不是打到 shell（退出后备用屏不会残留），启动详情被隐藏时什么都不加。
/// </summary>
public class InteractiveModeModelScopeTests
{
    private static readonly IReadOnlyList<ScopedModel> Scope =
    [
        new ScopedModel { Model = Model("model-a"), ThinkingLevel = ThinkingLevel.High },
        new ScopedModel { Model = Model("model-b") },
    ];

    [Fact]
    public void FormatsTheModelScopeNoticeWithThinkingLevelsAndCycleKeys()
    {
        ThemeRuntime.InitTheme("dark", false, new ThemeSeams { ColorMode = TerminalColorMode.Truecolor });
        var notice = InteractiveMode.FormatModelScopeNotice(Scope, true, _ => ["ctrl+p"]);
        Assert.NotNull(notice);
        Assert.Contains("Model scope: model-a:high, model-b (Ctrl+P to cycle)", AnsiText.StripAnsi(notice));
    }

    [Fact]
    public void AddsNothingWhenStartupDetailsAreHiddenOrThereIsNoScope()
    {
        Assert.Null(InteractiveMode.FormatModelScopeNotice(Scope, false, _ => ["ctrl+p"]));
        Assert.Null(InteractiveMode.FormatModelScopeNotice([], true, _ => ["ctrl+p"]));
    }

    private static ModelSpec Model(string id) => new()
    {
        Id = id,
        Name = id,
        Api = "test-api",
        Provider = "test",
        BaseUrl = "https://example.invalid",
        ContextWindow = 200_000,
    };
}
