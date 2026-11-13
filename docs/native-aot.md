# Native AOT 支持（无损）

`pi.net` 支持把 `pi` CLI 发布为 **Native AOT** 原生可执行文件：单个 PE 映像、无需
安装 .NET 运行时、无 JIT、启动更快。这是一项**附加的部署能力**：默认的多目标
（net9.0 / net10.0 / net11.0）JIT 构建、全部公开接口、全部能力都保持不变。

- 不删除、不替换任何功能；
- 不改任何公开类型 / 成员签名（新增的都是 `internal` 基础设施类型）；
- 不改 JIT 运行时的序列化行为；
- 不裁剪掉任何 Pi 程序集的代码或反射元数据。

## 发布

```bash
bash scripts/publish-aot.sh                      # win-x64 -> artifacts/aot/win-x64/pi.exe
bash scripts/publish-aot.sh linux-x64 /tmp/pi   # 换 RID / 输出目录
```

第三个参数可指定 4 个可执行宿主之一（短名或 csproj 路径），默认 `src/Pi.Cli`：

```bash
bash scripts/publish-aot.sh win-x64 artifacts/aot/win-x64-durable Pi.DurableHost
bash scripts/publish-aot.sh win-x64 artifacts/aot/win-x64-evals   Pi.Evals
bash scripts/publish-aot.sh win-x64 artifacts/aot/win-x64-vacation Pi.Vacation
```

等价于（所有 AOT 专属 MSBuild 设置都在 `Directory.Build.props` /
`Directory.Build.targets` 里由 `PublishAot=true` 条件激活）：

```bash
dotnet publish src/Pi.Cli/Pi.Cli.csproj -c Release -f net10.0 -r win-x64 \
  -p:PublishAot=true -o artifacts/aot/win-x64
```

前置条件：.NET 10 SDK、对应 RID 的 ILCompiler（NuGet 自动还原）、本机原生工具链
（Windows 需 MSVC `link.exe` + Windows SDK）。

`verify-aot.sh` 会自动挑选可用的 Python 3（`python3` / `python` / `py -3`）——
Windows Git Bash 常常只有 `py` 启动器、没有 `python3`；也可以直接
`py -3 scripts/verify-aot.py --aot artifacts/aot/win-x64/pi.exe`。

## 验证（AOT 与 JIT 行为一致性）

```bash
dotnet build src/Pi.Cli -c Release -p:TargetFrameworks=net10.0
bash scripts/verify-aot.sh artifacts/aot/win-x64
```

CI 里 `.github/workflows/ci.yml` 的 `native-aot` job（`workflow_dispatch` 勾选
`run_aot`）会重跑同一套发布 + 对拍；默认 push / PR 不触发，不影响常规流水线。

`verify-aot.py` 会：

1. 断言产物真的是原生映像（PE32+、无 `Pi.*.dll`、无
   `pi.runtimeconfig.json` / `pi.deps.json`、不依赖 `coreclr.dll` / `hostfxr.dll`）；
2. 对 **29 条 CLI 命令 + 23 条 RPC 命令（共 52 项）**分别跑 JIT 版与 AOT 版，
   逐条比对 **退出码 / stdout / stderr**（随机 session id、条目 id、时间戳与
   JIT/AOT 堆栈格式差异做规范化）；其中覆盖：
   - 模型目录解析（`--list-models`、带搜索词、无匹配），JSON 反序列化；
   - 设置文档（`list`、`mcp list --json`，JSON 读写）；
   - `doctor --json`（带参数化构造器的 record → POCO 序列化）；
   - 全部 `--help` / 错误分支 / 版本输出 / JSON 模式；
   - 认证、MCP、配置等元数据命令；
   - RPC 的 33 命令联合中 23 条确定性命令：`get_state`、
     `get_available_models`、`cycle_model`、`get_available_thinking_levels`、
     会话树 / 条目 / 统计 / 压缩 / 队列模式等，含事件行的 `type` 判别串。

