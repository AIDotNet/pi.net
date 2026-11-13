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
bash scripts/publish-aot.sh                      # win-x64 -> artifacts/aot/win-x64
bash scripts/publish-aot.sh linux-x64 /tmp/pi   # 换 RID / 输出目录
```

等价于（所有 AOT 专属 MSBuild 设置都在 `Directory.Build.props` /
`Directory.Build.targets` 里由 `PublishAot=true` 条件激活）：

```bash
dotnet publish src/Pi.Cli/Pi.Cli.csproj -c Release -f net10.0 -r win-x64 \
  -p:PublishAot=true -o artifacts/aot/win-x64
```

前置条件：.NET 10 SDK、对应 RID 的 ILCompiler（NuGet 自动还原）、本机原生工具链
（Windows 需 MSVC `link.exe` + Windows SDK）。

## 验证（AOT 与 JIT 行为一致性）

```bash
dotnet build src/Pi.Cli -c Release -p:TargetFrameworks=net10.0
bash scripts/verify-aot.sh artifacts/aot/win-x64
```

`verify-aot.py` 会：

1. 断言产物真的是原生映像（PE32+、无 `Pi.*.dll`、无
   `pi.runtimeconfig.json` / `pi.deps.json`、不依赖 `coreclr.dll` / `hostfxr.dll`）；
2. 对 19 条确定性、离线、只读的 CLI 命令分别跑 JIT 版与 AOT 版，逐条比对
   **退出码 / stdout / stderr**；其中覆盖：
   - 模型目录解析（`--list-models`，JSON 反序列化）；
   - 设置文档（`list`、`mcp list --json`，JSON 读写）；
   - `doctor --json`（带参数化构造器的 record → POCO 序列化）；
   - 全部 `--help` / 错误分支 / 版本输出；
   - 认证、MCP、配置等元数据命令。

只有全部一致且 AOT 侧无任何未处理异常才算通过。

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
     `scripts/gen-aot-contexts.py` 生成。
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

### 为什么 JIT 行为完全不变

`AotJson.Configure` 的分支条件是 `!RuntimeFeature.IsDynamicCodeSupported`。
在 JIT / ReadyToRun 构建里它为 `true`，函数直接原样返回 options —— 反射解析器、
命名策略、自定义 converter、modifier 全部保持改造前的形态。source-gen 上下文
虽然被编译进程序集（`internal`），但在 JIT 运行时从不参与序列化。

### 为什么 IL2xxx / IL3xxx 警告被抑制

这些诊断描述的是「裁剪 / 运行时代码生成可能破坏功能」。本方案显式地用
source-generated metadata 覆盖了所有 POCO 路径、用完整元数据 + 整体 root
保留其余一切，因此对本 AOT 配置它们是**已被验证的假阳性**。抑制范围仅限
`PublishAot=true` 的条件组，默认构建完全不受影响；每一类都被
`scripts/verify-aot.py` 的真实二进制端到端对拍覆盖，而不是靠静态断言。

## 下游 AOT 消费方

`AotJson.Configure` 的分支只看 `RuntimeFeature.IsDynamicCodeSupported`，因此**任何**
Native AOT 应用引用 `Pi.*` NuGet 包时，source-gen 上下文都会自动接管 POCO JSON，
无需消费方改代码。若消费方希望像本仓库一样「一点不裁剪」，建议在自己的项目里
照 `Directory.Build.props` / `Directory.Build.targets` 加上
`IlcGenerateCompleteTypeMetadata=true` 与对 Pi 程序集的 `TrimmerRootAssembly`；
不裁剪时包体积更大，但反射能力完整保留。

## 已知平台限制（不是本次改造引入的退化）

- **外部 .NET 插件程序集**：`AssemblyLoadContextExtensionLoader` 用
  `LoadFromAssemblyPath` 在运行时加载第三方 `.dll`。Native AOT 没有 JIT，无法
  执行这类托管程序集（.NET 平台的固有限制）。内置的、编译进映像的扩展与 JS
  （Jint）扩展不受影响；默认 JIT 构建仍完整支持外部插件。
- 其余一切能力（内置扩展、工具、TUI、MCP、RPC、会话、模型目录、导出等）都由
  「完整元数据 + 整体 root + source-gen JSON」保证在 AOT 二进制里保留。

## 验证记录（2026-11-13，本机 .NET 10.0.401 / Windows 11）

- `bash scripts/publish-aot.sh win-x64 artifacts/aot/win-x64`：**0 警告 0 错误**，
  产出 `pi.exe`（PE32+ x64，67,725,824 字节）；输出目录无托管 `Pi.*.dll` /
  `runtimeconfig.json` / `coreclr.dll` 依赖。
- 第二个宿主 `dotnet publish src/Pi.DurableHost -p:PublishAot=true`：同样 0 警告
  0 错误，产出 `pi-durable.exe`（原生映像）。4 个可执行工程带
  `PublishAot=true` 构建全部 0 警告 0 错误。
- `bash scripts/verify-aot.sh artifacts/aot/win-x64`：**19/19 命令 JIT 与 AOT
  完全一致**（退出码 / stdout / stderr），AOT 侧无未处理异常；原生映像检查通过。
- `dotnet build Pi.slnx -c Debug` / `-c Release`（net10.0）：0 警告 0 错误。
- `bash scripts/gate-tests.sh Release` / `Debug`：**各 11/11 测试工程全绿**。
  两轮各有一次已知负载敏感 flake（`Pi.Durable.Tests` 计时用例 /
  `Pi.CodingAgent.Tests.ModelSelectorTests` 的 TUI 渲染竞态），门禁自带的重跑
  机制均通过；该 UI 用例单独重跑 5400/5405 全绿、Release 门禁中也通过。证明
  JIT 侧能力/接口无回归。
- 公开/受保护声明逐行对比 `HEAD`：57 个改动的 `.cs` 文件 **0 处签名差异**；
  新增类型全部为 `internal`。
