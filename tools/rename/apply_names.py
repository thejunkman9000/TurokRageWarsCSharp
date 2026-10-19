"""Applies the human-readable names and the file grouping of tools/names/functions.tsv to the C# sources (docs/NAMING.md).

The generators (Trw.Transpile, tools/mips2cs) always emit the ORIGINAL names (func_XXXXXXXX, the C port's names) into one file per function; this
tool runs after them (the end of tools/regen.ps1) and on demand after editing the name list. It is idempotent.

  * Identifiers are renamed in every .cs under src/ (generated and hand-written code, comments included). String literals are left alone: the debug
    paths (Dbg.Enter, Funcs.Add, TRW_TRACE_FN, the differential tester's sigs.txt) identify functions by their original names, which stay stable.
  * Functions with a group are moved into src/Trw.Game/Generated/<group> (one partial-class file per group, members in ROM order); the others stay in
    one file each, named after their current name. Every member carries a marker line "// ==== <original> @ <address> [C|MIPS]" so the tool can split
    its own output again (regroup, rename again).
  * tools/names/applied.tsv records the names in effect (original -> current), so hand-written code that already uses an older name follows a later rename.

Usage: python tools/rename/apply_names.py [--check] [--template]
  --check     validate the name list only (names, collisions, groups); no files written
  --template  (re)write tools/names/functions.tsv with a row for every registered function, keeping existing rows
"""
import argparse, keyword, os, re, sys

REPO = os.path.dirname(os.path.dirname(os.path.dirname(os.path.abspath(__file__))))
SRC = os.path.join(REPO, 'src')
GEN = os.path.join(SRC, 'Trw.Game', 'Generated')
NAMES = os.path.join(REPO, 'tools', 'names', 'functions.tsv')
APPLIED = os.path.join(REPO, 'tools', 'names', 'applied.tsv')
SPECIAL = {'_Registry.cs', '_Stubs.cs', '_RegisterRecompiled.cs', '_NameMap.cs', '_Globals.cs', '_ClassUsings.cs'}       # generated tables: renamed in place, never split / grouped
MARK = re.compile(r'^    // ==== (\S+) @ (0x[0-9A-F]{8}|-) \[(C|MIPS)\]\s*$', re.M)
CS_KEYWORDS = set('abstract as base bool break byte case catch char checked class const continue decimal default delegate do double else enum event '
                  'explicit extern false finally fixed float for foreach goto if implicit in int interface internal is lock long namespace new null '
                  'object operator out override params private protected public readonly ref return sbyte sealed short sizeof stackalloc static string '
                  'struct switch this throw true try typeof uint ulong unchecked unsafe ushort using virtual void volatile while'.split())
IDENT = re.compile(r'[A-Za-z_][A-Za-z0-9_]*')


def read(p):
    with open(p, encoding='utf-8-sig') as f: return f.read()


def write_if_changed(p, s):
    """The tool works on \n text; files are written the way this checkout stores them (CRLF, git core.autocrlf) and keep a BOM they had. A file whose
    text is unchanged is not touched (its line endings included)."""
    bom = False
    if os.path.exists(p):
        with open(p, 'rb') as f: raw = f.read()
        if raw.decode('utf-8-sig').replace('\r\n', '\n') == s and b'\r\n' in raw: return False
        bom = raw.startswith(b'\xef\xbb\xbf')
    os.makedirs(os.path.dirname(p), exist_ok=True)
    with open(p, 'w', encoding='utf-8-sig' if bom else 'utf-8', newline='\r\n') as f: f.write(s)
    return True


def cs_files(root):
    for d, dirs, files in os.walk(root):
        dirs[:] = [x for x in dirs if x not in ('bin', 'obj')]
        for f in files:
            if f.endswith('.cs'): yield os.path.join(d, f)


# ---------------------------------------------------------------------------------------------- C# scanner: code / comments vs string literals