只有全部一致且 AOT 侧无任何未处理异常 / `missing native code` 才算通过。
带搜索词的 `--list-models gpt --offline` 是 provider 表竞态的回归用例；
`cycle_model` / `get_available_thinking_levels` / 事件行是 source-gen 类型清单
的回归用例。

## 原理：为什么需要改造

端口里有约 250 处 `JsonSerializer.Serialize(value, options)` /
`Deserialize<T>(...)` 反射式重载。在 Native AOT 下，
`DefaultJsonTypeInfoResolver` 会在运行时用反射拼出
`JsonPropertyInfo<bool?>`、
`SmallObjectWithParameterizedConstructorConverter<DoctorReport, int, string, …>`
这类泛型实例，而 AOT 编译器从未见过它们、无法生成对应本机代码，运行即抛
`NotSupportedException: ... is missing native code or metadata`。

实测结论（本机 .NET 10.0.401 / Windows）：

| 方案 | 结果 |
| --- | --- |
| 仅 `PublishAot=true` | 启动即崩（反射式 JSON） |
| + `JsonSerializerIsReflectionEnabledByDefault=true` + 完整元数据 | 仍崩（动态泛型实例缺本机代码） |
| + 把每个 Pi 程序集整体 root | 仍崩（同上） |
| **+ source-generated `JsonSerializerContext`** | **全部通过** |

### 落地方式

1. **每个含 `JsonSerializerOptions` 的包**新增 `Aot/AotJson.cs`：
   - 一个 `internal partial class XxxJsonContext : JsonSerializerContext`，
     用 `[JsonSerializable(typeof(T))]` 列出本包 `JsonSerializer` 调用会触达的
     具体根类型（嵌套类型由生成器自动展开）；类型清单由
     `scripts/gen-aot-contexts.py` 生成，其中判别联合
     （`AgentSessionEvent` / `FileEntry`）的变体与运行时 modifier 挂载的
     `AssistantMessageEvent` 流式事件都从源码自动提取，避免手工漏项。
     漏登记一个根类型，AOT 侧就会在运行时抛 `... is missing native code`。
   - `AotJson.Configure(options)`：仅当
     `!RuntimeFeature.IsDynamicCodeSupported`（即 Native AOT 二进制）时才把
     `JsonTypeInfoResolver.Combine(上下文, 原有解析器)` 装到 options 上。
     `DefaultJsonTypeInfoResolver` 保留在链尾，让 primitive / `JsonNode` 以及
     未登记类型继续走原反射路径。
   - 若调用方原本挂了 `DefaultJsonTypeInfoResolver.Modifiers`（例如会话线路给
     `ChatMessage` 追加 4 个自定义 role 的 `JsonDerivedType`），
     `ModifierTypeInfoResolver` 会把这些 modifier 同样应用到 source-gen 的
     `JsonTypeInfo` 上，行为不丢。
2. **所有 `new JsonSerializerOptions` 创建点**（57 处）改为
   `AotJson.Configure(new ... { ... })`，表达式体不变。
3. `Directory.Build.props` 的 AOT 条件组：
   - `TargetFrameworks=net10.0`（AOT 只出一个 TFM + RID 的原生映像）；
   - `JsonSerializerIsReflectionEnabledByDefault=true`（保留反射重载）；
   - `IlcGenerateCompleteTypeMetadata=true`；
   - `NoWarn` 关闭 IL2xxx / IL3xxx 建议（见下）。
4. `Directory.Build.targets`：AOT 时把 12 个 Pi 程序集全部
   `TrimmerRootAssembly`，配合完整元数据 ⇒ **不裁剪任何代码/反射数据**，
   这是「无损」的关键。
5. `Directory.Build.props` 的 `InternalsVisibleTo`：source-gen 上下文在编译期需要
   看到兄弟包里的 `internal JsonConverter`（如 `MessageContentJsonConverter`）。
   这是纯编译期友元授权，不改公开 API、不改运行时行为。

### 动态类型与多态判别联合的两个坑

`JsonSerializer.SerializeToNode(object)` / `SerializeLine(object)` 这类以 `object`
为声明类型的调用，运行时会按 `value.GetType()` 查 `JsonTypeInfo`。这带来两类
只在 AOT 下暴露的问题：

