namespace ChibiClash.Netcode
{
    /// <summary>
    /// One one-way platform in sim milli, feet-relative (simY = (worldY + 2.5) x 1000). Collides only with a fighter
    /// falling onto its top (surfaceArc 170). Source: docs/architecture/netcode/stage-conversion.md sections 1-2.
    /// Rules v20 (design/gdd/stages/README.md 3.2, formula F1): a platform may move on one axis as a pure function of
    /// GameTicks (smooth ping-pong between Min and Max, period P, phase Ph), so it needs no rollback state.
    /// </summary>
    public readonly struct ChibiPlatform
    {
        /// <summary>Movement axis: none, X (Cx moves) or Y (Top moves).</summary>
        public const int AxisNone = 0, AxisX = 1, AxisY = 2;

        /// <summary>Centre X (milli) at GameTicks 0. Moving platforms: use <see cref="CxAt"/>.</summary>
        public readonly int Cx;
        /// <summary>Top surface Y (milli) at GameTicks 0; the underside is Top - <see cref="ChibiStageTable.PlatThick"/>. Moving: <see cref="TopAt"/>.</summary>
        public readonly int Top;
        /// <summary>Half of the collider width (milli).</summary>
        public readonly int HalfW;
        /// <summary><see cref="AxisNone"/>, <see cref="AxisX"/> or <see cref="AxisY"/>.</summary>
        public readonly int Axis;
        /// <summary>Travel bounds on the axis (milli), period (ticks, even) and phase (ticks) of F1.</summary>
        public readonly int Min, Max, Period, Phase;

        /// <summary>Creates a static platform from its centre X, top Y and half-width (milli).</summary>
        public ChibiPlatform(int cx, int top, int halfW)
        {
            Cx = cx; Top = top; HalfW = halfW; Axis = AxisNone; Min = Max = Period = Phase = 0;
        }

        /// <summary>Creates a moving platform (README 3.2): the moving coordinate follows F1 between min and max.</summary>
        /// <param name="cx">Centre X when the axis is Y (ignored for X).</param>
        /// <param name="top">Top Y when the axis is X (ignored for Y).</param>
        public ChibiPlatform(int cx, int top, int halfW, int axis, int min, int max, int period, int phase)
        {
            HalfW = halfW; Axis = axis; Min = min; Max = max; Period = period; Phase = phase;
            int p0 = Pos(min, max, period, phase, 0);
            Cx = axis == AxisX ? p0 : cx;
            Top = axis == AxisY ? p0 : top;
        }

        /// <summary>Centre X at game tick <paramref name="t"/>.</summary>
        public int CxAt(int t) => Axis == AxisX ? Pos(Min, Max, Period, Phase, t) : Cx;
        /// <summary>Top Y at game tick <paramref name="t"/>.</summary>
        public int TopAt(int t) => Axis == AxisY ? Pos(Min, Max, Period, Phase, t) : Top;
        /// <summary>True for a moving platform.</summary>
        public bool Moves => Axis != AxisNone;

        /// <summary>
        /// F1 (README section 4): p = (t + Ph) mod P, H = P/2, u = triangle 0..H..0, S = u²(3H - 2u),
        /// pos = Min + (Max - Min)·S / H³ (long, truncating). Example Mountain D (-5600..5600, P 900): t 225 -> 0.
        /// </summary>
        public static int Pos(int min, int max, int period, int phase, int t)
        {
            if (period <= 0) return min;
            long p = ((long)t + phase) % period;
            if (p < 0) p += period;
            long h = period / 2;
            long u = p <= h ? p : period - p;
            long sNum = u * u * (3 * h - 2 * u);
            return (int)(min + (long)(max - min) * sNum / (h * h * h));
        }
    }

