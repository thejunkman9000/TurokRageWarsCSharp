#!/usr/bin/env python3
"""Static recompiler: the ROM's MIPS code -> C#, for the functions that have no C source.

This is the C# backend of the C port's recompiler (D:\\Projects\\TurokRageWars\\port\\mips2c.py): it reuses that tool's asm parser and its
analysis (branch-target resolution, stack/argument detection, 64-bit FPU mode detection) and replaces the code generation.
Registers become C# locals r0..r31, the FPU registers live in Cpu.Fpr, 64-bit each as on the real console (FR=1) (shared across calls: arguments in F12/F14, result in F0),
memory goes through Mem, the function's frame is a block on the guest stack (Stack.Alloc).

usage: python tools/mips2cs/mips2cs.py --c-root <C port repo> --sigs <sigs.txt> --out-dir <dir> --sigs-out <file> [name ...]
(recompiles everything listed in <c-root>/port/recomp.txt plus the extra names; names may be assembly-only libultra functions)

Calling convention of the generated code
* a recompiled function is `uint name(uint a0, uint a1, uint a2, uint a3[, uint a4 ...])` and returns v0 (v1 in Cpu.V1, FPU results in Cpu.Fpr[0]).
* when the C prototype of the function involves floats, 64-bit integers or structs, that register-level body is `name_rc` and `name` is a
  typed wrapper with the prototype's signature (the same trick as the C port).
* calls to translated C functions are marshalled from the registers according to the callee's signature (sigs file).
"""
import argparse, glob, os, re, sys

ap = argparse.ArgumentParser()
ap.add_argument('--c-root', required=True)
ap.add_argument('--sigs', required=True)
ap.add_argument('--out-dir', required=True)
ap.add_argument('--sigs-out', required=True)
ap.add_argument('--called', help='names the translated C calls: those without a C definition are recompiled (seeds)')
ap.add_argument('--all-asm', action='store_true', help='recompile every assembly-only function (function pointers can reach any of them)')
ap.add_argument('--defs', help='names that have a C translation (never recompiled; enables following the call graph)')
ap.add_argument('--called-out', help='write the call targets that have no code anywhere (they get stubs)')
ap.add_argument('--used', help='file name|ret from the transpiler: result types callers actually use')
ap.add_argument('--void-used', help='file name|ret: functions the C declares void but whose callers use a value; recompiled from the MIPS instead')
ap.add_argument('names', nargs='*')
args = ap.parse_args()

REPO = os.path.abspath(os.path.join(os.path.dirname(os.path.abspath(__file__)), '..', '..'))
C_ROOT = os.path.abspath(args.c_root)
OUT_DIR = os.path.abspath(args.out_dir)
SIGS_PATH = os.path.abspath(args.sigs)
SIGS_OUT = os.path.abspath(args.sigs_out)
os.chdir(C_ROOT)
sys.path.insert(0, os.path.join(C_ROOT, 'port'))
import mips2c
from mips2c import parse, imm, simm, reg, freg, int_arg_count, fa_read_first, BR, is_ctl, label_name

# named data symbols (libultra globals, jump tables) for %hi/%lo operands
SYMBOLS = {}
for _l in open('symbol_addrs.txt'):
    _m = re.match(r'(\w+)\s*=\s*0x([0-9A-Fa-f]+)', _l)
    if _m: SYMBOLS[_m.group(1)] = int(_m.group(2), 16)
_orig_sym_addr = mips2c.sym_addr
def _sym_addr(s):
    try:
        return _orig_sym_addr(s)
    except ValueError:
        m = re.match(r'jtbl_([0-9A-Fa-f]{8})$', s)
        if m: return int(m.group(1), 16)
        if s in SYMBOLS: return SYMBOLS[s]
        raise
mips2c.sym_addr = _sym_addr

# ----------------------------------------------------------------------------------------------- signatures

class Sig:
    def __init__(self, ret, params, structs=None, hidden=False):
        self.ret, self.params, self.hidden = ret, params, hidden
        self.structs = structs if structs is not None else [0] * len(params)

def load_sigs(path):
    out = {}
    for line in open(path, encoding='utf-8'):
        line = line.rstrip('\n')
        if not line: continue
        p = line.split('|')
        ps = p[2].split(',') if p[2] else []
        st = [int(x) for x in p[3].split(',')] if len(p) > 3 and p[3] else [0] * len(ps)
        out[p[0]] = Sig(p[1], ps, st, len(p) > 4 and p[4] == '1')
    return out

SIGS = load_sigs(SIGS_PATH)

FP = ('float', 'double')
# fpsig_override.txt: name ret params... (v i f d / i f d) for prototypes that hide float parameters behind s32
CS_OF = {'i': 'int', 'f': 'float', 'd': 'double', 'v': 'void', 'l': 'long'}
if os.path.exists('port/fpsig_override.txt'):
    for l in open('port/fpsig_override.txt'):
        f = l.split('#')[0].split()
        if len(f) >= 2:
            _new = Sig(CS_OF[f[1]], [CS_OF[t] for t in f[2:]])
            _old = SIGS.get(f[0])
            # a signature taken from the matched C already knows the exact types (short, ushort, ...): keep it when it has the same shape
            if _old is not None and len(_old.params) == len(_new.params) and not _old.hidden and not any(_old.structs) and \
               all((a in FP) == (b in FP) for a, b in zip(_old.params, _new.params)) and (_old.ret in FP) == (_new.ret in FP) and \
               (_old.ret == 'void') == (_new.ret == 'void'):
                continue
            SIGS[f[0]] = _new

FP = ('float', 'double')
WIDE = ('long', 'ulong')
SMALL_INT = ('sbyte', 'byte', 'short', 'ushort', 'int', 'uint')

def trivial(sig):
    return (not sig.hidden and all(t in SMALL_INT for t in sig.params) and sig.ret in ('void',) + SMALL_INT
            and not any(sig.structs))

def norm(n):
    if re.fullmatch(r'func_[0-9A-Fa-f]{6}', n): return 'func_00' + n[5:]
    m = re.fullmatch(r'func_([89ABab][0-9A-Fa-f]{7})', n)          # KSEG0/KSEG1 alias of a code address
    if m: return 'func_%08X' % (int(m.group(1), 16) & 0x1FFFFFFF)
    return n

# ----------------------------------------------------------------------------------------------- register/ABI marshalling text

def word_expr(k):
    return ['r4', 'r5', 'r6', 'r7'][k] if k < 4 else 'Mem.U32(r29 + %du)' % (0x10 + 4 * (k - 4))

