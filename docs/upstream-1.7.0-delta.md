# 上游 v1.7.0 迁移记录

本文记录 `v1.6.0`（`faac58588`，2026-11-20）..`v1.7.0`（`fb0fe82353`，2026-11-27）
（含 `v1.6.1`，2026-11-25）之间、触及端口已迁移 package 的改动落地情况。逐提交/逐发布说明处置见
[v1.7.0-disposition.md](./v1.7.0-disposition.md)。

## 1. 范围与口径

- 上游区间 896 个提交；触及 12 个已迁移 package 的 309 个，其中 chord 222（端口无 sync 子系统）、
  ai 43、coding-agent 40、tui 11、durable 4、mcp 3、agent 1、env 1。
- 端口基线 `1.6.0`，本轮提升到 **`1.7.0`**：`Directory.Build.props`、`Assets/package.json`、CLI
  `--version` 一致。
- 口径与 v1.6.0 一致：只移植用户可见且落在端口已迁移 package 的行为；纯 TS 类型形状、npm 运行时、
  未移植的 p2/p3 与 chord sync 子系统不移植。

## 2. 本轮新增落地

### 2.1 图片生成协议栈（v1.7.0）

端口在 v1.6.0 已有 `openrouter-images` 与图片模型目录，但没有 `pi-image` 协议、图片输出 token 拆分
与图片费率。本轮补齐整条链路：

| 落点 | 内容 |
|---|---|
| `src/Pi.Ai/Api/PiImage.cs`（新） | `POST <baseUrl>/image`，`{model, context:{input}, options}` → `{output, usage?, responseId?}`；内容块按 camelCase 手工序列化；非 2xx 的 `{error:{message}}` 信封进 error 结果 |
| `src/Pi.Ai/Providers/Images/RegisterBuiltins.cs` | 注册 `pi-image` 实现 |
| `src/Pi.Ai/Types/Images.cs` | `ImagesOptions.AspectRatio/Size/SessionId` |
| `src/Pi.Ai/Api/OpenRouterImages.cs` | `image_config.{aspect_ratio,image_size}`；`completion_tokens_details.{reasoning_tokens,image_tokens}`；无 choices / provider 错误 / 非 stop|end finish_reason 的 error 结果；`calculateCost` + OpenRouter 上报计费覆盖 |
| `src/Pi.Ai/Types/Messages.cs` | `Usage.ImageOutput`（`Output` 子集） |
| `src/Pi.Ai/Models/ModelCatalog.cs` | `ModelCostRates.ImageOutput` 与 JSON 读写 |
| `src/Pi.Ai/Utils/ModelOperations.cs` | `CalculateCost`：图片 token 用 imageOutput 费率，其余 output 用普通费率；未上报拆分时全部 output 按图片费率 |

用例：`tests/Pi.Ai.Tests/Api/V170ImageTests.cs`（PiImage 往返、错误信封、image_config、
imageOutput/reasoning 用量、无 choices 与 finish_reason 校验）。

### 2.2 pi-decision 与 Radius 全类别目录（v1.6.1 / v1.7.0）

- `src/Pi.Ai/Api/PiDecision.cs`（新）：`POST <baseUrl>/decision`，问题/答案按 TS `ClassifierQuestion`/
  `ClassifierAnswer` 的 camelCase 形状读写；`responseId` 与 `parsePiUsage` 复用 `PiImage.ParseUsage`。
- `Types/Classifier.cs`：`ClassifierResult.ResponseId`、`ClassifierOptions.SessionId`。
- `Auth/OAuth/RadiusConfig.cs`：`RadiusGatewayClassifierModel` / `RadiusGatewayImageModel`，
  `SanitizeGatewayConfig` 解析 `decision[]` / `images[]`（过滤未知模态）。
- `Providers/RadiusConfig.cs`：`ModelsFromConfig` 产出 chat + classifier + image 三类。
- `Providers/Radius.cs`：`GetModels()` 只列 chat、`GetAllModels()` 列全部；实现
  `IClassifierProvider` / `IImagesProvider`。

### 2.3 OSC 7577 程序恢复（v1.7.0）

- `src/Pi.Tui/ProgramResume.cs`：`ProgramResumeCommand` / `ProgramResumeSender` / `Format` /
  `FormatClear` / `TakeToken`。SHA-256 以 token 结尾，argv 与 cwd base64 编码；控制字符、孤立代理项、
  相对程序名/cwd、超 2048 字节或 64 参数均不可报告，改发 clear。
- `src/Pi.CodingAgent/Modes/Interactive/ProgramResumeReporter.cs`：`Report` / `ReportIfNone` /
  `ReportAgain` / `Clear`，按命令 fingerprint 去重。
- `InteractiveMode` 构造时取走并清除 `OSC7577_TOKEN`（不传给子进程），有 token 才创建 reporter；
  会话有文件可恢复时报告，正常退出清除，信号退出保留。

用例：`tests/Pi.Tui.Tests/ProgramResumeTests.cs`（MAC、拒绝不可报告命令、clear、token 清除）。

### 2.4 transcript notice 拆分（v1.6.1）

`showCacheMissNotices` → `notices.{cacheMisses,cacheWarming,compactionCosts,reasoningDrops}`。
`SettingsManager.GetNotice/GetNotices/SetNotice` 提供新 API；旧 getter/setter 保留为兼容包装并保持
「旧值迁移到四类」语义；交互模式五处读取分别走对应开关。

### 2.5 目录与默认值

ant-ling 改为 Ling 3.x（3.0 Tiny / Flash / Flash VL、3.1 Flash、3.2 Flash），默认 `Ling-3.1-flash`；
`thinkingFormat: "ant-ling"` 作为 `"deepseek"` 废弃别名，detect 路径直接产出 `deepseek`，请求里发送
`thinking: {type: enabled|disabled}`。Qwen Token Plan Individual 的 `qwen4-max-preview` → `qwen4-max`；
Cloudflare 两家默认 `@cf/zai-org/glm-5.3`；Mistral 默认 `mistral-medium-latest`；移除
`gpt-5-chat-latest`、Groq 三个下线模型、`mistral-medium-3.5` 与 `devstral-medium-latest`。

### 2.6 Bedrock 思考回放

`BedrockConverseStream.ConvertMessages` 读取 `compat.supportsReasoningReplay === false`，跳过历史
`ThinkingContent`（含 redacted 分支），避免 Kimi K3 在 Converse 上因回放 reasoning 报
`InternalServerException`；`amazon-bedrock.json` 的两条 Kimi K3 已打标。

## 3. 未移植与既有覆盖

- chord 222 个提交全部落在端口没有的 sync / remote contract / web 构建子系统（沿用 v1.6.0 判定）。
- durable 的 1.6.1 breaking changes 是 TS tagged-union 序列化形状，端口用 C# 判别类型表达。
- npm/Node/WSL 安装、遥测删除、`wl-copy` MIME 探测、AuthStorage 时间戳 flake 等不适用。
- Google strict/xhigh、retry 分类器、Copilot 提示缓存与 decision 账号过滤、`rawStopReason`、
  SSE 线性化等已在此前对齐批次覆盖，本轮不重复移植（逐条见 disposition B/C）。
