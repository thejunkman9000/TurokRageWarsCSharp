#!/usr/bin/env python3
"""Compares two oracle recordings (files or run.sh output directories).

usage: compare.py <a> <b> [-v] [--max N]
Reports, per stream (graphics frames F, audio blocks A, game-state ticks), how many entries matched and the first divergence.
-v additionally uses the optional side files (<file>.detail, .frames, .ticks, recorded with ORACLE_DETAIL / ORACLE_DUMP_FRAME / ORACLE_DUMP_TICK) to show
which command, which DrawState field, which vertex or which RAM word differs.
Exit status 0 = identical, 1 = different.
"""
import argparse, os, re, sys


def path_of(p):
    return os.path.join(p, "oracle.txt") if os.path.isdir(p) else p


def load(p):
    streams = {"F": [], "A": [], "T": []}
    notes = []
    with open(p) as f:
        for n, line in enumerate(f, 1):
            line = line.rstrip("\n")
            if line.startswith("F "):
                streams["F"].append((n, line))
            elif line.startswith("A "):
                streams["A"].append((n, line))
            elif line.startswith("[tick "):
                streams["T"].append((n, line))
            elif line.startswith("#"):
                notes.append(line)
    return streams, notes


def fields(line):
    return dict(re.findall(r"(\w+)=(\S+)", line))


def side(p, ext):
    q = p + ext
    if ext == ".detail" and not os.path.exists(q) and os.path.exists(q + ".gz"):
        return q + ".gz"
    return q if os.path.exists(q) else None


def opener(p):
    import gzip
    return gzip.open(p, "rt") if p.endswith(".gz") else open(p)


def detail_for(p, frame=None, tick=None):
    """Streams a .detail(.gz) file and returns (command lines of <frame>, region hashes of <tick>); the files can be hundreds of MB."""
    cmds, regions, cur = [], None, None
    with opener(p) as f:
        for line in f:
            if line.startswith("F "):
                cur = int(line.split()[1])
                if frame is not None and cur > frame and (tick is None or regions is not None):
                    break
            elif line.startswith("c "):
                if cur == frame:
                    cmds.append(line.rstrip("\n"))
            elif line.startswith("T ") and tick is not None:
                t = line.split()
                if int(t[1]) == tick:
                    regions = dict(x.split("=") for x in t[2:])
                    if frame is None:
                        break
    return cmds, regions


def explain_frame(pa, pb, idx):
    da, db = side(pa, ".detail"), side(pb, ".detail")
    if da and db:
        ca, _ = detail_for(da, frame=idx); cb, _ = detail_for(db, frame=idx)
        print(f"    commands: a={len(ca)} b={len(cb)}")
        for i in range(max(len(ca), len(cb))):
            la = ca[i] if i < len(ca) else None
            lb = cb[i] if i < len(cb) else None
            if la == lb:
                continue
            print(f"    first differing command: index {i}")
            if la is None or lb is None:
                print(f"      only in {'a' if lb is None else 'b'}: {la or lb}")
                break
            ta, tb = la.split(), lb.split()
            print(f"      kind a={ta[2]} b={tb[2]}")
            ka = dict(x.split("=", 1) for x in ta[3:] if "=" in x); kb = dict(x.split("=", 1) for x in tb[3:] if "=" in x)
            for k in ka:
                if ka.get(k) != kb.get(k):
                    print(f"      field {k}: a={ka.get(k)} b={kb.get(k)}")
            break
    else:
        print("    (no .detail files: rerun both with ORACLE_DETAIL=1 for the command/field diff)")
    xa, xb = side(pa, ".frames"), side(pb, ".frames")
    if xa and xb:
        def vs(p):
            d, on = {}, False
            for line in open(p):
                if line.startswith("F "): on = int(line.split()[1]) == idx
                elif on and line.startswith("v "):
                    _, c, i, *vals = line.split(); d[(int(c), int(i))] = vals
            return d
        va, vb = vs(xa), vs(xb)
        names = "X Y Z W R G B A U V".split()
        for key in sorted(set(va) | set(vb)):
            if va.get(key) != vb.get(key):
                a, b = va.get(key), vb.get(key)
                print(f"    first differing vertex: command {key[0]} vertex {key[1]}")
                if a and b:
                    for n, x, y in zip(names, a, b):
                        if x != y:
                            import struct
                            fx = struct.unpack(">f", bytes.fromhex(x))[0]; fy = struct.unpack(">f", bytes.fromhex(y))[0]
                            print(f"      {n}: a={fx!r} ({x}) b={fy!r} ({y})")
                break
        else:
            print("    vertices identical in the dump (difference is in the state or the command list)")
    else:
        print(f"    (for vertex level: rerun with ORACLE_DUMP_FRAME={idx} on both)")


