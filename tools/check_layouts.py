#!/usr/bin/env python3
"""Checks the transpiler's struct layouts against clang's own (-fdump-record-layouts, MIPS o32), file by file.
usage: python tools/check_layouts.py <c-repo> <list-of-.c-files.txt> [max-files]
A wrong layout moves every field behind it, so this must stay clean."""
import os, re, subprocess, sys, tempfile

root, listfile = sys.argv[1], sys.argv[2]
limit = int(sys.argv[3]) if len(sys.argv) > 3 else 150
all_files = [l.strip() for l in open(listfile) if l.strip()]
files = all_files[::max(1, len(all_files) // limit)][:limit]

tmp = os.path.join(tempfile.gettempdir(), 'layouts_mine.txt')
if os.path.exists(tmp): os.remove(tmp)
env = dict(os.environ, TRW_DUMP_LAYOUTS=tmp)
repo = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
subprocess.run(['dotnet', 'run', '--project', os.path.join(repo, 'tools', 'Trw.Transpile'), '--no-build', '--', '--ast-cache',
                os.path.join(tempfile.gettempdir(), 'trw_ast'), '--root', root] + files, env=env, capture_output=True, text=True)

# mine: file -> name -> (size, align)
mine = {}
cur_file = cur = None
for l in open(tmp, encoding='utf-8'):
    m = re.match(r'# file (\S+)', l)
    if m: cur_file = m.group(1).replace('\\', '/'); mine[cur_file] = {}; continue
    m = re.match(r'record (\S+) (struct|union) size=(\d+) align=(\d+)', l)
    if m and cur_file and not m.group(1).startswith('<'):
        mine[cur_file][m.group(1)] = (int(m.group(3)), int(m.group(4)))

n = bad = 0
for f in files:
    r = subprocess.run([sys.executable, '-m', 'ziglang', 'cc', '-target', 'mips-linux-gnueabi', '-fsyntax-only', '-w', '-std=gnu89', '-D_LANGUAGE_C',
                        '-DTARGET_PC', '-DM2CTX', '-Iinclude', '-Xclang', '-fdump-record-layouts', f], cwd=root, capture_output=True, text=True)
    theirs = {}
    for blk in r.stdout.split('*** Dumping AST Record Layout')[1:]:
        lines = [x for x in blk.split('\n') if x.strip()]
        head = re.match(r'\s*0 \| (struct|union) (\S+)\s*$', lines[0]) if lines else None
        sz = re.search(r'\[sizeof=(\d+)(?:, dsize=\d+)?, align=(\d+)', blk)
        if head and sz: theirs[head.group(2)] = (int(sz.group(1)), int(sz.group(2)))
    for name, v in theirs.items():
        got = mine.get(f.replace('\\', '/'), {}).get(name)
        if got is None: continue
        n += 1
        if got != v:
            bad += 1
            print(f'MISMATCH {f}: {name}: mine size={got[0]} align={got[1]}  clang size={v[0]} align={v[1]}')
print(f'compared {n} named records from {len(files)} files, {bad} mismatches')
