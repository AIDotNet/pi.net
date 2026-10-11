//------------------------------------------------------------------------------
// Explicit xunit.v3 / Microsoft.Testing.Platform entry point. The MSBuild
// auto-generated copies live under obj/ and are not guaranteed after a clean
// rebuild in restricted environments; keep this file as the source of truth.
//------------------------------------------------------------------------------

namespace Pi.CodingAgent.Tests;

[System.Diagnostics.CodeAnalysis.ExcludeFromCodeCoverage]
internal static class SelfRegisteredExtensions
{
    public static void AddSelfRegisteredExtensions(
        this Microsoft.Testing.Platform.Builder.ITestApplicationBuilder builder,
        string[] args)
    {
        Microsoft.Testing.Platform.MSBuild.TestingPlatformBuilderHook.AddExtensions(builder, args);
        Microsoft.Testing.Extensions.Telemetry.TestingPlatformBuilderHook.AddExtensions(builder, args);
    }
}

[System.Diagnostics.CodeAnalysis.ExcludeFromCodeCoverage]
internal static class TestEntryPoint
{
    public static int Main(string[] args)
    {
        if (args.Any(arg => arg == "-automated" || arg == "@@"))
        {
            return Xunit.Runner.InProc.SystemConsole.ConsoleRunner.Run(args).GetAwaiter().GetResult();
        }

        return Xunit.MicrosoftTestingPlatform.TestPlatformTestFramework
            .RunAsync(args, SelfRegisteredExtensions.AddSelfRegisteredExtensions)
            .GetAwaiter()
            .GetResult();
    }
}