def segments(s):
    """Splits C# source into (renamable, text) pieces: code and comments are renamable, string and char literal contents are not. Interpolation holes
    of $"..." strings are code again (recursively)."""
    out = []; i = 0; n = len(s); start = 0

    def flush(j):
        if j > start: out.append((True, s[start:j]))

    while i < n:
        c = s[i]
        if c == '/' and i + 1 < n and s[i + 1] == '/':
            j = s.find('\n', i); j = n if j < 0 else j
            if s.startswith('// ==== ', i):                                 # this tool's member markers keep the original name
                flush(i); out.append((False, s[i:j])); start = j
            i = j
            continue
        if c == '/' and i + 1 < n and s[i + 1] == '*':
            j = s.find('*/', i + 2); i = n if j < 0 else j + 2
            continue
        if c == "'":
            j = i + 1
            while j < n and s[j] != "'":
                j += 2 if s[j] == '\\' else 1
            flush(i); out.append((False, s[i:j + 1])); i = j + 1; start = i
            continue
        if c in '$@"':
            m = re.match(r'(\$@|@\$|\$|@)?"("")?', s[i:])
            if not m or (c != '"' and not m.group(0).endswith('"')):
                i += 1; continue
            flush(i)
            prefix = m.group(1) or ''
            interp, verbatim = '$' in prefix, '@' in prefix
            if s.startswith('"""', i + len(prefix)):                       # raw string literal: not used by the generators; kept whole
                j = s.find('"""', i + len(prefix) + 3); j = n if j < 0 else j + 3
                out.append((False, s[i:j])); i = j; start = i; continue
            j = i + len(prefix) + 1; lit_start = i
            while j < n:
                ch = s[j]
                if not verbatim and ch == '\\': j += 2; continue
                if verbatim and ch == '"' and j + 1 < n and s[j + 1] == '"': j += 2; continue
                if ch == '"': j += 1; break
                if interp and ch == '{':
                    if j + 1 < n and s[j + 1] == '{': j += 2; continue
                    out.append((False, s[lit_start:j + 1]))
                    depth = 1; k = j + 1
                    while k < n and depth:                                  # the hole: find its end, skipping nested strings
                        if s[k] == '"':
                            q = k + 1
                            while q < n and s[q] != '"': q += 2 if s[q] == '\\' else 1
                            k = q + 1; continue
                        if s[k] == '{': depth += 1
                        elif s[k] == '}': depth -= 1
                        k += 1
                    hole = s[j + 1:k - 1]
                    out.extend(segments(hole))
                    lit_start = k - 1; j = k; continue
                j += 1
            out.append((False, s[lit_start:j])); i = j; start = i
            continue
        i += 1
    flush(n)
    return out


def rename_text(s, mapping):
    if not mapping: return s
    sub = lambda m: mapping.get(m.group(0), m.group(0))
    return ''.join(IDENT.sub(sub, t) if code else t for code, t in segments(s))


def identifiers(s):
    return {m.group(0) for code, t in segments(s) if code for m in IDENT.finditer(t)}


# ---------------------------------------------------------------------------------------------- inputs

def registry():
    """original name -> address, from the generated registration tables (and the hand-kept Preserved/)."""
    addr = {}
    for p in [os.path.join(GEN, '_Registry.cs'), os.path.join(GEN, 'Recompiled', '_RegisterRecompiled.cs'),
              os.path.join(SRC, 'Trw.Game', 'Preserved', '_Preserved.cs')]:
        if os.path.exists(p):
            for m in re.finditer(r'Funcs\.Add\(0x([0-9A-Fa-f]{8})[uU], "([^"]+)"', read(p)): addr[m.group(2)] = int(m.group(1), 16)
    return addr


def load_names():
    rows = {}
    if not os.path.exists(NAMES): return rows
    for ln, line in enumerate(read(NAMES).splitlines(), 1):
        if not line.strip() or line.startswith('#'): continue
        f = line.split('\t')
        f += [''] * (6 - len(f))
        orig, address, name, group, conf, note = (x.strip() for x in f[:6])
        if orig in rows: sys.exit(f'{NAMES}:{ln}: {orig} listed twice')
        rows[orig] = dict(address=address, name=name, group=group, conf=conf, note=note, line=ln)
    return rows


