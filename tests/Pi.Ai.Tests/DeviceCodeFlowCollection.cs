using Xunit;

namespace Pi.Ai.Tests;

/// <summary>
/// 会改动 `DeviceCodeFlow.DefaultSleepAsync` 这个静态钩子的夹具集合。
/// <para>
/// 那个钩子是进程级的：`OpenAiAndDeviceOAuthTests` 把它设成「永不睡的完成任务」，另一处
/// `DeviceCodeFlowTests.CancelsInFlightWait` 依赖默认的可中止 sleep 来观察取消。两者并行时后者会继承
/// 前者的钩子，取消永远不到，30 秒后以 <see cref="TimeoutException"/> 失败——表现为偶发、且只在满载时出现。
/// 这些类必须串行。
/// </para>
/// </summary>
[CollectionDefinition("device-code-flow", DisableParallelization = true)]
public sealed class DeviceCodeFlowCollection;
