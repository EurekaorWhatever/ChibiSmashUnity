"""
Generate the moveset data for the deterministic simulation from the design table.

    python tools/gameplay/gen_movesets.py            # writes Assets/ChibiClash/Scripts/Netcode/Movesets/ChibiMovesetData.g.cs
    python tools/gameplay/gen_movesets.py --check    # exit 1 if the generated file is out of date (CI / pre-commit)

Source of truth: design/gdd/movesets/movesets.csv (spec: design/gdd/movesets/README.md, rules v18).
Output is deterministic (CSV order inside each class, classes in FighterClass order, LF line endings, no timestamp),
so re-running on the same CSV gives a byte-identical file.

What it writes (all constant data, nothing mutable):
  * ChibiMovesetData.Extend(t): appends the CSV rows to each class's ChibiMove table as slots 7, 8, ... (slots 0-6 keep
    their meaning). Each new row carries a ChibiMoveExt with the README section 5 mechanics.
  * ChibiMovesetData.Resolve[cls]: input pattern -> slot (README section 1.2), key = ChibiMovesetData.Key(button, dir, air).
  * ChibiMovesetData.DashSlot / DashDownSlot: L@dash and L+D@dash per class.
  * ChibiMoveInfo: presentation metadata (id, name, anim kind, vfx, sfx). The simulation never reads it.
"""
import csv
import os
import sys

ROOT = os.path.abspath(os.path.join(os.path.dirname(__file__), "..", ".."))
CSV_PATH = os.path.join(ROOT, "design", "gdd", "movesets", "movesets.csv")
OUT_PATH = os.path.join(ROOT, "Assets", "ChibiClash", "Scripts", "Netcode", "Movesets", "ChibiMovesetData.g.cs")

CLASSES = ["Melee", "Heavy", "Ranged", "Magic", "Polearm"]          # FighterClass enum order
FIRST_NEW_SLOT = 7
BUTTONS = {"L": 0, "H": 1, "B": 2, "S1": 3, "S2": 4, "S3": 5}
BUTTON_BITS = {0: "ChibiButtons.Light", 1: "ChibiButtons.Heavy", 2: "ChibiButtons.Special",
               3: "ChibiButtons.Skill1", 4: "ChibiButtons.Skill2", 5: "ChibiButtons.Skill3"}
DIRS = {"": 0, "F": 1, "BK": 2, "U": 3, "D": 4}
PROJ_KINDS = {"Arrow", "ArrowBig", "Bolt", "BoltBig", "WindBlade", "GroundWave", "Grapple", "LanceArrow"}

# ChibiMoveExt.Flags bits (must match ChibiMoveExt in Movesets/ChibiMoveExt.cs).
MF = {
    "spike": 1 << 0, "trip": 1 << 1, "back": 1 << 2, "pull": 1 << 3, "counter": 1 << 4, "counterproj": 1 << 5,
    "reflect": 1 << 6, "dive": 1 << 7, "teleport": 1 << 8, "aironce": 1 << 9, "restorejump": 1 << 10,
    "roll": 1 << 11, "pierce": 1 << 12, "tetherself": 1 << 13, "tetherfoe": 1 << 14, "bounce": 1 << 15,
    "pv": 1 << 16, "armor": 1 << 17, "recoil": 1 << 18, "invuln": 1 << 19, "landhit": 1 << 20, "zone": 1 << 21,
    "charge": 1 << 22, "chargelvl": 1 << 23, "hold": 1 << 24, "multi": 1 << 25, "fan": 1 << 26, "circle": 1 << 27,
    "est": 1 << 28,
}
MF_NAMES = {
    "spike": "Spike", "trip": "Trip", "back": "Back", "pull": "Pull", "counter": "Counter",
    "counterproj": "CounterProj", "reflect": "Reflect", "dive": "Dive", "teleport": "Teleport",
    "aironce": "AirOnce", "restorejump": "RestoreJump", "roll": "Roll", "pierce": "Pierce",
    "tetherself": "TetherSelf", "tetherfoe": "TetherFoe", "bounce": "Bounce", "pv": "ExplicitPv", "armor": "Armor",
    "recoil": "Recoil", "invuln": "InvulnWindow", "landhit": "LandHit", "zone": "Zone", "charge": "Charge",
    "chargelvl": "ChargeLevel", "hold": "Hold", "multi": "Multi", "fan": "Fan", "circle": "Circle", "est": "Estimated",
}
ZONE_KINDS = {"contact": 1, "timed": 2, "periodic": 3}

