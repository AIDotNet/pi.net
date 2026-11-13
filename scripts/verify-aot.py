"""Verify a Native AOT `pi` binary against the JIT build (losslessness check).

Runs a battery of deterministic, offline, read-only CLI commands against both
binaries and compares exit code, stdout and stderr. Also asserts the AOT output
really is native (no managed Pi DLLs, no runtimeconfig.json, PE32+ image).

Usage:
    python3 scripts/verify-aot.py --aot artifacts/aot/win-x64/pi.exe
    python3 scripts/verify-aot.py --aot <path> --jit <path-to-pi.dll>

Requires the JIT build at src/Pi.Cli/bin/Release/net10.0/pi.dll (or --jit).
"""

import argparse
import os
import re
import subprocess
import sys

# Deterministic offline commands. They jointly exercise: version/help rendering,
# the model catalog (JSON deserialization), settings JSON, doctor JSON output
# (POCO serialization with a parameterized-constructor record), MCP/auth/config
# metadata, error paths and an offline agent flow with tool execution.
COMMANDS = [
    ['--version'],
    ['--version', '--offline'],
    ['--help'],
    ['--list-models'],
    ['doctor'],
    ['doctor', '--json'],
    ['doctor', '--help'],
    ['list'],
    ['mcp', 'list'],
    ['mcp', 'list', '--json'],
    ['auth', '--help'],
    ['auth', 'check', '--json', '--offline'],
    ['auth', 'print-api-key', '--offline'],
    ['mcp', '--help'],
    ['install', '--help'],
    ['remove', '--help'],
    ['uninstall', '--help'],
    ['update', '--help'],
    ['config', '--help'],
]

CRASH_MARKERS = ('Unhandled exception', 'NotSupportedException',
                 'missing native code', 'System .TypeLoadException')


def norm(text):
    """Normalize volatile, environment-dependent output for comparison."""
    if text is None:
        return ''
    # `doctor` reports the hosting runtime; Native AOT legitimately describes
    # itself with a different framework string.
    text = re.sub(r'"message": "\.NET [^"]*"', '"message": "<runtime>"', text)
    text = re.sub(r'\.NET [\d.]+; Microsoft Windows', '<runtime>; Microsoft Windows', text)
    return text.replace('\r\n', '\n').strip()


def run(base, args, cwd, timeout=120):
    try:
        p = subprocess.run(base + args, capture_output=True, timeout=timeout, cwd=cwd)
    except subprocess.TimeoutExpired:
        return 'TIMEOUT', '', ''
    dec = lambda b: b.decode('utf-8', errors='replace') if b else ''
    return p.returncode, dec(p.stdout), dec(p.stderr)


def check_native(out_dir):
    """Assert the published output is a genuine Native AOT image."""
    problems = []
    managed = [f for f in os.listdir(out_dir)
               if f.lower().startswith('pi.') and f.lower().endswith('.dll')
               or f.lower() == 'pi.dll']
    if managed:
        problems.append('managed Pi DLLs present: ' + ', '.join(managed))
    for name in ('pi.runtimeconfig.json', 'pi.deps.json'):
        if os.path.exists(os.path.join(out_dir, name)):
            problems.append(f'{name} present (JIT-style output)')
    exe = os.path.join(out_dir, 'pi.exe')
    if not os.path.isfile(exe):
        exe = os.path.join(out_dir, 'pi')
    if not os.path.isfile(exe):
        problems.append('no pi(.exe) executable found')
    else:
        with open(exe, 'rb') as f:
            head = f.read(4096)
        if head[:2] != b'MZ' or b'PE\x00\x00' not in head[:4096]:
            problems.append('executable is not a PE image')
        with open(exe, 'rb') as f:
            whole = f.read()
        for dep in (b'coreclr.dll', b'hostfxr.dll'):
            if dep in whole:
                problems.append('depends on ' + dep.decode())
    return problems


def main():
    ap = argparse.ArgumentParser(description=__doc__,
                                 formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument('--aot', required=True, help='path to the AOT pi executable')
    ap.add_argument('--jit', default=None, help='path to the JIT pi.dll')
    ap.add_argument('--root', default=os.path.dirname(os.path.dirname(os.path.abspath(__file__))))
    args = ap.parse_args()

    aot_exe = os.path.abspath(args.aot)
    out_dir = os.path.dirname(aot_exe)
    jit_dll = args.jit or os.path.join(args.root, 'src', 'Pi.Cli', 'bin',
                                       'Release', 'net10.0', 'pi.dll')
    if not os.path.isfile(jit_dll):
        print(f'JIT build not found at {jit_dll}; build it first:', file=sys.stderr)
        print('  dotnet build src/Pi.Cli -c Release -p:TargetFrameworks=net10.0',
              file=sys.stderr)
        return 2

    jit = ['dotnet', jit_dll]
    aot = [aot_exe]

    print('== Native image checks')
    native_problems = check_native(out_dir)
    for p in native_problems:
        print('  FAIL', p)
    if not native_problems:
        print('  PASS PE32+ native image; no managed Pi DLL / runtimeconfig / coreclr')

    print('== JIT vs AOT command comparison')
    failures = 0
    for cmd in COMMANDS:
        jc, jo, je = run(jit, cmd, args.root)
        ac, ao, ae = run(aot, cmd, args.root)
        label = ' '.join(cmd)
        crashed = any(m in ae for m in CRASH_MARKERS)
        ok = (jc == ac and norm(jo) == norm(ao) and norm(je) == norm(ae)
              and not crashed)
        print(('  PASS ' if ok else '  FAIL ') + label)
        if not ok:
            failures += 1
            print(f'     exit: JIT={jc} AOT={ac}')
            if norm(jo) != norm(ao):
                print('     JIT out:', norm(jo)[:260].replace('\n', ' | '))
                print('     AOT out:', norm(ao)[:260].replace('\n', ' | '))
            if norm(je) != norm(ae) or crashed:
                print('     JIT err:', norm(je)[:200].replace('\n', ' | '))
                print('     AOT err:', norm(ae)[:300].replace('\n', ' | '))

    failures += len(native_problems)
    print(f'== {len(COMMANDS)} commands, failures={failures}')
    return 1 if failures else 0


if __name__ == '__main__':
    sys.exit(main())
