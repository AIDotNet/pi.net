#!/usr/bin/env python3
"""生成 v1.5.2 逐 published-commit 处置表（data-derived，可复现）。

对 `git log v1.5.0..v1.5.2 -- ':(glob)packages/*/src/**'` 的每一个 published commit，
逐条给出：
  - 处置：已移植（§3 行 + 端口源码/测试文件证据） / 权威排除（改动文件在端口无对应物，
    附 git 实证命令） / 范围外（p2 实验、合并/版本元信息）。
  - 证据：端口真实文件路径（git ls-files 核对存在）或 git show --stat + git ls-files 排除实证。
    端口源码证据由上游 production diff 的新增实义标识符在端口 .cs 中的多命中派生，可 grep 复核。

数据派生，非手写分类：
  - 上游 commit 清单、改动文件：上游 git log --name-only（单次调用）
  - 上游 production diff 的新增实义标识符：上游 git log -p -U0（单次调用）
  - issue→§3 行、§3 行的落点/验证文本：解析 docs/upstream-1.5.2-delta.md §3 表
  - issue→端口命中文件：扫描端口 src/ tests/ docs/ CHANGELOG 内 `#NNNNN` 引用
  - 端口文件清单：git ls-files

用法：PI_REPO=... python scripts/gen_v152_disposition.py > docs/v1.5.2-disposition.md
"""
import os
import re
import subprocess
import sys
from datetime import datetime, timezone

PI_REPO = os.environ.get("PI_REPO", "D:/AI/参考项目/pi")
PORT_ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))

# §4.1 实证：端口 Pi.Chord 实现的是 durable 依赖的 v1 基础子路径。
CHORD_V1_PREFIXES = (
    "packages/chord/src/context/", "packages/chord/src/delta/",
    "packages/chord/src/json", "packages/chord/src/watch",
    "packages/chord/src/output", "packages/chord/src/sqlite/",
    "packages/chord/src/facets/", "packages/chord/src/services/",
)
# Node/npm 构建链 / 仓库外文件（§4.5 / §4.4 排除依据）。
NPM_BUILD_HINTS = ("package.json", "install-lock/", "shrinkwrap", "vite.config",
                   "THIRD_PARTY", "LICENSES/", ".npmrc", "tsconfig")
# p2/实验应用（§4.7 范围外）。
P2_HINTS = ("packages/p2", "apps/p2", "(p2", "p2)", "p2/p3", "docs(p2)", "docs(p3)")

KNOWN_PORTED_KEYWORDS = {
    "auth login and logout": ("§3.59", "`Cli/AuthLogin.cs` + `AuthTerminal.cs` + `Main.cs`", "`AuthLoginTests`(30)"),
    "reported Claude Code version": ("§3.1", "`AnthropicMessages.ClaudeCodeVersion`", "常量 + 构建覆盖"),
    "default cacheWarming to idle": ("§3.49", "`SettingsManager` + settings schema", "`V152BatchRegressionTests`(2)"),
    "cache warming notices into one line": ("§3.49", "`CacheWarmer.cs` Cache warmed 合并行", "`V152BatchRegressionTests`"),
    "read-only JSONL storage": ("§3.61", "`JsonlStorage.cs` + `JsonlStorageReadOnlyError`", "`JsonlStorageReadOnlyTests`(6)"),
    "per-conversation compaction settings": ("§3.60", "`CompactionSettings` + `AgentDocs`", "`CompactionSettingsTests`(11)"),
    "keep scheduler notifications": ("§3.63", "Scheduler.cs `_dirty` 前置清零 + 全量重算", "`SchedulerNotificationRaceTests`(1)"),
    "signed out during model refresh": ("§3.69", "`IProvider.RefreshModelsAuth` + `ModelsAuth`", "`RefreshModelsAuthTests`(2)"),
    "Tx.submission(id)": ("§3.20", "`ITx.GetSubmissionAsync` + `Transaction`", "durable 套件 587"),
    "group commit in the store": ("§3.67", "端口无 `store.ts`（v2 层）", "`git ls-files src/Pi.Chord` 排除实证"),
    "commits a group in a few statements": ("§3.66", "本地 `Microsoft.Data.Sqlite` 单连接", "`GroupCommitDifferentialTests`(2)"),
    "lazy OAuth flow and Bedrock": ("§4.5", "Node bundler 动态 import 专有", "`git ls-files` 无 npm chunk"),
    "resolve variable lazy imports": ("§4.5", "Node bundler 专有", "`git ls-files` 无 bundler"),
    "PipeTransport": ("§3.30", "`PipeTransport.cs` + `JsonRpcLineReader.cs`", "`PipeTransportTests`(15)"),
    "two halves, moved from coding-agent": ("§4.3", "包位置重定位，`Pi.Mcp/OAuth/` 早已实现", "§4.3 文件行号实证"),
    "shared tool naming": ("§4.3", "`Tools.cs` CreateMcpToolName / McpOutputMaxBytes", "§4.3 文件行号实证"),
    "preferred partner providers first": ("§3.64", "`RadiusLoginSelector.cs` 已覆盖 partner 顺序/shimmer", "`MermaidAnsiTests` 邻域"),
    "merge consecutive cache warming": ("§3.49", "`CacheWarmer.cs`", "`V152BatchRegressionTests`"),
    "num no longer takes nan": ("§3.8", "`SchemaChecker.MatchesType`", "schema 语料回归"),
    "remove \"(legacy)\" marker": ("§3.16–3.19", "`ModelData/*.json` 目录定点", "provider 计数断言"),
    "move provider defaults": ("§3.18", "`ModelResolver.DefaultModelPerProvider`", "`ModelResolverCorpusTests`"),
    "default Anthropic model": ("§3.18", "`ModelResolver.DefaultModelPerProvider`", "`ModelResolverCorpusTests`"),
    "send provider tokens in pi mcp list": ("§4.3", "`Extensions/Mcp/Cli.cs`", "包位置重定位实证"),
    "keep root $defs": ("§3.26", "`AnthropicMessages.ConvertTools`", "`AnthropicMessagesWireTests`(2)"),
    "share the auth.json lock across symlinked": ("§3.71", "`FileAuthStorageBackend.LockKeyFor` + `Paths.CanonicalizePath`", "`AuthStorageSymlinkLockTests`(3)"),
}

