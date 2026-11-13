# 应用里集成 pi.net，同时吃上游插件

插件本身怎么加载见 [plugin-integration.md](plugin-integration.md)。这一页只讲一件事：**你自己的应用**
（桌面端、Web 后端、CLI 工具）怎么把 pi.net 装进去，还能直接用上游那批 `.ts` 扩展。

## 先选形态

| | 进程内（SDK） | 子进程（RPC） |
|---|---|---|
| 入口 | `Sdk.CreateAgentSessionAsync` | `pi --mode rpc` + `RpcClient` |
| 插件跑在哪 | 你的进程 | 子进程 |
| `ctx.ui.*` 谁实现 | 你实现 `IExtensionUIContext` | 框架实现，对话框变成 JSON 事件 |
| 列插件命令 | 没有公开 API | `RpcClient.GetCommandsAsync()` |
| 隔离 | 无，插件与你同权限 | 进程级，崩在子进程里 |
| 适合 | 只装可信插件、要共享内存对象、要低延迟 | 第三方插件、桌面端已有自己的 UI |

判断标准就一条：**插件代码你信不信**。信，进程内省事；不信，RPC 至少不会让它把你的应用一起带走。

## 形态 A：进程内

```csharp
var agentDir = Path.Combine(appDataDir, "pi-agent");   // 1
var settings = SettingsManager.Create(workspace, agentDir);
var loader = new DefaultResourceLoader(
    workspace, agentDir, settings,
    additionalExtensionPaths: [Path.Combine(AppContext.BaseDirectory, "plugins", "review.ts")]);

await loader.ReloadAsync(new ResourceLoaderReloadOptions        // 2
{
    ResolveProjectTrust = _ => Task.FromResult(false),          // 5
});

var result = await Sdk.CreateAgentSessionAsync(new CreateAgentSessionOptions
{
    Cwd = workspace,
    AgentDir = agentDir,
    ResourceLoader = loader,                                    // 2
});

await result.Session.BindExtensionsAsync(new ExtensionBindings  // 3
{
    UiContext = new AppUiContext(mainWindow),                    // 4
    OnError = error => log.Warn($"{error.ExtensionPath}: {error.Error}"),
});

foreach (var error in result.ExtensionsResult.Errors)            // 6
{
    log.Warn($"plugin failed: {error.Path} {error.Error}");
}
```

六个编号各自都是一个坑：

1. **`AgentDir` 用应用私有目录**。默认值是 `~/.pi/agent`，用户机器上大概率还装着 `pi` CLI，
   共用目录意味着你的插件集合、设置、凭据会和它互相污染。
2. **自己 new 的 loader 要自己 `ReloadAsync`**。`Sdk` 只在自己创建 loader 时才 reload
   （`Sdk.cs:533`）；传进去的 loader 没 reload，扩展集合是空的，而且**不报任何错**。
3. **`BindExtensionsAsync` 是 `session_start` 的唯一触发点**（`AgentSessionBuild.cs:1140`）。不调它，
   插件注册的工具照样进工具表、模型照样能调，但插件的初始化逻辑和 `resources_discover` 贡献的
   技能/提示词全都不跑——最难查的半死状态：插件"在"，但像是没生效。
4. **不给 `UiContext` 就退化成 `NoOpUIContext`**：`confirm` 恒 `false`、`input` 返回 `null`、
   `notify` 直接丢。插件"点了没反应"、"确认框从来不弹"都是这个原因。
5. **项目信任默认是关的**。`cwd/.pi` 下的插件与包不会加载。要支持就传 `ResolveProjectTrust`
   自己弹确认，或显式 `SettingsManager.SetProjectTrusted(true)`——后者等于替用户签字，别默认这么干。
6. **`ExtensionsResult.Errors` 是聚合的**，一个插件失败不影响其他插件，所以它往往是你唯一的线索。
   顺手也把 `Warnings` 打出来：渲染器注册降级那类问题只在 warning 里。

`ExtensionBindings.Mode` 只有 `tui` / `rpc` / `json` / `print` 四个合法值，没有「app」。**留空**即可
（默认 `print`）；插件判断有没有 UI 用的是 `ctx.hasUI`，不是 mode。硬塞一个自定义字符串，只会让
按 mode 分支的插件走进它没测过的路径。

### 工具与命令怎么用

- **工具**：不用你做任何事，注册完就在工具表里；`session.GetCallableToolNames()` 可以核对。
- **命令**：`await session.PromptAsync("/hello 参数")`。命中的扩展命令会被直接执行，**不会发给模型**
  （`AgentSessionPrompt.cs:53`）；没命中才走正常提示词流程。
- **列命令**：SDK 侧没有公开的命令列表（runner 由会话内部持有）。要列表就改用 RPC 的 `get_commands`，
  或者自己在注册时记一份。

### 实现 `IExtensionUIContext`