    /// <summary>
    /// Immutable geometry and KO edges of one stage (stage-conversion.md section 1; rules v20 design/gdd/stages/README.md
    /// 3.1 and percent-knockback.md 3.4). Not part of the rollback state: the state stores only the stage index.
    /// Each edge either KOs (strict compare on feet coordinates) or is a solid boundary: side walls at |X| = BlastX (centre
    /// clamped to BlastX - HurtHalfW), a sky cap at Y = CeilingY. Rules v21 (lead, GL-2 applied consistently): every side
    /// KO line sits at least 8000 beyond the outermost walkable surface, so walk-off floors became finite: an optional
    /// solid floor at FloorY spanning |X| &lt; FloorHalfW (Bird Lair's cloud shelf), with a KO bottom below it.
    /// </summary>
    public sealed class ChibiStageDef
    {
        /// <summary>Main-stage half-width (milli). The main stage box is x in [-HalfWidth, HalfWidth], y in [MainBottom, 0].</summary>
        public readonly int HalfWidth;
        /// <summary>Side edge |X| (milli): KO when |X| &gt; BlastX if <see cref="KoSides"/>, else a solid wall plane.</summary>
        public readonly int BlastX;
        /// <summary>Bottom edge (feet milli): KO when Y &lt; KillY if <see cref="KoBottom"/>, else a solid floor top.</summary>
        public readonly int KillY;
        /// <summary>Top edge (feet milli): KO when Y &gt; CeilingY if <see cref="KoTop"/>, else the sky cap (fall back).</summary>
        public readonly int CeilingY;
        /// <summary>Spawn |X| (P0 at -SpawnX, P1 at +SpawnX) and feet Y; fighters spawn airborne.</summary>
        public readonly int SpawnX, SpawnY;
        /// <summary>Respawn position after a KO (feet).</summary>
        public readonly int RespawnX, RespawnY;
        /// <summary>Platforms in build order; the array index is the value stored in <c>ChibiFighterSim.DropPlatform</c>.
        /// Treat as read-only.</summary>
        public readonly ChibiPlatform[] Platforms;
        /// <summary>Which edges KO (rules v20, ST-2). Classic stages 0..2: all four.</summary>
        public readonly bool KoSides, KoTop, KoBottom;
        /// <summary>Main-stage underside Y (milli): -1200 except Bird Lair's plateau, solid down to its floor.</summary>
        public readonly int MainBottom;
        /// <summary>Stage event (<see cref="ChibiStageTable"/> Hazard* ids); 0 = none.</summary>
        public readonly int Hazard;
        /// <summary>Rules v21: a solid floor (walkable, cannot be dropped through) at <see cref="FloorY"/> for |X| &lt; FloorHalfW.</summary>
        public readonly bool HasFloor;
        /// <summary>Floor top Y and half-width (milli); valid when <see cref="HasFloor"/>.</summary>
        public readonly int FloorY, FloorHalfW;

        /// <summary>Creates a classic stage (values from stage-conversion.md section 1): all four edges KO, no hazard.</summary>
        public ChibiStageDef(int halfWidth, int blastX, int killY, int ceilingY, int spawnX, int spawnY,
                             int respawnX, int respawnY, params ChibiPlatform[] platforms)
            : this(halfWidth, ChibiStageTable.MainBottom, blastX, killY, ceilingY, true, true, true, spawnX, spawnY,
                   respawnX, respawnY, 0, platforms) { }

        /// <summary>Creates a stage with explicit KO flags, main underside and hazard (rules v20), no extra floor.</summary>
        public ChibiStageDef(int halfWidth, int mainBottom, int blastX, int killY, int ceilingY,
                             bool koSides, bool koTop, bool koBottom, int spawnX, int spawnY, int respawnX, int respawnY,
                             int hazard, params ChibiPlatform[] platforms)
            : this(halfWidth, mainBottom, blastX, killY, ceilingY, koSides, koTop, koBottom, spawnX, spawnY, respawnX, respawnY,
                   hazard, 0, 0, platforms) { }

        /// <summary>Creates a stage with a solid floor at <paramref name="floorY"/> spanning |X| &lt; <paramref name="floorHalfW"/> (rules v21; 0 = none).</summary>
        public ChibiStageDef(int halfWidth, int mainBottom, int blastX, int killY, int ceilingY,
                             bool koSides, bool koTop, bool koBottom, int spawnX, int spawnY, int respawnX, int respawnY,
                             int hazard, int floorY, int floorHalfW, params ChibiPlatform[] platforms)
        {
            HasFloor = floorHalfW > 0; FloorY = floorY; FloorHalfW = floorHalfW;
            HalfWidth = halfWidth; MainBottom = mainBottom; BlastX = blastX; KillY = killY; CeilingY = ceilingY;
            KoSides = koSides; KoTop = koTop; KoBottom = koBottom;
            SpawnX = spawnX; SpawnY = spawnY; RespawnX = respawnX; RespawnY = respawnY;
            Hazard = hazard;
            Platforms = platforms;
        }

