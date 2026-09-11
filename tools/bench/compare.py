#!/usr/bin/env python3
"""Compare two benchmark runs and flag regressions.

    tools/bench/compare.py before.json after.json [--tolerance 0.10]

Exits 1 if any case is slower than the tolerance allows, so this can gate a change.
A single run on a laptop is noisy; treat anything under about 10% as noise.
"""
import json, sys

def load(path):
    cases = {}
    for line in open(path):
        line = line.strip()
        if not line:
            continue
        d = json.loads(line)
        if "case" in d:
            cases[d["case"]] = d["seconds"]
    return cases

def main():
    args = [a for a in sys.argv[1:] if not a.startswith("--")]
    tol = 0.10
    for i, a in enumerate(sys.argv):
        if a == "--tolerance":
            tol = float(sys.argv[i + 1])
    if len(args) != 2:
        print(__doc__)
        return 2

    before, after = load(args[0]), load(args[1])
    worse = []
    print(f"{'case':<22} {'before':>9} {'after':>9} {'change':>9}")
    for name in sorted(set(before) | set(after)):
        b, a = before.get(name), after.get(name)
        if b is None or a is None:
            print(f"{name:<22} {'-' if b is None else b:>9} {'-' if a is None else a:>9} {'new/gone':>9}")
            continue
        delta = (a - b) / b if b else 0.0
        flag = "  SLOWER" if delta > tol else ""
        print(f"{name:<22} {b:>9.2f} {a:>9.2f} {delta*100:>8.1f}%{flag}")
        if delta > tol:
            worse.append(name)
    if worse:
        print(f"\n{len(worse)} case(s) slower than {tol*100:.0f}%: {', '.join(worse)}")
        return 1
    return 0

if __name__ == "__main__":
    sys.exit(main())
