using Pi.CodingAgent.Cli.Experimental;
using Xunit;

namespace Pi.CodingAgent.Tests;

/// <summary>
/// <c>cli/experimental/*</c>：对齐 <c>test/experimental-cli-command.test.ts</c> 的全部用例，
/// 并补上 <c>parseTransportAddress</c> 的语料（期望值由 TS 实现在 Node 里跑出，见提交说明）。
/// </summary>
public class CliExperimentalCommandTests
{
    private const string UnsupportedServerOptions =
        "The experimental server command does not support existing CLI options yet";

    private const string UnsupportedClientOptions =
        "The experimental client command does not support existing CLI options yet";

    private const string ServerId = "00000000-0000-4000-8000-000000000001";

    // ------------------------------------------------------------------ server

    [Fact]
    public void ParsesServerConfiguration()
    {
        var command = ParseServer(
            "server",
            "--server-id", ServerId,
            "--session-dir", "~/pi-sessions",
            "--provider", "anthropic",
            "--model", "claude-sonnet-4-5",
            "-e", "./first-plugin",
            "-e=./second-plugin");

        Assert.Equal("server", command.Command);
        Assert.Equal(ServerId, command.ServerId);
        Assert.Equal("~/pi-sessions", command.SessionDir);
        Assert.Equal("anthropic", command.Provider);
        Assert.Equal("claude-sonnet-4-5", command.Model);
        Assert.Equal(new[] { "./first-plugin", "./second-plugin" }, command.PluginPackages);
        Assert.Null(command.Auth);
    }

    [Fact]
    public void ParsesProviderQualifiedServerModels()
    {
        var command = ParseServer("server", "--model", "anthropic/claude-sonnet-4-5:high");

        Assert.Equal("anthropic/claude-sonnet-4-5:high", command.Model);
        Assert.Null(command.Provider);
    }

    // ------------------------------------------------------------------ client

    [Fact]
    public void ParsesClientTransportAddresses()
    {
        var unix = ParseClient("client", "--connect", "unix:///tmp/pi.sock");
        Assert.Equal(new TransportAddress.Unix("/tmp/pi.sock"), unix.Connect);

        var radius = ParseClient(
            "client", "--connect", $"radius://{ServerId}", "--session-id", "demo-1");
        Assert.Equal(new TransportAddress.Radius(ServerId), radius.Connect);
        Assert.Equal("demo-1", radius.SessionId);
    }

    [Theory]
    [InlineData("-c")]
    [InlineData("--continue")]
    public void ParsesContinueSelection(string option)
    {
        var command = ParseClient("client", option);

        Assert.True(command.Continue);
        Assert.Null(command.Resume);
    }

    [Theory]
    [InlineData("-r")]
    [InlineData("--resume")]
    public void ParsesResumeSelection(string option)
    {
        var command = ParseClient("client", option);

        Assert.True(command.Resume);
        Assert.Null(command.Continue);
    }

    [Fact]
    public void ParsesSessionSelectionFollowedByOneShotPrompt()
    {
        var command = ParseClient("client", "-r", "Explain this project");

        Assert.True(command.Resume);
        Assert.Equal("Explain this project", command.Prompt);
    }

    [Fact]
    public void ParsesClientModelAndPluginConfiguration()
    {
        var command = ParseClient(
            "client",
            "--model", "anthropic/claude-sonnet-4-5:high",
            "-e", "./first-plugin",
            "-e", "./second-plugin");

        Assert.Equal("anthropic/claude-sonnet-4-5:high", command.Model);
        Assert.Equal(new[] { "./first-plugin", "./second-plugin" }, command.PluginPackages);
        Assert.Null(command.Prompt);
    }

    [Fact]
    public void DoubleDashAllowsDashPrefixedPrompt()
    {
        // TS：首参是 `--` 时，后面的 token 即使以 `-` 开头也当 prompt。
        var command = ParseClient("client", "--", "--model");
        Assert.Equal("--model", command.Prompt);
    }

    // ------------------------------------------------------------------ auth

    [Fact]
    public void ParsesAuthenticationSourceForBothCommands()
    {
        var token = new AuthInput.Token("secret");
        Assert.Equal(token, ParseServer("server", "--auth-token", "secret").Auth);
        Assert.Equal(token, ParseClient("client", "--auth-token", "secret").Auth);

        var file = new AuthInput.File("/tmp/token");
        Assert.Equal(file, ParseServer("server", "--auth-token-file", "/tmp/token").Auth);
        Assert.Equal(file, ParseClient("client", "--auth-token-file", "/tmp/token").Auth);
    }