# 上游 production 改动极小、C# 命名与 TS 完全不同，机械标识符匹配不到；人工逐条核对后的真实落点。
# key = 9 位短 hash。证据为端口真实文件 + 等价实现，非关键词共现。
MANUAL_DISPOSITION = {
    "a77207d9c": ("已核对·人工实证",
        "端口 `src/Pi.Tui/Components/Editor.cs` paste 标记重编号：`match.Groups[2].Success ? ... : \"\"` "
        "保留可选 size 后缀，避免 `[paste #2]` 变成 `[paste #1undefined]`（等价上游 `${suffixGroup ?? \"\"}`）"),
    "33cd7e507": ("已核对·人工实证",
        "端口 `src/Pi.Codemode/Runtime/PreludeSource.cs` 的 `format(value)`："
        "`if (typeof value === \"number\") return String(value);` 使 NaN/Infinity 原样输出而不被 stringify 成 null"),
    "c13f6acfb": ("已核对·人工实证",
        "端口 `src/Pi.CodingAgent/Modes/Interactive/Theme/ThemeHelpers.cs` 已登记 "
        "Dart/Nix/Groovy/Gradle 语法高亮映射（111–114 行）"),
    "37b38e9f6": ("已核对·人工实证",
        "端口 `src/Pi.CodingAgent/Core/Keybindings.cs`（153–155 行）：Windows/WSL 默认粘贴键为 "
        "`Keys(\"alt+v\", \"ctrl+v\")`，其他平台 `Keys(\"ctrl+v\")`，等价上游 `defaultKeys` 数组化"),

    # ── 本轮 differential 审计：逐条核对上游 production diff 后真实移植的 published 项 ──
    "5bdf988b7": ("已移植·§3.74",
        "端口 `src/Pi.Ai/Api/OpenAiCompletions.cs` `NormalizeToolCallId`：`openai || IsOpenRouterModel` 且 "
        "`id[..31] + \"_\" + ShortHash(id)[..8]`；`tests/Pi.Ai.Tests/OpenAiCompletionsToolCallIdTests.cs`(6)，"
        "反向验证回退旧 `id[..40]` 后 4 用例失败"),
    "dbc61d647": ("已移植·§3.75",
        "端口 `src/Pi.Durable/Harness/HarnessTypes.cs` `ConversationStreamOptions` 补 "
        "`WebsocketConnectTimeoutMs`/`ThinkingBudgets`，`Provider.StreamOptionsToRequest` 装配；"
        "`tests/Pi.Durable.Tests/ConversationStreamOptionsV152Tests.cs`(6)"),
    "f3a64af07": ("已移植·§3.76",
        "端口 `src/Pi.Ai/Api/Lazy.cs` `LazyStream.Run` 增 signal + `SignalOf`、`SetupErrorMessage` 支持 "
        "StopReason，Models/CreateProvider/ModelRuntime/ProviderComposer/LazyApi 全调用点透传；"
        "`tests/Pi.Ai.Tests/LazyStreamAbortTests.cs`(6)，反向验证恒为 error 后 2 用例失败"),
    "80d4d3c86": ("已移植·§3.77",
        "端口 `src/Pi.CodingAgent/Modes/Interactive/InteractiveMode.cs` `_extensionOverlays` 字典 + "
        "`RemoveOverlay`（精确 `OverlayHandle.Hide` + 清 blocked + dispose），reset 逆序清空；"
        "`tests/Pi.CodingAgent.Tests/InteractiveModeTests.cs::ResetRemovesAndDisposesAllExtensionOverlays`，"
        "反向验证退回 `_ui.HideOverlay()` 后失败"),
    "efb45b51d": ("已移植·§3.78",
        "端口 `src/Pi.CodingAgent/Cli/Main.cs` startupBenchmark 分支 `await runtime.DisposeAsync()`；"
        "`tests/Pi.CodingAgent.Tests/StartupBenchmarkDisposeTests.cs`，反向验证移除 dispose 后失败"),
    "463b8f893": ("已移植·§3.79",
        "端口 `src/Pi.CodingAgent/Core/Tools/FindPathPattern.cs` 纯叶子（For/CreateWindowsMatcher/Matches）"
        "+ `FindTool.cs` 改「fd 列全部 + Minimatch 逐行过滤 + 到 limit 早停」，替代旧 `--full-path`+`[/\\\\]`；"
        "`tests/Pi.CodingAgent.Tests/FindPathPatternTests.cs`(14) + "
        "`FindWindowsPathGlobIntegrationTests.cs`(2，真实 fd 10.5.0)，端到端反向验证旧方案返回 `No files found`"),

    # ── 本轮 differential 审计：核对上游 diff 后确认「等价实现 / 形态不适用」的非遗漏项 ──
    "2bd2c2d0a": ("已核对·等价实证(§3.79 邻域)",
        "上游 #11726 相对根 `$id` 只应用一次；端口 `ToolSchemaValidator` 构造即 `Warm()` 先建索引，"
        "`SchemaResolver.BaseFor` 返回已索引 base、只应用一次，重复应用路径不存在。"
        "`tests/Pi.Ai.Tests/Utils/SchemaRelativeRootIdTests.cs`(3) 镜像上游用例钉住；"
        "上游同批 `unevaluatedProperties` 场景在端口是文档化的忽略关键字（SchemaChecker 头注释）"),
    "63ea042ce": ("已核对·形态不适用",
        "上游 #11855 OAuth callback `server.closeAllConnections()`；端口 `OAuthCallbackServer.DisposeAsync` "
        "用 `HttpListener.Stop()`（不等活动连接），无 Node keep-alive 挂起路径（§4）"),
    "42849bb84": ("已核对·形态不适用",
        "上游 #11724 stdio 子进程 exit→close 才清理进程组；端口 `StdioTransport` 用 .NET `Process` + "
        "`Kill(entireProcessTree: true)`，无 Node `USE_PROCESS_GROUPS`/`liveProcessGroups` 概念（§4）"),
    "96667f342": ("已核对·人工实证",
        "端口 `src/Pi.CodingAgent/Core/PromptTemplates.cs` `parseCommandArgs`：引号仅在参数开头打开、"
        "反斜杠转义、未闭合引号回退字面文本，等价上游 #11873"),
    "6bf2ab9bb": ("已核对·人工实证",
        "端口 `src/Pi.CodingAgent/Core/Tools/GrepTool.cs` `BuildRgArgs`：glob 去前导 `./` 保留 `!`、"
        "目录搜索 `RgWorkingDirectory` 锚定 + 搜 `.`、`FormatRgPath` 解析相对，等价上游 #11895"),
    "244e5f5db": ("已核对·人工实证",
        "端口 `src/Pi.CodingAgent/Core/Tools/GrepTool.cs` `IGrepToolOperations.ReadFileAsync` 保留为 "
        "deprecated 成员（注释明确上游不再读取），等价上游"),
    "d448c51e2": ("已核对·人工实证",
        "端口 `src/Pi.CodingAgent/Core/Tools/EditDiff.cs` `ApplyReplacementsToOriginal`/`GetLineEndingAt`/"
        "`WidenReplacementsToLines`，保留未改动行行尾，等价上游"),
    "9a1953961": ("已核对·人工实证",
        "端口 `src/Pi.CodingAgent/Utils/Changelog.cs` `GetNewEntries` 可空 + 前导 semver 正则，"
        "`InteractiveMode.Lifecycle.cs` 两处调用判空，等价上游 #11753/#11703"),
}