def typed_args_from_regs(sig, nwords_avail=None):
    """C# argument expressions (and pre/post statements) that read the arguments of `sig` from the MIPS argument registers / stack."""
    pre, post, out = [], [], []
    word = 0
    for i, t in enumerate(sig.params):
        in_fpreg = t in FP and (i == 0 or (i == 1 and sig.params[0] in FP))
        if t == 'double' or t in WIDE: word = (word + 1) & ~1
        if in_fpreg:
            out.append('Cpu.S(%d)' % (12 + 2 * i) if t == 'float' else 'Cpu.D(%d)' % (12 + 2 * i))
            word += 2 if t == 'double' else 1
        elif t == 'float': out.append('Cpu.U2F(%s)' % word_expr(word)); word += 1
        elif t == 'double': out.append('Cpu.U2D(%s, %s)' % (word_expr(word), word_expr(word + 1))); word += 2
        elif t == 'long': out.append('Cpu.Long(%s, %s)' % (word_expr(word), word_expr(word + 1))); word += 2
        elif t == 'ulong': out.append('(ulong)Cpu.Long(%s, %s)' % (word_expr(word), word_expr(word + 1))); word += 2
        elif sig.structs[i] > 0:
            size = sig.structs[i]; n = (size + 3) // 4
            pre.append('uint s%d_ = Stack.Alloc(%d);' % (i, size))
            for k in range(n): pre.append('Mem.Set(s%d_ + %du, %s);' % (i, 4 * k, word_expr(word + k)))
            post.append('Stack.Free(%d);' % size)
            out.append('s%d_' % i); word += n
        else:
            out.append(word_expr(word) if t == 'uint' else '(%s)%s' % (t, word_expr(word))); word += 1
    return pre, post, out

def store_result(sig, call):
    """statements that put the typed result of `call` into v0/v1/F0"""
    r = sig.ret
    if r == 'void': return ['%s;' % call]
    if r == 'float': return ['Cpu.SetS(0, %s);' % call]
    if r == 'double': return ['Cpu.SetD(0, %s);' % call]
    if r in ('long', 'ulong'): return ['{ var v_ = %s; r2 = (uint)((ulong)v_ >> 32); r3 = (uint)v_; }' % call]
    if r == 'uint': return ['r2 = %s;' % call]
    return ['r2 = (uint)%s;' % call]

def words_from_typed(sig, names):
    """for a typed wrapper: statements and the list of argument words (strings) that carry the parameters `names` in the register convention"""
    pre, words = [], []
    def put(k, v):
        while len(words) <= k: words.append('0u')
        words[k] = v
    word = 0
    for i, t in enumerate(sig.params):
        n = names[i]
        in_fpreg = t in FP and (i == 0 or (i == 1 and sig.params[0] in FP))
        if t == 'double' or t in WIDE: word = (word + 1) & ~1
        if in_fpreg:
            pre.append(('Cpu.SetS(%d, %s);' % (12 + 2 * i, n)) if t == 'float' else 'Cpu.SetD(%d, %s);' % (12 + 2 * i, n))
            word += 2 if t == 'double' else 1
        elif t == 'float': put(word, 'Cpu.F2U(%s)' % n); word += 1
        elif t == 'double': put(word, 'Cpu.D2Hi(%s)' % n); put(word + 1, 'Cpu.D2Lo(%s)' % n); word += 2
        elif t in WIDE: put(word, '(uint)((ulong)%s >> 32)' % n); put(word + 1, '(uint)%s' % n); word += 2
        elif sig.structs[i] > 0:
            k = (sig.structs[i] + 3) // 4
            for j in range(k): put(word + j, 'Mem.U32(%s + %du)' % (n, 4 * j))
            word += k
        else:
            put(word, n if t == 'uint' else '(uint)%s' % n); word += 1
    return pre, words

# ----------------------------------------------------------------------------------------------- instruction templates

R = lambda i: 'r%d' % i

def addr_of(base, off):
    return '(%s + %du)' % (base, off) if off >= 0 else '(%s - %du)' % (base, -off)

def addi_expr(a, v):
    return '%s + %du' % (R(a), v) if v >= 0 else '%s - %du' % (R(a), -v)

