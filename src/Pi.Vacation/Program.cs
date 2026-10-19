using Pi.CodingAgent.Experimental.Vacation;

namespace Pi.Vacation;

public static class Program
{
    public static async Task<int> Main(string[] args)
    {
        try
        {
            var options = VacationCommandLine.Parse(args);
            await using var runtime = await VacationRuntime.OpenAsync(options).ConfigureAwait(false);
            await VacationTui.RunAsync(runtime.View, runtime.Controller, runtime.Settings).ConfigureAwait(false);
            return 0;
        }
        catch (Exception error)
        {
            Console.Error.WriteLine(error);
            return 1;
        }
    }
}