# Sim limits the data must respect (SEC-105). Keep in sync with the C# side; ChibiMovesetDataLimitTests checks them too.
TECH_CD_SLOTS = 6          # ChibiFighterSim.TechCd length
MAX_ROWS_PER_CLASS = 31    # AirUsedMask is an int: bit (slot - 7) must stay below 31
ZONE_POOL = 4              # ChibiMovesetConst.ZonePool, shared by both fighters
MAX_ZONE_ROWS = ZONE_POOL // 2


def die(msg):
    sys.stderr.write("gen_movesets: " + msg + "\n")
    sys.exit(2)


def parse_input(text, rid):
    """'L+D@dash' -> (button, dir, dash)."""
    dash = text.endswith("@dash")
    if dash:
        text = text[: -len("@dash")]
    parts = text.split("+")
    if parts[0] not in BUTTONS:
        die("%s: unknown button in input %r" % (rid, text))
    d = parts[1] if len(parts) > 1 else ""
    if d not in DIRS:
        die("%s: unknown direction in input %r" % (rid, text))
    return BUTTONS[parts[0]], DIRS[d], dash


def to_int(row, col):
    v = row[col].strip()
    if v == "":
        return 0
    try:
        return int(v)
    except ValueError:
        die("%s: column %s is not an integer: %r" % (row["id"], col, v))


def cs_string(s):
    return '"' + s.replace("\\", "\\\\").replace('"', '\\"') + '"'


