using System.Diagnostics;
using System.Text.Json.Nodes;
using Pi.Codemode;
using Pi.Codemode.Runtime;
using Xunit;

namespace Pi.Codemode.Tests;

/// <summary>
/// <see cref="JintCodemodeEngine"/> 的真实 JavaScript 集成测试：默认沙箱不再注入假引擎，
/// prelude、工具桥、promise 排空、输出映射与停止语义都在 Jint 里跑通。
/// 对应 TS <c>packages/codemode/test</c> 中依赖真实 QuickJS VM 的沙箱用例。
/// </summary>
public class JintEngineTests
{
    private const string PngBase64 =
        "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mP8z8BQDwAEhQGAhKmMIQAAAABJRU5ErkJggg==";

    private static CodemodeSandbox Sandbox(CodemodeSandboxOptions? options = null) => new(options);

    [Fact]
    public async Task ExecutesScriptsWithToolCallsAndOutput()
    {
        var seen = new List<JsonNode?>();
        var sandbox = Sandbox(new CodemodeSandboxOptions
        {
            Tools =
            [
                new CodemodeTool
                {
                    Name = "echo",
                    Description = "Echo one value.",
                    Execute = (args, _) =>
                    {
                        seen.Add(args);
                        return Task.FromResult<JsonNode?>(new JsonObject
                        {
                            ["value"] = args?["value"]?.DeepClone(),
                        });
                    },
                },
            ],
        });

        var result = await sandbox.ExecuteAsync("""
            const first = await tools.echo({ value: "hi" });
            text("got " + first.value);
            console.log("done", { n: 1 });
            return { value: first.value, all: ALL_TOOLS.length };
            """);

        var success = Assert.IsType<CodemodeResult.Success>(result);
        Assert.Equal("""{"value":"hi","all":1}""", success.Value?.ToJsonString());
        Assert.Equal("""{"value":"hi"}""", Assert.Single(seen)?.ToJsonString());

        Assert.Equal(2, success.Output.Count);
        var firstOutput = Assert.IsType<CodemodeOutputItem.Text>(success.Output[0]);
        Assert.Equal("got hi", firstOutput.Value);
        Assert.Null(firstOutput.Console);
        var console = Assert.IsType<CodemodeOutputItem.Text>(success.Output[1]);
        Assert.Equal("""done {"n":1}""", console.Value);
        Assert.True(console.Console);

        var call = Assert.Single(success.Calls);
        Assert.Equal("echo", call.Name);
        Assert.Equal(CodemodeCallStatus.Ok, call.Status);
        Assert.True(call.DurationMs >= 0);
        await sandbox.CloseAsync();
    }

    [Fact]
    public async Task MapsGlobalsAndImageOutput()
    {
        var sandbox = Sandbox(new CodemodeSandboxOptions
        {
            Globals =
            [
                new CodemodeTool
                {
                    Name = "helper.add",
                    Spread = true,
                    Execute = (args, _) =>
                    {
                        var numbers = Assert.IsType<JsonArray>(args);
                        return Task.FromResult<JsonNode?>(
                            JsonValue.Create(numbers.Sum(number => number!.GetValue<int>())));
                    },
                },
            ],
        });

        var result = await sandbox.ExecuteAsync($$"""
            const total = await helper.add(2, 3);
            image("data:image/png;base64,{{PngBase64}}");
            return total;
            """);

        var success = Assert.IsType<CodemodeResult.Success>(result);
        Assert.Equal(5, success.Value?.GetValue<int>());
        Assert.Empty(success.Calls);
        var image = Assert.IsType<CodemodeOutputItem.Image>(Assert.Single(success.Output));
        Assert.Equal(PngBase64, image.Data);
        Assert.Equal("image/png", image.MimeType);
        await sandbox.CloseAsync();
    }

    [Fact]
    public async Task ReportsScriptErrorsWithEarlierOutputAndStack()
    {
        var sandbox = Sandbox();

        var result = await sandbox.ExecuteAsync("""
            text("before");
            throw new TypeError("boom");
            """);

        var failure = Assert.IsType<CodemodeResult.Failure>(result);
        Assert.Equal(CodemodeErrorKind.Script, failure.Error.Kind);
        Assert.Equal("TypeError", failure.Error.Name);
        Assert.Equal("boom", failure.Error.Message);
        Assert.Contains("TypeError: boom", failure.Error.Stack, StringComparison.Ordinal);
        Assert.Contains("codemode.js", failure.Error.Stack, StringComparison.Ordinal);
        Assert.Equal("before", Assert.IsType<CodemodeOutputItem.Text>(Assert.Single(failure.Output)).Value);
        await sandbox.CloseAsync();
    }

    [Fact]
    public async Task ReportsSyntaxErrorsAsScriptFailures()
    {
        var sandbox = Sandbox();

        var failure = Assert.IsType<CodemodeResult.Failure>(await sandbox.ExecuteAsync("const value = ;"));

        Assert.Equal(CodemodeErrorKind.Script, failure.Error.Kind);
        Assert.Equal("SyntaxError", failure.Error.Name);
        Assert.Contains("Unexpected token", failure.Error.Message, StringComparison.Ordinal);
        Assert.StartsWith("SyntaxError:", failure.Error.Stack, StringComparison.Ordinal);
        await sandbox.CloseAsync();
    }