def load_applied():
    m = {}
    if os.path.exists(APPLIED):
        for line in read(APPLIED).splitlines():
            if line and not line.startswith('#'):
                o, c = line.split('\t')[:2]; m[o] = c
    return m


def validate(rows, all_idents, applied, addr):
    errs = []
    seen = {}
    current_names = set(applied.values())
    for orig, r in rows.items():
        nm, g = r['name'], r['group']
        if nm:
            if not re.fullmatch(r'[A-Za-z_][A-Za-z0-9_]*', nm) or nm in CS_KEYWORDS or keyword.iskeyword(nm):
                errs.append(f'line {r["line"]}: {orig}: "{nm}" is not a valid identifier')
            if nm in seen: errs.append(f'line {r["line"]}: {orig}: name "{nm}" also used by {seen[nm]}')
            seen[nm] = orig
            if nm != orig and nm in addr: errs.append(f'line {r["line"]}: {orig}: "{nm}" is the original name of another function')
            if nm in all_idents and nm != applied.get(orig) and nm not in current_names and not ((applied.get(orig) or "").endswith("." + nm.split("_", 1)[-1]) or (applied.get(orig) or "").endswith("." + nm)):
                errs.append(f'line {r["line"]}: {orig}: "{nm}" already exists in the sources (a hand-written member, a local, a type...)')
        if g and (not g.endswith('.cs') or g.startswith(('/', '\\')) or '..' in g or os.path.basename(g) in SPECIAL):
            errs.append(f'line {r["line"]}: {orig}: bad group "{g}" (a relative path ending in .cs)')
    return errs


# ---------------------------------------------------------------------------------------------- members

HEADER_C = '// <auto-generated> by Trw.Transpile from the decompiled C. Edit freely: this file is the source now. </auto-generated>'
PRAGMA = '#pragma warning disable CS0162, CS0164, CS0168, CS0219'


CLASS_HDR = re.compile(r'^public static partial class (\w+)\s*\n\{\s*\n', re.M)


def class_blocks(s, path):
    """[(class name, body)] of the top-level `public static partial class X { ... }` blocks of a generated file (each block ends at the next
    column-0 '}')."""
    out = []
    for m in CLASS_HDR.finditer(s):
        e = s.find('\n}', m.end() - 1)
        if e < 0: sys.exit(f'{path}: class {m.group(1)} is not closed')
        out.append((m.group(1), s[m.end():e + 1].strip('\n') + '\n'))
    if not out: sys.exit(f'{path}: no "public static partial class" block')
    return out


def class_body(s, path):
    blocks = class_blocks(s, path)
    if len(blocks) != 1: sys.exit(f'{path}: expected one class block')
    return blocks[0][1]


# ---------------------------------------------------------------------------------------------- static classes

# Subject_Verb functions become members of a static class per subject: Weapon_GetAmmoType -> Weapon.GetAmmoType (docs/NAMING.md). The subject
# Game stays in class Game with its full names (hand-written Game members could collide); subjects that are Trw.Platform classes get another name.
CLASS_OVERRIDE = {'Mem': 'MemUtil', 'Cpu': 'CpuReg', 'Str': 'StrUtil', 'Color': 'ColorUtil', 'Effect': 'Effects', 'Exception': 'ExceptionHandler',
                  'Debug': 'DebugUtil', 'File': 'FileIo', 'Flags': 'FlagsUtil', 'Int': 'IntUtil', 'List': 'ListUtil', 'Math': 'MathUtil',
                  'Object': 'ObjectUtil', 'Path': 'PathUtil', 'Platform': 'PlatformObj', 'Random': 'RandomGen', 'Text': 'TextUtil',
                  'Lock': 'LockUtil', 'Mutex': 'MutexUtil'}      # Trw.Platform / MonoGame / System types, the Trw.Game.Debug namespace
NO_CLASS = {'Game'}
VIEW_SUFFIX = 'View'           # a struct view type with the same name as a subject class is renamed (Actor -> ActorView)
SUBJ = re.compile(r'([A-Z][A-Za-z0-9]*)_([A-Za-z_][A-Za-z0-9_]*)')
DEF = re.compile(r'\bstatic\s+[A-Za-z_][\w<>,\[\] ]*?\s([A-Za-z_]\w*)\s*\(')


