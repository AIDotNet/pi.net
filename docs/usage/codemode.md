# Codemode 参考

codemode 让模型**写脚本调用工具**，而不是一次一次地发工具调用：脚本在进程内跑，
`tools.*` 是类型化声明，模型可以组合、循环、分支。实现分两层：
`Pi.Codemode`（沙箱、声明渲染、`@options` 解析）与 `Pi.CodingAgent/Extensions/Codemode`
（工具装配与渲染，约 2,300 行）。

## 开关与呈现方式

`settings.codemode` 两个字段（`settings.schema.json`）：

| 字段 | 默认 | 含义 |
|---|---|---|
| `mode` | `"on"` | `"on"`：直接工具照常声明，脚本里可调用的工具带注释；`"only"`：不再声明直接工具，所有工具只在 codemode 里可调 |
| `inlineBudget` | `3000` | 内联工具声明的估算 token 预算 |

没有 worker 池：端口用进程内 Jint VM 跑脚本，不存在上游
`PI_CODEMODE_MAX_WORKERS` 这类并发上限（那是 QuickJS worker 方案的产物）。

## 脚本能拿到什么

- `tools`：按 settings 与 allowlist 解析出的可调用工具，签名由
  `Pi.Codemode.CodemodeDeclarations` 从 JSON Schema 渲染（约束会以
  `@minimum` / `@pattern` / `@default` 这类标签写进注释）。
- `tool_search`：工具太多时按需检索，避免把全部声明塞进上下文。
- `models.*`：从脚本里调模型（含 `models.classify()` 的图片输入）。
- `output`：把中间结果写成文件，避免撑爆返回值。
- `grammar`：约束模型输出的 grammar 工具。

脚本体第一条如果是 `// @options: {...}` 注释，会被解析成运行参数
（`Pi.Codemode/Source.cs:22`）。**必须位于首行**，否则抛
`CodemodeSourceError`——这是故意为之：藏在别处的 timeout 会被无声忽略。

| 字段 | 约束 |
|---|---|
| `max_output_tokens` | 安全整数 |
| `timeout_ms` | 正整数，上限 `MaxTimeoutMs` |

## 渲染与失败语义

- 相邻的文本块在截断前先拼接（`CodemodeExecute.JoinAdjacentText`，单次拼接而非 O(n²)），
  图片随其后，路径不会被截断。
- 脚本失败会带着引擎错误信息进工具结果；关闭 sandbox 不会替脚本决定结局
  （上游 `04566631c` 的语义，端口一致）。
- 结果里列出脚本发起过的工具调用（上游 `127b77121`）。

## 嵌入用法

SDK 里 codemode 随默认扩展加载，也可用 `--extension builtin:<name>` 显式挑选。
作为库作者想自己装配，用 `Pi.Codemode` 的三件套：

| 类型 | 用途 |
|---|---|
| `ICodemodeVm` / `ICodemodeJsEngine` | 沙箱执行（默认 Jint；Wasm 引擎可替换） |
| `CodemodeSource.Parse` | 解析脚本与 `@options` |
| `CodemodeDeclarations.RenderToolSignature` | 把工具渲染成 TS 声明（含约束标签） |

上游 `pi-durable/codemode`（脚本跑在持久任务里、可跨重启重放）**没有**移植，
理由与重启条件见 [`docs/upstream-1.4.0-delta.md`](../upstream-1.4.0-delta.md) §6 第 7 条。
