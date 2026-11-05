using Xunit;

namespace Pi.Ai.Tests;

/// <summary>
/// 起本地 <see cref="System.Net.HttpListener"/> 的夹具集合。
/// <para>
/// 这些夹具在 Windows 上走 http.sys：同一个端口可以注册多个前缀，请求会被路由到「最长匹配」的那个，
/// 于是并行跑时一个夹具的请求可能被另一个夹具的处理循环读走——实测表现为断言里出现别的测试的响应体
/// （例如 Anthropic 用例收到 Mistral 用例的文本）、请求次数多出一次、成本值取自别人的响应。
/// 端口去重挡不住这一点，因为问题出在前缀共享而不是端口冲突，所以这些类必须串行。
/// </para>
/// </summary>
[CollectionDefinition("http-fixtures", DisableParallelization = true)]
public sealed class HttpFixtureCollection;