def class_of(name):
    """(class, member name) a readable function name goes to; (Game, name) when it stays in class Game"""
    m = SUBJ.fullmatch(name)
    if not m: return 'Game', name
    s, v = m.groups()
    if s in NO_CLASS or v[0].isdigit() or v in CS_KEYWORDS: return 'Game', name
    c = CLASS_OVERRIDE.get(s, s)
    if v == c: return 'Game', name
    return c, v


def collect_members(addr, reverse):
    """Every function member under Generated/ (except the tables), from fresh generator output (one file per function, no markers) or from this tool's
    own output (marker lines). -> list of dict(orig, kind, text, path)"""
    members = []
    for p in cs_files(GEN):
        rel = os.path.relpath(p, GEN)
        if os.path.basename(p) in SPECIAL or rel.startswith('Structs' + os.sep): continue
        s = read(p)
        blocks = class_blocks(s, p)
        if len(blocks) == 1 and not MARK.search(blocks[0][1]):    # one function per file (generator output, or outside any group): text kept as it is
            cls, body = blocks[0]
            kind = 'MIPS' if rel.startswith('Recompiled' + os.sep) else 'C'
            stem = os.path.splitext(os.path.basename(p))[0]
            members.append(dict(orig=reverse.get(stem, stem), kind=kind, text=body.strip('\n') + '\n', path=p, whole=s, cls=cls))
            continue
        for cls, body in blocks:
            marks = list(MARK.finditer(body))
            if not marks or body[:marks[0].start()].strip(): sys.exit(f'{p}: class {cls}: text before the first member marker')
            for k, m in enumerate(marks):
                end = marks[k + 1].start() if k + 1 < len(marks) else len(body)
                members.append(dict(orig=reverse.get(m.group(1), m.group(1)), kind=m.group(3), text=body[m.end():end].strip('\n') + '\n', path=p, cls=cls))
    seen = {}                                                               # a C source file can sit next to a MIPS twin of the same name (its helpers stay C)
    for mb in members:
        key = (mb['orig'], mb['kind'])
        if key in seen: sys.exit(f'{mb["orig"]} [{mb["kind"]}] defined twice: {seen[key]} and {mb["path"]}')
        seen[key] = mb['path']
    return members


def render(members, addr, current):          # a group file (or a function leaving its group): marker line before every member
    lines = [HEADER_C.replace('</auto-generated>', '(grouped by tools/rename/apply_names.py) </auto-generated>') if len(members) > 1 or members[0]['kind'] == 'C'
             else '// <auto-generated> by tools/mips2cs from the ROM\'s MIPS code (no C source exists for this function). Edit freely. </auto-generated>']
    if any(m['kind'] == 'MIPS' for m in members): lines.append(PRAGMA)
    lines += ['using Trw.Platform;']
    if any(m.get('newcls', 'Game') != 'Game' for m in members): lines.append('using static Trw.Game.Game;      // the OS layer, helpers and the functions that stay in class Game')
    lines += ['', 'namespace Trw.Game;']
    cur = None
    for m in members:
        cls = m.get('newcls', 'Game')
        if cls != cur:
            if cur is not None: lines.append('}')
            lines += ['', f'public static partial class {cls}', '{']; cur = cls
        else: lines.append('')
        a = addr.get(m['orig'])
        lines.append(f'    // ==== {m["orig"]} @ {"0x%08X" % a if a is not None else "-"} [{m["kind"]}]')
        lines.append(m['text'].rstrip('\n'))
    lines.append('}')
    return '\n'.join(lines) + '\n'


# ---------------------------------------------------------------------------------------------- main