    [Fact]
    public void PermitsOmittedAuthentication()
    {
        Assert.Null(ParseServer("server").Auth);
        Assert.Null(ParseClient("client").Auth);
    }

    // ------------------------------------------------------------------ rejections

    /// <summary>
    /// 对齐 TS 的 <c>rejects invalid experimental input</c> 表。用 <see cref="TheoryData{T1,T2}"/>
    /// 而非 <c>[InlineData]</c>：数组实参不是编译期常量，属性实参会报 CS0182。
    /// </summary>
    public static TheoryData<string[], string> InvalidInputs => new()
    {
        { ["client", "--listen", "unix:///tmp/pi.sock"], UnsupportedClientOptions },
        { ["server", "--listen", "unix:///tmp/pi.sock"], UnsupportedServerOptions },
        { ["server", "--connect", "unix:///tmp/pi.sock"], UnsupportedServerOptions },
        { ["client", "--connect", "ws://localhost:8080"], "Unsupported --connect transport \"ws:\"" },
        { ["client", "--connect", "radius://not-a-server"], "Radius transport address requires" },
        { ["client", "--connect", "unix://relative.sock"], "Unix transport address must not include an authority" },
        { ["client", "--connect", "unix:///tmp/pi.sock?wrong=value"], "Invalid --connect address" },
        { ["client", "--provider", "anthropic"], "--provider requires --model" },
        { ["client", "-c", "-r"], "--session-id, --continue, and --resume are mutually exclusive" },
        { ["client", "--continue=true"], "--continue does not take a value" },
        { ["server", "--provider", "anthropic"], "--provider requires --model" },
        { ["server", "--server-id", "not-a-uuid"], "Invalid --server-id" },
        { ["server", "--server-id"], "--server-id requires a value" },
        { ["server", "--session-dir"], "--session-dir requires a value" },
        { ["client", "-e"], "-e requires a value" },
        { ["server", "--session-dir", "/tmp/first", "--session-dir=/tmp/second"], "--session-dir may only be specified once" },
        { ["client", "--connect="], "--connect requires a value" },
    };

    [Theory]
    [MemberData(nameof(InvalidInputs))]
    public void RejectsInvalidExperimentalInput(string[] argv, string expectedError)
    {
        var failed = Assert.IsType<CommandParseResult.Failed>(ExperimentalCli.Create().Parse(argv));

        Assert.Contains(failed.Errors, error => error.Contains(expectedError, StringComparison.Ordinal));
    }

    [Fact]
    public void RejectsUnsupportedOptionsWithoutParsingThemThroughTheStableCli()
    {
        var failed = Assert.IsType<CommandParseResult.Failed>(
            ExperimentalCli.Create().Parse(new[] { "client", "--tui-mode", "wrong", "--model", "claude-sonnet" }));

        Assert.Equal(new[] { UnsupportedClientOptions }, failed.Errors);
    }

    [Fact]
    public void MissingSubcommandReportsTheGroupError()
    {
        var failed = Assert.IsType<CommandParseResult.Failed>(ExperimentalCli.Create().Parse([]));
        Assert.Equal(new[] { ExperimentalCli.MissingSubcommandError }, failed.Errors);
    }

    // ------------------------------------------------------------------ transport addresses

    [Theory]
    [InlineData("unix:///tmp/pi.sock", "unix", "/tmp/pi.sock")]
    [InlineData("unix:///", "unix", "/")]
    [InlineData("unix:///tmp/pi.sock/", "unix", "/tmp/pi.sock/")]
    [InlineData("unix:///tmp/a%20b.sock", "unix", "/tmp/a b.sock")]
    [InlineData("unix:///tmp/pi%2Fsock", "unix", "/tmp/pi/sock")]
    [InlineData("radius://00000000-0000-4000-8000-000000000001", "radius", ServerId)]
    public void AcceptsValidTransportAddresses(string value, string transport, string payload)
    {
        var (address, error) = ExperimentalCommandOptions.ParseTransportAddress(value);

        Assert.Null(error);
        switch (transport)
        {
            case "unix":
                Assert.Equal(new TransportAddress.Unix(payload), address);
                break;
            default:
                Assert.Equal(new TransportAddress.Radius(payload), address);
                break;
        }
    }

