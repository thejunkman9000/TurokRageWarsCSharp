"""Layouts of the generated struct views (src/Trw.Game/Generated/Structs/*.cs) and how often the code uses each type (docs/NAMING.md, struct fields).

Writes tools/names/struct_types.tsv: one row per field of every struct view type the code uses:
  type  size  uses  offset  field  ftype
(uses = the number of `new Type(` in the generated code; ftype = the property type, a struct view type for nested structs / arrays).

Usage: python tools/rename/structs.py
"""
import os, re, sys
sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import apply_names as an

STRUCTS = os.path.join(an.GEN, 'Structs')
TYPE = re.compile(r'^public readonly struct (\w+)\s*$', re.M)
SIZE = re.compile(r'public const int SizeOf = (\d+);')
FIELD = re.compile(r'^    // \+0x([0-9A-Fa-f]+)\s*\n    public (\S+) (\w+)\b', re.M)


def layouts():
    """type -> dict(size, fields=[(offset, name, ftype)])"""
    out = {}
    for f in sorted(os.listdir(STRUCTS)):
        if not f.endswith('.cs'): continue
        s = an.read(os.path.join(STRUCTS, f))
        starts = [(m.start(), m.group(1)) for m in TYPE.finditer(s)] + [(len(s), None)]
        for (a, name), (b, _) in zip(starts, starts[1:]):
            body = s[a:b]
            m = SIZE.search(body)
            out[name] = dict(size=int(m.group(1)) if m else 0, file=f,
                             fields=[(int(x.group(1), 16), x.group(3), x.group(2)) for x in FIELD.finditer(body)])
    return out


def uses():
    n = {}
    pat = re.compile(r'\bnew ([A-Z_][A-Za-z0-9_]*)\(')
    for p in an.cs_files(an.GEN):
        if os.sep + 'Structs' + os.sep in p: continue
        for m in pat.finditer(an.read(p)): n[m.group(1)] = n.get(m.group(1), 0) + 1
    return n


def main():
    lay, use = layouts(), uses()
    out = ['# type\tsize\tuses\toffset\tfield\tftype   (written by tools/rename/structs.py)']
    for t in sorted(lay, key=lambda t: (-use.get(t, 0), t)):
        if not use.get(t): continue
        for off, name, ft in lay[t]['fields']:
            out.append(f'{t}\t0x{lay[t]["size"]:X}\t{use[t]}\t0x{off:X}\t{name}\t{ft}')
    an.write_if_changed(os.path.join(an.REPO, 'tools', 'names', 'struct_types.tsv'), '\n'.join(out) + '\n')
    used = [t for t in lay if use.get(t)]
    print(f'{len(lay)} struct view types, {len(used)} used by the code, {len(out) - 1} fields of used types')


if __name__ == '__main__':
    main()
