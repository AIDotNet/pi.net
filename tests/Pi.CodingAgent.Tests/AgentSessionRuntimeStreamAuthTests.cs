using Pi.Agent;
using Pi.Ai.Auth;
using Pi.Ai.Models;
using Pi.Ai.Types;
using Pi.CodingAgent.Core;
using Xunit;

namespace Pi.CodingAgent.Tests;

/// <summary>
/// 摘要 auth（`6fed3cfb9`，上游 #11520）：经运行时自己解析 auth 的流式函数要被认出来，摘要才不会把
/// 预解析的凭据当成显式 API key 传下去（那会顶掉存储的 OAuth 登录）。
/// </summary>
public class AgentSessionRuntimeStreamAuthTests
{
    [Fact]
    public async Task MarksAStreamFunctionAsResolvingAuthThroughOneRuntime()
    {
        var runtime = await ModelRuntime.CreateAsync(new CreateModelRuntimeOptions
        {
            Credentials = new InMemoryCredentialStore(),
            ModelsPathDisabled = true,
            ModelsStore = new InMemoryCodingAgentModelsStore(),
            RefreshOnCreate = false,
            Env = name => name == "PI_OFFLINE" ? "1" : null,
            AuthContext = new StubAuthContext(name => name == "PI_OFFLINE" ? "1" : null),
        });
        StreamFn marked = (_, _, _, _) => throw new InvalidOperationException("not streamed in this test");
        StreamFn other = (_, _, _, _) => throw new InvalidOperationException("not streamed in this test");

        Assert.False(AgentSession.IsRuntimeStreamFunction(marked, runtime));
        AgentSession.MarkRuntimeStreamFunction(marked, runtime);

        Assert.True(AgentSession.IsRuntimeStreamFunction(marked, runtime));
        // 没标记的流式函数、以及 null 都不算。
        Assert.False(AgentSession.IsRuntimeStreamFunction(other, runtime));
        Assert.False(AgentSession.IsRuntimeStreamFunction(marked, null));
        Assert.False(AgentSession.IsRuntimeStreamFunction(null, runtime));
    }
}
