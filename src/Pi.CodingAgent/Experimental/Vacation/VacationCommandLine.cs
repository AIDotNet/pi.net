namespace Pi.CodingAgent.Experimental.Vacation;

public static class VacationCommandLine
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