接口有二十多个成员，真正常用的就六个：`Select` / `Confirm` / `Input` / `Notify` / `SetStatus` / `Editor`。
其余返回默认值即可，但**别抛异常**——插件可能只是顺手 `setStatus` 或 `setWidget`，抛了会把它整个 handler 打死。

`Theme` 这个属性要注意：直接 `=> ThemeRuntime.Current` 在没初始化主题时会抛
`Theme not initialized`。先判 `ThemeRuntime.HasCurrent`，没有就给一个自己构造的默认主题。
所以依赖主题上色的插件（`ctx.ui.theme.fg(...)`）在无 TUI 的宿主里要么降级，要么别选。

## 形态 B：子进程 RPC

```csharp
var client = new RpcClient(new RpcClientOptions
{
    FileName = Path.Combine(AppContext.BaseDirectory, "pi", "pi.exe"),  // 必须显式给
    Cwd = workspace,
    Args = ["-e", pluginPath],                                          // 可重复，也是同一个 -e
});

client.OnEvent(node => route(node));      // 会话事件 + extension_ui_request
await client.StartAsync();
await client.PromptAsync("/hello");
var commands = await client.GetCommandsAsync();   // 扩展命令 / 提示词模板 / 技能
```

`RpcClient` 会自己补 `--mode rpc`，`Args` 里的东西原样跟在后面。两个细节：

- **`FileName` 一定要显式设**。它的默认值是 `Environment.ProcessPath`，也就是**你的应用自己**——
  不设就会把自己再拉一遍，然后一直等一个永远不会来的握手。
- 子进程的 stderr 会被客户端顺手 echo 到 `Console.Error`，桌面应用里记得把控制台输出接走。

插件 UI 走 stdout：`{"type":"extension_ui_request","id":...,"method":"select"|"confirm"|"input"|"editor"|...}`，
应用回一行 `{"type":"extension_ui_response","id":...,"value":"..."}`（`confirm` 用 `"confirmed":true|false`，
取消用 `"cancelled":true`）。协议见 `src/Pi.CodingAgent/Assets/docs/rpc-extension-ui.md`。

> **当前缺口**：`RpcClient` 没有暴露回复 UI 请求的方法（`SendAsync` 是 `internal`）。用现成客户端
> 就收不到插件的对话框答复，`select/confirm` 会一直等；要么给 `RpcClient` 加一个写 JSON 行的方法，
> 要么自己起进程读写 JSONL。这是客户端的缺口，协议本身是完整的。

RPC 模式没有 TUI，所以：`select / confirm / input / editor / notify / setStatus / setTitle /
setWidget(字符串数组) / setEditorText` 通；`ctx.ui.custom()`、`setFooter/Header`、组件工厂、
自动补全组合全被忽略。选插件时先看它用没用这些。

## 插件放哪

| 方式 | 怎么落 | 适合 |
|---|---|---|
| 随包目录 | 安装时拷进 `<agentDir>/extensions/` | 默认方案；自动发现，支持一层子目录 |
| 显式路径 | `additionalExtensionPaths` 传**具体 `.ts` 文件** | 插件不在标准目录 |
| npm 包 | `pi install npm:...` 写进 settings | 让用户自己挑插件；需要 npm |

`additionalExtensionPaths` 传**目录**时按「包」解析：认 `package.json` 的 `pi.extensions`，或
`extensions/` 这层约定子目录。根目录下一堆散装 `.ts` 不会被发现——目录会被原样当成扩展入口丢给
加载器，最后报 `Cannot find extension module`。要么逐个传文件，要么按包布局放。

插件自己的 `node_modules` 要跟着一起打进去，宿主不会替散装插件跑 npm install。最稳的做法是构建期
`PI_CODING_AGENT_DIR=<随包目录> pi install npm:...` 装到随包的 agentDir，把装好的目录整体打进安装包，
用户机器上不跑 npm/git。

## 性能与隔离

- 每个扩展一个 Jint engine，**不需要用户装 Node**；Babel 转译按内容哈希在进程内缓存。
  Babel 引擎首次初始化约 2s（3MB bundle 的实测值），之后每个文件只付一次转译成本。
- 插件与宿主同进程、同权限，`fs`、`child_process` 都是真能力。没有沙箱，别把它当安全边界。
- 要隔离就用 RPC：插件崩了、跑飞了，最多是子进程重启，应用还在。

## 相关文档

- 插件加载与能力边界：[plugin-integration.md](plugin-integration.md)
- SDK 入参全表：[usage/sdk.md](usage/sdk.md)
- RPC 协议：[Assets/docs/rpc.md](../src/Pi.CodingAgent/Assets/docs/rpc.md)、
  [Assets/docs/rpc-commands.md](../src/Pi.CodingAgent/Assets/docs/rpc-commands.md)
- 可编译示例：`samples/Pi.Samples.CodingSession`（SDK 装配）、`samples/Pi.Sample.Extension`（C# 插件）
