#!/usr/bin/env python3
"""Finds functions whose C prototype returns void but whose callers in the ROM use the result register.

A matching C decompilation can end a function with a call whose value the original function simply leaves in $v0/$f0 (`void f(...) { g(...); }` when the
ROM's f returns g's result): the C draft carries no result, so the translation loses it, and a caller that reads $v0/$f0 after `jal f` computes with garbage.
The functions listed here are recompiled from the MIPS (tools/regen.ps1 appends them to the void-used list), which keeps the register contents exact.

usage: retuse.py --asm <dir with *.s> --sigs <name|ret|params|...> --out <file with name|type lines>
A call counts as using the result when, after `jal f` (and its delay slot) and before the next call/jump/branch target, $v0 or $fv0 is read before it is
written. A `jr $ra` directly after counts when the calling function itself returns a value (or has been flagged: the check repeats until nothing changes),
which finds chains of tail calls."""
import argparse, glob, os, re

ap = argparse.ArgumentParser()
ap.add_argument('--asm', required=True)
ap.add_argument('--sigs', required=True)
ap.add_argument('--out', required=True)
args = ap.parse_args()

ret_of = {}
for line in open(args.sigs, encoding='utf-8'):
    p = line.rstrip('\n').split('|')
    if len(p) >= 2 and p[0]: ret_of[p[0]] = p[1]

DEST_FIRST = set('''addu addiu add addi subu sub and andi or ori xor xori nor sll srl sra sllv srlv srav slt sltu slti sltiu lui lw lb lbu lh lhu lwc1 ldc1 lwl lwr
mflo mfhi mfc1 dmfc1 li move la mtc1 dmtc1 mfc0 cfc1 daddu daddiu dsubu dsll dsrl dsra dsll32 dsrl32 dsra32 ld lld ll movz movn
add.s sub.s mul.s div.s mov.s neg.s abs.s sqrt.s add.d sub.d mul.d div.d mov.d neg.d abs.d sqrt.d cvt.s.w cvt.s.d cvt.d.w cvt.d.s cvt.w.s cvt.w.d
trunc.w.s trunc.w.d round.w.s round.w.d floor.w.s floor.w.d ceil.w.s ceil.w.d cvt.s.l cvt.d.l trunc.l.s trunc.l.d'''.split())
BRANCH = re.compile(r'^(b|beq|bne|beqz|bnez|bgez|bgtz|blez|bltz|bgezal|bltzal|bc1t|bc1f|bc1tl|bc1fl|beql|bnel|beqzl|bnezl|bgezl|bgtzl|blezl|bltzl|j|jr|jal|jalr)$')
REG = re.compile(r'\$(\w+)')
V0 = {'v0', '2'}
F0 = {'fv0', 'f0', 'fv0f', 'f1'}

def parse(line):
    line = re.sub(r'/\*.*?\*/', '', line).strip()
    if not line or line.startswith('.') and line.endswith(':') or line.startswith('#'): return None
    m = re.match(r'^([a-z0-9_.]+)\s*(.*)$', line)
    if not m: return None
    return m.group(1), [o.strip() for o in m.group(2).split(',')] if m.group(2) else []

def regs(op):
    return REG.findall(op)

def scan(body):
    """body: list of (kind, text) with kind 'ins' (mnemonic, ops) or 'label'. Returns ({callee: types read after the call}, {callee: types returned through `jr $ra`})."""
    found = {}; tails = {}
    n = len(body)
    for i, (kind, ins) in enumerate(body):
        if kind != 'ins' or ins[0] != 'jal': continue
        callee = ins[1][0]
        v0w = f0w = False; res = None
        j = i + 2                    # i + 1 is the delay slot: it runs before the callee
        steps = 0
        stop_after = None            # stop once the delay slot of a branch/jump was handled
        while j < n and steps < 40:
            k, x = body[j]
            if k == 'label': break
            mn, ops = x
            steps += 1
            reads, writes = [], []
            if mn in DEST_FIRST and ops:
                writes = regs(ops[0]); reads = [r for o in ops[1:] for r in regs(o)]
                if mn in ('lwl', 'lwr', 'movz', 'movn'): reads += writes
                if mn == 'mtc1' or mn == 'dmtc1': reads = regs(ops[0]); writes = [r for o in ops[1:] for r in regs(o)]
            elif mn == 'jal' or mn == 'jalr':
                # arguments of the next call may be the result being forwarded ($v0 as a0 is a move, which would be seen as a read first)
                reads = [r for o in ops for r in regs(o)]
            else:
                reads = [r for o in ops for r in regs(o)]
            if mn == 'jr' and ops and ops[0] == '$ra':
                if not v0w: tails.setdefault(callee, set()).add('int')
                if not f0w: tails.setdefault(callee, set()).add('float')
                break
            for r in reads:
                if r in V0 and not v0w: res = res or 'int'
                if r in F0 and not f0w:
                    res = res or ('double' if (mn.endswith('.d') or mn in ('sdc1', 'cvt.s.d', 'cvt.w.d', 'trunc.w.d')) else 'float')
            for r in writes:
                if r in V0: v0w = True
                if r in F0: f0w = True
            if res: break
            if mn in ('jal', 'jalr'): break
            if BRANCH.match(mn):
                if stop_after is None:
                    stop_after = j + 1
                    j += 1
                    continue
            if stop_after is not None and j >= stop_after: break
            j += 1
        if res: found.setdefault(callee, set()).add(res)
    return found, tails

funcs = {}           # name -> body
cur = None
for path in sorted(glob.glob(os.path.join(args.asm, '*.s'))):
    for line in open(path, encoding='utf-8', errors='replace'):
        s = line.strip()
        m = re.match(r'^(glabel|alabel)\s+(\S+)', s)
        if m:
            cur = m.group(2); funcs.setdefault(cur, []); continue
        if s.startswith('endlabel'): cur = None; continue
        if cur is None: continue
        if re.match(r'^\.L\w+:', s): funcs[cur].append(('label', s)); continue
        p = parse(line)
        if p: funcs[cur].append(('ins', p))

scans = {name: scan(body) for name, body in funcs.items()}
used = {}                                   # callee -> result types some caller reads
for name, (direct, tails) in scans.items():
    for callee, types in direct.items(): used.setdefault(callee, set()).update(types)
changed = True
rounds = 0
while changed and rounds < 12:
    changed = False; rounds += 1
    for name, (direct, tails) in scans.items():
        if name not in used: continue        # a tail call only forwards the result when the caller's own result is used
        want = used[name]
        for callee, types in tails.items():
            # the forwarded kind: $v0 when the caller's result is an integer, $f0 for floats
            t = set()
            if 'int' in want and 'int' in types: t.add('int')
            if ({'float', 'double'} & want) and 'float' in types: t.update(want & {'float', 'double'})
            if t - used.get(callee, set()): used.setdefault(callee, set()).update(t); changed = True

flagged = {}
for callee, types in used.items():
    if ret_of.get(callee) == 'void': flagged[callee] = 'double' if 'double' in types else 'float' if 'float' in types else 'int'

with open(args.out, 'w', encoding='utf-8', newline='\n') as f:
    for name in sorted(flagged):
        f.write(f'{name}|{flagged[name]}\n')
print(f'{len(flagged)} functions return a value in the ROM that the C prototype (void) drops, in {rounds} rounds -> {args.out}')
