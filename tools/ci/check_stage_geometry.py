"""
Guards art <-> collider drift: design/gdd/stages/stage-geometry.json (art-measured) must match the platform table in
Assets/ChibiClash/Scripts/Netcode/ChibiStageTable.cs for stages 3..7 within TOL milli.

    python tools/ci/check_stage_geometry.py [--tol 2]

Compares per stage: main half-width, and every non-MAIN platform (cx, top, halfW) in order. Moving platforms: the JSON
holds the art "home" position, i.e. Y-movers top = Max, X-movers cx = Min (see ChibiStageTable comments).
Exit 0 = consistent, 1 = mismatch or parse failure.
"""
import json
import os
import re
import sys

ROOT = os.path.abspath(os.path.join(os.path.dirname(__file__), "..", ".."))
JSON_PATH = os.path.join(ROOT, "design", "gdd", "stages", "stage-geometry.json")
CS_PATH = os.path.join(ROOT, "Assets", "ChibiClash", "Scripts", "Netcode", "ChibiStageTable.cs")
CONSTS = {"MainBottom": -1200, "X": 1, "Y": 2, "KillY": -9725, "CeilingY": 17775, "SpawnY": 775, "RespawnY": 7775,
          "SkyCapY": 16000, "true": 1, "false": 0}
STAGES = range(3, 8)


def val(tok):
    tok = tok.strip()
    if tok in CONSTS:
        return CONSTS[tok]
    if re.fullmatch(r"-?\d+", tok):
        return int(tok)
    raise ValueError("unparsable token %r" % tok)


def parse_cs(text):
    body = text[text.index("Stages ="):]
    chunks = body.split("new ChibiStageDef(")[1:]
    stages = []
    for ch in chunks:
        ch = re.sub(r"//[^\n]*", "", ch)  # strip comments
        main = val(ch.split(",")[0])
        plats = []
        for m in re.finditer(r"new ChibiPlatform\(([^()]*)\)", ch):
            a = [val(t) for t in m.group(1).split(",")]
            if len(a) == 3:
                plats.append({"cx": a[0], "top": a[1], "halfW": a[2], "axis": 0})
            else:
                cx, top, hw, axis, mn, mx = a[:6]
                plats.append({"cx": mn if axis == 1 else cx, "top": mx if axis == 2 else top, "halfW": hw, "axis": axis})
        stages.append({"half": main, "plats": plats})
    return stages


def main():
    tol = 2
    if "--tol" in sys.argv:
        tol = int(sys.argv[sys.argv.index("--tol") + 1])
    try:
        data = json.load(open(JSON_PATH, encoding="utf-8"))["stages"]
    except Exception as e:
        print("FAIL: cannot parse %s: %s" % (JSON_PATH, e))
        return 1
    try:
        cs = parse_cs(open(CS_PATH, encoding="utf-8-sig").read())
    except Exception as e:
        print("FAIL: cannot parse %s: %s" % (CS_PATH, e))
        return 1
    by_idx = {s["stageIndex"]: (n, s) for n, s in data.items()}
    bad = []
    for i in STAGES:
        if i not in by_idx or i >= len(cs):
            bad.append("stage %d missing in json or C# table" % i)
            continue
        name, js = by_idx[i]
        c = cs[i]
        jp = js["platforms"]
        main_p = [p for p in jp if p["id"] == "MAIN"]
        others = [p for p in jp if p["id"] != "MAIN"]
        if main_p and abs(main_p[0]["halfWMilli"] - c["half"]) > tol:
            bad.append("%s(%d) main halfW json %d vs cs %d" % (name, i, main_p[0]["halfWMilli"], c["half"]))
        if len(others) != len(c["plats"]):
            bad.append("%s(%d) platform count json %d vs cs %d" % (name, i, len(others), len(c["plats"])))
            continue
        for jpl, cp in zip(others, c["plats"]):
            for k, jk in (("cx", "cxMilli"), ("top", "topMilli"), ("halfW", "halfWMilli")):
                if abs(jpl[jk] - cp[k]) > tol:
                    bad.append("%s(%d) %s.%s json %d vs cs %d" % (name, i, jpl["id"], k, jpl[jk], cp[k]))
    for b in bad:
        print("MISMATCH:", b)
    print("stage geometry: %s (tol %d milli, stages 3..7)" % ("FAILED" if bad else "OK", tol))
    return 1 if bad else 0


if __name__ == "__main__":
    sys.exit(main())
