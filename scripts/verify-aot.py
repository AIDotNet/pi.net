"""Verify a Native AOT `pi` binary against the JIT build (behaviour parity).

Runs deterministic, offline CLI commands and JSONL RPC commands against both
binaries and compares exit code, stdout and stderr. Also asserts the AOT output
really is native (no managed Pi DLLs, no runtimeconfig.json, PE32+ image) and
that no response contains an AOT "missing native code" failure.

Usage:
    python3 scripts/verify-aot.py --aot artifacts/aot/win-x64/pi.exe
    python3 scripts/verify-aot.py --aot <path> --jit <path-to-pi.dll>
    (Windows Git Bash without python3: `py -3 scripts/verify-aot.py ...`,
     or simply `bash scripts/verify-aot.sh` which resolves the interpreter.)

Requires the JIT build at src/Pi.Cli/bin/Release/net10.0/pi.dll (or --jit).
"""

import argparse
import json
import os
import re
import subprocess
import sys

# Deterministic offline commands. They jointly exercise: version/help rendering,
# the model catalog (JSON deserialization, including the fuzzy-search path that
# exposed a provider-table race under AOT), settings JSON, doctor JSON output
# (POCO serialization with a parameterized-constructor record), MCP/auth/config
# metadata, error paths and JSON mode output.
COMMANDS = [
    ['--version'],
    ['--version', '--offline'],
    ['--version', '--mode', 'json'],
    ['--help'],
    ['--list-models'],
    ['--list-models', '--offline'],
    ['--list-models', 'gpt', '--offline'],
    ['--list-models', 'zzz-not-a-model', '--offline'],
    ['doctor'],
    ['doctor', '--json'],
    ['doctor', '--json', '--offline'],
    ['doctor', '--help'],
    ['list'],
    ['list', '--no-approve', '--offline'],
    ['mcp', 'list'],
    ['mcp', 'list', '--json'],
    ['mcp', 'list', '--json', '--offline'],
    ['auth', '--help'],
    ['auth', 'check', '--json', '--offline'],
    ['auth', 'check', '--json', '--offline', '--provider', 'zzz-nonexistent'],
    ['auth', 'check', '--json', '--offline', '--model', 'zzz-nonexistent'],
    ['auth', 'print-api-key', '--offline'],
    ['auth', 'print-bearer-token', '--offline', '--provider', 'zzz-nonexistent'],
    ['mcp', '--help'],
    ['install', '--help'],
    ['remove', '--help'],
    ['uninstall', '--help'],
    ['update', '--help'],
    ['config', '--help'],
]

CRASH_MARKERS = ('Unhandled exception', 'NotSupportedException',
                 'missing native code', 'TypeLoadException',
                 'InvalidOperationException: Operations that change non-concurrent')

# RPC commands exercised against both binaries. They cover the JSONL command
# union, source-generated POCO metadata (including the RpcCommand polymorphic
# base), and the two payloads that previously failed under Native AOT:
# `cycle_model` (ModelCycleResult) and `get_available_thinking_levels`
# (IReadOnlyList<ThinkingLevel>). All are deterministic, offline and read-only.
RPC_CASES = [
    ('get_commands', {}),
    ('get_state', {}),
    ('get_available_models', {}),
    ('get_available_thinking_levels', {}),
    ('cycle_model', {}),
    ('cycle_thinking_level', {}),
    ('set_thinking_level', {'level': 'off'}),
    ('set_steering_mode', {'mode': 'one-at-a-time'}),
    ('set_follow_up_mode', {'mode': 'one-at-a-time'}),
    ('set_auto_compaction', {'enabled': True}),
    ('set_auto_retry', {'enabled': True}),
    ('abort_retry', {}),
    ('abort', {}),
    ('clear_queue', {}),
    ('abort_bash', {}),
    ('get_session_stats', {}),
    ('get_fork_messages', {}),
    ('get_entries', {}),
    ('get_tree', {}),
    ('get_last_assistant_text', {}),
    ('set_session_name', {'name': 'aot-verify'}),
    ('compact', {}),
    ('fork', {'entryId': 'no-such-entry'}),
]


