#!/usr/bin/env python3
"""ROM ground truth for argument registers: name|words = how many of a0-a3 each function's MIPS reads before writing them
(a `jal` reads the argument words its callee reads, found by a fixpoint over the call graph).

The decompiled C prototypes are sometimes shorter than what the ROM code really reads (a (void) wrapper that is only `jal f` forwards its caller's
a0..a3, a stale prototype omits trailing arguments). tools/Trw.Transpile uses this table to give such functions the hidden parameters the machine
code has, and to forward the caller's registers into them.

The scan is linear (no control flow): it can over-count, which only adds an unused hidden parameter, and under-count a parameter that is first
written on one branch and read on another; it never invents a read that is not in the code.
"""
import argparse, glob, os, re

ap = argparse.ArgumentParser()
ap.add_argument('--c-root', required=True, help='root of the C port (port/build/asm_func/*.s)')
ap.add_argument('--out', required=True)
A = ap.parse_args()

INS = re.compile(r'/\*[^*]*\*/\s+(\w[\w.]*)\s*(.*)$')
REG = re.compile(r'\$(a[0-3])\b')
# instructions whose first operand is only written (the rest is read)
DEST_FIRST = {'lw', 'lh', 'lhu', 'lb', 'lbu', 'lwl', 'lwr', 'addu', 'addiu', 'addi', 'subu', 'sub', 'and', 'andi', 'or', 'ori', 'xor', 'xori',
              'nor', 'slt', 'sltu', 'slti', 'sltiu', 'sll', 'srl', 'sra', 'sllv', 'srlv', 'srav', 'lui', 'mfc1', 'move', 'li', 'negu', 'not',
              'mul', 'mflo', 'mfhi', 'mfc0', 'cfc1'}
CTL = {'jal', 'jalr', 'j', 'jr', 'beq', 'bne', 'beqz', 'bnez', 'bgez', 'bltz', 'bgtz', 'blez', 'b', 'bc1t', 'bc1f', 'beql', 'bnel', 'beqzl',
       'bnezl', 'bgezl', 'bltzl', 'bc1tl', 'bc1fl', 'bgtzl', 'blezl', 'bgezal', 'bltzal'}


def norm(n):
    return 'func_00' + n[5:] if re.fullmatch(r'func_[0-9A-Fa-f]{6}', n) else n


asm, labels, cur = {}, {}, None
LAB = re.compile(r'^(\.L\w+):')
for p in glob.glob(os.path.join(A.c_root, 'port', 'build', 'asm_func', '*.s')):
    for l in open(p, errors='replace'):
        m = re.match(r'glabel (\S+)', l)
        if m:
            cur = norm(m.group(1)); asm[cur] = []; labels[cur] = {}; continue
        m = LAB.match(l.strip())
        if m and cur:
            labels[cur][m.group(1)] = len(asm[cur]); continue
        m = INS.match(l.strip())
        if m and cur:
            asm[cur].append((m.group(1), m.group(2)))


def trim(items, labs):
    """The listing of a function can run on into mis-split junk: stop after the first `jr $ra` (and its delay slot) that no branch jumps over."""
    reach = 0                                  # furthest instruction index a branch seen so far jumps to
    for i, (m, ops) in enumerate(items):
        if m in CTL and m not in ('jal', 'jalr', 'jr', 'j'):
            t = re.search(r'(\.L\w+)', ops)
            if t and t.group(1) in labs:
                reach = max(reach, labs[t.group(1)])
        if m == 'jr' and ops.strip() == '$ra' and i + 1 >= reach:
            return items[:i + 2]
    return items


for n in asm:
    asm[n] = trim(asm[n], labels[n])


SPILL = re.compile(r'^\$a[0-3]\s*,\s*-?\d+\(\$sp\)$')


def rw(m, ops):
    """(a-registers read, a-registers written) by one instruction"""
    if m in ('sw', 'sh', 'sb') and SPILL.match(ops.strip()):
        return [], []                          # saving an argument register to its stack home slot is not a use of the argument
    parts = [o.strip() for o in ops.split(',')] if ops else []
    if m in DEST_FIRST and parts and '(' not in parts[0]:
        return [r for o in parts[1:] for r in REG.findall(o)], REG.findall(parts[0])
    return REG.findall(ops), []


def words(items, W):
    wr, n, i = set(), 0, 0

    def rd(rs):
        nonlocal n
        for r in rs:
            if r not in wr:
                n = max(n, int(r[1]) + 1)

    while i < len(items):
        m, ops = items[i]
        if m in CTL:
            d = items[i + 1] if i + 1 < len(items) and items[i + 1][0] not in CTL else None
            if m in ('jal', 'bgezal', 'bltzal'):
                # the delay slot runs before the callee, which then reads its own argument words
                if d:
                    s, w = rw(*d); rd(s); wr.update(w)
                if m == 'jal':
                    for k in range(W.get(norm(ops.strip()), 0)):
                        if 'a%d' % k not in wr:
                            n = max(n, k + 1)
                wr.update(['a0', 'a1', 'a2', 'a3'])
            else:
                rd(REG.findall(ops))                       # branch reads happen before the delay slot executes
                if d:
                    s, w = rw(*d); rd(s); wr.update(w)
                if m == 'jalr':
                    wr.update(['a0', 'a1', 'a2', 'a3'])
            i += 2 if d else 1
            continue
        s, w = rw(m, ops); rd(s); wr.update(w); i += 1
    return n


W = {}
for _ in range(12):                                         # fixpoint through the call graph
    changed = False
    for n, it in asm.items():
        a = min(words(it, W), 4)
        if W.get(n, 0) != a:
            W[n] = a; changed = True
    if not changed:
        break
open(A.out, 'w', newline='\n').write(''.join('%s|%d\n' % (n, w) for n, w in sorted(W.items()) if w))
print('arity: %d functions read argument registers (%d total in the ROM listing)' % (sum(1 for w in W.values() if w), len(asm)))