def parse_flags(row):
    """Returns (flag bits, dict of ext fields, base-ChibiMove flag expressions)."""
    rid = row["id"]
    bits = 0
    ext = {}
    base = []
    raw = row["flags"].strip()
    if raw == "":
        return bits, ext, base
    for item in raw.split("|"):
        parts = item.split(":")
        name, args = parts[0], parts[1:]

        def ints(n):
            if len(args) != n:
                die("%s: flag %s needs %d args" % (rid, name, n))
            try:
                return [int(a) for a in args]
            except ValueError:
                die("%s: flag %s has a non-integer arg" % (rid, item))

        if name == "cd":
            ext["Cooldown"] = ints(1)[0]
        elif name == "techslot":
            # Share the technique cooldown slot of another input (SEC-101: Jade Stance uses Jade Counter's S2+D slot).
            if len(args) != 1:
                die("%s: techslot needs an Sk+U / Sk+D input" % rid)
            tb, td, tdash = parse_input(args[0], rid)
            if tb < 3 or td not in (DIRS["U"], DIRS["D"]) or tdash:
                die("%s: techslot must name a Skill + Up / Down input" % rid)
            ext["TechSlot"] = (tb - 3) * 2 + (0 if td == DIRS["U"] else 1)
        elif name == "charge":
            mx, bonus = ints(2)
            bits |= MF["charge"]
            ext["ChargeMax"], ext["ChargeBonus"] = mx, bonus
        elif name == "chargelvl":
            lvl, mn = ints(2)
            bits |= MF["chargelvl"]
            ext["ChargeLevel"], ext["ChargeLevelMin"] = lvl, mn
        elif name == "hold":
            bits |= MF["hold"]
            ext["HoldMax"] = ints(1)[0]
        elif name == "multi":
            n, r = ints(2)
            bits |= MF["multi"]
            ext["MultiHits"] = n
            ext["Rehit"] = r
        elif name == "armor":
            if len(args) != 3:
                die("%s: armor needs A:B:KBP" % rid)
            bits |= MF["armor"]
            ext["ArmorFrom"] = int(args[0])
            ext["ArmorTo"] = -1 if args[1] == "end" else int(args[1])
            ext["ArmorKbPermille"] = int(args[2])
        elif name == "counter":
            bits |= MF["counter"]
            if args == ["proj"]:
                bits |= MF["counterproj"]
            elif args:
                die("%s: counter takes no args except proj" % rid)
        elif name == "reflect":
            p, w = ints(2)
            bits |= MF["reflect"]
            ext["ReflectPermille"], ext["ReflectMaxMult"] = p, w
        elif name == "zone":
            if len(args) != 3 or args[0] not in ZONE_KINDS:
                die("%s: zone needs kind:A:B" % rid)
            bits |= MF["zone"]
            ext["ZoneKind"] = ZONE_KINDS[args[0]]
            ext["ZoneA"], ext["ZoneB"] = int(args[1]), int(args[2])
        elif name == "slow":
            ext["ZoneSlow"] = ints(1)[0]
        elif name == "dive":
            bits |= MF["dive"]
            ext["DiveMax"] = ints(1)[0]
        elif name == "landhit":
            m, kx, ky, st, w, h = ints(6)
            bits |= MF["landhit"]
            ext.update(LandMult=m, LandKbX=kx, LandKbY=ky, LandStun=st, LandW=w, LandH=h)
        elif name == "teleport":
            bits |= MF["teleport"]
            ext["TeleportDist"] = ints(1)[0]
        elif name == "invuln":
            a, b = ints(2)
            bits |= MF["invuln"]
            ext["InvulnFrom"], ext["InvulnTo"] = a, b
        elif name == "recoil":
            vx, vy = ints(2)
            bits |= MF["recoil"]
            ext["RecoilVx"], ext["RecoilVy"] = vx, vy
        elif name == "tether":
            if len(args) != 3 or args[0] not in ("self", "foe"):
                die("%s: tether needs self|foe:SPD:MAX" % rid)
            bits |= MF["tetherself"] if args[0] == "self" else MF["tetherfoe"]
            ext["TetherSpeed"], ext["TetherMax"] = int(args[1]), int(args[2])
        elif name == "bounce":
            n, m2 = ints(2)
            bits |= MF["bounce"]
            ext["BounceMax"], ext["BounceMult"] = n, m2
        elif name == "rehit":
            ext["Rehit"] = ints(1)[0]
        elif name == "pv":
            vx, vy = ints(2)
            bits |= MF["pv"]
            ext["ProjVx"], ext["ProjVy"] = vx, vy
        elif name == "fan":
            if len(args) != 1:
                die("%s: fan needs v1;v2;..." % rid)
            fx, fy = [], []
            for pair in args[0].split(";"):
                if pair.strip() == "":
                    continue
                a, b = pair.split("/")
                fx.append(int(a))
                fy.append(int(b))
            if not fx:
                die("%s: fan needs at least one velocity" % rid)
            bits |= MF["fan"]
            ext["FanVx"], ext["FanVy"] = fx, fy
        elif name == "pk":
            if len(args) != 1 or args[0] not in PROJ_KINDS:
                die("%s: unknown projectile kind %r" % (rid, item))
            ext["ProjKind"] = args[0]
        elif name in ("spike", "trip", "back", "pull", "aironce", "restorejump", "roll", "pierce", "est"):
            if args:
                die("%s: flag %s takes no args" % (rid, name))
            bits |= MF[name]
        elif name == "centered":
            base.append("ChibiMove.FCentered")
        elif name == "level":
            base.append("ChibiMove.FLevel")
        elif name == "unblockable":
            base.append("ChibiMove.FUnblockable")
        else:
            die("%s: unknown flag %r" % (rid, item))
    return bits, ext, base


def load_rows():
    with open(CSV_PATH, encoding="utf-8", newline="") as fh:
        rows = list(csv.DictReader(fh))
    per_class = {c: [] for c in CLASSES}
    for row in rows:
        cls = row["class"].strip()
        if cls not in per_class:
            die("%s: unknown class %r" % (row["id"], cls))
        per_class[cls].append(row)
    return per_class