def main():
    ap = argparse.ArgumentParser()
    ap.add_argument('--check', action='store_true')
    ap.add_argument('--template', action='store_true')
    a = ap.parse_args()
    addr = registry()
    rows = load_names()

    if a.template:
        os.makedirs(os.path.dirname(NAMES), exist_ok=True)
        for o in addr:
            if o not in rows: rows[o] = dict(address='0x%08X' % addr[o], name='', group='', conf='', note='', line=0)
        out = ['# orig\taddress\tname\tgroup\tconfidence\tnote   (tab-separated; docs/NAMING.md. Empty name = keep the original; empty group = own file)']
        for o in sorted(rows, key=lambda o: (addr.get(o, 1 << 40), o)):
            r = rows[o]
            out.append('\t'.join([o, r['address'] or ('0x%08X' % addr[o] if o in addr else '-'), r['name'], r['group'], r['conf'], r['note']]))
        write_if_changed(NAMES, '\n'.join(out) + '\n')
        print(f'{NAMES}: {len(rows)} rows'); return

    applied = load_applied()
    texts = {p: read(p) for p in cs_files(SRC)}
    all_idents = set()
    for s in texts.values(): all_idents |= identifiers(s)
    errs = validate(rows, all_idents, applied, addr)
    unknown = [o for o in rows if o not in addr and rows[o]['name']]
    for o in unknown: errs.append(f'line {rows[o]["line"]}: {o} is not a registered function')
    if errs:
        print('\n'.join(errs[:50])); sys.exit(f'{len(errs)} problems in {NAMES}')
    if a.check: print('name list ok:', sum(1 for r in rows.values() if r['name']), 'names,', sum(1 for r in rows.values() if r['group']), 'grouped'); return

    desired = {o: (rows[o]['name'] if o in rows and rows[o]['name'] else o) for o in addr}
    # 0. which static class each function goes to. A generated member file (blob) holds a primary function (its marker / file name) and sometimes
    # more registered functions of the same C file: those go with the blob. Functions defined outside the generated members (tables, Preserved/,
    # the OS layer) stay in class Game.
    reverse0 = {w: o for o, w in desired.items() if w != o}
    reverse0.update({c: o for o, c in applied.items() if c not in reverse0 and '.' not in c})
    pre = collect_members(addr, reverse0)
    primary = {m['orig'] for m in pre}
    secondary = {}
    for m in pre:
        for d in DEF.finditer(m['text']):
            o = reverse0.get(d.group(1), d.group(1))
            if o in addr and o != m['orig'] and o not in primary: secondary[o] = m['orig']
    cls_of, member = {}, {}
    for o in addr:
        c, v = class_of(desired[o]) if o in primary else ('Game', desired[o])
        cls_of[o], member[o] = c, v
    for o, owner in secondary.items(): cls_of[o], member[o] = cls_of.get(owner, 'Game'), desired[o]
    # every class is imported everywhere (_ClassUsings.cs), so a member must not be named like a class (Printf.X would become ambiguous), and a
    # class must not be named like a function that stays in class Game (LevelIdToWorld)
    while True:
        classes = {c for c in cls_of.values() if c != 'Game'}
        game_names = {member[o] for o in addr if cls_of[o] == 'Game'}
        bad_cls = classes & game_names
        changed_any = False
        for o in addr:
            if cls_of[o] in bad_cls: cls_of[o], member[o] = 'Game', desired[o]; changed_any = True
            elif cls_of[o] != 'Game' and member[o] in classes and member[o] != desired[o]: member[o] = desired[o]; changed_any = True
        if not changed_any: break
    code = {o: (f'{cls_of[o]}.{member[o]}' if cls_of[o] != 'Game' else member[o]) for o in addr}
    classes = {c for c in cls_of.values() if c != 'Game'}
    mapping, dotted = {}, {}
    for o, want in code.items():
        if want != o: mapping[o] = want                                  # fresh generator output
        for old in (desired[o], applied.get(o)):                         # hand-written code / earlier runs: older names or class forms
            if old and old != want: (dotted if '.' in old else mapping)[old] = want
    game_qual = re.compile(r'\bGame\.(?=(' + '|'.join(sorted(classes, key=len, reverse=True)) + r')\.)') if classes else None
    def sub_ident(seg):                                                  # a name already qualified by a subject class (View.Hud_X) stays
        def f(m):
            w = m.group(0); i = m.start()
            if i > 0 and seg[i - 1] == '.':
                j = i - 1
                while j > 0 and (seg[j - 1].isalnum() or seg[j - 1] == '_'): j -= 1
                if seg[j:i - 1] in classes: return w
            return mapping.get(w, w)
        return IDENT.sub(f, seg)
    def rename_all(s):
        s = ''.join(sub_ident(t) if code_ else t for code_, t in segments(s)) if mapping else s
        if dotted or game_qual:
            out = []
            for is_code, seg in segments(s):
                if is_code:
                    if dotted: seg = DOTTED.sub(lambda m: dotted.get(m.group(0), m.group(0)), seg)
                    if game_qual: seg = game_qual.sub('', seg)          # Game.Weapon_X -> Game.Weapon.X -> Weapon.X
                out.append(seg)
            s = ''.join(out)
        return s
    # 1. rename everywhere under src/
    changed = 0
    for p, s in texts.items():
        if p == GFILE: continue                                              # written from globals.tsv by apply_globals
        t = rename_all(s)
        if t != s: texts[p] = t; changed += write_if_changed(p, t)
    # 1b. named globals: raw guest addresses -> Gv.<Name> constants (tools/names/globals.tsv)
    gchanged = apply_globals(texts)
    # 2. regroup the generated members
    reverse = {w: o for o, w in desired.items() if w != o}
    reverse.update({c: o for o, c in applied.items() if c not in reverse})
    reverse.update({c: o for o, c in code.items() if c != o and '.' not in c and c not in reverse})
    members = collect_members(addr, reverse)
    targets = {}
    for m in members:
        o = m['orig']; g = rows.get(o, {}).get('group', '')
        c = cls_of.get(o, 'Game'); m['newcls'] = c
        if c != 'Game':                                                  # declarations by member name inside the class: static T Weapon.X( -> static T X(
            m['text'] = re.sub(r'(\bstatic\s+[A-Za-z_][\w<>,\[\] ]*?\s)' + re.escape(c) + r'\.([A-Za-z_]\w*)\s*\(', r'\1\2(', m['text'])
        if c != m.get('cls', 'Game'): m.pop('whole', None)
        if g: target = os.path.join(GEN, g.replace('/', os.sep))
        else: target = os.path.join(GEN, 'Recompiled' if m['kind'] == 'MIPS' else '', desired.get(o, o) + '.cs')
        targets.setdefault(target, []).append(m)
    old_paths = {m['path'] for m in members}
    written = 0
    for target, ms in targets.items():
        ms.sort(key=lambda m: (m['newcls'] != 'Game', m['newcls'], addr.get(m['orig'], 1 << 40), m['orig'], m['kind']))   # one block per class
        grouped = bool(rows.get(ms[0]['orig'], {}).get('group'))
        text = ms[0]['whole'] if not grouped and len(ms) == 1 and 'whole' in ms[0] else render(ms, addr, desired)
        written += write_if_changed(target, text)
    removed = 0
    for p in old_paths - set(targets):
        os.remove(p); removed += 1
    for d, dirs, files in os.walk(GEN, topdown=False):                    # empty group folders
        if d != GEN and not os.listdir(d): os.rmdir(d)
    # 3. the original -> readable map for the debug paths (Dbg.ReadableName: TRW_TRACE_FN accepts both names)
    nm = ['// <auto-generated> by tools/rename/apply_names.py from tools/names/functions.tsv: original name -> readable name (Dbg.ReadableName). </auto-generated>',
          'namespace Trw.Game;', '', 'public static partial class Game', '{',
          '    public static readonly System.Collections.Generic.Dictionary<string, string> ReadableNames = new()', '    {']
    nm += [f'        ["{o}"] = "{w}",' for o, w in sorted(desired.items()) if w != o]
    nm += ['    };', '}']
    write_if_changed(os.path.join(GEN, '_NameMap.cs'), '\n'.join(nm) + '\n')
    # 3b. struct fields: canonical names per game object (tools/names/objects.tsv + fields.tsv, tools/rename/fields.py)
    import fields as struct_fields
    fren, ffiles = struct_fields.apply()
    if fren: print(f'struct fields: {fren} member renames, {ffiles} files touched')
    # 3b'. helpers (unregistered methods of a member, e.g. the recompiled _rc twins) are called unqualified from other classes: every subject
    # class is imported everywhere (registered functions are always called qualified, Class.Member)
    gu = ['// <auto-generated> by tools/rename/apply_names.py: the subject classes of the generated code, imported everywhere. </auto-generated>']
    gu += [f'global using static Trw.Game.{c};' for c in sorted(classes)]
    write_if_changed(os.path.join(GEN, '_ClassUsings.cs'), '\n'.join(gu) + '\n')
    # 3c. struct view types named like a subject class get a suffix (Actor -> ActorView): only type positions in the generated code
    vchanged = rename_view_types(classes)
    # 4. remember what is in effect
    out = ['# original name -> name in effect (written by tools/rename/apply_names.py; do not edit)']
    out += [f'{o}\t{w}' for o, w in sorted(code.items()) if w != o]
    write_if_changed(APPLIED, '\n'.join(out) + '\n')
    print(f'renamed {len(mapping) + len(dotted)} identifiers ({changed} files touched); globals: {gchanged} files touched; {len(targets)} member files ({written} written, {removed} removed); {len(classes)} classes, view types: {vchanged} files')


