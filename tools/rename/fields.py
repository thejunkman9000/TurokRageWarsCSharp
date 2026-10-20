"""Struct field names (docs/NAMING.md, "Struct fields"): canonical names per game object, applied to every struct view type that describes it.

The decompilation describes one game object (the player, an actor ...) with many per-file C structs; the struct views keep those C names
(Plr, Plr24d, B04Act ... with fields unk5E4, f5E4 ...). tools/names/objects.tsv lists which view types alias an object, tools/names/fields.tsv
names the object's fields by offset. This tool renames, in every alias type, the member at each named offset (and its _Set method) to the
canonical name, and renames every access to it in the C# code. Accesses are resolved by type: the code reaches struct members only through
`new Type(expr).member`, chained through members of struct view type, `.At(i)` and array view indexers, so the chain is followed through the
struct definitions. Idempotent; runs after the function / globals renames (apply_names.py calls it).

  objects.tsv:  object <TAB> alias types (comma separated) <TAB> base object (its field names apply too, e.g. Player <- Actor) <TAB> note
  fields.tsv:   object <TAB> offset (0x..) <TAB> name <TAB> confidence <TAB> note

Usage: python tools/rename/fields.py [--check]
"""
import os, re, sys
sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import apply_names as an

STRUCTS = os.path.join(an.GEN, 'Structs')
OBJECTS = os.path.join(an.REPO, 'tools', 'names', 'objects.tsv')
FIELDS = os.path.join(an.REPO, 'tools', 'names', 'fields.tsv')
TYPE_HDR = re.compile(r'^public readonly struct (\w+)\s*$', re.M)
MEMBER = re.compile(r'^    // \+0x([0-9A-Fa-f]+)\s*\n    public (\S+) (\w+)\b', re.M)
INDEXER = re.compile(r'^    public (\w+) this\[int i\]', re.M)
IDENT = re.compile(r'[A-Za-z_][A-Za-z0-9_]*')


def parse_structs():
    """type -> dict(file, members={name: (offset, type)}, elem=indexer type or None)"""
    types = {}
    for f in sorted(os.listdir(STRUCTS)):
        if not f.endswith('.cs'): continue
        s = an.read(os.path.join(STRUCTS, f))
        starts = [(m.start(), m.group(1)) for m in TYPE_HDR.finditer(s)] + [(len(s), None)]
        for (a, name), (b, _) in zip(starts, starts[1:]):
            body = s[a:b]
            ix = INDEXER.search(body)
            types[name] = dict(file=f, members={m.group(3): (int(m.group(1), 16), m.group(2)) for m in MEMBER.finditer(body)},
                               elem=ix.group(1) if ix else None)
    return types


def load_tables():
    objects, fields, bases = {}, {}, {}
    if os.path.exists(OBJECTS):
        for ln, l in enumerate(an.read(OBJECTS).splitlines(), 1):
            if not l.strip() or l.startswith('#'): continue
            f = [x.strip() for x in l.split('\t')] + ['', '']
            objects[f[0]] = [t.strip() for t in f[1].split(',') if t.strip()]
            bases[f[0]] = f[2] if len(f) > 2 and f[2] and not f[2].startswith(' ') else ''
    if os.path.exists(FIELDS):
        for ln, l in enumerate(an.read(FIELDS).splitlines(), 1):
            if not l.strip() or l.startswith('#'): continue
            f = [x.strip() for x in l.split('\t')] + [''] * 4
            if not f[2]: continue
            fields.setdefault(f[0], {})[int(f[1], 16)] = dict(name=f[2], conf=f[3], note=f[4], line=ln)
    for o in objects:                                                        # inherited names (a base object's fields, nearest first)
        b, seen = bases.get(o, ''), {o}
        while b and b in fields and b not in seen:
            for off, r in fields[b].items(): fields.setdefault(o, {}).setdefault(off, r)
            seen.add(b); b = bases.get(b, '')
    return objects, fields


def plan(types, objects, fields):
    """(type, current member name) -> new name, with validation"""
    ren, errs = {}, []
    owner = {}
    for obj, aliases in objects.items():
        for t in aliases:
            if t in owner: errs.append(f'objects.tsv: {t} is listed under both {owner[t]} and {obj}')
            owner[t] = obj
    for obj in objects:                                                      # a view type renamed after a subject class (Actor -> ActorView)
        objects[obj] = [t if t in types or t + an.VIEW_SUFFIX not in types else t + an.VIEW_SUFFIX for t in objects[obj]]
    for obj, aliases in objects.items():
        named = fields.get(obj, {})
        seen_names = {}
        for off, r in named.items():
            n = r['name']
            if not re.fullmatch(r'[A-Za-z_][A-Za-z0-9_]*', n) or n in an.CS_KEYWORDS: errs.append(f'fields.tsv:{r["line"]}: bad name "{n}"')
            if n in seen_names: errs.append(f'fields.tsv:{r["line"]}: {obj}.{n} also names +0x{seen_names[n]:X}')
            seen_names[n] = off
        for t in aliases:
            if t not in types: errs.append(f'objects.tsv: {obj}: unknown struct view type {t}'); continue
            mem = types[t]['members']
            by_off = {}
            for m, (off, mt) in mem.items():
                if m.startswith('pad') and re.fullmatch(r'U8x\d+', mt): continue      # padding spans several fields: never named
                by_off.setdefault(off, []).append(m)
            existing = set(mem)
            for off, r in named.items():
                ms = by_off.get(off, [])
                for k, m in enumerate(sorted(ms)):
                    want = r['name'] if k == 0 else f'{r["name"]}_{k + 1}'      # a union: more members at one offset
                    if m == want: continue
                    if want in existing and want not in ms: errs.append(f'{t}: cannot rename {m} -> {want}: {want} exists at another offset'); continue
                    ren[(t, m)] = want
    return ren, errs