def build():
    per_class = load_rows()
    out_rows = {}      # cls -> list of dicts
    resolve = {}       # cls -> list of 60 slots
    dash_slot, dash_down_slot = [], []
    for ci, cls in enumerate(CLASSES):
        table = [-1] * 60
        ds, dds = -1, -1
        rows = []
        seen = {}
        for k, row in enumerate(per_class[cls]):
            slot = FIRST_NEW_SLOT + k
            rid = row["id"]
            btn, d, dash = parse_input(row["input"].strip(), rid)
            ga = row["ground_air"].strip()
            if ga not in ("G", "A", "GA"):
                die("%s: ground_air must be G, A or GA" % rid)
            air_mask = (1 if "G" in ga else 0) | (2 if "A" in ga else 0)
            bits, ext, base = parse_flags(row)
            r = {k2: to_int(row, k2) for k2 in ("startup", "active", "recovery", "mult", "kbx", "kby", "stun",
                                                "box_x", "box_w", "box_y", "box_h", "radius", "proj_speed",
                                                "proj_life", "proj_radius", "self_vx", "self_vy")}
            if r["active"] <= 0:
                die("%s: active must be > 0" % rid)
            if r["mult"] > 3000:
                die("%s: mult %d > 3000 (README 7)" % (rid, r["mult"]))
            if r["radius"] > 0:
                bits |= MF["circle"]
            if "MultiHits" in ext:
                n, rr = ext["MultiHits"], ext["Rehit"]
                if r["active"] < (n - 1) * rr + 1:
                    die("%s: multi needs active >= (N-1)*R+1" % rid)
            if bits & MF["dive"] and ext.get("DiveMax") != r["active"]:
                die("%s: dive MAX must equal active" % rid)
            key = (btn, d, dash, ga)
            if key in seen and not (bits & MF["chargelvl"]):
                die("%s: same input + ground_air as %s" % (rid, seen[key]))
            seen.setdefault(key, rid)
            # Technique cooldown slot (README 1.3): TechCd[k*2 + (U ? 0 : 1)] for Sk+U / Sk+D.
            tech = -1
            if btn >= 3 and d in (DIRS["U"], DIRS["D"]):
                tech = (btn - 3) * 2 + (0 if d == DIRS["U"] else 1)
            if "TechSlot" in ext:
                tech = ext.pop("TechSlot")
            if ext.get("Cooldown", 0) > 0 and tech < 0:
                die("%s: cd on a row without a technique slot (use a Skill+U/D input or techslot:)" % rid)
            if not -1 <= tech < TECH_CD_SLOTS:
                die("%s: technique slot %d outside TechCd[%d]" % (rid, tech, TECH_CD_SLOTS))
            if k >= MAX_ROWS_PER_CLASS:
                die("%s: more than %d rows for %s (AirUsedMask bit)" % (rid, MAX_ROWS_PER_CLASS, cls))
            if "FanVx" in ext:
                rh = ext.get("Rehit", 0)
                shots = 1 if rh <= 0 else (r["active"] - 1) // rh + 1
                if len(ext["FanVx"]) < shots:
                    die("%s: fan has %d velocities for %d shots" % (rid, len(ext["FanVx"]), shots))
            rows.append(dict(slot=slot, row=row, btn=btn, dir=d, dash=dash, air=air_mask, bits=bits, ext=ext,
                             base=base, r=r, tech=tech))
            if dash:
                if btn != 0:
                    die("%s: only Light has a dash variant" % rid)
                if d == DIRS["D"]:
                    dds = slot
                elif d == 0:
                    ds = slot
                else:
                    die("%s: dash input must be L@dash or L+D@dash" % rid)
                continue
            for air in (0, 1):
                if not air_mask & (1 << air):
                    continue
                idx = (btn * 5 + d) * 2 + air
                if bits & MF["chargelvl"] and table[idx] >= 0:
                    continue            # the group's first row is the one the input resolves to
                if table[idx] >= 0:
                    die("%s: resolution clash at key %d" % (rid, idx))
                table[idx] = slot
        # Charge-level groups: consecutive rows sharing input + ground_air.
        for rr in rows:
            if rr["bits"] & MF["chargelvl"]:
                group = [o for o in rows if o["bits"] & MF["chargelvl"] and o["btn"] == rr["btn"]
                         and o["dir"] == rr["dir"] and o["air"] == rr["air"]]
                slots = [o["slot"] for o in group]
                if slots != list(range(slots[0], slots[0] + len(slots))):
                    die("%s: chargelvl rows must be consecutive" % rr["row"]["id"])
                rr["ext"]["ChargeGroupBase"] = slots[0]
                rr["ext"]["ChargeGroupCount"] = len(slots)
        zones = sum(1 for o in rows if o["bits"] & MF["zone"])
        if zones > MAX_ZONE_ROWS:
            die("%s: %d zone rows > %d (ZonePool %d shared by two fighters)" % (cls, zones, MAX_ZONE_ROWS, ZONE_POOL))
        out_rows[cls] = rows
        resolve[cls] = table
        dash_slot.append(ds)
        dash_down_slot.append(dds)
    return out_rows, resolve, dash_slot, dash_down_slot