DOTTED = re.compile(r'\b[A-Za-z_]\w*\.[A-Za-z_]\w*\b')
IDENT_BEFORE = re.compile(r'([A-Za-z_]\w*)\Z')           # with endpos at a '.': the qualifier in front of it


def rename_view_types(classes):
    """Struct view types (Generated/Structs) with the name of a subject class are renamed to Name + VIEW_SUFFIX in the generated code, at type
    positions only: `new T(`, `struct T`, a constructor / operator `T(`, `(T)`, and `T name` declarations. `T.` is the class and stays."""
    structs = os.path.join(GEN, 'Structs')
    if not os.path.isdir(structs): return 0
    defined = set()
    for f in os.listdir(structs):
        if f.endswith('.cs'): defined |= set(re.findall(r'^public readonly struct (\w+)\s*$', read(os.path.join(structs, f)), re.M))
    ren = {t: t + VIEW_SUFFIX for t in defined & classes if t + VIEW_SUFFIX not in defined}
    if not ren: return 0
    alt = '|'.join(sorted(map(re.escape, ren), key=len, reverse=True))
    pats = [re.compile(r'(\bnew\s+)(' + alt + r')(?=\s*\()'), re.compile(r'(\bstruct\s+)(' + alt + r')\b'),
            re.compile(r'(\b(?:public|private|internal|operator)\s+)(' + alt + r')(?=\s*\()'), re.compile(r'(\()(' + alt + r')(?=\))'),
            re.compile(r'(^|[\s(,<])(' + alt + r')(?=\s+[A-Za-z_@]\w*\b(?!\s*\.))', re.M)]
    n = 0
    for p in cs_files(GEN):
        if os.path.basename(p) in SPECIAL and os.path.basename(p) != '_Registry.cs': continue      # tables written by this tool (_Globals.cs ...)
        s = read(p)
        out = []
        for is_code, seg in segments(s):
            if is_code:
                for pat in pats: seg = pat.sub(lambda m: m.group(1) + ren[m.group(2)], seg)
            out.append(seg)
        t = ''.join(out)
        if t != s: n += write_if_changed(p, t)
    return n


