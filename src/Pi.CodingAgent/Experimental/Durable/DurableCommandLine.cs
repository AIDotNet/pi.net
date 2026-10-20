namespace Pi.CodingAgent.Experimental.Durable;

/// <summary>
/// <c>main.ts</c> 的参数解析：只认 <c>--continue</c> / <c>-c</c>，其它一律报错。
/// 对应 TS <c>parseArgs</c>。
/// </summary>
public static class DurableCommandLine
{
    public static OpenExperimentalOptions Parse(IReadOnlyList<string> arguments)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        var continueSession = false;
        foreach (var argument in arguments)
        {
            if (argument is "--continue" or "-c")
            {
                continueSession = true;
                continue;
            }

            throw new ArgumentException($"Unknown argument: {argument}", nameof(arguments));
        }

        return new OpenExperimentalOptions { ContinueSession = continueSession };
    }
}