def rename_struct_files(types, ren):
    by_file = {}
    for (t, m), n in ren.items(): by_file.setdefault(types[t]['file'], {}).setdefault(t, {})[m] = n
    changed = 0
    for f, per_type in by_file.items():
        p = os.path.join(STRUCTS, f)
        s = an.read(p)
        starts = [(m.start(), m.group(1)) for m in TYPE_HDR.finditer(s)] + [(len(s), None)]
        out, last = [], 0
        for (a, name), (b, _) in zip(starts, starts[1:]):
            if name not in per_type: continue
            body = s[a:b]
            mp = per_type[name]
            plain = {m: n for m, n in mp.items() if m != 'value'}
            if plain:
                pat = re.compile(r'\b(' + '|'.join(re.escape(m) for m in sorted(plain, key=len, reverse=True)) + r')(_Set)?\b')
                body = pat.sub(lambda x: plain[x.group(1)] + (x.group(2) or ''), body)
            if 'value' in mp:      # a member named like the setter keyword: only its declaration and its _Set method, never `value` inside the setters
                body = re.sub(r'(?<=\n    public )(\S+) value\b', lambda x: f'{x.group(1)} {mp["value"]}', body)
                body = re.sub(r'\bvalue_Set\b', mp['value'] + '_Set', body)
            out.append(s[last:a]); out.append(body); last = b
        out.append(s[last:])
        changed += an.write_if_changed(p, ''.join(out))
    return changed


def rewrite_code(text, types, ren):
    """renames member accesses whose resolved (type, member) is in ren"""
    if not ren: return text
    segs = an.segments(text)
    out = []
    for code, seg in segs:
        out.append(rewrite_segment(seg, types, ren) if code else seg)
    return ''.join(out)


NEW = re.compile(r'\bnew ([A-Za-z_][A-Za-z0-9_]*)\(')      # view types can start lower case (func_8044CBE0_S1_v2)


def match_close(s, i, open_ch, close_ch):
    depth = 0
    for j in range(i, len(s)):
        c = s[j]
        if c == open_ch: depth += 1
        elif c == close_ch:
            depth -= 1
            if depth == 0: return j
    return -1


def rewrite_segment(s, types, ren):
    edits = []
    for m in NEW.finditer(s):
        t = m.group(1)
        if t not in types: continue
        j = match_close(s, m.end() - 1, '(', ')')
        if j < 0: continue
        cur, k = t, j + 1
        while cur in types:
            while k < len(s) and s[k] == ' ': k += 1
            if k < len(s) and s[k] == '[':
                e = match_close(s, k, '[', ']')
                if e < 0: break
                cur = types[cur]['elem']; k = e + 1; continue
            if k >= len(s) or s[k] != '.': break
            idm = IDENT.match(s, k + 1)
            if not idm: break
            name = idm.group(0)
            base = name[:-4] if name.endswith('_Set') else name
            if (cur, base) in ren: edits.append((idm.start(), idm.start() + len(base), ren[(cur, base)]))
            nxt = idm.end()
            if name == 'At':
                e = match_close(s, nxt, '(', ')') if nxt < len(s) and s[nxt] == '(' else -1
                if e < 0: break
                k = e + 1; continue
            if base in types[cur]['members'] and not name.endswith('_Set'):
                cur = types[cur]['members'][base][1]; k = nxt; continue
            break
    for a, b, n in sorted(edits, reverse=True):
        s = s[:a] + n + s[b:]
    return s


def apply(check=False):
    types = parse_structs()
    objects, fields = load_tables()
    if not objects: return 0, 0
    ren, errs = plan(types, objects, fields)
    if errs:
        print('\n'.join(errs[:40])); sys.exit(f'{len(errs)} problems in the struct field names')
    if check: print(f'field names ok: {len(ren)} member renames pending'); return 0, 0
    sf = rename_struct_files(types, ren)
    cf = 0
    for p in an.cs_files(os.path.join(an.SRC, 'Trw.Game')):
        if os.sep + 'Structs' + os.sep in p: continue
        s = an.read(p)
        t = rewrite_code(s, types, ren)
        if t != s: cf += an.write_if_changed(p, t)
    return len(ren), sf + cf


if __name__ == '__main__':
    n, f = apply('--check' in sys.argv)
    print(f'struct fields: {n} member renames, {f} files touched')
