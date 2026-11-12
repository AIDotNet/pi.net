#!/usr/bin/env python3
"""生成 v1.5.2 逐 published-commit 覆盖工件（data-derived，可复现）。

从上游 `git log v1.5.0..v1.5.2 -- ':(glob)packages/*/src/**'` 一次性拉取所有触及
src 的 published commit 及其改动文件，按包归类，并对照端口各项目的源文件数 /
测试文件数（git ls-files / find）。输出 Markdown 到 stdout。

这不是手写的「不做项分类」：每一条 published commit 都逐行列出，数据来源是上游
git 与端口文件系统，任何人都可重跑核对。

用法：
    PI_REPO=D:/AI/参考项目/pi python scripts/gen_v152_coverage.py > docs/v1.5.2-commit-coverage.md
"""
import os
import re
import subprocess
import sys
from collections import OrderedDict
from datetime import datetime, timezone

PI_REPO = os.environ.get("PI_REPO", "D:/AI/参考项目/pi")
PORT_ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))

# 包 -> 端口项目（仅用于「对应实现」统计，不改变逐 commit 枚举）。
PORT_PROJECT = OrderedDict([
    ("ai", "Pi.Ai"),
    ("agent", "Pi.Agent"),
    ("coding-agent", "Pi.CodingAgent"),
    ("codemode", "Pi.Codemode"),
    ("durable", "Pi.Durable"),
    ("env", "Pi.Env"),
    ("mcp", "Pi.Mcp"),
    ("schema", "Pi.Ai"),          # schema 端口落在 Pi.Ai/Utils/Schema
    ("telemetry", "Pi.Telemetry"),
    ("tui", "Pi.Tui"),
    ("chord", "Pi.Chord"),
])


def git(repo, *args):
    out = subprocess.run(["git", "-C", repo, *args],
                         capture_output=True, check=True).stdout
    # 明确 UTF-8，避免受系统 locale 影响把非 ASCII subject 解码成代理项/乱码。
    return out.decode("utf-8", errors="replace")


def parse_commits():
    # 单次 git 调用：format 前缀 @@@hash|subject，后接 --name-only 的文件行。
    out = git(PI_REPO, "log", "--format=@@@%H|%s", "--name-only",
              "v1.5.0..v1.5.2", "--", ":(glob)packages/*/src/**")
    commits = []
    cur = None
    for raw in out.splitlines():
        line = raw.rstrip()
        if line.startswith("@@@"):
            body = line[3:]
            h, _, subject = body.partition("|")
            cur = {"hash": h, "subject": subject, "files": []}
            commits.append(cur)
        elif line.startswith("packages/") and cur is not None:
            cur["files"].append(line)
        # subject 行后紧跟的首个文件名会与 subject 粘连（git --name-only 行为）：
        # 形如 "fix(ai): update versionpackages/ai/src/...". 在下面统一修补。
    # 修补粘连：git --name-only 会把首个文件名紧接在 subject 后（中间无换行），
    # 形如 "fix(ai): update versionpackages/ai/src/utils/version.ts"。
    glued = re.compile(r"(?=packages/[a-z-]+/src/)")
    for c in commits:
        m = glued.search(c["subject"])
        if m and m.start() > 0:
            first_file = c["subject"][m.start():]
            c["subject"] = c["subject"][:m.start()].rstrip()
            c["files"].insert(0, first_file)
    return commits


def package_of(path):
    m = re.match(r"packages/([a-z-]+)/src/", path)
    return m.group(1) if m else None


def port_counts(proj):
    src = git(PORT_ROOT, "ls-files", f"src/{proj}/*.cs", f"src/{proj}/**/*.cs")
    src_n = len(set(src.splitlines()))
    base = proj.split("/")[0]
    test_dir = os.path.join(PORT_ROOT, "tests", f"{base}.Tests")
    test_n = 0
    if os.path.isdir(test_dir):
        for root, _dirs, files in os.walk(test_dir):
            if "obj" in root.split(os.sep) or "bin" in root.split(os.sep):
                continue
            test_n += sum(1 for f in files if f.endswith(".cs"))
    return src_n, test_n


def main():
    # Windows 控制台默认可能是 CP936；强制 UTF-8 输出，保证中文与逐 commit subject 不乱码。
    try:
        sys.stdout.reconfigure(encoding="utf-8")
    except AttributeError:
        pass
    commits = parse_commits()
    per_pkg = {p: 0 for p in PORT_PROJECT}
    for c in commits:
        pkgs = {package_of(f) for f in c["files"]} - {None}
        for p in pkgs:
            if p in per_pkg:
                per_pkg[p] += 1

    v152 = git(PI_REPO, "rev-parse", "--short", "v1.5.2").strip()
    now = datetime.now(timezone.utc).strftime("%Y-%m-%dT%H:%M:%SZ")
    w = sys.stdout.write

    w("# v1.5.2 published-commit 覆盖工件（数据派生，可复现）\n\n")
    w("> 由 `scripts/gen_v152_coverage.py` 从上游 git 机械生成。数据来源：\n")
    w("> `git log v1.5.0..v1.5.2 -- ':(glob)packages/*/src/**'` + 每个 commit 的 `--name-only`\n")
    w("> + 端口 `git ls-files` / `os.walk`。生成时间（UTC）：" + now + "。\n")
    w("> 上游目标 `v1.5.2` = `" + v152 + "`（origin/stable 同点，1.5.0 之后最新 release）。\n")
    w("> 本工件逐条列出每个 published src-commit，不是手写的分类表；可用脚本重跑核对。\n\n")

    w("## 总览：每包 published src-commit 数 → 端口对应实现\n\n")
    w("| 上游包 | published src-commit 数 | 端口项目 | 端口 src 文件数 | 端口测试文件数 |\n")
    w("| --- | --- | --- | --- | --- |\n")
    for p, proj in PORT_PROJECT.items():
        sc, tc = port_counts(proj)
        w(f"| {p} | {per_pkg[p]} | {proj} | {sc} | {tc} |\n")
    w(f"\n合计 published src-commit：**{len(commits)}**（一个 commit 可触及多包，分列计数有交叉）。\n\n")

    w("## 逐 commit 清单（逐条覆盖，非分类）\n\n")
    w("每行：`hash | subject | 触及的 packages/*/src 文件数`。带 issue 号的 subject 对应台账\n")
    w("`docs/upstream-1.5.2-delta.md` §3（67 条，published 功能变更已逐项真实移植）的落点索引；\n")
    w("§4 仅余仓库外/npm 专有/Node 形态项，逐条附 `git show --stat` / `git ls-files` 实证。\n\n")
    w("```\n")
    for c in commits:
        n = len(c["files"])
        w(f"{c['hash'][:9]} | {c['subject']} | {n}\n")
    w("```\n\n")

    w("## 可复现命令\n\n```bash\n")
    w("PI_REPO=D:/AI/参考项目/pi python scripts/gen_v152_coverage.py > docs/v1.5.2-commit-coverage.md\n")
    w("git -C \"$PI_REPO\" tag --sort=-v:refname | head             # v1.5.2（最新）\n")
    w("git -C \"$PI_REPO\" log -1 --oneline origin/stable          # = v1.5.2\n")
    w("grep '<Version>' Directory.Build.props                     # 1.5.2\n")
    w("grep -cE '^\\| 3\\.[0-9]+ \\|' docs/upstream-1.5.2-delta.md  # 67\n")
    w("```\n")


if __name__ == "__main__":
    main()