        /// <summary>Side wall centre clamp |X| (milli) when the sides do not KO: BlastX - HurtHalfW (Mountain 13125).</summary>
        public int WallClampX => BlastX - ChibiSimConst.HurtHalfW;

        /// <summary>True when feet (x, y) are past an ACTIVE KO edge (strict). Inactive edges never KO.</summary>
        public bool CrossesKoEdge(int x, int y) =>
            (KoSides && (x > BlastX || x < -BlastX)) || (KoBottom && y < KillY) || (KoTop && y > CeilingY);

        /// <summary>
        /// Teleport / safe clamp of a target position: inside every edge (active or solid) by <paramref name="margin"/>
        /// (percent-knockback.md 3.5 teleport row).
        /// </summary>
        public void ClampInside(ref int x, ref int y, int margin)
        {
            int lim = (KoSides ? BlastX : WallClampX) - margin;
            if (x > lim) x = lim; else if (x < -lim) x = -lim;
            int lo = KillY + (KoBottom ? margin : 0), hi = CeilingY - margin;
            if (y > hi) y = hi;
            if (y < lo) y = lo;
        }
    }

    /// <summary>
    /// Stages 0..2: the original Stage.cs set (Classic), converted to sim milli with the feet convention
    /// (stage-conversion.md sections 1 and 8). Stages 3..7 (rules v20, ST-1): the five design stages from
    /// design/gdd/stages/0N-*.md with the blast lines of GL-2 (sides 8000-10000 beyond the ledge, top = max(10000,
    /// highest platform + 5000)). Platform geometry = design/gdd/stages/stage-geometry.json (art-measured, wins over the
    /// level-designer's proposed numbers) for every new stage; the rules (edges, floor, underside, events) from the specs.
    /// </summary>
    public static class ChibiStageTable
    {
        /// <summary>Stage indices (Stage.cs:19-51 for 0..2; ST-1 for 3..7).</summary>
        public const int SkyDojo = 0, NeonCity = 1, LavaForge = 2,
                         Mountain = 3, Plain = 4, Volcano = 5, BirdLair = 6, Ocean = 7;
        /// <summary>Number of stages.</summary>
        public const int Count = 8;
        /// <summary>Stage event ids (<see cref="ChibiStageDef.Hazard"/>).</summary>
        public const int HazardNone = 0, HazardFire = 1, HazardBird = 2, HazardWhale = 3;
        /// <summary>Default main-stage underside Y (collider height 1.2 u below the top at 0).</summary>
        public const int MainBottom = -1200;
        /// <summary>Platform collider thickness (0.3 u).</summary>
        public const int PlatThick = 300;
        /// <summary>Ground-probe width x2 (2 x 337.5 = 0.9 x 0.75 u), kept doubled so it stays an exact integer.</summary>
        public const int ProbeHalfW2 = 675;
        /// <summary>Support half-width = fighter collider half-width (0.375 u).</summary>
        public const int SupportHalfW = ChibiSimConst.HurtHalfW;
        /// <summary>Drop-through duration: 0.35 s x 60 = 21 ticks (Fighter.cs:291-297).</summary>
        public const int DropDuration = 21;
        /// <summary>Sky cap of the no-top stages (Plain, Bird Lair), README 3.1 / 7.</summary>
        public const int SkyCapY = 16000;

        const int KillY = -9725, CeilingY = 17775, SpawnY = 775, RespawnY = 7775;
        const int X = ChibiPlatform.AxisX, Y = ChibiPlatform.AxisY;

