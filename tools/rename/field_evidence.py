"""Evidence for naming the fields of one game object: every access to a field of the given struct view types, grouped by offset.

Usage: python tools/rename/field_evidence.py OUT.md Type1,Type2,...
Writes, per offset: the members of the alias types there (name, type), the number of accesses, the functions that make them (readable names)
and a few sample lines. Only direct accesses (new T(x).member) are collected; chains through other structs are not followed.
"""
import os, re, sys
sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import apply_names as an
import fields as fl

FUNC = re.compile(r'^    public static \S+ (\w+)\(', re.M)


def main():
    out_path, tlist = sys.argv[1], sys.argv[2].split(',')
    types = fl.parse_structs()
    tset = [t for t in tlist if t in types]
    pat = re.compile(r'\bnew (' + '|'.join(map(re.escape, tset)) + r')\(')
    acc = {}       # offset -> dict(members=set, n=0, funcs=set, samples=[])
    for p in an.cs_files(an.GEN):
        if os.sep + 'Structs' + os.sep in p: continue
        s = an.read(p)
        funcs = [(m.start(), m.group(1)) for m in FUNC.finditer(s)]
        for m in pat.finditer(s):
            t = m.group(1)
            j = fl.match_close(s, m.end() - 1, '(', ')')
            if j < 0 or j + 1 >= len(s) or s[j + 1] != '.': continue
            idm = fl.IDENT.match(s, j + 2)
            if not idm: continue
            name = idm.group(0); base = name[:-4] if name.endswith('_Set') else name
            if base not in types[t]['members']: continue
            off, mt = types[t]['members'][base]
            a = acc.setdefault(off, dict(members=set(), n=0, funcs=set(), samples=[]))
            a['members'].add(f'{t}.{base}:{mt}'); a['n'] += 1
            fn = next((f for pos, f in reversed(funcs) if pos < m.start()), '?')
            a['funcs'].add(fn)
            if len(a['samples']) < 3:
                ls = s.rfind('\n', 0, m.start()) + 1; le = s.find('\n', m.start())
                line = s[ls:le].strip()
                if line not in a['samples']: a['samples'].append(line[:220])
    # members never accessed directly still belong to the layout
    for t in tset:
        for base, (off, mt) in types[t]['members'].items():
            acc.setdefault(off, dict(members=set(), n=0, funcs=set(), samples=[]))['members'].add(f'{t}.{base}:{mt}')
    with open(out_path, 'w', encoding='utf-8') as f:
        f.write(f'# field evidence for {",".join(tset)}\n\n')
        for off in sorted(acc):
            a = acc[off]
            f.write(f'## +0x{off:X}  accesses {a["n"]}\nmembers: {", ".join(sorted(a["members"]))}\n')
            if a['funcs']: f.write(f'functions: {", ".join(sorted(a["funcs"])[:20])}{" ..." if len(a["funcs"]) > 20 else ""}\n')
            for l in a['samples']: f.write(f'    {l}\n')
            f.write('\n')
    print(f'{out_path}: {len(acc)} offsets, {sum(a["n"] for a in acc.values())} direct accesses')


if __name__ == '__main__':
    main()