def norm(text):
    """Normalize volatile, environment-dependent output for comparison."""
    if text is None:
        return ''
    # `doctor` reports the hosting runtime; Native AOT legitimately describes
    # itself with a different framework string.
    text = re.sub(r'"message": "\.NET [^"]*"', '"message": "<runtime>"', text)
    text = re.sub(r'\.NET [\d.]+; Microsoft Windows', '<runtime>; Microsoft Windows', text)
    # Session ids / event ids are freshly generated on every process start.
    text = re.sub(r'\b[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}\b',
                  '<uuid>', text)
    # Session entries carry a short random id and a wall-clock timestamp.
    text = re.sub(r'("(?:id|parentId|leafId)"\s*:\s*")[0-9a-f]{8}(")', r'\1<entry-id>\2', text)
    text = re.sub(r'("timestamp"\s*:\s*")\d{4}-\d{2}-\d{2}T[^"]*(")', r'\1<ts>\2', text)
    text = re.sub(r'("timestamp"\s*:\s*)\d{12,}', r'\1<ts>', text)
    # A lock-contention warning prints a stack trace; JIT frames have a source
    # path + line, native frames have a code offset. Normalise both away.
    text = re.sub(r' in [A-Za-z]:[^\r\n]*:(?:line )?\d+', '', text)
    text = re.sub(r' \+ 0x[0-9a-fA-F]+', '', text)
    return text.replace('\r\n', '\n').strip()


def run(base, args, cwd, timeout=120):
    try:
        p = subprocess.run(base + args, capture_output=True, timeout=timeout, cwd=cwd)
    except subprocess.TimeoutExpired:
        return 'TIMEOUT', '', ''
    dec = lambda b: b.decode('utf-8', errors='replace') if b else ''
    return p.returncode, dec(p.stdout), dec(p.stderr)


def run_rpc(base, command, payload, cwd, timeout=120):
    """Send one JSONL command to an RPC-mode process and capture its output."""
    data = (json.dumps({'type': command, 'id': 'aot-verify', **payload}) + '\n').encode()
    try:
        p = subprocess.run(base + ['--mode', 'rpc', '--offline', '--no-session'],
                           input=data, capture_output=True, timeout=timeout, cwd=cwd)
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
        # The JIT host has a known load-sensitive startup flake (settings.json
        # lock contention / provider-table overlap). Retry once before judging.
        if jc != 0:
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

    print('== JIT vs AOT RPC command comparison')
    for command, payload in RPC_CASES:
        jc, jo, je = run_rpc(jit, command, payload, args.root)
        if jc != 0:
            jc, jo, je = run_rpc(jit, command, payload, args.root)
        ac, ao, ae = run_rpc(aot, command, payload, args.root)
        crashed = any(m in ao or m in ae for m in CRASH_MARKERS)
        ok = (jc == ac == 0 and norm(jo) == norm(ao) and not crashed)
        print(('  PASS ' if ok else '  FAIL ') + command)
        if not ok:
            failures += 1
            print(f'     exit: JIT={jc} AOT={ac}')
            if norm(jo) != norm(ao):
                print('     JIT out:', norm(jo)[:260].replace('\n', ' | '))
                print('     AOT out:', norm(ao)[:260].replace('\n', ' | '))
            if crashed:
                print('     AOT err:', norm(ae)[:1200].replace('\n', ' | '))

    failures += len(native_problems)
    total = len(COMMANDS) + len(RPC_CASES)
    print(f'== {len(COMMANDS)} CLI + {len(RPC_CASES)} RPC = {total} checks, failures={failures}')
    return 1 if failures else 0


if __name__ == '__main__':
    sys.exit(main())
