#!/usr/bin/env python3
"""Differential audit of an upstream release range against the port's .cs files.

For every upstream commit that changes packages/*/src, extract newly added identifiers from the
-U0 patch and look them up in the port's src/ and tests/ .cs files. Prints a per-commit hit rate
and the identifiers that appear nowhere (a signal, not proof: TS and C# names differ).

Usage:
  PI_REPO=/path/to/pi PI_PORT=/path/to/pi.net RANGE=v1.5.2..v1.6.0 python scripts/audit_v160.py
"""
import collections
import os
import re
import subprocess

UP = os.environ.get("PI_REPO", r"D:/AI/参考项目/pi")
PORT = os.environ.get("PI_PORT", os.path.dirname(os.path.dirname(os.path.abspath(__file__))))
RANGE = os.environ.get("RANGE", "v1.5.2..v1.6.0")
PKGS = ("agent", "ai", "chord", "codemode", "coding-agent", "durable", "env", "evals",
        "mcp", "schema", "telemetry", "tui")


def git(args):
    return subprocess.run(["git"] + args, cwd=UP, capture_output=True, text=True,
                          encoding="utf-8", errors="replace").stdout


log = git(["log", "--format=@@@%h|%s", "-p", "-U0", RANGE, "--"] +
          [f"packages/{p}/src" for p in PKGS])
port_files = []
for base in ("src", "tests"):
    for root, _dirs, files in os.walk(os.path.join(PORT, base)):
        for f in files:
            if f.endswith(".cs"):
                port_files.append(os.path.join(root, f))
blob = "\n".join(open(f, encoding="utf-8", errors="replace").read() for f in port_files)

IDENT = re.compile(r"^\+.*?\b([a-z][A-Za-z0-9]{6,}|[A-Z][A-Za-z0-9]{5,})\b")
SKIP = re.compile(r"^(?:[\d_]+|true|false|null|undefined|string|number|boolean|Promise|Array|Object|JSON|Math|window|document)$")

cur = None
data = collections.OrderedDict()
for line in log.splitlines():
    if line.startswith("@@@"):
        h, subject = line[3:].split("|", 1)
        cur = h
        data[cur] = {"subject": subject, "files": set(), "idents": collections.Counter()}
    elif line.startswith("+++ b/"):
        if cur is not None and "/test" not in line and "/tests" not in line:
            data[cur]["files"].add(line[6:])
    elif line.startswith("+") and not line.startswith("+++"):
        m = IDENT.match(line)
        if m:
            ident = m.group(1)
            if not SKIP.match(ident) and len(ident) > 5 and cur is not None:
                data[cur]["idents"][ident] += 1

print(f"range {RANGE}; commits with production src changes: {sum(1 for c in data.values() if c['files'])}")
print()
zero = []
for h, c in data.items():
    if not c["files"]:
        continue
    missing = [i for i in c["idents"] if i not in blob]
    total = len(c["idents"])
    hit = total - len(missing)
    rate = (hit / total * 100) if total else 100.0
    flag = "  <-- LOW" if total >= 3 and rate < 40 else ""
    print(f"{h} {rate:5.1f}% ({hit}/{total}) {c['subject'][:78]}{flag}")
    for ident in missing:
        zero.append((h, c["subject"], ident))

print()
print("=== identifiers absent from the port ===")
seen = collections.Counter(z[2] for z in zero)
for ident, n in seen.most_common(120):
    commits = [f"{h}:{s[:50]}" for h, s, i in zero if i == ident][:2]
    print(f"{ident:44s} x{n}  {commits}")