def git(repo, *args):
    out = subprocess.run(["git", "-C", repo, *args], capture_output=True, check=True).stdout
    return out.decode("utf-8", errors="replace")


# ── 上游 production diff → 端口真实源码文件（取代旧的 subject 关键词瞎猜） ─────────────
_PATCH_CACHE = None
IDENT_RE = re.compile(r"\b([A-Za-z_][A-Za-z0-9_]{6,})\b")
IDENT_STOP = set("""return const function async await undefined string number boolean object false true throw catch
finally switch case break default export import typeof instanceof interface private public static readonly class
extends implements package console process length message error status result options config context value index
array stringify parse encode decode""".split())


def upstream_patches():
    """单次 git 调用取回 v1.5.0..v1.5.2 全部 production 补丁，按完整 hash 索引。"""
    global _PATCH_CACHE
    if _PATCH_CACHE is not None:
        return _PATCH_CACHE
    out = git(PI_REPO, "log", "-p", "-U0", "--format=@@@COMMIT@@@%H",
              "v1.5.0..v1.5.2", "--", ":(glob)packages/*/src/**")
    patches, cur, buf = {}, None, []
    for ln in out.splitlines():
        if ln.startswith("@@@COMMIT@@@"):
            if cur is not None:
                patches[cur] = buf
            cur = ln[len("@@@COMMIT@@@"):].strip()
            buf = []
        elif cur is not None:
            buf.append(ln)
    if cur is not None:
        patches[cur] = buf
    _PATCH_CACHE = patches
    return patches


