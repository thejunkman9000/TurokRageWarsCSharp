"""Merges proposed names (orig, name, group, confidence, evidence rows, e.g. from a naming pass) into tools/names/functions.tsv.

Checks every row before anything is written: the function exists and has no name yet, the name is a valid identifier that is unique (within the batch
and against the list) and does not collide with an identifier in src/, low-confidence names end with the address, the group is a relative .cs path.
Rows that fail are reported and skipped; the rest are merged. Run tools/rename/apply_names.py afterwards.

Usage: python tools/rename/merge_rows.py rows.tsv [rows2.tsv ...] [--dry-run]
"""
import os, re, sys
sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import apply_names as an


def main():
    args = [a for a in sys.argv[1:] if not a.startswith('--')]
    dry = '--dry-run' in sys.argv
    lines = an.read(an.NAMES).split('\n')
    idx = {}
    taken = set()
    for i, l in enumerate(lines):
        f = l.split('\t')
        if l.startswith('#') or len(f) < 2: continue
        idx[f[0]] = i
        if len(f) > 2 and f[2]: taken.add(f[2])
    src_idents = set()
    for p in an.cs_files(an.SRC): src_idents |= an.identifiers(an.read(p))
    applied = set(an.load_applied().values())
    ok = bad = 0
    for path in args:
        for ln, l in enumerate(an.read(path).splitlines(), 1):
            f = [x.strip() for x in l.split('\t')]
            if ln == 1 and f[0].lower() == 'orig' or not l.strip() or l.startswith('#'): continue
            if len(f) < 5: print(f'{path}:{ln}: too few columns'); bad += 1; continue
            o, n, g, c, e = f[:5]
            why = None
            fl = lines[idx[o]].split('\t') if o in idx else None
            if fl is None: why = 'unknown function'
            elif len(fl) > 2 and fl[2]: why = f'already named {fl[2]}'
            elif not re.fullmatch(r'[A-Za-z_][A-Za-z0-9_]*', n) or n in an.CS_KEYWORDS: why = f'bad identifier "{n}"'
            elif n in taken: why = f'name "{n}" already used'
            elif n in src_idents and n not in applied: why = f'"{n}" already exists in src/'
            elif c not in ('high', 'medium', 'low'): why = f'bad confidence "{c}"'
            elif c == 'low' and not n.upper().endswith(fl[1][2:].upper()): why = f'low name without the address {fl[1]}'
            elif not g.endswith('.cs') or g.startswith(('/', '\\')) or '..' in g: why = f'bad group "{g}"'
            if why: print(f'{os.path.basename(path)}:{ln}: {o}: {why}'); bad += 1; continue
            fl += [''] * (6 - len(fl))
            fl[2], fl[3], fl[4], fl[5] = n, g.replace('\\', '/'), c, e.replace('\t', ' ')
            lines[idx[o]] = '\t'.join(fl[:6]); taken.add(n); ok += 1
    if not dry: an.write_if_changed(an.NAMES, '\n'.join(lines))
    print(f'{"would merge" if dry else "merged"} {ok} rows, skipped {bad}')


if __name__ == '__main__':
    main()
