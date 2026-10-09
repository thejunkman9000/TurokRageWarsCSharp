"""Adds Dbg.Leave tracing to the return statements of generated functions (debug aid; edit the .cs by hand and run again with --remove to undo).
usage: python tools/dbg_addleave.py func_00250ACC func_00250754 ...   |   --remove names..."""
import re, sys, pathlib
gen = pathlib.Path(__file__).resolve().parent.parent / 'src' / 'Trw.Game' / 'Generated'
remove = '--remove' in sys.argv
for name in [a for a in sys.argv[1:] if not a.startswith('--')]:
    p = gen / f'{name}.cs'
    if not p.exists(): p = gen / 'Recompiled' / f'{name}.cs'
    s = p.read_text(encoding='utf-8')
    if remove:
        s = re.sub(r'return \(_dbgR = (.*?)\) \* 0 \+ _dbgLeave\("' + name + r'", _dbgR\);', r'return \1;', s)
        # (not reachable form; fall through)
    pat = re.compile(r'^(\s*)return (.*);\s*$', re.M)
    if remove:
        s = re.sub(r'^(\s*)\{ var _r = (.*?); if \(Dbg\.On\) Dbg\.Leave\("' + name + r'", \(uint\)_r\); return _r; \}\s*$', r'\1return \2;', s, flags=re.M)
    else:
        if f'Dbg.Leave("{name}"' in s: continue
        s = pat.sub(lambda m: f'{m.group(1)}{{ var _r = {m.group(2)}; if (Dbg.On) Dbg.Leave("{name}", (uint)_r); return _r; }}', s)
    p.write_text(s, encoding='utf-8', newline='')
    print(('removed' if remove else 'patched'), p.name)