        /// <summary>Stage definitions, index = <see cref="ChibiMatchConfig.Stage"/>. Treat as read-only.</summary>
        public static readonly ChibiStageDef[] Stages =
        {
            new ChibiStageDef(8000, 17000, KillY, CeilingY, 3600, SpawnY, 0, RespawnY,
                new ChibiPlatform(-4200, 3050, 1700), new ChibiPlatform(4200, 3050, 1700), new ChibiPlatform(0, 5550, 1500)),
            new ChibiStageDef(9000, 18000, KillY, CeilingY, 4050, SpawnY, 0, RespawnY,
                new ChibiPlatform(-5500, 2450, 1700), new ChibiPlatform(5500, 2450, 1700), new ChibiPlatform(0, 4650, 2000)),
            new ChibiStageDef(7000, 16000, KillY, CeilingY, 3150, SpawnY, 0, RespawnY,
                new ChibiPlatform(-3600, 3250, 1500), new ChibiPlatform(3600, 3250, 1500)),

            // 3 Mountain of the Beginning (01-mountain.md; platforms from stage-geometry.json): walls at +-16000 (no side KO,
            // outside A/B's outer edge 14604), top 13302 (GL-2: D 8302 + 5000), bottom -9725 (GL-3 R1: lowest top - 9725). A/B move Y 1208..3208 (art home =
            // top, P 360, Ph 90), C static, D slides X -7349..7349 (art home = left end, P 900, Ph 0).
            new ChibiStageDef(7981, MainBottom, 16000, -9725, 13302, false, true, true, 4000, SpawnY, 0, 10000, HazardNone,
                new ChibiPlatform(-11765, 0, 2839, Y, 1208, 3208, 360, 90),
                new ChibiPlatform(11787, 0, 2806, Y, 1217, 3217, 360, 90),
                new ChibiPlatform(33, 5566, 3090),
                new ChibiPlatform(0, 8302, 2311, X, -7349, 7349, 900, 0)),
            // 4 Plain of Unity (02-plain.md; stage-geometry.json): flat ground +-16991, side KO +-25000 (rules v21, GL-2: 8000
            // beyond the end of the ground), no top (sky cap 16000), bottom -9725 below the ground's ends, no platforms.
            new ChibiStageDef(16991, MainBottom, 25000, -9725, SkyCapY, true, false, true, 4500, SpawnY, 0, 6000, HazardNone),
            // 5 Volcano Abyss (03-volcano.md; stage-geometry.json): all four KO, sides 17500 (rules v21: 8000 beyond L1's outer
            // edge 9500, the outermost walkable surface),
            // top 15472 (GL-2: L4 10472 + 5000), bottom -9725 (GL-3 R1; the lava art stays at -4066 and is drawn as a foreground
            // so falling in reads as sinking); the asymmetric staircase L1..L4; fire bursts.
            new ChibiStageDef(7599, MainBottom, 17500, -9725, 15472, true, true, true, 4500, SpawnY, -3500, 6000, HazardFire,
                new ChibiPlatform(7179, 3019, 2321), new ChibiPlatform(953, 5472, 2132),
                new ChibiPlatform(5113, 8019, 1953), new ChibiPlatform(-137, 10472, 1712)),
            // 6 Giant Bird Lair (04-bird-lair.md; stage-geometry.json re-measured): plateau 7765 solid down to the cloud-sea
            // floor -3000, which spans +-12000 (the art edge); side KO +-20000 (rules v21: 8000 beyond the floor's end), bottom
            // -12725 (floor - 9725) beyond it, no top (sky cap); L/R mesas; bird swipe.
            new ChibiStageDef(7765, -3000, 20000, -12725, SkyCapY, true, false, true, 4800, SpawnY, 0, 6000, HazardBird,
                -3000, 12000, new ChibiPlatform(-3975, 1731, 1656), new ChibiPlatform(3900, 1731, 1656)),
            // 7 Abandoned Ocean Monument (05-ocean.md; stage-geometry.json): all four KO, sides 19800 (rules v21: 8000 beyond
            // T1/T2's outer edge 11755), top 10000 (GL-2), bottom -16989 (GL-3 R1: T3 top -7264 - 9725); tiers T1/T2/T3 below MAIN; whale.
            new ChibiStageDef(7613, MainBottom, 19800, -16989, 10000, true, true, true, 4500, SpawnY, 0, 6000, HazardWhale,
                new ChibiPlatform(-8114, -3868, 3641), new ChibiPlatform(8094, -3868, 3641), new ChibiPlatform(-10, -7264, 4396)),
        };

        /// <summary>Maps any stage value to a valid index: out-of-range values play as Sky Dojo (Stage.cs <c>default:</c>).</summary>
        /// <example><code>int idx = ChibiStageTable.Normalize(9); // 0</code></example>
        public static int Normalize(int stage) => stage >= 0 && stage < Stages.Length ? stage : SkyDojo;

        /// <summary>Stage definition for <paramref name="stage"/> (normalized first).</summary>
        public static ChibiStageDef Get(int stage) => Stages[Normalize(stage)];
    }
}