EXT_ORDER = ["Cooldown", "ChargeMax", "ChargeBonus", "ChargeLevel", "ChargeLevelMin", "ChargeGroupBase",
             "ChargeGroupCount", "HoldMax", "MultiHits", "ArmorFrom", "ArmorTo", "ArmorKbPermille",
             "ReflectPermille", "ReflectMaxMult", "ZoneKind", "ZoneA", "ZoneB", "ZoneSlow", "DiveMax", "LandMult",
             "LandKbX", "LandKbY", "LandStun", "LandW", "LandH", "TeleportDist", "InvulnFrom", "InvulnTo", "RecoilVx",
             "RecoilVy", "TetherSpeed", "TetherMax", "BounceMax", "BounceMult", "ProjVx", "ProjVy"]


def emit_row(rr):
    r, ext, row = rr["r"], rr["ext"], rr["row"]
    bits = rr["bits"]
    lines = []
    flag_expr = " | ".join(rr["base"]) if rr["base"] else "0"
    proj = r["proj_speed"] > 0
    kind = ext.get("ProjKind", "Arrow")
    pflags = []
    if proj:
        if bits & MF["pierce"]:
            pflags.append("ChibiProjectileSim.FPierce")
        elif not bits & (MF["tetherself"] | MF["tetherfoe"]):
            pflags.append("ChibiProjectileSim.FDestructible")
    pflag_expr = " | ".join(pflags) if pflags else "0"
    lines.append("            // %s %s (%s, %s) slot %d" % (row["id"], row["name"], row["input"], row["ground_air"], rr["slot"]))
    lines.append("            new ChibiMove")
    lines.append("            {")
    lines.append("                Startup = %d, Active = %d, Recovery = %d, Mult = %d," % (r["startup"], r["active"], r["recovery"], r["mult"]))
    lines.append("                BoxX = %d, BoxW = %d, BoxY = %d, BoxH = %d, KbX = %d, KbY = %d, Stun = %d,"
                 % (r["box_x"], r["box_w"], r["box_y"], r["box_h"], r["kbx"], r["kby"], r["stun"]))
    lines.append("                SelfVx = %d, SelfVy = %d, Rehit = %d, Cooldown = %d, Flags = %s,"
                 % (r["self_vx"], r["self_vy"], ext.get("Rehit", 0), ext.get("Cooldown", 0), flag_expr))
    if proj:
        lines.append("                ProjSpeed = %d, ProjLife = %d, ProjR = %d, ProjOffX = %d, ProjOffY = %d,"
                     % (r["proj_speed"], r["proj_life"], r["proj_radius"], r["box_x"], r["box_y"]))
        lines.append("                ProjKind = ChibiProjKind.%s, ProjFlags = %s," % (kind, pflag_expr))
    lines.append("                Ext = new ChibiMoveExt")
    lines.append("                {")
    names = [MF_NAMES[k] for k in sorted(MF, key=lambda x: MF[x]) if bits & MF[k]]
    fl = " | ".join("ChibiMoveExt.F" + n for n in names) if names else "0"
    lines.append("                    Button = %d, Dir = %d, AirMask = %d, Dash = %s, Trigger = (int)%s, TechIdx = %d,"
                 % (rr["btn"], rr["dir"], rr["air"], "true" if rr["dash"] else "false", BUTTON_BITS[rr["btn"]], rr["tech"]))
    lines.append("                    Flags = %s," % fl)
    lines.append("                    Radius = %d," % r["radius"])
    vals = ["%s = %d" % (k, ext[k]) for k in EXT_ORDER if k in ext and k not in ("Cooldown",)]
    for k in range(0, len(vals), 6):
        lines.append("                    " + ", ".join(vals[k:k + 6]) + ",")
    if "FanVx" in ext:
        lines.append("                    FanVx = new[] { %s }," % ", ".join(str(v) for v in ext["FanVx"]))
        lines.append("                    FanVy = new[] { %s }," % ", ".join(str(v) for v in ext["FanVy"]))
    lines.append("                },")
    lines.append("            },")
    return lines


