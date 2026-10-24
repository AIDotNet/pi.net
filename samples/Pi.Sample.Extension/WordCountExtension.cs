using Pi.Agent.Types;
using Pi.Ai.Types;
using Pi.CodingAgent.Core.Extensions;
using ToolDefinition = Pi.CodingAgent.Core.Extensions.ToolDefinition;
using ToolSchema = Pi.Ai.Types.ToolSchema;

namespace Pi.Sample.Extension;

/// <summary>
/// 最小可用的扩展：注册一个工具、一个斜杠命令，并在状态栏留一行字。
/// </summary>
/// <remarks>
/// <para>
/// 与上游 TypeScript 扩展的对应关系：上游 export 一个函数作为扩展入口，端口要求程序集里
/// <b>恰好一个</b> public 的 <see cref="IExtensionEntry"/> 实现，它的 <see cref="Factory"/>
/// 就是那个入口函数。
/// </para>
/// <para>
/// 用法：
/// <c>dotnet build samples/Pi.Sample.Extension</c>，把
/// <c>bin/Debug/net10.0/Pi.Sample.Extension.dll</c> 拷进项目的 <c>.pi/extensions/</c>
/// 或全局 <c>~/.pi/extensions/</c>，重启 pi 即可。模型会看到一个 <c>word_count</c> 工具，
/// 你也可以输入 <c>/wordcount 一二三</c>。
/// </para>
/// </remarks>
public sealed class WordCountExtension : IExtensionEntry
{
    public ExtensionFactory Factory => static pi =>
    {
        pi.RegisterTool(new ToolDefinition
        {
            Name = "word_count",
            Label = "Word count",
            Description = "Count the words and characters in a piece of text.",
            Parameters = new ToolSchema(new Dictionary<string, object?>
            {
                ["type"] = "object",
                ["properties"] = new Dictionary<string, object?>
                {
                    ["text"] = new Dictionary<string, object?>
                    {
                        ["type"] = "string",
                        ["description"] = "The text to count.",
                    },
                },
                ["required"] = new[] { "text" },
                ["additionalProperties"] = false,
            }),
            Execute = static (_, arguments, _, _, _) =>
            {
                if (!arguments.TryGetValue("text", out var value) || value is not string text)
                {
                    return Task.FromResult(AgentToolResult.FromError("`text` is required."));
                }

                var words = CountWords(text);
                return Task.FromResult(new AgentToolResult(
                    [new TextContent($"words={words}, characters={text.Length}")]));
            },
        });

        pi.RegisterCommand("wordcount", new CommandRegistration
        {
            Description = "Count words in the text you pass: /wordcount <text>",
            Handler = static (arguments, context) =>
            {
                var words = CountWords(arguments);
                // 状态栏只在 TUI / RPC 模式有；print 模式没有 UI 也就不显示。
                context.Ui.SetStatus("wordcount", $"words={words}");
                return Task.CompletedTask;
            },
        });

        return Task.CompletedTask;
    };

    private static int CountWords(string text)
        => text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Length;
}
