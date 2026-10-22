# 发布到 NuGet

Pi.NET 的包发布由 `.github/workflows/nuget-publish.yml` 完成。本文说明一次性配置和日常发版流程。

## 发布哪些包

固定 13 个库项目，包 ID 与项目名一致（已确认在 nuget.org 上均未被占用）：

```
Pi.Telemetry  Pi.Chord   Pi.Protocol  Pi.Ai     Pi.Agent   Pi.Mcp
Pi.Server     Pi.Client  Pi.Codemode  Pi.Tui    Pi.Durable Pi.Env
Pi.CodingAgent
```

4 个可执行项目 **不** 打包，由 `Directory.Build.targets` 统一设 `IsPackable=false`：
`Pi.Cli`（`pi`）、`Pi.Vacation`、`Pi.DurableHost`、`Pi.Evals`。测试项目由
`tests/Directory.Build.props` 设 `IsPackable=false`。

## 一次性配置

1. 在 nuget.org 生成 API Key：
   - Scopes 选 `Push new packages and package versions`；
   - Glob pattern 填 `Pi.*`（只允许改 Pi 前缀的包）；
   - 有效期按需选择（最长 365 天），到期需重新生成并更新 secret。
2. 在 GitHub 仓库 `Settings -> Secrets and variables -> Actions -> New repository secret`
   添加 `NUGET_API_KEY`，值为上一步的 key。

> 首次发布时 nuget.org 会把 `Pi.` 前缀归到该账号名下。若返回 403 且提示前缀已被保留，
> 说明该前缀已被其他账号占用，需要给包 ID 加自己的前缀（例如 `xuzeyu91.Pi.Ai`），
> 同时改 `Directory.Build.props` 的 `PackageId` 逻辑和本文的包清单。

## 日常发版

```bash
# 1. 改版本号（唯一来源）并补 CHANGELOG
#    Directory.Build.props:  <Version>1.0.5</Version>

# 2. 本地先验证能打包
dotnet pack Pi.slnx -c Release -o artifacts

# 3. 提交并打 tag 推送 —— 推送 tag 即触发发布
git add -A && git commit -m "release: 1.0.5"
git tag v1.0.5
git push origin main --follow-tags
```

工作流会依次执行：解析版本 → restore → build（带 `-p:Version`）→ pack →
校验包集合 → 上传构建产物 → 推送 nuget.org（nupkg + snupkg）→ 创建 GitHub Release。

**版本号从 tag 推导**（`v1.0.5` → `1.0.5`），tag 与 `Directory.Build.props` 不一致时以 tag 为准。

### 不发布、只试跑

`Actions -> Publish NuGet packages -> Run workflow`，`dry_run` 保持 `true`。
这样会完整构建打包并把 nupkg 作为 artifact 上传，但不推送、不建 Release。
输入 `version` 可覆盖版本号（留空则读 `Directory.Build.props`）。

### 半失败重跑

推送阶段用 `--skip-duplicate`，重复运行是幂等的：已上传的版本跳过，剩下的继续推。
所以直接重跑工作流即可，不需要手动清理。

## 回退

nuget.org 允许 unlist（不可删除）。收回某个版本：

```bash
curl -X DELETE "https://www.nuget.org/api/v2/package/Pi.Ai/1.0.5" \
  -H "X-NuGet-ApiKey: <你的 key>"
```

这会让包不可被新安装，但已有消费者不受影响。真正的修复方式是发一个更高版本。

## 包内容的几个约定

- **单一目标框架** `net10.0`：消费方项目也必须是 net10.0 及以上。
- **`Pi.Mcp`** 依赖 `Microsoft.AspNetCore.App` 框架引用，消费方项目需要 ASP.NET Core（`Web` SDK 或 `FrameworkReference`）。
- **`Pi.CodingAgent`** 带 3 个运行时资源目录（`Assets/theme`、`Assets/export-html`、`Assets/assets`），
  以 `contentFiles` + `PackageCopyToOutput` 发布，还原后会落到消费方输出目录，
  正好匹配 `Config.GetThemesDir()` 等对 `AppContext.BaseDirectory/Assets` 的探测。
  上游 `Assets/docs`、`Assets/examples`（4.1MB）不随包分发。
- **符号包**：同时产出 `.snupkg`（含 SourceLink），调试时可单步进入 Pi 源码。
- 包内附带 `LICENSE` 与 `readme.md`，`repository` 字段带 commit 哈希。

## 本地自查

```bash
dotnet pack Pi.slnx -c Release -o artifacts        # 应产出 13 个 .nupkg + 13 个 .snupkg
ls artifacts/*.nupkg | wc -l                       # 13
```

CI（`ci.yml`）在每次 PR / push main 时也会做一次打包冒烟检查，磁盘上包数量不是 13 或混入
可执行项目时会直接失败。