1. **匿名类型 / 未登记的具体类型**：反射解析器会构造
   `SmallObjectWithParameterizedConstructorConverter<T, …>` 或
   `IEnumerableOfTConverter<TCollection, TElement>` 的泛型实例，ILC 从未见过就
   缺本机代码。`cycle_model`（`ModelCycleResult`）、
   `get_available_thinking_levels`（`IReadOnlyList<ThinkingLevel>`）、
   `cycle_thinking_level`（匿名 `new { level }`）都曾因此失败。前两类靠登记
   根类型解决；匿名类型无法 `typeof`，改为具名 `internal` 负载
   `ThinkingLevelPayload` 后登记。
2. **判别联合的 `type` 字段**：`AgentSessionEvent` 是
   `[JsonPolymorphic(TypeDiscriminatorPropertyName = "type")]` 的 23 变体联合。
   若按**派生类型**序列化，source-gen 生成的变体 `JsonTypeInfo` 不含判别串，
   事件会退化成 `{"level":"low"}`（丢了 `"type":"thinking_level_changed"`）——
   JIT 的反射解析器恰好会向上继承基类的多态设置，于是两边输出不一致。
   `JsonL.SerializeLine` / `SerializeToNode` 与 `JsExtensionHost.SerializeEvent`
   统一改为对 `AgentSessionEvent` 用基类 `typeof(AgentSessionEvent)` 作为声明
   类型，判别串由基类 TypeInfo 写入，AOT 与 JIT 一致。

`JsonL` 的两个 `DefaultJsonTypeInfoResolver.Modifiers` 在运行时给
`ChatMessage` / `AssistantMessageEvent` 追加派生类型，source-gen 生成器看不到，
所以这些变体也必须显式登记（现已由 `gen-aot-contexts.py` 从源码自动提取）。

### 为什么 JIT 输出不变

`AotJson.Configure` 的分支条件是 `!RuntimeFeature.IsDynamicCodeSupported`。
在 JIT / ReadyToRun 构建里它为 `true`，函数直接原样返回 options —— 反射解析器、
命名策略、自定义 converter、modifier 全部保持改造前的形态。source-gen 上下文
虽然被编译进程序集（`internal`），但在 JIT 运行时从不参与序列化。

`JsonL` / `JsExtensionHost` 里改成「按基类声明类型序列化事件」是唯一在 JIT
下也会执行的代码改动：它只是把 `value.GetType()` 换成
`typeof(AgentSessionEvent)`，反射解析器本来就会向上继承基类的
`[JsonPolymorphic]`，因此 JSON 输出逐字节不变——这一点由
`verify-aot.py` 的事件行对拍与 7,519 条 JIT 测试共同确认。

### 为什么 IL2xxx / IL3xxx 警告被抑制

这些诊断描述的是「裁剪 / 运行时代码生成可能破坏功能」。本方案显式地用
source-generated metadata 覆盖了所有 POCO 路径、用完整元数据 + 整体 root
保留其余一切，因此对本 AOT 配置它们是**已被验证的假阳性**。抑制范围仅限
`PublishAot=true` 的条件组，默认构建完全不受影响；每一类都被
`scripts/verify-aot.py` 的真实二进制端到端对拍覆盖，而不是靠静态断言。

逐类说明（含非 JSON 类别）：