def gen_ins(m, ops):
    if m == 'nop': return ''
    if m == 'break': return 'throw new InvalidOperationException("MIPS break");'
    if m in ('sync', 'cache'): return ''
    if m == 'mfc0': return '%s = Cpu.Cp0[%d];' % (R(reg(ops[0])), reg(ops[1]))
    if m == 'mtc0': return 'Cpu.Cp0[%d] = %s;' % (reg(ops[1]), R(reg(ops[0])))
    if m == 'cfc1': return '%s = Cpu.Fcsr;' % R(reg(ops[0]))
    if m == 'ctc1': return 'Cpu.Fcsr = %s;' % R(reg(ops[0]))
    if m in ('lwl', 'lwr', 'swl', 'swr'):
        mm = re.match(r'(.*)\((\$?\w+)\)$', ops[1])
        off = simm(imm(mm.group(1) or '0')); addr = addr_of(R(reg(mm.group(2))), off); t = R(reg(ops[0]))
        if m == 'lwl': return '%s = Cpu.Lwl(%s, %s);' % (t, t, addr)
        if m == 'lwr': return '%s = Cpu.Lwr(%s, %s);' % (t, t, addr)
        if m == 'swl': return 'Cpu.Swl(%s, %s);' % (t, addr)
        return 'Cpu.Swr(%s, %s);' % (t, addr)
    if m in ('addu', 'add', 'addi', 'addiu', 'subu', 'sub', 'and', 'or', 'xor', 'nor', 'slt', 'sltu', 'sllv', 'srlv', 'srav',
             'move', 'li', 'negu', 'not', 'slti', 'sltiu', 'mul'):
        if m == 'move': return '%s = %s;' % (R(reg(ops[0])), R(reg(ops[1])))
        if m == 'li': return '%s = (uint)%s;' % (R(reg(ops[0])), imm(ops[1]))
        if m == 'negu': return '%s = 0u - %s;' % (R(reg(ops[0])), R(reg(ops[1])))
        if m == 'not': return '%s = ~%s;' % (R(reg(ops[0])), R(reg(ops[1])))
        d, a = reg(ops[0]), reg(ops[1]); b = ops[2]
        if m in ('addi', 'addiu'): e = addi_expr(a, simm(imm(b)))
        elif m in ('addu', 'add'): e = '%s + %s' % (R(a), R(reg(b)))
        elif m in ('subu', 'sub'): e = '%s - %s' % (R(a), R(reg(b)))
        elif m == 'mul': e = '(uint)((int)%s * (int)%s)' % (R(a), R(reg(b)))
        elif m == 'and': e = '%s & %s' % (R(a), R(reg(b)))
        elif m == 'or': e = '%s | %s' % (R(a), R(reg(b)))
        elif m == 'xor': e = '%s ^ %s' % (R(a), R(reg(b)))
        elif m == 'nor': e = '~(%s | %s)' % (R(a), R(reg(b)))
        elif m == 'slt': e = '(uint)((int)%s < (int)%s ? 1 : 0)' % (R(a), R(reg(b)))
        elif m == 'sltu': e = '(uint)(%s < %s ? 1 : 0)' % (R(a), R(reg(b)))
        elif m == 'slti': e = '(uint)((int)%s < %d ? 1 : 0)' % (R(a), simm(imm(b)))
        elif m == 'sltiu': e = '(uint)(%s < (uint)(%d) ? 1 : 0)' % (R(a), simm(imm(b)))
        elif m == 'sllv': e = '%s << (int)(%s & 31)' % (R(reg(ops[1])), R(reg(ops[2])))
        elif m == 'srlv': e = '%s >> (int)(%s & 31)' % (R(reg(ops[1])), R(reg(ops[2])))
        elif m == 'srav': e = '(uint)((int)%s >> (int)(%s & 31))' % (R(reg(ops[1])), R(reg(ops[2])))
        return '%s = %s;' % (R(d), e)
    if m in ('andi', 'ori', 'xori'):
        op = {'andi': '&', 'ori': '|', 'xori': '^'}[m]
        return '%s = %s %s %du;' % (R(reg(ops[0])), R(reg(ops[1])), op, imm(ops[2]) & 0xFFFF)
    if m == 'lui': return '%s = %du;' % (R(reg(ops[0])), (imm(ops[1]) & 0xFFFF) << 16)
    if m in ('sll', 'srl', 'sra'):
        sh = imm(ops[2])
        if m == 'sll': return '%s = %s << %d;' % (R(reg(ops[0])), R(reg(ops[1])), sh)
        if m == 'srl': return '%s = %s >> %d;' % (R(reg(ops[0])), R(reg(ops[1])), sh)
        return '%s = (uint)((int)%s >> %d);' % (R(reg(ops[0])), R(reg(ops[1])), sh)
    if m in ('mult', 'multu'):
        a, b = R(reg(ops[0])), R(reg(ops[1]))
        if m == 'mult': return '{ long p_ = (long)(int)%s * (int)%s; lo = (uint)p_; hi = (uint)(p_ >> 32); }' % (a, b)
        return '{ ulong p_ = (ulong)%s * %s; lo = (uint)p_; hi = (uint)(p_ >> 32); }' % (a, b)
    if m in ('div', 'divu'):
        a, b = R(reg(ops[-2])), R(reg(ops[-1]))
        if m == 'div': return 'if (%s != 0) { lo = (uint)Cpu.SDiv((int)%s, (int)%s); hi = (uint)Cpu.SRem((int)%s, (int)%s); }' % (b, a, b, a, b)
        return 'if (%s != 0) { lo = %s / %s; hi = %s %% %s; }' % (b, a, b, a, b)
    if m == 'mflo': return '%s = lo;' % R(reg(ops[0]))
    if m == 'mfhi': return '%s = hi;' % R(reg(ops[0]))
    if m == 'mtlo': return 'lo = %s;' % R(reg(ops[0]))
    if m == 'mthi': return 'hi = %s;' % R(reg(ops[0]))
    if m in ('lw', 'lh', 'lhu', 'lb', 'lbu', 'sw', 'sh', 'sb', 'lwc1', 'swc1', 'ldc1', 'sdc1'):
        mm = re.match(r'(.*)\((\$?\w+)\)$', ops[1])
        off = simm(imm(mm.group(1) or '0')); base = R(reg(mm.group(2)))
        addr = addr_of(base, off)
        if m in ('lwc1', 'swc1', 'ldc1', 'sdc1'):
            fr = freg(ops[0])
            if m == 'lwc1': return 'Cpu.SetW(%d, Mem.U32(%s));' % (fr, addr)
            if m == 'swc1': return 'Mem.Set(%s, Cpu.W(%d));' % (addr, fr)
            if m == 'ldc1': return 'Cpu.Fpr[%d] = Mem.U64(%s);' % (fr, addr)
            return 'Mem.Set(%s, Cpu.Fpr[%d]);' % (addr, fr)
        t = R(reg(ops[0]))
        if m == 'lw': return '%s = Mem.U32(%s);' % (t, addr)
        if m == 'lh': return '%s = (uint)(int)Mem.S16(%s);' % (t, addr)
        if m == 'lhu': return '%s = Mem.U16(%s);' % (t, addr)
        if m == 'lb': return '%s = (uint)(int)Mem.S8(%s);' % (t, addr)
        if m == 'lbu': return '%s = Mem.U8(%s);' % (t, addr)
        if m == 'sw': return 'Mem.Set(%s, %s);' % (addr, t)
        if m == 'sh': return 'Mem.Set(%s, (ushort)%s);' % (addr, t)
        if m == 'sb': return 'Mem.Set(%s, (byte)%s);' % (addr, t)
    if m == 'mtc1': return 'Cpu.SetW(%d, %s);' % (freg(ops[1]), R(reg(ops[0])))
    if m == 'mfc1': return '%s = Cpu.W(%d);' % (R(reg(ops[0])), freg(ops[1]))
    if m in ('add.s', 'sub.s', 'mul.s', 'div.s'):
        op = {'add': '+', 'sub': '-', 'mul': '*', 'div': '/'}[m[:3]]
        return 'Cpu.SetS(%d, Cpu.S(%d) %s Cpu.S(%d));' % (freg(ops[0]), freg(ops[1]), op, freg(ops[2]))
    if m in ('add.d', 'sub.d', 'mul.d', 'div.d'):
        op = {'add': '+', 'sub': '-', 'mul': '*', 'div': '/'}[m[:3]]
        return 'Cpu.SetD(%d, Cpu.D(%d) %s Cpu.D(%d));' % (freg(ops[0]), freg(ops[1]), op, freg(ops[2]))
    if m == 'mov.d': return 'Cpu.Fpr[%d] = Cpu.Fpr[%d];' % (freg(ops[0]), freg(ops[1]))
    if m == 'neg.d': return 'Cpu.SetD(%d, -Cpu.D(%d));' % (freg(ops[0]), freg(ops[1]))
    if m == 'abs.d': return 'Cpu.SetD(%d, Math.Abs(Cpu.D(%d)));' % (freg(ops[0]), freg(ops[1]))
    if m == 'sqrt.d': return 'Cpu.SetD(%d, Math.Sqrt(Cpu.D(%d)));' % (freg(ops[0]), freg(ops[1]))
    if m == 'cvt.d.s': return 'Cpu.SetD(%d, (double)Cpu.S(%d));' % (freg(ops[0]), freg(ops[1]))
    if m == 'cvt.s.d': return 'Cpu.SetS(%d, (float)Cpu.D(%d));' % (freg(ops[0]), freg(ops[1]))
    if m == 'cvt.d.w': return 'Cpu.SetD(%d, (double)(int)Cpu.W(%d));' % (freg(ops[0]), freg(ops[1]))
    if m in ('trunc.w.d', 'cvt.w.d'): return 'Cpu.SetW(%d, (uint)(int)Cpu.D(%d));' % (freg(ops[0]), freg(ops[1]))
    if m in ('round.w.s', 'floor.w.s', 'ceil.w.s'):
        fn = {'round': 'MathF.Round(%s, MidpointRounding.ToEven)', 'floor': 'MathF.Floor(%s)', 'ceil': 'MathF.Ceiling(%s)'}[m.split('.')[0]] % ('Cpu.S(%d)' % freg(ops[1]))
        return 'Cpu.SetW(%d, (uint)(int)%s);' % (freg(ops[0]), fn)
    if m in ('round.w.d', 'floor.w.d', 'ceil.w.d'):
        fn = {'round': 'Math.Round(%s, MidpointRounding.ToEven)', 'floor': 'Math.Floor(%s)', 'ceil': 'Math.Ceiling(%s)'}[m.split('.')[0]] % ('Cpu.D(%d)' % freg(ops[1]))
        return 'Cpu.SetW(%d, (uint)(int)%s);' % (freg(ops[0]), fn)
    if m.startswith('c.') and m.endswith('.d'):
        k = m[2:-2]; cmp = {'lt': '<', 'le': '<=', 'eq': '==', 'ult': '<', 'ule': '<=', 'ueq': '=='}[k]
        return 'fcc = Cpu.D(%d) %s Cpu.D(%d);' % (freg(ops[0]), cmp, freg(ops[1]))
    if m == 'mov.s': return 'Cpu.SetS(%d, Cpu.S(%d));' % (freg(ops[0]), freg(ops[1]))
    if m == 'neg.s': return 'Cpu.SetS(%d, -Cpu.S(%d));' % (freg(ops[0]), freg(ops[1]))
    if m == 'abs.s': return 'Cpu.SetS(%d, MathF.Abs(Cpu.S(%d)));' % (freg(ops[0]), freg(ops[1]))
    if m == 'sqrt.s': return 'Cpu.SetS(%d, MathF.Sqrt(Cpu.S(%d)));' % (freg(ops[0]), freg(ops[1]))
    if m == 'cvt.s.w': return 'Cpu.SetS(%d, (float)(int)Cpu.W(%d));' % (freg(ops[0]), freg(ops[1]))
    if m in ('trunc.w.s', 'cvt.w.s'): return 'Cpu.SetW(%d, (uint)(int)Cpu.S(%d));' % (freg(ops[0]), freg(ops[1]))
    if m.startswith('c.') and m.endswith('.s'):
        k = m[2:-2]; cmp = {'lt': '<', 'le': '<=', 'eq': '==', 'ult': '<', 'ule': '<=', 'ueq': '=='}[k]
        return 'fcc = Cpu.S(%d) %s Cpu.S(%d);' % (freg(ops[0]), cmp, freg(ops[1]))
    raise ValueError('unsupported instruction: %s %s' % (m, ops))

