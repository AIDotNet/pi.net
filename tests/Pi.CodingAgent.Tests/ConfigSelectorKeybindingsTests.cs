using Pi.CodingAgent.Cli;
using Pi.Tui;
using Xunit;

namespace Pi.CodingAgent.Tests;

/// <summary>
/// 上游 1.5.1 #11651：`pi config` 之前没有加载 `keybindings.json`，自定义键位在选择器内不生效。
/// 此处验证 `ConfigSelectorCli` 现在使用的加载路径——从 agentDir 读 keybindings.json 并装入
/// GlobalKeybindings——确实把用户自定义键位（tui.select.up / tui.select.down）解析出来。
/// </summary>
public class ConfigSelectorKeybindingsTests
{
    [Fact]
    public void AgentDirKeybindingsAreLoadedIntoGlobalRegistry()
    {
        var agentDir = Path.Combine(Path.GetTempPath(), "pi-config-keys-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(agentDir);
        var original = GlobalKeybindings.Get();
        try
        {
            File.WriteAllText(
                Path.Combine(agentDir, "keybindings.json"),
                """{"tui.select.up":["up","ctrl+p"],"tui.select.down":["down","ctrl+n"]}""");

            // 直接调用 SelectConfigAsync 使用的装载方法，验证 CLI 路径确实生效。
            ConfigSelectorCli.LoadGlobalKeybindings(agentDir);

            Assert.Contains("ctrl+p", GlobalKeybindings.Get().GetKeys(TuiKeybindingIds.SelectUp));
            Assert.Contains("ctrl+n", GlobalKeybindings.Get().GetKeys(TuiKeybindingIds.SelectDown));
        }
        finally
        {
            GlobalKeybindings.Set(original);
            Directory.Delete(agentDir, recursive: true);
        }
    }
}