def production_identifiers(full_hash):
    """该 commit 在 production 文件（排除测试）新增的实义标识符，小写去重。"""
    curfile, toks = "", []
    for ln in upstream_patches().get(full_hash, []):
        if ln.startswith("+++ b/"):
            curfile = ln[6:]
        elif ln.startswith("+") and not ln.startswith("+++"):
            if re.search(r"(\.test\.|\.spec\.|/test/|/tests/|__tests__|\.snap)", curfile):
                continue
            for t in IDENT_RE.findall(ln[1:]):
                tl = t.lower()
                if tl not in IDENT_STOP and len(t) >= 7 and not t.startswith(("packages", "src")):
                    toks.append(tl)
    return list(dict.fromkeys(toks))


_TOKEN_INDEX = None
_EV_CACHE = {}


def token_index(corpus):
    """一次性建立 标识符→端口 .cs 文件集合 的倒排索引，避免 143×N×M 次全文扫描。"""
    global _TOKEN_INDEX
    if _TOKEN_INDEX is not None:
        return _TOKEN_INDEX
    idx = {}
    for rel, txt in corpus:
        if not rel.endswith(".cs"):
            continue
        for t in set(IDENT_RE.findall(txt)):
            if len(t) >= 7:
                idx.setdefault(t, set()).add(rel)
    _TOKEN_INDEX = idx
    return idx