def cond(m, ops):
    if m in ('beq', 'beql'): return '%s == %s' % (R(reg(ops[0])), R(reg(ops[1])))
    if m in ('bne', 'bnel'): return '%s != %s' % (R(reg(ops[0])), R(reg(ops[1])))
    if m in ('beqz', 'beqzl'): return '%s == 0' % R(reg(ops[0]))
    if m in ('bnez', 'bnezl'): return '%s != 0' % R(reg(ops[0]))
    if m in ('bgez', 'bgezl'): return '(int)%s >= 0' % R(reg(ops[0]))
    if m in ('bltz', 'bltzl'): return '(int)%s < 0' % R(reg(ops[0]))
    if m in ('blez', 'blezl'): return '(int)%s <= 0' % R(reg(ops[0]))
    if m in ('bgtz', 'bgtzl'): return '(int)%s > 0' % R(reg(ops[0]))
    if m in ('bc1t', 'bc1tl'): return 'fcc'
    if m in ('bc1f', 'bc1fl'): return '!fcc'
    if m == 'b': return 'true'
    raise ValueError(m)

# ----------------------------------------------------------------------------------------------- analysis (shared by both passes)

class Analysis:
    pass

SYM_BY_ADDR = {}
for _n, _a in SYMBOLS.items(): SYM_BY_ADDR.setdefault(_a & 0x1FFFFFFF, []).append(_n)

_ADDR = re.compile(r'/\*\s*[0-9A-Fa-f]+\s+([0-9A-Fa-f]{8})\s+[0-9A-Fa-f]{8}\s*\*/')

def parse_a(text, name, extend=0, prepend=0):
    """mips2c.parse, but every instruction keeps its address: ('ins', mnemonic, operands, address).
    (Computed jumps can target instructions the disassembly never gave a label.)"""
    chunks = []; cur = None
    for l in text.split('\n'):
        s = re.sub(r'/\*.*?\*/', '', l).strip()
        if s.startswith(('glabel ', 'alabel ')):
            lab = s.split()[1]
            if s.startswith('glabel') or cur is None:
                cur = (lab, []); chunks.append(cur)
            else:
                cur[1].append(('label', lab))
            continue
        if cur is None or s.startswith(('endlabel', 'enddlabel', 'dlabel', 'jlabel', 'endjlabel', 'nonmatching')): continue
        if s.startswith('.jtbl'): cur[1].append(('jtbl', s.split()[1:])); continue
        if not s or (s.startswith('.') and not s.startswith('.L')): continue
        m = re.match(r'(\.L\w+):$', s)
        if m: cur[1].append(('label', m.group(1))); continue
        m = re.match(r'(\w[\w.]*)\s*(.*)$', s)
        if m:
            am = _ADDR.search(l)
            cur[1].append(('ins', m.group(1), [o.strip() for o in re.split(r',(?![^(]*\))', m.group(2))] if m.group(2) else [],
                           int(am.group(1), 16) if am else None))
    idx = next((k for k, c in enumerate(chunks) if c[0] == name), None)
    if idx is None: return []
    out = []
    for k in range(max(0, idx - prepend), min(len(chunks), idx + extend + 1)):
        out.append(('label', chunks[k][0])); out.extend(chunks[k][1])
    return out

