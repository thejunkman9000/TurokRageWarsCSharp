"""Call graph and data use of the generated code, for naming work (docs/NAMING.md).

Writes tools/names/graph.tsv: one row per registered function, in ROM order:
  orig  address  name  level  callers  callees  data
    level   = height in the call graph (0 = calls no other game function; a cycle shares one level), so naming can go bottom-up
    callers = original names of the functions that call it (direct calls; calls through function pointers are not visible)
    callees = original names it calls
    data    = the guest data addresses (KSEG0 0x80xxxxxx literals) its code uses, sorted
and tools/names/clusters.tsv: ROM-contiguous runs of functions that are linked to a neighbour (a call either way, or shared data addresses), a rough
stand-in for the original source files (the ROM keeps no file boundaries: no padding, no file names).

Usage: python tools/rename/graph.py
"""
import os, re, sys
sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import apply_names as an

DATA = re.compile(r'0x(80[0-9A-Fa-f]{6})[uU]?')


def main():
    addr = an.registry()
    applied = an.load_applied()
    cur2orig = {c: o for o, c in applied.items()}
    known = set(addr) | set(cur2orig)
    members = an.collect_members(addr, cur2orig)
    body = {}
    for m in members:
        body.setdefault(m['orig'], []).append(m['text'])
    callees, data = {}, {}
    for o, texts in body.items():
        t = '\n'.join(texts)
        ids = an.identifiers(t)
        cs = {cur2orig.get(i, i) for i in ids if i in known}
        cs.discard(o)
        callees[o] = cs
        data[o] = sorted({int(x, 16) for code, seg in an.segments(t) if code for x in DATA.findall(seg)})
    callers = {o: set() for o in addr}
    for o, cs in callees.items():
        for c in cs:
            if c in callers: callers[c].add(o)
    # levels: longest path to a leaf; strongly connected components share a level (iterative Tarjan)
    nodes = [o for o in addr if o in callees]
    index, low, onstack, stack, comp = {}, {}, set(), [], {}
    counter = [0]; comps = []
    for root in nodes:
        if root in index: continue
        work = [(root, iter(sorted(callees.get(root, ()))))]
        index[root] = low[root] = counter[0]; counter[0] += 1; stack.append(root); onstack.add(root)
        while work:
            v, it = work[-1]
            nxt = next((w for w in it if w in callees), None)
            if nxt is not None:
                if nxt not in index:
                    index[nxt] = low[nxt] = counter[0]; counter[0] += 1; stack.append(nxt); onstack.add(nxt)
                    work.append((nxt, iter(sorted(callees.get(nxt, ())))))
                elif nxt in onstack: low[v] = min(low[v], index[nxt])
                continue
            work.pop()
            if work: low[work[-1][0]] = min(low[work[-1][0]], low[v])
            if low[v] == index[v]:
                c = []
                while True:
                    w = stack.pop(); onstack.discard(w); comp[w] = len(comps); c.append(w)
                    if w == v: break
                comps.append(c)
    level_c = [0] * len(comps)
    for ci, c in enumerate(comps):                                           # Tarjan emits components callees-first
        lv = 0
        for v in c:
            for w in callees.get(v, ()):
                if w in comp and comp[w] != ci: lv = max(lv, level_c[comp[w]] + 1)
        level_c[ci] = lv
    order = sorted(addr, key=lambda o: (addr[o], o))
    out = ['# orig\taddress\tname\tlevel\tcallers\tcallees\tdata   (written by tools/rename/graph.py)']
    for o in order:
        out.append('\t'.join([o, '0x%08X' % addr[o], applied.get(o, ''), str(level_c[comp[o]]) if o in comp else '-',
                              ','.join(sorted(callers.get(o, ()))), ','.join(sorted(callees.get(o, ()))), ','.join('%08X' % d for d in data.get(o, []))]))
    an.write_if_changed(os.path.join(an.REPO, 'tools', 'names', 'graph.tsv'), '\n'.join(out) + '\n')
    # clusters: contiguous ROM runs. Two functions up to WINDOW apart are linked by a direct call either way, a shared rarely used callee, or shared rarely used
    # data; a link merges everything between them (a source file is contiguous in the ROM).
    WINDOW = 4
    users = {}
    for o in order:
        for d in set(data.get(o, [])): users.setdefault(d, []).append(addr[o])
    def dense(us):                                                           # the 0x3000-byte ROM window holding most of a datum's users: its source file
        us = sorted(us); best = (0, us[0]); j = 0
        for i in range(len(us)):
            while us[i] - us[j] >= 0x3000: j += 1
            if i - j + 1 > best[0]: best = (i - j + 1, us[j])
        return best[1], best[1] + 0x3000
    win = {d: dense(us) for d, us in users.items()}
    # data ties two functions when both sit in the densest window of its users (a static of one source file; users elsewhere are other files). Globals
    # spread thinly over the whole game (the game state, the player table...) have no dense window worth the name
    def rare_d(d, a, b):
        lo, hi = win[d]; inside = sum(1 for u in users[d] if lo <= u < hi)
        return lo <= addr[a] < hi and lo <= addr[b] < hi and (inside * 2 >= len(users[d]) or inside >= 6)
    rare_c = lambda c: len(callers.get(c, ())) <= 6
    def linked(a, b):
        if b in callees.get(a, ()) or a in callees.get(b, ()): return True
        if any(rare_c(c) for c in callees.get(a, set()) & callees.get(b, set())): return True
        return any(rare_d(d, a, b) for d in set(data.get(a, [])) & set(data.get(b, [])))
    reach = list(range(len(order)))                                          # reach[i] = furthest index i is merged with
    for i in range(len(order)):
        for j in range(i + 1, min(i + 1 + WINDOW, len(order))):
            if addr[order[j]] - addr[order[i]] < 0x4000 and linked(order[i], order[j]): reach[i] = max(reach[i], j)
    clusters, cur, far = [], [], -1
    for i, o in enumerate(order):
        if cur and i > far: clusters.append(cur); cur = []
        cur.append(o); far = max(far, reach[i])
    clusters.append(cur)
    out = ['# cluster\tfirst\tlast\tcount\tfunctions   (written by tools/rename/graph.py)']
    for i, c in enumerate(clusters):
        out.append('\t'.join([str(i), '0x%08X' % addr[c[0]], '0x%08X' % addr[c[-1]], str(len(c)), ','.join(c)]))
    an.write_if_changed(os.path.join(an.REPO, 'tools', 'names', 'clusters.tsv'), '\n'.join(out) + '\n')
    sizes = sorted(len(c) for c in clusters)
    lv = {}
    for o in order:
        if o in comp: lv[level_c[comp[o]]] = lv.get(level_c[comp[o]], 0) + 1
    print(f'{len(order)} functions; levels: ' + ' '.join(f'{k}:{v}' for k, v in sorted(lv.items())[:12]) + (' ...' if len(lv) > 12 else ''))
    print(f'{len(clusters)} clusters; singletons {sum(1 for s in sizes if s == 1)}; median {sizes[len(sizes)//2]}; largest {sizes[-5:]}')


if __name__ == '__main__':
    main()
