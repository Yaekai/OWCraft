#!/usr/bin/env python3
"""
OWCraft log check: reads the latest Outer Wilds (OWML) log and Minecraft's latest.log and lists the
known problems, so a play test can be judged in seconds.

    python tools/check-logs.py                 # newest logs in the default places
    python tools/check-logs.py --owml FILE --mc FILE

Default places: %AppData%/OuterWildsModManager/OWML/Logs/OWML.Log.*.txt (newest) and
%AppData%/.minecraft/logs/latest.log. Exit code 1 if something looks wrong.
"""
import argparse
import glob
import os
import re
import sys


def num(text):
    # The logs use the machine's locale: "7,7 ms" on a French Windows.
    return float(text.replace(",", "."))


def newest(pattern):
    files = glob.glob(pattern)
    return max(files, key=os.path.getmtime) if files else None


def read(path):
    with open(path, encoding="utf-8", errors="replace") as f:
        return f.read().splitlines()


def check_host(lines, problems, notes):
    resyncs = [l for l in lines if "off this tile" in l]
    tiles = sum("tile change" in l for l in lines)
    modes = sum("Minecraft mode on" in l for l in lines)
    notes.append(f"host: {modes} Minecraft mode sessions, {tiles} tile changes")
    if resyncs:
        problems.append(f"host: {len(resyncs)} 'moved off this tile' resyncs (each one freezes the player). First: {resyncs[0][:160]}")

    # A -> B then straight back B -> A: the player sits on a tile edge and every change carries the
    # world back and forth (it shakes).
    changes = [re.search(r"tile change (.+) -> (.+)$", l) for l in lines if "tile change" in l]
    changes = [(m.group(1), m.group(2)) for m in changes if m]
    flips = sum(1 for a, b in zip(changes, changes[1:]) if a[0] == b[1] and a[1] == b[0])
    if flips >= 3:
        problems.append(f"host: {flips} tile changes straight back to the previous tile (the world shakes)")

    # Errors with our code in the stack. The game's own OnDestroy/OnDisable errors when it quits are
    # its own and harmless.
    ours = 0
    first = None
    for i, l in enumerate(lines):
        if "Error:" not in l and "Exception" not in l:
            continue
        # The error line and its stack trace, up to the next log entry (which may be ours).
        stack = [l]
        for nxt in lines[i + 1:i + 30]:
            if re.match(r"\d\d/\d\d/\d{4} ", nxt):
                break
            stack.append(nxt)
        if re.search(r"OWCraft\.\w", " ".join(stack)):
            ours += 1
            first = first or l[:200]
    if ours:
        problems.append(f"host: {ours} errors in OWCraft code. First: {first}")

    tick_age = [num(m) for l in lines for m in re.findall(r"worst tick age ([\d.,]+) ms", l)]
    over50 = [int(m) for l in lines for m in re.findall(r"frames: \d+, (\d+) over 50 ms", l)]
    worst_frame = [num(m) for l in lines for m in re.findall(r"over 50 ms, worst ([\d.,]+) ms", l)]
    mod_worst = [num(m) for l in lines for m in re.findall(r"ms avg / ([\d.,]+) ms worst \(player", l)]
    side = [num(m) for l in lines for m in re.findall(r"side samples ([\d.,]+) ms", l)]
    mc_fps = [num(m) for l in lines for m in re.findall(r"Minecraft ([\d.,]+) fps", l)]
    if tick_age:
        notes.append(f"host: worst tick age {max(tick_age):.0f} ms (normal is under ~150)")
        big = [a for a in tick_age if a > 1000]
        if big:
            problems.append(f"host: {len(big)} stats windows with a tick age over 1 s (Minecraft stalled or paused)")
    if over50:
        notes.append(f"host: {sum(over50)} frames over 50 ms, worst frame {max(worst_frame or [0]):.0f} ms, worst mod time {max(mod_worst or [0]):.0f} ms")
    if side:
        notes.append(f"host: side-sample collision up to {max(side):.0f} ms per stats window")
    if mc_fps:
        low = min(mc_fps)
        notes.append(f"host: Minecraft {low:.0f}-{max(mc_fps):.0f} fps")
        if low < 30:
            problems.append(f"host: Minecraft dropped to {low:.0f} fps")
    inv = sum("player is invincible" in l for l in lines)
    if inv:
        notes.append(f"host: invincibility turned on {inv} times (Creative/Spectator)")


def check_guest(lines, problems, notes):
    shifts = sum("tile shift by" in l for l in lines)
    teleports = sum("SkyCraft: teleported to" in l for l in lines)
    carried = [int(m) for l in lines for m in re.findall(r"carried (\d+) entities", l)]
    downs = sum("link down" in l for l in lines)
    notes.append(f"guest: {shifts} tile shifts, {teleports} full teleports, {sum(carried)} entities carried, link lost {downs} times")
    # A full teleport right after a tile shift is a held teleport mid-air: a freeze.
    for i, l in enumerate(lines):
        if "tile shift by" in l:
            later = " ".join(lines[i + 1:i + 4])
            if "teleported to" in later:
                problems.append(f"guest: a full teleport right after a tile shift (a mid-air freeze): {l[:120]}")
                break

    work = [num(m) for l in lines for m in re.findall(r"/ ([\d.,]+) ms worst, pacing", l)]
    gap = [num(m) for l in lines for m in re.findall(r"tick gap ([\d.,]+) ms worst", l)]
    if work:
        notes.append(f"guest: worst frame work {max(work):.0f} ms, worst tick gap {max(gap or [0]):.0f} ms")
    # Loading the world always falls behind once; after the first teleport it means a real stall.
    started = next((i for i, l in enumerate(lines) if "SkyCraft: teleported to" in l), len(lines))
    behind = sum("Can't keep up" in l for l in lines[started:])
    if behind:
        problems.append(f"guest: the server fell behind {behind} times during play ('Can't keep up')")

    errors = [l for l in lines if re.search(r"/(ERROR|FATAL)\]", l) or ("/WARN]" in l and "SkyCraft" in l)]
    if errors:
        problems.append(f"guest: {len(errors)} errors or SkyCraft warnings. First: {errors[0][:200]}")
    mixin = [l for l in lines if "Mixin" in l and ("fail" in l.lower() or "error" in l.lower())]
    if mixin:
        problems.append(f"guest: mixin failure: {mixin[0][:200]}")


def main():
    appdata = os.environ.get("APPDATA", "")
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("--owml", default=newest(os.path.join(appdata, "OuterWildsModManager", "OWML", "Logs", "OWML.Log.*.txt")))
    ap.add_argument("--mc", default=os.path.join(appdata, ".minecraft", "logs", "latest.log"))
    args = ap.parse_args()

    problems, notes = [], []
    for name, path, check in (("Outer Wilds", args.owml, check_host), ("Minecraft", args.mc, check_guest)):
        if not path or not os.path.exists(path):
            notes.append(f"{name} log not found")
            continue
        notes.append(f"{name} log: {os.path.basename(path)}")
        check(read(path), problems, notes)

    for n in notes:
        print("  " + n)
    print()
    if problems:
        print(f"{len(problems)} problem(s):")
        for p in problems:
            print("  - " + p)
        return 1
    print("No known problems.")
    return 0


if __name__ == "__main__":
    sys.exit(main())