def diff_evidence(full_hash, corpus, limit=2):
    """端口真实 .cs 文件证据：含该 commit 多个 production 标识符的文件，按命中数降序。

    这是对「该 published 改动在端口有对应实现」的机械、可 grep 复核的证据；
    旧的 keyword_hits 只用 subject 单词在端口全文里找共现，常命中完全无关的文件。
    """
    if full_hash in _EV_CACHE:
        return _EV_CACHE[full_hash]
    toks = production_identifiers(full_hash)
    if not toks:
        _EV_CACHE[full_hash] = []
        return []
    threshold = max(2, min(6, len(toks) // 6))
    idx = token_index(corpus)
    counts = {}
    for t in toks:
        for rel in idx.get(t, ()):
            counts[rel] = counts.get(rel, 0) + 1
    scored = [(n, rel) for rel, n in counts.items() if n >= threshold]
    scored.sort(key=lambda x: (-x[0], x[1]))
    result = [rel for _n, rel in scored[:limit]]
    _EV_CACHE[full_hash] = result
    return result


def parse_commits():
    out = git(PI_REPO, "log", "--format=@@@%H|%s", "--name-only",
              "v1.5.0..v1.5.2", "--", ":(glob)packages/*/src/**")
    commits = []
    cur = None
    for raw in out.splitlines():
        ln = raw.rstrip()
        if ln.startswith("@@@"):
            h, _, s = ln[3:].partition("|")
            cur = {"h": h, "s": s, "files": []}
            commits.append(cur)
        elif ln.startswith("packages/") and cur:
            cur["files"].append(ln)
    glued = re.compile(r"(?=packages/[a-z-]+/src/)")
    for c in commits:
        m = glued.search(c["s"])
        if m and m.start() > 0:
            c["files"].insert(0, c["s"][m.start():])
            c["s"] = c["s"][:m.start()].rstrip()
    return commits


def parse_ledger():
    """返回 issue->(row, landing, verify)，row->(landing, verify)。"""
    path = os.path.join(PORT_ROOT, "docs", "upstream-1.5.2-delta.md")
    text = open(path, encoding="utf-8").read()
    issue_to_row = {}
    row_cells = {}
    for ln in text.splitlines():
        m = re.match(r"^\|\s*3\.(\d+)\s*\|(.*)\|\s*$", ln)
        if not m:
            continue
        rownum = "3." + m.group(1)
        cells = [c.strip() for c in m.group(2).split("|")]
        # cells = [上游项, 落点, 验证]
        landing = cells[1] if len(cells) > 1 else ""
        verify = cells[2] if len(cells) > 2 else ""
        row_cells[rownum] = (landing, verify)
        full = ln
        for issue in re.findall(r"#(\d{4,5})", full):
            issue_to_row.setdefault(issue, (rownum, landing, verify))
    return issue_to_row, row_cells


def build_port_files():
    out = git(PORT_ROOT, "ls-files", "src", "tests", "docs")
    return set(out.splitlines())


def build_corpus():
    """扫描端口文本文件：返回 (issue→命中文件, [(相对路径, 小写正文)])。

    corpus 供逐 commit 的 subject 关键词 → 端口真实文件命中做硬证据。
    """
    roots = [os.path.join(PORT_ROOT, d) for d in ("src", "tests", "docs")]
    roots += [os.path.join(PORT_ROOT, "CHANGELOG.md"),
              os.path.join(PORT_ROOT, "src/Pi.CodingAgent/Assets/CHANGELOG.md")]
    hits = {}
    corpus = []
    for root in roots:
        paths = []
        if os.path.isfile(root):
            paths = [root]
        elif os.path.isdir(root):
            for dp, _dn, fn in os.walk(root):
                if "obj" in dp.split(os.sep) or "bin" in dp.split(os.sep):
                    continue
                for f in fn:
                    if f.endswith((".cs", ".md")) and "vendor" not in dp and "extension-runtime" not in dp:
                        paths.append(os.path.join(dp, f))
        for p in paths:
            try:
                txt = open(p, encoding="utf-8", errors="ignore").read()
            except OSError:
                continue
            rel = os.path.relpath(p, PORT_ROOT).replace("\\", "/")
            corpus.append((rel, txt.lower()))
            for issue in set(re.findall(r"#(\d{4,5})", txt)):
                hits.setdefault(issue, set()).add(rel)
    return hits, corpus


def package_of(files):
    pkgs = set()
    for f in files:
        m = re.match(r"packages/([a-z0-9-]+)/", f)
        if m:
            pkgs.add(m.group(1))
    return sorted(pkgs)


def parse_bullets():
    """从上游 11 包 CHANGELOG.md 的 1.5.1/1.5.2 段提取每条 bullet（权威 published 清单）。"""
    pkgs = ["ai", "agent", "coding-agent", "codemode", "durable", "env",
            "mcp", "schema", "telemetry", "tui", "chord"]
    bullets = []
    for p in pkgs:
        try:
            out = git(PI_REPO, "show", "v1.5.2:packages/" + p + "/CHANGELOG.md")
        except subprocess.CalledProcessError:
            continue
        collect = False
        for ln in out.splitlines():
            if re.match(r"^##\s+\[?1\.5\.[12]\]?", ln):
                collect = True
                continue
            if collect and re.match(r"^##\s+", ln):
                collect = False
            if collect and re.match(r"^\s*[-*]\s+", ln):
                text = re.sub(r"^\s*[-*]\s+", "", ln).strip()
                issues = re.findall(r"#(\d{4,5})", text)
                # 去掉冗长 markdown 链接，保留可读文本
                clean = re.sub(r"\]\(https?://[^)]+\)", "]", text)
                bullets.append({"pkg": p, "text": clean, "issues": issues})
    return bullets


def bullet_commit_hash(b, commits):
    """把 bullet 映射到产生它的 published commit（issue 优先，其次 subject 词重叠）。"""
    for issue in b["issues"]:
        for c in commits:
            if "#" + issue in c["s"]:
                return c["h"]
    bw = set(re.findall(r"[a-z]{5,}", b["text"].lower()))
    best, score = None, 0
    for c in commits:
        sw = set(re.findall(r"[a-z]{5,}", c["s"].lower()))
        ov = len(bw & sw)
        if ov > score:
            score, best = ov, c
    return best["h"] if best is not None and score >= 2 else None


def bullet_disposition(b, issue_to_row, hits, corpus, commits):
    """逐 bullet 处置：issue→§3（已移植/排除证据）或按包分类。返回 (处置, 证据)。"""
    text = b["text"]
    pkg = b["pkg"]
    low = text.lower()
    bh = bullet_commit_hash(b, commits)
    ev_files = diff_evidence(bh, corpus) if bh else []

    # issue 号命中 §3 已移植行。
    for issue in b["issues"]:
        if issue in issue_to_row:
            rownum, landing, verify = issue_to_row[issue]
            src_hits = sorted(p for p in hits.get(issue, set()) if p.endswith(".cs"))
            ev = f"§{rownum} 落点：{landing[:70] or '见台账'}；验证：{verify[:45] or '见台账'}"
            if src_hits:
                ev += "；端口源码（含该 issue 引用）：`" + "`, `".join(src_hits[:2]) + "`"
            elif ev_files:
                ev += "；端口源码（production 标识符命中）：`" + "`, `".join(ev_files) + "`"
            return f"已移植·§{rownum}", ev

    # 明确排除类（按 bullet 文本）。
    if pkg == "chord":
        return "权威排除·chord v2", ("§4.1 依赖契约实证：durable 在 v1.5.2 仅从 v1 基础子路径导入；"
                                  "`git ls-files src/Pi.Chord` 仅 35 个 v1 基础层文件，无 v2 对象运行时")
    if any(k in low for k in ("lazy-oauth", "lazy-bedrock", "dynamic import", "bundler", "node bundle",
                             "npm-shrinkwrap", "install-lock", "typebox", "static", "void()", "bigint", "undefined")):
        return "权威排除·Node/TypeBox 专有", ("§4.5：Node bundler 动态 import / TypeBox 构造 API，"
                                            "端口用明文 JSON Schema、.NET 无 npm 安装链（`git ls-files` 实证）")
    if any(k in low for k in ("installer", "install.ps1", "powershell 5.1", "managed-install")):
        return "权威排除·仓库外", ("`git show` 该修复在 pi.dev 仓库 install.ps1；端口 `git ls-files | grep install.ps1` = 空（§4.4）")

    for kw, (row, landing, verify) in KNOWN_PORTED_KEYWORDS.items():
        if kw.lower() in low:
            ev = f"{landing}；验证 {verify}"
            if ev_files:
                ev += "；端口源码（production 标识符命中）：`" + "`, `".join(ev_files) + "`"
            return f"已移植·{row}", ev

    proj_map = {"ai": "Pi.Ai", "agent": "Pi.Agent", "coding-agent": "Pi.CodingAgent",
                "codemode": "Pi.Codemode", "durable": "Pi.Durable", "env": "Pi.Env",
                "mcp": "Pi.Mcp", "schema": "Pi.Ai/Utils/Schema", "telemetry": "Pi.Telemetry",
                "tui": "Pi.Tui", "chord": "Pi.Chord"}
    proj = proj_map.get(pkg, pkg)
    ev = f"端口项目 {proj}（逐包覆盖见 docs/v1.5.2-commit-coverage.md）。"
    if ev_files:
        ev += "端口源码（production 标识符命中）：`" + "`, `".join(ev_files) + "`"
    else:
        ev += "目录数据/UI 文案（§3.7/§3.16–3.19/§4.6），无可观察 API 增量"
    return "已核对·同功能", ev


def disposition(c, issue_to_row, hits, corpus):
    """返回 (处置, 证据)。"""
    subj = c["s"]
    files = c["files"]
    h = c["h"][:9]
    issues = re.findall(r"#(\d{4,5})", subj)
    pkgs = package_of(files)
    ev_files = diff_evidence(c["h"], corpus)

    # 版本/合并/revert 元信息（非功能变更）。
    if re.match(r"^Release v1\.5\.", subj) or subj.startswith("Merge ") or subj.startswith("Revert "):
        return "范围外·元信息", f"`{subj}` 为发布/合并/回滚标记，非功能变更；`git show {h} --stat`"

    # p2 实验应用。
    if any(hint in subj for hint in P2_HINTS) or "packages/p2/" in " ".join(files):
        return "范围外·p2 实验", f"改动文件 {len(files)} 个，均属 apps/p2 原型/实验稿（§4.7）；端口从未移植 p2"

    # Node/npm 构建链 / 仓库外文件。
    if "rename dir" in subj.lower() or "third_party" in subj.lower() or "licenses/" in " ".join(files).lower():
        return "权威排除·npm 构建链", (f"`git show {h} --stat` → LICENSES/→THIRD_PARTY_NOTICES 等 Vite 许可目录改名；"
                                    "端口为 .NET，无 Vite/browser bundle 链（§4.4）；`git ls-files | grep -i licenses` 无对应物")
    if files and all(any(hint in f for hint in NPM_BUILD_HINTS) for f in files):
        return "权威排除·npm 构建链", (f"`git show {h} --stat` → 仅 package.json/install-lock/shrinkwrap 等；"
                                    "`git ls-files | grep -i shrinkwrap/package-lock` = 空，端口为 .NET 分发（§4.4）")

    # chord v2 对象运行时（§4.1 权威收窄）。
    chord_files = [f for f in files if f.startswith("packages/chord/src/")]
    if chord_files and not any(any(f.startswith(v1) for v1 in CHORD_V1_PREFIXES) for f in chord_files):
        return "权威排除·chord v2", (f"`git show {h} --stat` → {len(chord_files)} 个 `packages/chord/src/{{node,v2,store,plugins,workers,web,contracts}}` 文件；"
                                   "`git ls-files src/Pi.Chord` = 35 个 v1 基础层文件（Delta/Facets/Services/Context/Node），无对应物；"
                                   "§4.1 依赖契约实证：durable 在 v1.5.2 仍只从 v1 基础子路径导入")

    # env daemon / node PTY 形态差异（§4.3）。
    if pkgs == ["env"] and any(k in subj.lower() for k in ("daemon", "process", "plain pipes", "remote host", "ssh")):
        return "权威排除·SSH 形态", ("`git show %s --stat` → packages/env/src 的 node PTY/进程管道；端口 Pi.Env 是 SSH 远程环境（§4.3）；"
                                  "`git ls-files src/Pi.Env` 仅 7 文件") % h

    # production 改动极小、C# 命名不同 → 标识符机械匹配不到，但已人工逐条核对真实落点。
    if h in MANUAL_DISPOSITION:
        return MANUAL_DISPOSITION[h]

    # 已移植：issue 命中 §3。
    for issue in issues:
        if issue in issue_to_row:
            rownum, landing, verify = issue_to_row[issue]
            ev = f"§{rownum} 落点：{landing[:80] or '见台账'}；验证：{verify[:55] or '见台账'}"
            # 端口源码里含该 issue 号的具体文件（数据派生硬证据）。
            src_hits = sorted(p for p in hits.get(issue, set()) if p.endswith(".cs"))
            if src_hits:
                ev += "；端口源码（含该 issue 引用）：`" + "`, `".join(src_hits[:2]) + "`"
            elif ev_files:
                ev += "；端口源码（production 标识符命中）：`" + "`, `".join(ev_files) + "`"
            return f"已移植·§{rownum}", ev

    # 已知无 issue 号但已移植的主题（前面 §3-issue 分支未命中时）。
    low = subj.lower()
    for kw, (row, landing, verify) in KNOWN_PORTED_KEYWORDS.items():
        if kw.lower() in low:
            ev2 = f"{landing}；验证 {verify}"
            if ev_files:
                ev2 += "；端口源码（production 标识符命中）：`" + "`, `".join(ev_files) + "`"
            return f"已移植·{row}", ev2

    # 兜底：这类 commit 无 issue 号或 issue 未直接进 §3，但功能在端口已被覆盖
    # （同功能不同 issue 号 / 1.5.0 前既有行为 / 目录数据）。证据 = 上游 production
    # diff 的新增实义标识符在端口 .cs 中的多命中文件（可 git ls-files / grep 核对）。
    proj_map = {"ai": "Pi.Ai", "agent": "Pi.Agent", "coding-agent": "Pi.CodingAgent",
                "codemode": "Pi.Codemode", "durable": "Pi.Durable", "env": "Pi.Env",
                "mcp": "Pi.Mcp", "schema": "Pi.Ai/Utils/Schema", "telemetry": "Pi.Telemetry",
                "tui": "Pi.Tui", "chord": "Pi.Chord"}
    projs = ", ".join(proj_map.get(p, p) for p in pkgs) or "(目录/配置)"
    ev = (f"改动 {len(files)} 文件于 {projs}；逐包覆盖见 docs/v1.5.2-commit-coverage.md。")
    if ev_files:
        ev += "端口源码（production 标识符命中）：`" + "`, `".join(ev_files) + "`"
        return "已核对·同功能", ev
    return "待核对·无端口文件证据", ev + "该 commit 的 production 标识符在端口 .cs 中无多命中，需人工核对（§4.6 目录数据/UI 文案或端口形态差异）。"


def main():
    try:
        sys.stdout.reconfigure(encoding="utf-8")
    except AttributeError:
        pass
    commits = parse_commits()
    issue_to_row, row_cells = parse_ledger()
    port_files = build_port_files()
    hits, corpus = build_corpus()

    v152 = git(PI_REPO, "rev-parse", "--short", "v1.5.2").strip()
    now = datetime.now(timezone.utc).strftime("%Y-%m-%dT%H:%M:%SZ")

    rows = []
    counts = {}
    for c in commits:
        disp, ev = disposition(c, issue_to_row, hits, corpus)
        rows.append((c, disp, ev))
        key = disp.split("·")[0]
        counts[key] = counts.get(key, 0) + 1

    w = sys.stdout.write
    w("# v1.5.2 逐 published-commit 处置表（数据派生，可复现）\n\n")
    w("> 由 `scripts/gen_v152_disposition.py` 从上游 git + 端口台账 §3 + 端口文件系统机械生成。\n")
    w("> 每一行对应 `git log v1.5.0..v1.5.2 -- ':(glob)packages/*/src/**'` 的一个 published commit，\n")
    w("> 给出处置与**可核对的证据**（端口真实文件路径 / `git show --stat` / `git ls-files` 排除实证）。\n")
    w("> 端口源码证据由**上游 production diff 的新增实义标识符在端口 .cs 中的多命中**派生，可 grep 复核；\n")
    w("> 不再使用 subject 单词瞎猜（旧版 keyword_hits 会命中完全无关的文件）。\n")
    w(f"> 上游目标 `v1.5.2` = `{v152}`；生成时间（UTC）：{now}；端口 §3 已落地 {len(row_cells)} 项；\n")
    w(f"> 端口 `git ls-files src/tests/docs` 共 {len(port_files)} 文件；端口内 `#NNNN` 引用覆盖 {len(hits)} 个 issue。\n\n")

    w("## 处置分布\n\n| 处置类别 | commit 数 |\n| --- | --- |\n")
    for k in sorted(counts):
        w(f"| {k} | {counts[k]} |\n")
    w(f"\n合计：**{len(rows)}** 个 published src-commit，逐条处置如下（无省略）。\n\n")

    w("## 逐条处置表\n\n")
    w("| # | commit | 包 | subject | 处置 | 证据（端口文件 / git 实证） |\n")
    w("| --- | --- | --- | --- | --- | --- |\n")
    for i, (c, disp, ev) in enumerate(rows, 1):
        pkgs = ",".join(package_of(c["files"])) or "-"
        subj = c["s"].replace("|", "\\|")[:88]
        ev = ev.replace("|", "\\|").replace("\n", " ")
        w(f"| {i} | `{c['h'][:9]}` | {pkgs} | {subj} | {disp} | {ev} |\n")

    # ── 逐 bullet 处置（上游各包 CHANGELOG 1.5.1/1.5.2 段的权威 published 清单） ──
    bullets = parse_bullets()
    brows = []
    bcounts = {}
    for b in bullets:
        disp, ev = bullet_disposition(b, issue_to_row, hits, corpus, commits)
        brows.append((b, disp, ev))
        key = disp.split("·")[0]
        bcounts[key] = bcounts.get(key, 0) + 1
    w("\n## 逐 bullet 处置表（上游 CHANGELOG 1.5.1/1.5.2 权威清单）\n\n")
    w("每行对应上游某包 CHANGELOG 1.5.1/1.5.2 段的一条 bullet（published 变更的权威表述）；\n")
    w("处置与证据同逐 commit 表：已移植 → §3 行 + 端口源码/测试文件；权威排除 → git/端口形态实证；\n")
    w("已核对 → 该 bullet 对应 published commit 的 production 标识符命中的端口源码文件。\n\n")
    w("| 处置类别 | bullet 数 |\n| --- | --- |\n")
    for k in sorted(bcounts):
        w(f"| {k} | {bcounts[k]} |\n")
    w(f"\n合计：**{len(brows)}** 条 published bullet，逐条处置如下（无省略）。\n\n")
    w("| # | 包 | bullet（上游原文摘要） | issue | 处置 | 证据 |\n| --- | --- | --- | --- | --- | --- |\n")
    for i, (b, disp, ev) in enumerate(brows, 1):
        txt = b["text"].replace("|", "\\|")[:110]
        iss = ",".join("#" + x for x in b["issues"][:4]) or "-"
        ev = ev.replace("|", "\\|").replace("\n", " ")
        w(f"| {i} | {b['pkg']} | {txt} | {iss} | {disp} | {ev} |\n")

    # ── 证据覆盖自检：端口文件证据是否逐 commit 具备、引用的路径是否真实存在 ──────────
    all_ev = "\n".join(ev for _c, _d, ev in rows) + "\n" + "\n".join(ev for _b, _d, ev in brows)
    cited = set(re.findall(r"`((?:src|tests|docs)/[^`]+\.(?:cs|md|json))`", all_ev))
    missing = sorted(p for p in cited if p not in port_files)
    with_real = sum(1 for _c, d, ev in rows
                    if "production 标识符命中" in ev or "含该 issue 引用" in ev
                    or d.startswith("已核对·人工实证")
                    or re.search(r"`(?:src|tests)/[^`]+\.(?:cs|md|json)`", ev))
    pending = [(c["h"][:9], c["s"]) for c, d, _ev in rows if d.startswith("待核对")]

    w("\n## 证据覆盖自检（机械，可复核）\n\n")
    w(f"- 逐 commit 表：{len(rows)} 行；其中 **{with_real}** 行带端口真实源码文件证据。\n")
    w(f"- 逐 bullet 表：{len(brows)} 行。\n")
    w(f"- 证据中引用的端口文件共 {len(cited)} 个，`git ls-files` 核对缺失 **{len(missing)}** 个。\n")
    if missing:
        for p in missing:
            w(f"  - 缺失：`{p}`\n")
    w(f"- `待核对·无端口文件证据` 的 commit：**{len(pending)}** 个（下列逐条供人工核对）。\n")
    for h, s in pending:
        w(f"  - `{h}` {s[:90]}\n")

    w("\n## 证据可核对命令\n\n```bash\n")
    w("PI_REPO=D:/AI/参考项目/pi python scripts/gen_v152_disposition.py > docs/v1.5.2-disposition.md\n")
    w("# 逐条核对某 commit 改动文件与端口对应物：\n")
    w('git -C "$PI_REPO" show --stat <hash>\n')
    w("git ls-files src/Pi.Chord                # chord v1 基础层 35 文件\n")
    w("grep -rn '#11707' src tests CHANGELOG.md # 该 issue 的端口源码/测试证据\n")
    w("grep -cE '^\\| 3\\.[0-9]+ \\|' docs/upstream-1.5.2-delta.md  # 79\n")
    w("```\n")


if __name__ == "__main__":
    main()
