"""Float constants (docs/NAMING.md, "Float constants"): reads of the game's constant pools become literals.

The decompiled C loads most float constants from the ROM's literal pools (`Mem.F32(0x800C793Cu)`); this pass replaces such a read with the value,
`0.833f`, for the addresses listed in tools/names/rodata_floats.tsv. The literal is the exact value (shortest decimal that rounds back to the same
bits), so the code computes the same thing.

  rodata_floats.tsv:  address <TAB> width (32 / 64) <TAB> bits (hex) <TAB> literal

The list is made once from the ROM (`--scan ROM`): every address in the constant region 0x800C6AB0..0x800E2830 that the code only reads as a float
(every literal mention is a Mem.F32 / Mem.F64 read, no recompiled MIPS store resolves to it). The region also holds a few variables
(0x800D298C..0x800D2994, the frame-time scales State.PlayInit sets): those are written as literals and so are never listed. A scan keeps the rows
already listed (their reads are literals by then) and adds new ones. NaN, infinities, -0 and denormals stay reads.

Usage: python tools/rename/consts.py --scan baserom.us.v11.z64     (apply_names.py runs the replacement)
"""
import math, os, re, struct, sys
sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import apply_names as an

TABLE = os.path.join(an.REPO, 'tools', 'names', 'rodata_floats.tsv')
LO, HI = 0x800C6AB0, 0x800E2830
ROM_BASE = 0x7FFFF400                    # main segment: KSEG0 address - ROM_BASE = ROM offset
READ = re.compile(r'Mem\.F(32|64)\(0x(80[0-9A-Fa-f]{6})u\)')
LIT = re.compile(r'0x(80[0-9A-Fa-f]{6})u?\b')
ASSIGN = re.compile(r'\b(r\d+) = (\d+)u;')
ANYASSIGN = re.compile(r'\b(r\d+) = ')
STORE = re.compile(r'Mem\.Set\(\((r\d+) \+ (\d+)u\)')


def load():
    rows = {}
    if os.path.exists(TABLE):
        for l in an.read(TABLE).splitlines():
            if not l.strip() or l.startswith('#'): continue
            a, w, bits, lit = l.split('\t')[:4]
            rows[int(a, 16)] = (int(w), bits, lit)
    return rows


def literal(w, b):
    """C# literal with exactly these bits, or None"""
    if w == 32:
        v = struct.unpack('>f', b)[0]
        if math.isnan(v) or math.isinf(v) or (v == 0 and b[0] & 0x80) or (v != 0 and abs(v) < 1.1754944e-38): return None
        for n in range(1, 10):
            s = f'{v:.{n}g}'
            try:
                if struct.pack('>f', float(s)) == b: break
            except OverflowError: pass                                     # near FLT_MAX: a short form rounds past the float range
        if 'e+' in s and v == int(v) and abs(v) < 1e10: s = str(int(v))     # 1000f, not 1e+03f
        s += 'f'
    else:
        v = struct.unpack('>d', b)[0]
        if math.isnan(v) or math.isinf(v) or (v == 0 and b[0] & 0x80) or (v != 0 and abs(v) < 2.2250738585072014e-308): return None
        s = repr(v) + 'd'
    return f'({s})' if s.startswith('-') else s


def scan(rom_path):
    rom = open(rom_path, 'rb').read()
    rows = load()
    cand, bad, stores = {}, set(), set()
    for p in an.cs_files(os.path.join(an.SRC, 'Trw.Game')):
        s = an.read(p)
        for m in READ.finditer(s):
            a = int(m.group(2), 16)
            if LO <= a < HI: cand[a] = int(m.group(1))
        for m in LIT.finditer(s):
            a = int(m.group(1), 16)
            if LO <= a < HI and not READ.fullmatch(s, max(0, m.start() - 8), min(len(s), m.end() + 1)): bad.add(a)
        regs = {}
        for line in s.split('\n'):
            for m in STORE.finditer(line):
                if m.group(1) in regs: stores.add((regs[m.group(1)] + int(m.group(2))) & 0xFFFFFFFF)
            for m in ANYASSIGN.finditer(line): regs.pop(m.group(1), None)
            for m in ASSIGN.finditer(line): regs[m.group(1)] = int(m.group(2))
    added = skipped = 0
    for a, w in sorted(cand.items()):
        n = w // 8
        if a in rows: continue
        if a in bad or any(x in stores for x in range(a - 7, a + n)): skipped += 1; continue
        b = rom[a - ROM_BASE:a - ROM_BASE + n]
        lit = literal(w, b)
        if lit is None: skipped += 1; continue
        rows[a] = (w, b.hex().upper(), lit); added += 1
    out = ['# address\twidth\tbits\tliteral   (float constants of the ROM literal pools; tools/rename/consts.py --scan)']
    out += [f'0x{a:08X}\t{w}\t{bits}\t{lit}' for a, (w, bits, lit) in sorted(rows.items())]
    an.write_if_changed(TABLE, '\n'.join(out) + '\n')
    print(f'float constants: {added} added, {skipped} left as reads (written, address taken or not a plain number), {len(rows)} listed')


def apply():
    rows = load()
    if not rows: return 0
    def sub(m):
        r = rows.get(int(m.group(2), 16))
        return r[2] if r and r[0] == int(m.group(1)) else m.group(0)
    changed = 0
    for p in an.cs_files(os.path.join(an.SRC, 'Trw.Game')):
        s = an.read(p)
        if 'Mem.F' not in s: continue
        t = READ.sub(sub, s)
        if t != s: changed += an.write_if_changed(p, t)
    return changed


if __name__ == '__main__':
    if '--scan' in sys.argv: scan(sys.argv[sys.argv.index('--scan') + 1])
    else: print(f'float constants: {apply()} files touched')
