using Pi.CodingAgent.Experimental.Durable;

namespace Pi.DurableHost;

/// <summary>
/// 对应 TS <c>experimental/durable/main.ts</c>：解析 <c>--continue</c>，打开 durable 会话，跑它的 TUI，
/// 最后关闭（关闭会写回会话；被中断的一轮在下次 <c>--continue</c> 时继续）。
/// </summary>
public static class Program
{
    public static async Task<int> Main(string[] args)
    {
        try
        {
            var options = DurableCommandLine.Parse(args);
            await using var runtime = await DurableRuntime.OpenAsync(options).ConfigureAwait(false);
            await DurableTui.RunAsync(runtime.View, runtime.Controller, runtime.Settings).ConfigureAwait(false);
            return 0;
        }
        catch (Exception error)
        {
            Console.Error.WriteLine(error);
            return 1;
        }
    }
}