def analyze(text, name, sig_name=None):
    for ext, pre in ((0, 0), (1, 0), (0, 1), (1, 1), (2, 0), (0, 2), (2, 2), (3, 3), (5, 5)):
        items = parse_a(text, name, ext, pre)
        if not items: raise ValueError('function %s not found' % name)
        labels = {it[1] for it in items if it[0] == 'label'}
        targets = {it[2][-1] for it in items if it[0] == 'ins' and it[1] in BR and it[2]} | \
                  {it[2][0] for it in items if it[0] == 'ins' and it[1] == 'j' and it[2][0].startswith('.L')}
        entry_goto = pre > 0
        if targets <= labels: break
    else:
        raise ValueError('unresolved branch targets: ' + ' '.join(sorted(targets - labels)[:3]))
    a = Analysis()
    a.asm_name = name
    sg = SIGS.get(sig_name or name)
    a.items, a.labels, a.entry_goto = items, labels, entry_goto
    if fa_read_first(items) and (sg is None or not any(t in FP for t in sg.params)):
        raise ValueError('float parameters of unknown type')
    a.argc = int_arg_count(items)
    frame = 0; inargs = 0
    for it in items:
        if it[0] == 'ins' and it[1] in ('addiu', 'addi') and it[2][0] in ('$sp', 'sp') and it[2][1] in ('$sp', 'sp'):
            v = simm(imm(it[2][2]))
            if v < 0: frame = max(frame, -v)
    for it in items:
        if it[0] == 'ins' and it[1] in ('lw', 'lh', 'lhu', 'lb', 'lbu', 'lwc1'):
            mm = re.match(r'(.*)\(\$?sp\)$', it[2][1])
            if mm and imm(mm.group(1) or '0') >= frame + 0x10:
                inargs = max(inargs, (imm(mm.group(1)) - frame - 0x10) // 4 + 1)
    if frame:
        for it in items:
            if it[0] == 'ins' and it[1] in ('addiu', 'addi') and it[2][1] in ('$sp', 'sp') and it[2][0] not in ('$sp', 'sp'):
                v = simm(imm(it[2][2]))
                if frame + 4 <= v < frame + 0x40: inargs = max(inargs, 7)
    a.inargs = min(inargs, 20)
    a.frame = frame
    a.jtargets = [t for it in items if it[0] == 'jtbl' for t in it[1]]
    return a

# ----------------------------------------------------------------------------------------------- emission

NULLGUARD = set(open('port/recomp_nullguard.txt').read().split()) if os.path.exists('port/recomp_nullguard.txt') else set()
ANALYSES = {}      # name -> Analysis, for every function being recompiled
RC_SIG = {}        # name -> (callable C# name, number of register parameters)

def callee_rc(target):
    t = norm(target)
    return RC_SIG.get(t)

def emit_call(target, nargs, delay, reg_target=None, tail_label=None):
    """C# statements for a jal/jalr/tail call, leaving the results in r2/r3/F0"""
    words = ['r4', 'r5', 'r6', 'r7'] + ['Mem.U32(r29 + %du)' % (0x10 + 4 * k) for k in range(max(0, nargs - 4))]
    out = []
    if delay: out.append(delay)
    if reg_target is not None:
        ex = words[4:]
        if ex: out.append('r2 = Funcs.CallRegX(%s, %s, new uint[] { %s }); r3 = Cpu.V1;' % (R(reg_target), ', '.join(words[:4]), ', '.join(ex)))
        else: out.append('r2 = Funcs.CallReg(%s, %s); r3 = Cpu.V1;' % (R(reg_target), ', '.join(words[:4])))
        return out
    t = norm(target)
    rc = RC_SIG.get(t)
    if rc:
        cname, np_ = rc
        a = (words + ['0u'] * np_)[:np_]
        out.append('r2 = %s(%s); r3 = Cpu.V1;' % (cname, ', '.join(a)))
        return out
    sg = SIGS.get(t)
    if sg:
        pre, post, a = typed_args_from_regs(sg)
        call = '%s(%s)' % (t, ', '.join(a))
        body = pre + store_result(sg, call) + post
        out.append('{ ' + ' '.join(body) + ' }' if len(body) > 1 else body[0])
        return out
    out.append('r2 = %s(%s); r3 = Cpu.V1;' % (t, ', '.join(words[:4])))      # a hand-written register-convention function
    return out

def recompile(name, a):
    items, labels = a.items, a.labels
    pending = set(); saved_slots = set(); saved_regs = set(); seen_call = False
    body = []
    i = 0
    cname = RC_SIG[name][0]
    def ret_stmt(delay=''):
        return ('%s Cpu.V1 = r3; return r2;' % delay).strip()
    # computed jumps (jr to a table entry) can land on any instruction: label them all by address
    has_jr = any(it[0] == 'ins' and it[1] == 'jr' and reg(it[2][0]) != 31 for it in items)
    slot_idx = set()
    for k, it in enumerate(items):
        if it[0] == 'ins' and is_ctl(it[1]):
            j = k + 1
            while j < len(items) and items[j][0] == 'label': j += 1
            if j < len(items): slot_idx.add(j)
    iaddr = {k: it[3] for k, it in enumerate(items) if it[0] == 'ins' and len(it) > 3 and it[3] is not None and k not in slot_idx}
    while i < len(items):
        it = items[i]
        if it[0] == 'jtbl': i += 1; continue
        if it[0] == 'label':
            body.append('%s: ;' % label_name(it[1])); i += 1; continue
        if has_jr and i in iaddr: body.append('L_I%08X: ;' % iaddr[i])
        _, m, ops = it[:3]
        if is_ctl(m):
            j = i + 1
            while j < len(items) and items[j][0] == 'label': j += 1
            if j > i + 1: raise ValueError('label between a branch and its delay slot')
            if j >= len(items): dm, dops, delay = 'nop', [], ''
            else:
                dm, dops = items[j][1], items[j][2]
                if is_ctl(dm): raise ValueError('branch in delay slot')
                delay = gen_ins(dm, dops)
            if m in BR:
                likely = (m.endswith('l') and m != 'b') or m in ('bc1tl', 'bc1fl')
                if ops[-1] not in labels:                         # conditional tail call into another function
                    tail = emit_call(ops[-1], 4, '') + ['Cpu.V1 = r3; return r2;']
                    if likely: body.append('if (%s) { %s %s }' % (cond(m, ops), delay, ' '.join(tail)))
                    else: body.append('{ bool c_ = %s; %s if (c_) { %s } }' % (cond(m, ops), delay, ' '.join(tail)))
                    i = min(j + 1, len(items)); continue
                tgt = label_name(ops[-1])
                if likely: body.append('if (%s) { %s goto %s; }' % (cond(m, ops), delay, tgt))
                else: body.append('{ bool c_ = %s; %s if (c_) goto %s; }' % (cond(m, ops), delay, tgt))
            elif m == 'j':
                if ops[0] in labels: body.append('%s goto %s;' % (delay, label_name(ops[0])))
                else: body.append('{ %s %s Cpu.V1 = r3; return r2; }' % (delay, ' '.join(emit_call(ops[0], 4, ''))))
            elif m == 'jr':
                if reg(ops[0]) != 31:
                    if iaddr:
                        cases = ' '.join('case %du: goto L_I%08X;' % (ad, ad) for ad in sorted(set(iaddr.values())))
                    else:
                        targets = sorted(set(a.jtargets)) or sorted(l for l in labels if re.fullmatch(r'\.L[0-9A-Fa-f]{6,8}', l))
                        if not targets: raise ValueError('jr to non-ra (jump table?)')
                        cases = ' '.join('case %du: goto %s;' % (int(t[2:], 16), label_name(t)) for t in targets)
                    body.append('%s switch (%s) { %s default: throw new InvalidOperationException($"bad jump table target {%s:X8} in %s"); }' % (delay, R(reg(ops[0])), cases, R(reg(ops[0])), name))
                else:
                    body.append(ret_stmt(delay))
            elif m in ('jal', 'jalr'):
                target = ops[0] if m == 'jal' else None
                if dm in ('sw', 'swc1', 'sdc1'):
                    mm = re.match(r'(.*)\(\$?sp\)$', dops[1])
                    if mm:
                        off = simm(imm(mm.group(1) or '0'))
                        if dm == 'sw' and not seen_call and reg(dops[0]) in (16, 17, 18, 19, 20, 21, 22, 23, 30, 31) and reg(dops[0]) not in saved_regs:
                            saved_slots.add(off); saved_regs.add(reg(dops[0]))
                        elif 0x10 <= off < 0x70 and off not in saved_slots:
                            pending.add(('sp', off))
                            if dm == 'sdc1': pending.add(('sp', off + 4))
                nargs = 4
                while ('sp', 0x10 + 4 * (nargs - 4)) in pending: nargs += 1
                stmts = emit_call(target, nargs, delay, reg_target=None if target else reg(ops[0]))
                body.append(' '.join(stmts))
                pending = set(); seen_call = True
            i = min(j + 1, len(items)); continue
        if m in ('sw', 'swc1', 'sdc1'):
            mm = re.match(r'(.*)\(\$?sp\)$', ops[1])
            if mm:
                off = simm(imm(mm.group(1) or '0'))
                if m == 'sw' and not seen_call and reg(ops[0]) in (16, 17, 18, 19, 20, 21, 22, 23, 30, 31) and reg(ops[0]) not in saved_regs:
                    saved_slots.add(off); saved_regs.add(reg(ops[0]))
                elif 0x10 <= off < 0x70 and off not in saved_slots:
                    pending.add(('sp', off))
                    if m == 'sdc1': pending.add(('sp', off + 4))
        body.append(gen_ins(m, ops)); i += 1

    stk_words = max(a.frame // 4 + 16 + 8, 64)
    total_bytes = (stk_words + 32) * 4
    np_ = 4 + a.inargs
    params = ', '.join('uint a%d' % k for k in range(np_))
    L = []
    L.append('    public static uint %s(%s)' % (cname, params))
    L.append('    {')
    if name in NULLGUARD: L.append('        if (a0 == 0) return 0;      // port guard: callers pass NULL here')
    L.append('        uint ' + ', '.join('r%d = 0' % k for k in range(32)) + ';')
    L.append('        uint lo = 0, hi = 0; bool fcc = false;')
    L.append('        uint _fp = Stack.Alloc(%d);' % total_bytes)
    L.append('        try')
    L.append('        {')
    L.append('            unchecked')
    L.append('            {')
    L.append('                Mem.Fill(_fp, %d, 0);' % total_bytes)
    L.append('                if (Dbg.On) Dbg.Enter("%s", a0, a1, a2, a3);' % name)
    L.append('                r4 = a0; r5 = a1; r6 = a2; r7 = a3; r31 = 0;')
    L.append('                r29 = _fp + %du;' % (stk_words * 4))
    for k in range(a.inargs): L.append('                Mem.Set(r29 + %du, a%d);' % (0x10 + 4 * k, 4 + k))
    if a.entry_goto: L.append('                goto %s;' % label_name(a.asm_name))
    for b in body:
        if b: L.append('                ' + b)
    L.append('                Cpu.V1 = r3; return r2;')
    L.append('            }')
    L.append('        }')
    L.append('        finally { if (Dbg.On) Dbg.Leave("%s", r2); Stack.Free(%d); }' % (name, total_bytes))
    L.append('    }')
    return L

def wrapper(name, sig, inargs):
    """typed public function for a recompiled one whose prototype involves floats / 64-bit values / structs"""
    cname = name + '_rc'
    names = ['p%d' % i for i in range(len(sig.params))]
    plist = ', '.join('%s p%d' % (t, i) for i, t in enumerate(sig.params))
    pre, words = words_from_typed(sig, names)
    np_ = 4 + inargs
    words = (words + ['0u'] * np_)[:max(np_, len(words))]
    call = '%s(%s)' % (cname, ', '.join(words[:max(np_, 4)]))
    ret = 'void' if sig.ret == 'void' else sig.ret
    L = ['    public static %s %s(%s)' % (ret, name, plist), '    {']
    for p in pre: L.append('        ' + p)
    if sig.ret == 'void': L.append('        %s;' % call)
    elif sig.ret == 'float': L.append('        %s; return Cpu.S(0);' % call)
    elif sig.ret == 'double': L.append('        %s; return Cpu.D(0);' % call)
    elif sig.ret == 'long': L.append('        uint v0_ = %s; return Cpu.Long(v0_, Cpu.V1);' % call)
    elif sig.ret == 'ulong': L.append('        uint v0_ = %s; return (ulong)Cpu.Long(v0_, Cpu.V1);' % call)
    elif sig.ret == 'uint': L.append('        return %s;' % call)
    else: L.append('        return (%s)%s;' % (sig.ret, call))
    L.append('    }')
    return L

def main():
    idx = {}
    fn_names = set()       # names that are functions (as opposed to data labels in the same assembly files)
    for p in glob.glob('port/build/asm_func/*.s'):
        idx[os.path.basename(p)[:-2]] = p
        fn_names.add(os.path.basename(p)[:-2])
    for p in glob.glob('src/**/*.s', recursive=True) + glob.glob('asm/**/*.s', recursive=True):
        t = open(p, encoding='utf-8').read()
        is_func_file = p.replace('\\', '/').startswith('src/') or os.path.basename(p)[:-2] == os.path.basename(os.path.dirname(p)) \
            or '/nonmatchings/' in p.replace('\\', '/')
        for m in re.finditer(r'glabel (\S+)', t):
            idx.setdefault(m.group(1), p)
            if is_func_file and re.fullmatch(r'[A-Za-z_]\w*', m.group(1)) and len(m.group(1)) > 2 and not m.group(1).startswith(('D_', 'jtbl_', 'L8', 'dlabel')): fn_names.add(m.group(1))
    # functions the host implements by hand (src/Trw.Game/Os) are not recompiled
    host_impl = set()
    for p in glob.glob(os.path.join(REPO, 'src', 'Trw.Game', 'Os', '*.cs')):
        host_impl.update(re.findall(r'public static [\w<>\[\]]+ (\w+)\(', open(p, encoding='utf-8').read()))
    void_used = {}
    if args.void_used and os.path.exists(args.void_used):
        for line in open(args.void_used, encoding='utf-8'):
            line = line.strip()
            if line and not line.startswith('#'):
                parts = line.split('|')
                void_used[norm(parts[0])] = parts[1] if len(parts) > 1 else ''     # "name|" = recompile only, keep the C result type
    defs_for_all = set()
    if args.defs and os.path.exists(args.defs):
        defs_for_all = {l.strip() for l in open(args.defs, encoding='utf-8') if l.strip()}
    all_asm = [n for n in fn_names if args.all_asm and n not in defs_for_all and n not in host_impl] if args.all_asm else []
    # a function with a C translation is not recompiled (unless the C could not be used)
    called_seeds = []
    if args.called and os.path.exists(args.called):
        called_seeds = [norm(l.strip()) for l in open(args.called, encoding='utf-8') if l.strip()]
    names = [n for n in open('port/recomp.txt').read().split() + args.names + list(void_used) + all_asm + called_seeds
             if n not in host_impl and (n in void_used or norm(n) not in defs_for_all)]
    for nm, rt in void_used.items():          # keep the C parameters, take the result type the callers expect
        cur = SIGS.get(nm)
        if nm in host_impl: continue          # implemented by hand with its own (void) signature
        if rt and cur is not None and cur.ret == 'void':
            SIGS[nm] = Sig(rt, cur.params, cur.structs, cur.hidden)
    names = sorted(set(norm(n) for n in names))
    # result types the callers actually use: the prototype of a function with a MIPS-only body is sometimes declared void
    used_ret = {}
    if args.used and os.path.exists(args.used):
        for line in open(args.used, encoding='utf-8'):
            parts = line.strip().split('|')
            if len(parts) < 2 or parts[1] == 'void': continue
            nm = norm(parts[0])
            if nm not in used_ret or parts[1] in ('float', 'double'): used_ret[nm] = parts[1]
    for nm in names:
        cur, rt = SIGS.get(nm), used_ret.get(nm)
        if rt and cur is not None and cur.ret == 'void':
            SIGS[nm] = Sig(rt, cur.params, cur.structs, cur.hidden)
    failed = []
    # The C port's regex-based float signatures also look at the workers' drafts, which see float parameters the matched prototypes hide behind s32.
    # They are only trusted for the functions being recompiled here (matched C has exact signatures from the AST).
    try:
        import fpsig as _fpsig
        FPSIG_ALL = _fpsig.load()
    except Exception as _e:
        print('warning: fpsig unavailable:', _e)
        FPSIG_ALL = {}

    def apply_sig_overrides(n):
        ent = FPSIG_ALL.get(n)
        if ent is not None:
            _rt, _types = ent
            if '?' not in _types and _rt not in ('l', 's') and any(t in ('f', 'd') for t in _types + [_rt]):
                _cur = SIGS.get(n)
                if _cur is None or not any(t in ('float', 'double') for t in _cur.params + [_cur.ret]):
                    SIGS[n] = Sig(CS_OF[_rt], [CS_OF[t] for t in _types])
        cur, rt = SIGS.get(n), used_ret.get(n)
        if rt and cur is not None and cur.ret == 'void':
            SIGS[n] = Sig(rt, cur.params, cur.structs, cur.hidden)

    # every instruction line of every assembly file, by address (the disassembly splits some code into the wrong functions; the bytes are right)
    addr_lines = {}
    def load_addr_lines():
        if addr_lines: return
        pat = re.compile(r'/\*\s*[0-9A-Fa-f]+\s+([0-9A-Fa-f]{8})\s+[0-9A-Fa-f]{8}\s*\*/')
        for p in set(idx.values()):
            for l in open(p, encoding='utf-8'):
                m = pat.search(l)
                if m: addr_lines.setdefault(int(m.group(1), 16), l.rstrip('\n'))

    synth_text = {}
    def synth_chunk(name, addr):
        """A function body assembled from the instruction stream starting at `addr` (ends at the first jr ra whose delay slot lies beyond every forward branch target)."""
        load_addr_lines()
        if addr not in addr_lines: return None
        rows = []; a = addr; maxt = addr; lo = addr
        while a in addr_lines:
            body = re.sub(r'/\*.*?\*/', '', addr_lines[a]).strip()
            m = re.match(r'(\w[\w.]*)\s*(.*)$', body)
            if not m: break
            mn, ops = m.group(1), m.group(2).strip()
            rows.append((a, addr_lines[a], mn, ops))
            if mn in BR or mn == 'j':
                tm = re.search(r'(?:func_|\.L)([0-9A-Fa-f]{8})\s*$', ops)
                if tm:
                    t = int(tm.group(1), 16)
                    if t > a and t < a + 0x4000: maxt = max(maxt, t)
            if mn == 'jr' and ops in ('$ra', 'ra'):
                if a + 4 in addr_lines:
                    d = re.sub(r'/\*.*?\*/', '', addr_lines[a + 4]).strip()
                    dm = re.match(r'(\w[\w.]*)\s*(.*)$', d)
                    rows.append((a + 4, addr_lines[a + 4], dm.group(1), dm.group(2).strip())); a += 4
                if a + 4 >= maxt: break
            a += 4
        if not rows: return None
        end = rows[-1][0]
        # internal branch targets become local labels
        targets = set()
        out = ['glabel ' + name]
        fixed = []
        for (ad, raw, mn, ops) in rows:
            if (mn in BR or mn == 'j'):
                tm = re.search(r'(?:func_|\.L)([0-9A-Fa-f]{8})\s*$', ops)
                if tm:
                    t = int(tm.group(1), 16)
                    if addr <= t <= end:
                        targets.add(t)
                        raw = re.sub(r'(?:func_|\.L)[0-9A-Fa-f]{8}\s*$', '.L%08X' % t, raw.rstrip())
            fixed.append((ad, raw))
        for ad, raw in fixed:
            if ad in targets and ad != addr: out.append('.L%08X:' % ad)
            out.append(raw)
        return '\n'.join(out) + '\n'

    def find_asm(n):
        src = idx.get(n) or idx.get(n.replace('func_00', 'func_'))
        if src: return src, n
        # the code may be known under another symbol name at the same address
        mm = re.fullmatch(r'func_([0-9A-F]{8})', n)
        addr_n = int(mm.group(1), 16) if mm else SYMBOLS.get(n)
        if addr_n is not None:
            addr_n &= 0x1FFFFFFF
            for alt in SYM_BY_ADDR.get(addr_n, []) + ['func_%08X' % addr_n]:
                if alt in idx and alt != n: return idx[alt], alt
            # last resort: no chunk starts here, so assemble one from the ROM's instruction stream
            txt = synth_chunk(n, addr_n)
            if txt is not None:
                synth_text[n] = txt
                return '<synth>', n
        return None, None

    def read_asm(src, n):
        return synth_text[n] if src == '<synth>' else open(src, encoding='utf-8').read()

    # splat sometimes cuts a function in the middle: its code runs on into the next symbol (func_00449E38 continues into func_0044A100 after its last jal;
    # that tail stores the player's respawn key). The following symbols' code is appended as labels of the function until it ends in a return / jump
    # (the C port's gen_recomp.py with_fallthrough, 486377e; its wip/scan_fallthrough.py lists the cases).
    _INS = re.compile(r'\s*/\* [0-9A-F]+ ([0-9A-F]{8}) [0-9A-F]{8} \*/\s*(.*)$')
    def _ends(text):
        ins = [m.group(2).strip() for m in map(_INS.match, text.split('\n')) if m]
        return not ins or any(re.match(r'(jr|j|b|eret)\b', x) or re.match(r'beq\s+\$zero,\s*\$zero', x) for x in ins[-2:])
    ft_addrs = sorted((int(k[5:], 16), k) for k, pth in idx.items() if re.match(r'func_[0-9A-F]{8}$', k) and pth.replace(os.sep, '/').startswith('port'))
    def with_fallthrough(name, text):
        if not re.match(r'func_[0-9A-F]{8}$', name): return text
        last = text; prev = name
        for _ in range(6):
            if _ends(last): break
            cur = max([int(m.group(1), 16) for m in map(_INS.match, text.split('\n')) if m] or [int(name[5:], 16)])
            nxt = next((k for a, k in ft_addrs if a > cur), None)      # the first symbol after the code already present
            if not nxt: break
            u = open(idx[nxt], encoding='utf-8').read()
            ins_here = [m.group(2).strip() for m in map(_INS.match, text.split('\n')) if m]
            if ins_here and re.match(r'(j|jal|jalr|jr|b\w*)\s', ins_here[-1] + ' '):
                u = re.sub(r'^\s*glabel\s+' + nxt + r'\b.*$', '', u, flags=re.M)      # its first instruction is the delay slot of our last jump: no label between them
            else:
                u = re.sub(r'^(\s*)glabel\s+' + nxt + r'\b', r'\1alabel ' + nxt, u, flags=re.M)
            u = re.sub(r'^\s*endlabel\b.*$', '', u, flags=re.M)
            text = re.sub(r'^\s*endlabel\s+' + prev + r'\b.*$', '', text, flags=re.M) + '\n' + u
            last = u; prev = nxt
        return text

    # functions that have a C translation are not recompiled
    defined_c = set()
    if args.defs and os.path.exists(args.defs):
        defined_c = {l.strip() for l in open(args.defs, encoding='utf-8') if l.strip()}
    unresolved = set()
    fallthrough_used = []; ft_raw = {}

    # pass 1: analysis and public names; follow the call graph into asm-only functions that nobody else defines
    pending = list(names)
    seen = set(names)
    while pending:
        n = pending.pop()
        apply_sig_overrides(n)
        src, asm_name = find_asm(n)
        if not src:
            failed.append((n, 'no asm')); unresolved.add(n); continue
        try:
            raw = read_asm(src, n)
            ft = with_fallthrough(asm_name, raw) if src != '<synth>' else raw
            try: ANALYSES[n] = analyze(ft, asm_name, n)
            except Exception:
                if ft == raw: raise
                ANALYSES[n] = analyze(raw, asm_name, n)          # the appended code cannot be translated: as before
            if ft != raw: fallthrough_used.append(n); ft_raw[n] = (raw, asm_name)
        except Exception as e:
            failed.append((n, str(e))); continue
        if not defined_c: continue
        for it in ANALYSES[n].items:
            if it[0] == 'ins' and it[1] in ('jal', 'j') and it[2] and not it[2][0].startswith('.'):
                t = norm(it[2][0])
                if t in seen or t in defined_c or t in host_impl: continue
                seen.add(t); pending.append(t)
    sigs_out = {}
    for n, a in ANALYSES.items():
        np_ = 4 + a.inargs
        proto = SIGS.get(n)
        if proto is not None and not trivial(proto):
            RC_SIG[n] = (n + '_rc', np_)
            sigs_out[n] = proto
            sigs_out[n + '_rc'] = Sig('uint', ['uint'] * np_)
        else:
            RC_SIG[n] = (n, np_)
            sigs_out[n] = Sig('uint', ['uint'] * np_)
    # pass 2: emit
    os.makedirs(OUT_DIR, exist_ok=True)
    head = ('// <auto-generated> by tools/mips2cs from the ROM\'s MIPS code (no C source exists for this function). Edit freely. </auto-generated>\n'
            '#pragma warning disable CS0162, CS0164, CS0168, CS0219\n'
            'using Trw.Platform;\n\nnamespace Trw.Game;\n\npublic static partial class Game\n{\n')
    reg_lines = []
    done = []
    for n, a in ANALYSES.items():
        try:
            try: L = recompile(n, a)
            except Exception:
                # the appended run-on code cannot be translated (e.g. a label in a delay slot): the function alone, as before, if its interface is the same
                if n not in ft_raw: raise
                a2 = analyze(ft_raw[n][0], ft_raw[n][1], n)
                if a2.inargs != a.inargs: raise
                a = a2; L = recompile(n, a); fallthrough_used.remove(n)
            if RC_SIG[n][0] != n: L += [''] + wrapper(n, SIGS[n], a.inargs)
        except Exception as e:
            failed.append((n, str(e))); continue
        with open(os.path.join(OUT_DIR, n + '.cs'), 'w', newline='\n') as f:
            f.write(head + '\n'.join(L) + '\n}\n')
        done.append(n)
        m = re.fullmatch(r'func_([0-9A-Fa-f]{8})', n)
        addr_hex = m.group(1) if m else ('%08X' % (SYMBOLS[n] & 0x1FFFFFFF) if n in SYMBOLS else None)
        if addr_hex:
            cname, np_ = RC_SIG[n]
            extra = ''.join(', Cpu.Stk[%d]' % k for k in range(4, np_))
            reg_lines.append('        Funcs.Add(0x%sU, "%s", (a0, a1, a2, a3) => %s(a0, a1, a2, a3%s));' % (addr_hex, n, cname, extra))
    # failed functions must not leave stale signatures behind
    for n, _ in failed:
        sigs_out.pop(n, None); sigs_out.pop(n + '_rc', None)
    chunks = [reg_lines[k:k + 150] for k in range(0, len(reg_lines), 150)]
    R_ = [head.rstrip('\n'), '    /// <summary>Registers the recompiled functions by their original code address (function pointers, jalr).</summary>',
          '    public static void RegisterRecompiled()', '    {']
    R_ += ['        RegisterRecompiled%d();' % k for k in range(len(chunks))] + ['    }', '']
    for k, ch in enumerate(chunks):
        R_ += ['    private static void RegisterRecompiled%d()' % k, '    {'] + ch + ['    }', '']
    with open(os.path.join(OUT_DIR, '_RegisterRecompiled.cs'), 'w', newline='\n') as f:
        f.write('\n'.join(R_) + '}\n')
    with open(SIGS_OUT, 'w', newline='\n') as f:
        for n in sorted(sigs_out):
            s = sigs_out[n]
            f.write('%s|%s|%s|%s|%d\n' % (n, s.ret, ','.join(s.params), ','.join(str(x) for x in s.structs), 1 if s.hidden else 0))
    if args.called_out:
        with open(args.called_out, 'w', newline='\n') as f:
            f.write('\n'.join(sorted(n for n in (unresolved | {n for n, _ in failed}) if re.fullmatch(r'[A-Za-z_]\w*', n))) + '\n')
    print('recompiled %d, failed %d (%d followed their code into the next symbol)' % (len(done), len(failed), len(fallthrough_used)))
    for n, why in failed:
        if why != 'no asm': print('  FAILED', n, why)
    print('  (%d more failed with no asm: not functions of the ROM, e.g. local static names)' % sum(1 for _, w in failed if w == 'no asm'))

main()