# ---------------------------------------------------------------------------------------------- globals

GLOBALS = os.path.join(REPO, 'tools', 'names', 'globals.tsv')
GAPPLIED = os.path.join(REPO, 'tools', 'names', 'globals_applied.tsv')
GFILE = os.path.join(GEN, '_Globals.cs')
LIT = re.compile(r'\b0x(80[0-9A-Fa-f]{6})([uU]?)\b')
GCLASS = 'Gv'                                                                # not G: the decompiled C has locals named G
GREF = re.compile(r'\b(?:G|Gv)\.([A-Za-z_][A-Za-z0-9_]*)\b')                 # G. = the first spelling (converted)


def load_globals():
    """address -> dict(name, conf, note) from tools/names/globals.tsv: address <TAB> name <TAB> confidence <TAB> note"""
    rows, seen, errs = {}, {}, []
    if os.path.exists(GLOBALS):
        for ln, line in enumerate(read(GLOBALS).splitlines(), 1):
            if not line.strip() or line.startswith('#'): continue
            f = [x.strip() for x in line.split('\t')] + [''] * 4
            a, n, c, note = f[:4]
            if not re.fullmatch(r'0x80[0-9A-Fa-f]{6}', a): errs.append(f'globals.tsv:{ln}: bad address "{a}"'); continue
            if not n: continue
            ai = int(a, 16)
            if not re.fullmatch(r'[A-Za-z_][A-Za-z0-9_]*', n) or n in CS_KEYWORDS: errs.append(f'globals.tsv:{ln}: bad name "{n}"')
            elif n in seen: errs.append(f'globals.tsv:{ln}: "{n}" also names {seen[n]:08X}')
            elif ai in rows: errs.append(f'globals.tsv:{ln}: address {a} listed twice')
            elif c == 'low' and not n.upper().endswith('%08X' % ai): errs.append(f'globals.tsv:{ln}: low name without the address')
            else: rows[ai] = dict(name=n, conf=c, note=note); seen[n] = ai
    if errs: print('\n'.join(errs[:50])); sys.exit(f'{len(errs)} problems in {GLOBALS}')
    return rows


