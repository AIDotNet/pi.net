#!/usr/bin/env python3
"""列出上游 CHANGELOG 某个版本的条目（用于逐条处置表的自检）。

用法：python scripts/list-changelog-bullets.py [CHANGELOG.md] [版本，如 1.6.0] [下一版本，如 1.5.2]
"""
import io
import sys

p = sys.argv[1] if len(sys.argv) > 1 else r'D:/AI/参考项目/pi/packages/coding-agent/CHANGELOG.md'
version = sys.argv[2] if len(sys.argv) > 2 else '1.6.0'
next_version = sys.argv[3] if len(sys.argv) > 3 else '1.5.2'

s = io.open(p, encoding='utf-8', newline='').read()
start = s.index('## [' + version + ']')
end = s.index('## [' + next_version + ']')
sec = s[start:end]

section = None
out = []
for line in sec.splitlines():
    if line.startswith('### '):
        section = line[4:].strip()
    elif line.startswith('- '):
        out.append((section, line[2:].strip()))
    elif line.strip() and out and not line.startswith('#'):
        prev = out[-1]
        out[-1] = (prev[0], prev[1] + ' ' + line.strip())

for i, (section_name, text) in enumerate(out, 1):
    print(f'{i:02d} [{section_name}] {text}')
print('TOTAL', len(out))
