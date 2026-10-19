"""Merges proposed struct field names into tools/names/objects.tsv + fields.tsv (docs/NAMING.md, struct fields).

Input files: tab-separated `object, offset, name, confidence, note` rows, optionally with
  #OBJECT <TAB> Name              the object the rows belong to (overrides the object column)
  #ALIASES <TAB> T1,T2,...        its alias struct view types (added to objects.tsv; types already listed are kept)
  #BASE <TAB> Object              its base object (optional)
Rows for offsets already named are skipped; a name already used in the object (or its base) gets a suffix and is reported.

Usage: python tools/rename/merge_fields.py rows.tsv [rows2.tsv ...]
"""
import os, sys
sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import apply_names as an
import fields as fl


def main():
    objs_lines = an.read(fl.OBJECTS).rstrip('\n').split('\n') if os.path.exists(fl.OBJECTS) else ['# object\talias struct view types (comma separated)\tbase object\tnote']
    field_lines = an.read(fl.FIELDS).rstrip('\n').split('\n') if os.path.exists(fl.FIELDS) else ['# object\toffset\tname\tconfidence\tnote']
    objs = {}
    for i, l in enumerate(objs_lines):
        if l and not l.startswith('#'): objs[l.split('\t')[0]] = i
    named = {}
    for l in field_lines[1:]:
        if not l or l.startswith('#'): continue
        f = l.split('\t'); named.setdefault(f[0], {})[int(f[1], 16)] = f[2]
    for path in sys.argv[1:]:
        obj, aliases, base, added, skipped = None, [], '', 0, 0
        rows = an.read(path).splitlines()
        for l in rows:
            f = [x.strip() for x in l.split('\t')]
            if f[0] == '#OBJECT' and len(f) > 1: obj = f[1]
            elif f[0] == '#ALIASES' and len(f) > 1: aliases = [t.strip() for t in f[1].split(',') if t.strip()]
            elif f[0] == '#BASE' and len(f) > 1: base = f[1]
        for l in rows:
            f = [x.strip() for x in l.split('\t')]
            if not l.strip() or l.startswith('#') or f[0].lower() == 'object' or len(f) < 3: continue
            o = obj or f[0]
            off = int(f[1], 16)
            if off in named.get(o, {}): skipped += 1; continue
            used = set(named.get(o, {}).values()) | set(named.get(base, {}).values())
            n = f[2]
            if n in used:
                k = 2
                while f'{n}{k}' in used: k += 1
                print(f'{os.path.basename(path)}: {o}.{n} already used, +0x{off:X} becomes {n}{k}'); n = f'{n}{k}'
            named.setdefault(o, {})[off] = n
            field_lines.append('\t'.join([o, f'0x{off:X}', n, f[3] if len(f) > 3 else '', f[4] if len(f) > 4 else '']))
            added += 1
        if obj and aliases:
            if obj in objs:
                cur = objs_lines[objs[obj]].split('\t') + ['', '', '']
                have = [t for t in cur[1].split(',') if t]
                cur[1] = ','.join(have + [t for t in aliases if t not in have])
                if base and not cur[2]: cur[2] = base
                objs_lines[objs[obj]] = '\t'.join(cur[:4])
            else:
                objs[obj] = len(objs_lines); objs_lines.append('\t'.join([obj, ','.join(aliases), base, f'from {os.path.basename(path)}']))
        print(f'{os.path.basename(path)}: object {obj or "(from rows)"}, {len(aliases)} aliases, {added} fields added, {skipped} skipped')
    an.write_if_changed(fl.OBJECTS, '\n'.join(objs_lines) + '\n')
    an.write_if_changed(fl.FIELDS, '\n'.join(field_lines) + '\n')


if __name__ == '__main__':
    main()