def apply_globals(texts):
    """Replaces the exact address literals of named globals with G.<Name> in src/Trw.Game (code and comments, not strings), follows renames of
    globals (and turns a dropped name back into its literal), and writes Generated/_Globals.cs. A const uint folds to the same IL as the literal."""
    rows = load_globals()
    old = {}
    if os.path.exists(GAPPLIED):
        for line in read(GAPPLIED).splitlines():
            if line and not line.startswith('#'):
                a, n = line.split('\t')[:2]; old[n] = int(a, 16)
    want = {a: r['name'] for a, r in rows.items()}
    def ref(m):                                                              # G.OldName -> G.NewName / literal
        n = m.group(1)
        if n in old and (want.get(old[n]) != n or not m.group(0).startswith(GCLASS + '.')):
            a = old[n]
            return f'{GCLASS}.{want[a]}' if a in want else '0x%08Xu' % a
        return m.group(0)
    def lit(m):
        a = int(m.group(1), 16)
        return f'{GCLASS}.{want[a]}' if a in want else m.group(0)
    game = os.path.join(SRC, 'Trw.Game') + os.sep
    n = 0
    for p, s in texts.items():
        if not p.startswith(game) or p == GFILE: continue
        t = ''.join(LIT.sub(lit, GREF.sub(ref, seg)) if code else seg for code, seg in segments(s))
        if t != s: texts[p] = t; n += write_if_changed(p, t)
    out = ['// <auto-generated> by tools/rename/apply_names.py from tools/names/globals.tsv: named guest addresses (KSEG0). </auto-generated>',
           'namespace Trw.Game;', '', '/// <summary>Named guest RAM addresses of the game\'s globals (docs/NAMING.md). Use them where the code had the raw address.</summary>',
           f'public static class {GCLASS}', '{']
    for a in sorted(rows):
        r = rows[a]
        note = r['note'].replace('&', '&amp;').replace('<', '&lt;').replace('>', '&gt;')
        out.append(f'    /// <summary>{note} [{r["conf"] or "?"}]</summary>')
        out.append(f'    public const uint {r["name"]} = 0x{a:08X}u;')
    out.append('}')
    write_if_changed(GFILE, '\n'.join(out) + '\n')
    write_if_changed(GAPPLIED, '\n'.join(['# address -> global name in effect (written by tools/rename/apply_names.py; do not edit)'] +
                                         [f'0x{a:08X}\t{r["name"]}' for a, r in sorted(rows.items())]) + '\n')
    return n


if __name__ == '__main__':
    main()