    [Fact]
    public async Task ReportsScriptsWaitingOnAPromiseThatCanNeverSettle()
    {
        var sandbox = Sandbox();

        var failure = Assert.IsType<CodemodeResult.Failure>(
            await sandbox.ExecuteAsync("await new Promise(() => {});"));

        Assert.Equal(CodemodeErrorKind.Script, failure.Error.Kind);
        Assert.Contains("can never settle", failure.Error.Message, StringComparison.Ordinal);
        await sandbox.CloseAsync();
    }

    [Fact]
    public async Task ToolFailuresSurfaceToScriptsAndTheResult()
    {
        var sandbox = Sandbox(new CodemodeSandboxOptions
        {
            Tools =
            [
                new CodemodeTool
                {
                    Name = "boom",
                    Execute = (_, _) => throw new InvalidOperationException("tool exploded"),
                },
            ],
        });

        var caught = Assert.IsType<CodemodeResult.Success>(
            await sandbox.ExecuteAsync("""
                try {
                  await tools.boom();
                } catch (error) {
                  return error.message;
                }
                """));
        Assert.Equal("tool exploded", caught.Value?.GetValue<string>());
        Assert.Equal(CodemodeCallStatus.Error, Assert.Single(caught.Calls).Status);

        var failure = Assert.IsType<CodemodeResult.Failure>(await sandbox.ExecuteAsync("await tools.boom();"));
        Assert.Equal(CodemodeErrorKind.Script, failure.Error.Kind);
        Assert.Equal("tool exploded", failure.Error.Message);
        Assert.Equal(CodemodeCallStatus.Error, Assert.Single(failure.Calls).Status);
        await sandbox.CloseAsync();
    }

    [Fact]
    public async Task RoundTripsStoreWrites()
    {
        var sandbox = Sandbox();

        var success = Assert.IsType<CodemodeResult.Success>(await sandbox.ExecuteAsync("""
            const next = load("counter") + 1;
            store("counter", next);
            store("old", undefined);
            return next;
            """, new CodemodeExecuteOptions
        {
            Store = new Dictionary<string, JsonNode?>
            {
                ["counter"] = JsonNode.Parse("41")!,
                ["old"] = JsonNode.Parse("\"gone\"")!,
            },
        }));

        Assert.Equal(42, success.Value?.GetValue<int>());
        Assert.Equal(42, success.StoreWrites.Set["counter"]?.GetValue<int>());
        Assert.Equal("old", Assert.Single(success.StoreWrites.Delete));
        await sandbox.CloseAsync();
    }

    [Fact]
    public async Task StopsRunawayScriptsAndRemainsUsable()
    {
        var sandbox = Sandbox(new CodemodeSandboxOptions { TimeoutMs = 100 });
        var stopwatch = Stopwatch.StartNew();

        var failure = Assert.IsType<CodemodeResult.Failure>(await sandbox.ExecuteAsync("while (true) {}"));

        Assert.Equal(CodemodeErrorKind.Timeout, failure.Error.Kind);
        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(10), $"stopped after {stopwatch.Elapsed}");

        var success = Assert.IsType<CodemodeResult.Success>(await sandbox.ExecuteAsync("return 1"));
        Assert.Equal(1, success.Value?.GetValue<int>());
        await sandbox.CloseAsync();
    }

    [Fact]
    public async Task ReportsRunawayRecursionWithoutKillingTheHost()
    {
        var sandbox = Sandbox();

        // QuickJS 用 wasm 栈上限把深递归变成脚本错误；Jint 必须有等价保护，
        // 否则 CLR StackOverflow 会直接终止进程而不是返回失败结果。
        var failure = Assert.IsType<CodemodeResult.Failure>(await sandbox.ExecuteAsync(
            "function recurse(n) { return recurse(n + 1); }\nreturn recurse(0);"));

        Assert.Equal(CodemodeErrorKind.Script, failure.Error.Kind);
        var success = Assert.IsType<CodemodeResult.Success>(await sandbox.ExecuteAsync("return 1"));
        Assert.Equal(1, success.Value?.GetValue<int>());
        await sandbox.CloseAsync();
    }

    [Fact]
    public async Task IsolatesExecutionsAndExposesOnlySandboxGlobals()
    {
        var sandbox = Sandbox();

        var capabilities = Assert.IsType<CodemodeResult.Success>(
            await sandbox.ExecuteAsync("""
                globalThis.leak = 7;
                return [typeof require, typeof process, typeof fetch, typeof Deno].join(",");
                """));
        Assert.Equal("undefined,undefined,undefined,undefined", capabilities.Value?.GetValue<string>());

        var isolated = Assert.IsType<CodemodeResult.Success>(await sandbox.ExecuteAsync("return typeof leak"));
        Assert.Equal("undefined", isolated.Value?.GetValue<string>());
        await sandbox.CloseAsync();
    }
}