def generate():
    out_rows, resolve, dash_slot, dash_down_slot = build()
    L = []
    L.append("// <auto-generated>")
    L.append("// generated, do not edit: tools/gameplay/gen_movesets.py from design/gdd/movesets/movesets.csv")
    L.append("// Re-run the generator after changing the CSV; the config hash mixes every value below (rules v18).")
    L.append("// </auto-generated>")
    L.append("namespace ChibiClash.Netcode")
    L.append("{")
    L.append("    /// <summary>Moveset rows (slots &gt;= 7) and the input resolution table, generated from movesets.csv.</summary>")
    L.append("    public static partial class ChibiMovesetData")
    L.append("    {")
    L.append("        /// <summary>First slot of the CSV rows in every class table (slots 0-6 keep their meaning).</summary>")
    L.append("        public const int FirstNewSlot = %d;" % FIRST_NEW_SLOT)
    L.append("")
    for ci, cls in enumerate(CLASSES):
        L.append("        static ChibiMove[] Rows%s() => new[]" % cls)
        L.append("        {")
        for rr in out_rows[cls]:
            L.extend(emit_row(rr))
        L.append("        };")
        L.append("")
    L.append("        /// <summary>CSV rows per class index (FighterClass order).</summary>")
    L.append("        static ChibiMove[][] NewRows() => new[] { %s };"
             % ", ".join("Rows%s()" % c for c in CLASSES))
    L.append("")
    L.append("        /// <summary>")
    L.append("        /// Input pattern -&gt; slot per class (-1 = none). Index = <see cref=\"Key\"/>(button, dir, air): button 0 L, 1 H,")
    L.append("        /// 2 B, 3-5 S1-S3; dir 0 neutral, 1 F, 2 BK, 3 U, 4 D; air 0 ground, 1 air. Charge-level groups map to their base row.")
    L.append("        /// </summary>")
    L.append("        public static readonly sbyte[][] Resolve =")
    L.append("        {")
    for cls in CLASSES:
        t = resolve[cls]
        L.append("            new sbyte[] // %s" % cls)
        L.append("            {")
        for b in range(6):
            L.append("                " + ", ".join(str(v) for v in t[b * 10:(b + 1) * 10]) + ",")
        L.append("            },")
    L.append("        };")
    L.append("")
    L.append("        /// <summary>Dash attack (L@dash) slot per class, -1 = none.</summary>")
    L.append("        public static readonly sbyte[] DashSlot = { %s };" % ", ".join(str(v) for v in dash_slot))
    L.append("        /// <summary>Dash + down Light (L+D@dash) slot per class, -1 = none (Kaia Low Sweep only).</summary>")
    L.append("        public static readonly sbyte[] DashDownSlot = { %s };" % ", ".join(str(v) for v in dash_down_slot))
    L.append("    }")
    L.append("")
    L.append("    /// <summary>")
    L.append("    /// Presentation metadata of the moveset rows (view, audio, HUD). The simulation NEVER reads this class: it is not")
    L.append("    /// in the config hash and changing a name or sound id does not change gameplay.")
    L.append("    /// </summary>")
    L.append("    public static partial class ChibiMoveInfo")
    L.append("    {")
    L.append("        static readonly Entry[][] Rows =")
    L.append("        {")
    for cls in CLASSES:
        L.append("            new[] // %s" % cls)
        L.append("            {")
        for rr in out_rows[cls]:
            row = rr["row"]
            L.append("                new Entry(%s, %s, %s, %s, %s, %s, %s)," % tuple(
                cs_string(row[c].strip()) for c in ("id", "name", "anim_kind", "vfx", "sfx", "sfx_start", "sfx_hit")))
        L.append("            },")
    L.append("        };")
    L.append("    }")
    L.append("}")
    return "\n".join(L) + "\n"


def main():
    text = generate()
    if "--check" in sys.argv:
        cur = open(OUT_PATH, encoding="utf-8", newline="").read().replace("\r\n", "\n") if os.path.exists(OUT_PATH) else ""
        if cur != text:
            print("ChibiMovesetData.g.cs is out of date: run python tools/gameplay/gen_movesets.py")
            sys.exit(1)
        print("ChibiMovesetData.g.cs is up to date")
        return
    os.makedirs(os.path.dirname(OUT_PATH), exist_ok=True)
    with open(OUT_PATH, "w", encoding="utf-8", newline="\n") as fh:
        fh.write(text)
    print("wrote " + os.path.relpath(OUT_PATH, ROOT))


if __name__ == "__main__":
    main()