| 诊断 | 出现形式 | 为什么不构成功能损失 |
| --- | --- | --- |
| `IL2026` / `IL3050` | `JsonSerializer.*` 反射重载、`JsonValue.Create<T>` / `JsonArray.Add<T>` 非基元类型 | POCO 走 source-gen；反射解析器在完整元数据 + 整体 root 下对基元 / `JsonNode` / 未登记类型仍可用。已用与最终 AOT 配置一致的独立原生探针验证：`JsonArray.Add<JsonObject>`、`JsonArray.Add<JsonArray>`、`JsonValue.Create<POCO>`、`Serialize<object>(POCO)` 全部成功（`FAILS=0`）。 |
| `IL2075` / `IL2072` / `IL2090` | `GetType().GetProperty/GetField/GetMethod`、`Activator.CreateInstance(Type)` | 完整类型元数据 + 整体 root 保留了全部成员与构造器；独立 AOT 探针中 `GetProperty` 与 `MakeGenericType` / `Activator.CreateInstance` 均通过。 |
| `IL3000` | `PluginBundles.ResolvePluginExternal` 的 `Assembly.Location` | 该返回值在 `JintFacetModuleHost` 里只用于 `resolver?.Invoke(specifier) is null` 的**非空哨兵判断**，Native AOT 返回空串仍非 null，JS 模块照常经 `__pi_pluginApi` 注册，行为不变。 |
| `IL2111` | `Delegate.CreateDelegate` 经反射访问 | 同上：整体 root + 完整元数据保留委托目标。 |

另有一处**平台固有**限制（未抑制、也非退化）：外部 .NET 插件程序集在
Native AOT 下无法被 JIT 加载，见下节。

### AOT 更快启动暴露的既存并发竞态

`sources-gen JSON` 只解决了元数据问题；AOT 二进制没有 JIT 预热，启动明显更快，
于是把原本在 JIT 下几乎不会重叠的操作挤到了一起：`ModelRuntime` 后台刷新目录时
`RebuildProviders()` 会 `ClearProviders` + 逐个 `SetProvider`，同一时刻 CLI 的
`--list-models` 正在遍历 `Models` 的 provider 表 / 刷新控制字典。`Dictionary<,>`
不是线程安全的，实测 AOT 二进制跑 `--list-models gpt` 会有约 20% 概率抛
`InvalidOperationException: Collection was modified` 或
`concurrent update was performed on this collection and corrupted its state`
（JIT 版 0/10、AOT 版 2/10），而 `--list-models`（不带搜索词）又恰好不触发。

之前用「遍历前先 `ToList()` 快照」缓解过几处，但快照本身仍是在**活动字典**上
枚举，写入落在枚举窗口里照样抛。本轮在 `Models`（`Models.cs` + `ModelsAuth.cs`）
引入一个 `_stateGate`，把 provider 表与刷新代次 / 控制器 / 发布链字典的**所有**
读写都收口到同一把锁：写入串行化，枚举在锁内取快照、在锁外调用 provider 代码，
锁内不 `await`、不调用外部实现。身份视图（`WithIdentity`）共用同一把锁，因此视图
与原对象互相可见的语义不变。单线程顺序、provider 注册顺序、序列化输出全部不变，
属非破坏性修复。

`verify-aot.py` 把 `--list-models gpt --offline` 纳入对拍作为回归护栏；
修复后该命令在 AOT 二进制上连跑 20 次 0 失败。此外 `ModelRuntime` 的
provider 表在高频连续启动时仍有负载敏感的锁竞争痕迹（`settings.json.lock`
警告 / 偶发启动时异常），`verify-aot.py` 对 JIT 侧启动 flake 做一次重试；
这些在单独运行时不出现，非 AOT 引入的确定性退化。

## 下游 AOT 消费方

`AotJson.Configure` 的分支只看 `RuntimeFeature.IsDynamicCodeSupported`，因此**任何**
Native AOT 应用引用 `Pi.*` NuGet 包时，source-gen 上下文都会自动接管 POCO JSON，
无需消费方改代码。若消费方希望像本仓库一样「一点不裁剪」，建议在自己的项目里
照 `Directory.Build.props` / `Directory.Build.targets` 加上三项：
`JsonSerializerIsReflectionEnabledByDefault=true`（`PublishAot=true` 时该开关默认
为 false，会让 source-gen 上下文链尾的反射兜底解析器失效）、
`IlcGenerateCompleteTypeMetadata=true`，以及对 Pi 程序集的
`TrimmerRootAssembly`。不裁剪时包体积更大，但反射能力完整保留。

## 已知平台限制（不是本次改造引入的退化）