def explain_tick(pa, pb, tick):
    da, db = side(pa, ".detail"), side(pb, ".detail")
    if da and db:
        _, a = detail_for(da, tick=tick); _, b = detail_for(db, tick=tick)
        if a and b:
            for k in a:
                if a[k] != b.get(k):
                    print(f"    region {k} differs (a={a[k]} b={b[k]})")
    xa, xb = side(pa, ".ticks"), side(pb, ".ticks")
    if xa and xb:
        def ws(p):
            d = {}
            for line in open(p):
                t, addr, w = line.split()
                if int(t) == tick: d[addr] = w
            return d
        wa, wb = ws(xa), ws(xb)
        diff = [(k, wa.get(k), wb.get(k)) for k in sorted(set(wa) | set(wb)) if wa.get(k) != wb.get(k)]
        print(f"    {len(diff)} differing words at tick {tick}; first: " + ", ".join(f"{k}: {x} vs {y}" for k, x, y in diff[:8]))
    else:
        print(f"    (for RAM words: rerun with ORACLE_DUMP_TICK={tick} on both)")


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("a"); ap.add_argument("b"); ap.add_argument("-v", "--verbose", action="store_true")
    ap.add_argument("--max", type=int, default=1, help="report this many divergences per stream")
    o = ap.parse_args()
    pa, pb = path_of(o.a), path_of(o.b)
    sa, na = load(pa); sb, nb = load(pb)
    names = {"F": "frames", "A": "audio blocks", "T": "ticks"}
    first = []   # (line in a, text)
    bad = False
    for k in "FAT":
        a, b = sa[k], sb[k]
        n = min(len(a), len(b))
        same = 0
        divs = []
        for i in range(n):
            if a[i][1] == b[i][1]:
                same += 1
            elif len(divs) < o.max:
                divs.append(i)
        status = "identical" if not divs and len(a) == len(b) else "DIFFERENT"
        print(f"{names[k]}: a={len(a)} b={len(b)} compared={n} matching={same} -> {status}")
        if status != "identical":
            bad = True
        if len(a) != len(b):
            print(f"  length differs: a has {len(a)}, b has {len(b)}")
        for i in divs:
            la, lb = a[i][1], b[i][1]
            print(f"  first divergence: {names[k][:-1]} #{i} (line {a[i][0]} of a, {b[i][0]} of b)")
            print(f"    a: {la}\n    b: {lb}")
            fa, fb = fields(la), fields(lb)
            diff = [x for x in fa if fa.get(x) != fb.get(x)]
            if diff:
                print("    differing fields: " + ", ".join(f"{x} ({fa.get(x)} vs {fb.get(x)})" for x in diff))
            if k == "F" and o.verbose:
                explain_frame(pa, pb, int(la.split()[1]))
            if k == "T" and o.verbose:
                explain_tick(pa, pb, int(re.match(r"\[tick (\d+)\]", la).group(1)))
            first.append((a[i][0], f"{names[k][:-1]} #{i}"))
    if na != nb:
        print("notes differ:", na[:3], nb[:3]); bad = True
    if first:
        print("earliest divergence in recording order:", min(first)[1])
    print("RESULT:", "DIFFERENT" if bad else f"IDENTICAL ({len(sa['F'])} frames, {len(sa['A'])} audio blocks, {len(sa['T'])} ticks)")
    return 1 if bad else 0


sys.exit(main())