    [Theory]
    [InlineData("unix://relative.sock", "Unix transport address must not include an authority")]
    [InlineData("unix:///tmp/pi.sock?wrong=value", "Invalid --connect address")]
    [InlineData("unix:///tmp/pi.sock#frag", "Invalid --connect address")]
    [InlineData("unix:////tmp/pi.sock", "Invalid --connect address")]
    [InlineData("unix:///tmp/a b.sock", "Invalid --connect address")]
    [InlineData("unix:///tmp/ä.sock", "Invalid --connect address")]
    [InlineData("unix:///tmp/%zz.sock", "Invalid --connect address")]
    [InlineData("unix:/tmp/pi.sock", "Invalid --connect address")]
    [InlineData("unix:", "Invalid --connect address")]
    [InlineData("unix://", "Invalid --connect address")]
    [InlineData("ws://localhost:8080", "Unsupported --connect transport \"ws:\"")]
    [InlineData("http://example.com", "Unsupported --connect transport \"http:\"")]
    [InlineData("not-a-url", "Invalid --connect address")]
    [InlineData("", "Invalid --connect address")]
    [InlineData("/tmp/pi.sock", "Invalid --connect address")]
    [InlineData("UNIX:///tmp/pi.sock", "Invalid --connect address")]
    [InlineData("radius://not-a-server", "Radius transport address requires a lowercase UUIDv4 server ID")]
    [InlineData("radius://00000000-0000-4000-8000-000000000001/x", "Invalid --connect address")]
    [InlineData("radius://user@00000000-0000-4000-8000-000000000001", "Invalid --connect address")]
    [InlineData("radius://00000000-0000-4000-8000-000000000001:80", "Invalid --connect address")]
    [InlineData("RADIUS://00000000-0000-4000-8000-000000000001", "Invalid --connect address")]
    public void RejectsInvalidTransportAddresses(string value, string expectedError)
    {
        var (address, error) = ExperimentalCommandOptions.ParseTransportAddress(value);

        Assert.Null(address);
        // 与 TS 测试的 `stringContaining` 同义：`Invalid --connect address` 后面会带具体地址。
        Assert.NotNull(error);
        Assert.Contains(expectedError, error, StringComparison.Ordinal);
    }

    // ------------------------------------------------------------------ framework

    [Fact]
    public void RepeatableOptionCollectsEveryValueInOrder()
    {
        var command = ParseClient("client", "-e", "a", "-e", "b", "-e", "c");
        Assert.Equal(new[] { "a", "b", "c" }, command.PluginPackages);
    }

    [Fact]
    public void DoubleDashStopsOptionParsingButKeepsItselfInRemainingArgs()
    {
        var input = ParseRawInput("client", "--session-id", "s1", "--", "--model", "m");
        Assert.Equal(new[] { "--", "--model", "m" }, input.RemainingArgs);
        Assert.Equal("s1", input.Value(ClientCommandDefinition.SessionIdOption));
    }

    [Fact]
    public void UnknownOptionStopsParsingAndLeavesTheRestUntouched()
    {
        var input = ParseRawInput("client", "--session-id", "s1", "--nope", "--model", "m");
        Assert.Equal(new[] { "--nope", "--model", "m" }, input.RemainingArgs);
        Assert.Equal("s1", input.Value(ClientCommandDefinition.SessionIdOption));
    }

    [Fact]
    public void DuplicateOptionRegistrationThrows()
    {
        var option = CommandOptions.StringOption("--x");
        var command = new Command<ExperimentalCommandGroup, IExperimentalCliContext>("t").Option(option);

        Assert.Throws<InvalidOperationException>(() => command.Option(option));
    }

    // ------------------------------------------------------------------ helpers

    private static ServerCommand ParseServer(params string[] argv) =>
        (ServerCommand)Assert.IsType<CommandParseResult.Ok>(ExperimentalCli.Create().Parse(argv)).Command;

    private static ClientCommand ParseClient(params string[] argv) =>
        (ClientCommand)Assert.IsType<CommandParseResult.Ok>(ExperimentalCli.Create().Parse(argv)).Command;

    /// <summary>
    /// 直接跑 <c>client</c> 子命令的选项解析，拿 <see cref="IParsedCommandInput"/> 视图
    /// （builder 之前的原始状态），用于断言 remainingArgs 的「整体落到尾部」行为。
    /// </summary>
    private static IParsedCommandInput ParseRawInput(params string[] argv)
    {
        IParsedCommandInput? captured = null;
        new Command<ClientCommand, IClientCommandContext>("client")
            .Option(ClientCommandDefinition.SessionIdOption)
            .Option(ClientCommandDefinition.ModelOption)
            .Build(input =>
            {
                captured = input;
                return new CommandBuildResult<ClientCommand>.Failed(["captured"]);
            })
            .Parse(argv.Skip(1).ToList());

        return Assert.IsAssignableFrom<IParsedCommandInput>(captured);
    }
}