- **外部 .NET 插件程序集**：`AssemblyLoadContextExtensionLoader` 用
  `LoadFromAssemblyPath` 在运行时加载第三方 `.dll`。Native AOT 没有 JIT，无法
  执行这类托管程序集（.NET 平台的固有限制）。内置的、编译进映像的扩展与 JS
  （Jint）扩展不受影响；默认 JIT 构建仍完整支持外部插件。
- 其余能力由「完整元数据 + 整体 root + source-gen JSON」保留，但**不保证 100%
  无损**：以 `object` 为声明类型的动态序列化路径依赖
  `Aot/AotJson.cs` 的根类型清单，清单目前是「源码自动提取判别联合变体 + 手工
  登记其余根类型」的混合维护。已通过 `verify-aot.py` 的 CLI / RPC / 事件对拍
  覆盖主要路径；新增序列化 DTO 时必须同步登记，并由 CI 的 `native-aot` job
  拦住回归。
- 未在本机端到端验证的路径：真实联网模型调用（需要有效凭据）、交互式 TUI、
  外部扩展的完整交互、`win-x64` 之外的 RID（需对应 ILCompiler 与原生工具链）。

## 验证记录

### 2026-11-13（本机 .NET 11.0.100 SDK / Windows 11；AOT 目标 net10.0 / win-x64）

- 4 个可执行宿主分别 `bash scripts/publish-aot.sh win-x64 <out> <host>`：
  **全部 0 警告 0 错误**，产出原生 PE32+ x64 映像：`pi.exe` 68,378,624 B、
  `pi-durable.exe` 68,370,432 B、`Pi.Evals.exe` 70,131,712 B、
  `pi-vacation.exe` 68,370,432 B；输出目录无托管 `Pi.*.dll` /
  `runtimeconfig.json` / `coreclr.dll` 依赖。
- `bash scripts/verify-aot.sh artifacts/aot/win-x64`：**29 条 CLI + 23 条 RPC
  = 52/52 项 JIT 与 AOT 完全一致**（退出码 / stdout / stderr），AOT 侧无
  未处理异常 / `missing native code`；原生映像检查通过。
- `--mode json -p`（agent 会话事件路径）在 AOT 二进制上无
  `missing native code` / `NotSupportedException`；事件行 `type` 判别串与
  JIT 一致。
- provider 表竞态修复后 `pi.exe --list-models gpt` 连跑 20 次 **0 失败**
  （修复前 JIT 0/10、AOT 2/10 崩溃）。
- `dotnet build Pi.slnx -c Release`（net9.0 / net10.0 / net11.0 共 36 个工程）：
  0 警告 0 错误。
- `bash scripts/gate-tests.sh Release`：**11/11 测试工程全绿**（7,519 条用例通过，
  8 条按设计跳过）。高负载连续跑多轮 AOT 发布时，`Pi.Durable.Tests` 的计时类
  用例偶发 `Condition was not met in time` 超时（每次失败用例不同），低负载单独
  复跑 613/613 通过，属门禁已记录的负载敏感 flake，与 AOT 改造无关
  （`Pi.Durable` 不依赖本次改动的 `Pi.CodingAgent`）。证明 JIT 侧能力 / 接口无回归。
- `dotnet pack Pi.slnx -c Release --no-build`：13 个 `.nupkg` / 13 个 `.snupkg`，
  4 个可执行工程均未产出包，消费方打包路径无回归。
- 相对 `main` 的 60 个改动 `.cs` 文件逐行比对成员声明：**0 处签名差异**；
  新增类型全部为 `internal`。

（演进过程：最初 .NET 10.0.401 上 19 条只读 CLI 命令对拍 → 扩到 29 条 →
本轮补上 RPC 与事件路径，发现并修掉 `ModelCycleResult` /
`IReadOnlyList<ThinkingLevel>` / `AgentSessionEvent` 判别联合 /
`AssistantMessageEvent` 流式事件 / 匿名负载共 5 类 source-gen 类型缺口，
最终 52 项全绿。更早的 Debug 门禁与 load-sensitive flake 记录见 git 历史。）
