"""Compare two scripted battle runs (tools/run_battle.sh A / B) for determinism.

Same save + same Shadow + same commands + same BattleRng seed should give the same turn order, the same enemy
choices and the same number of rolls per battle call site. Prints the first divergence.
Usage: python compare_runs.py A B
"""
import re
import sys

R = "C:/Users/mbasd/P3R-Coop/recon/"


def load(label):
    coop = open(f"{R}run_{label}_coop.log", encoding="utf-8", errors="replace").read().splitlines()
    cmds = []
    for line in coop:
        m = re.search(r"DoCommand b(\d+) n(\d+) (\S+) type=(\d+) id=(\d+) targets=(\S+)", line)
        if m and int(m.group(1)) == 1:
            cmds.append(" ".join(m.groups()[1:]))
    sites = {}
    for line in coop:
        m = re.search(r"BattleRng disarmed; battle sites: (.*)$", line)
        if m:
            for tok in re.findall(r"\+0x([0-9A-F]+) x(\d+)", m.group(1)):
                sites[tok[0]] = int(tok[1])
            break
    armed = [l for l in coop if "BattleRng" in l]
    return cmds, sites, armed


def main():
    a, b = sys.argv[1], sys.argv[2]
    ca, sa, aa = load(a)
    cb, sb, ab = load(b)
    print(f"run {a}: {len(ca)} commands, {len(sa)} battle sites; run {b}: {len(cb)} commands, {len(sb)} battle sites")
    for l in aa[:3] + ab[:3]:
        print("   ", l[:150])
    n = min(len(ca), len(cb))
    first = next((i for i in range(n) if ca[i] != cb[i]), None)
    if first is None and len(ca) == len(cb):
        print("COMMANDS: identical turn by turn")
    else:
        i = first if first is not None else n
        print(f"COMMANDS: diverge at command #{i + 1}")
        for j in range(max(0, i - 2), min(i + 3, max(len(ca), len(cb)))):
            print(f"  {j + 1:3}  {a}: {ca[j] if j < len(ca) else '-':40}  {b}: {cb[j] if j < len(cb) else '-'}")
    keys = sorted(set(sa) | set(sb))
    diff = [(k, sa.get(k), sb.get(k)) for k in keys if sa.get(k) != sb.get(k)]
    print("ROLLS PER SITE:", "identical" if not diff and keys else f"{len(diff)} sites differ")
    for k, x, y in diff[:15]:
        print(f"  +0x{k}: {a}={x} {b}={y}")


if __name__ == "__main__":
    main()
