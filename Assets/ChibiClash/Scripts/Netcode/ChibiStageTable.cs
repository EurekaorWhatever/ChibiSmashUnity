namespace ChibiClash.Netcode
{
    /// <summary>
    /// One one-way platform in sim milli, feet-relative (simY = (worldY + 2.5) x 1000). Collides only with a fighter
    /// falling onto its top (surfaceArc 170). Source: docs/architecture/netcode/stage-conversion.md sections 1-2.
    /// </summary>
    public readonly struct ChibiPlatform
    {
        /// <summary>Centre X (milli).</summary>
        public readonly int Cx;
        /// <summary>Top surface Y (milli); the underside is Top - <see cref="ChibiStageTable.PlatThick"/>.</summary>
        public readonly int Top;
        /// <summary>Half of the collider width (milli).</summary>
        public readonly int HalfW;

        /// <summary>Creates a platform from its centre X, top Y and half-width (milli).</summary>
        public ChibiPlatform(int cx, int top, int halfW) { Cx = cx; Top = top; HalfW = halfW; }
    }

    /// <summary>
    /// Immutable geometry and ring-out bounds of one stage (stage-conversion.md section 1). Not part of the rollback
    /// state: the state stores only the stage index.
    /// </summary>
    public sealed class ChibiStageDef
    {
        /// <summary>Main-stage half-width (milli). The main stage box is x in [-HalfWidth, HalfWidth], y in [MainBottom, 0].</summary>
        public readonly int HalfWidth;
        /// <summary>Ring-out when |X| &gt; BlastX (strict).</summary>
        public readonly int BlastX;
        /// <summary>Ring-out when feet Y &lt; KillY (strict).</summary>
        public readonly int KillY;
        /// <summary>Ring-out (not a KO) when feet Y &gt; CeilingY (strict).</summary>
        public readonly int CeilingY;
        /// <summary>Spawn |X| (P0 at -SpawnX, P1 at +SpawnX) and feet Y; fighters spawn airborne.</summary>
        public readonly int SpawnX, SpawnY;
        /// <summary>Respawn position after a ring-out (feet).</summary>
        public readonly int RespawnX, RespawnY;
        /// <summary>Platforms in build order; the array index is the value stored in <c>ChibiFighterSim.DropPlatform</c>.
        /// Treat as read-only.</summary>
        public readonly ChibiPlatform[] Platforms;

        /// <summary>Creates a stage definition (values from stage-conversion.md section 1).</summary>
        public ChibiStageDef(int halfWidth, int blastX, int killY, int ceilingY, int spawnX, int spawnY,
                             int respawnX, int respawnY, params ChibiPlatform[] platforms)
        {
            HalfWidth = halfWidth; BlastX = blastX; KillY = killY; CeilingY = ceilingY;
            SpawnX = spawnX; SpawnY = spawnY; RespawnX = respawnX; RespawnY = respawnY;
            Platforms = platforms;
        }
    }

    /// <summary>
    /// The three stages of the original Stage.cs (0 Sky Dojo, 1 Neon City, 2 Lava Forge), converted to sim milli with
    /// the feet convention. Source: docs/architecture/netcode/stage-conversion.md sections 1 and 8.
    /// </summary>
    public static class ChibiStageTable
    {
        /// <summary>Stage indices (Stage.cs:19-51).</summary>
        public const int SkyDojo = 0, NeonCity = 1, LavaForge = 2;
        /// <summary>Main-stage underside Y (collider height 1.2 u below the top at 0).</summary>
        public const int MainBottom = -1200;
        /// <summary>Platform collider thickness (0.3 u).</summary>
        public const int PlatThick = 300;
        /// <summary>Ground-probe width x2 (2 x 337.5 = 0.9 x 0.75 u), kept doubled so it stays an exact integer.</summary>
        public const int ProbeHalfW2 = 675;
        /// <summary>Support half-width = fighter collider half-width (0.375 u).</summary>
        public const int SupportHalfW = ChibiSimConst.HurtHalfW;
        /// <summary>Drop-through duration: 0.35 s x 60 = 21 ticks (Fighter.cs:291-297).</summary>
        public const int DropDuration = 21;

        const int KillY = -9725, CeilingY = 17775, SpawnY = 775, RespawnY = 7775;

        /// <summary>Stage definitions, index = <see cref="ChibiMatchConfig.Stage"/>. Treat as read-only.</summary>
        public static readonly ChibiStageDef[] Stages =
        {
            new ChibiStageDef(8000, 17000, KillY, CeilingY, 3600, SpawnY, 0, RespawnY,
                new ChibiPlatform(-4200, 3050, 1700), new ChibiPlatform(4200, 3050, 1700), new ChibiPlatform(0, 5550, 1500)),
            new ChibiStageDef(9000, 18000, KillY, CeilingY, 4050, SpawnY, 0, RespawnY,
                new ChibiPlatform(-5500, 2450, 1700), new ChibiPlatform(5500, 2450, 1700), new ChibiPlatform(0, 4650, 2000)),
            new ChibiStageDef(7000, 16000, KillY, CeilingY, 3150, SpawnY, 0, RespawnY,
                new ChibiPlatform(-3600, 3250, 1500), new ChibiPlatform(3600, 3250, 1500)),
        };

        /// <summary>Maps any stage value to a valid index: out-of-range values play as Sky Dojo (Stage.cs <c>default:</c>).</summary>
        /// <example><code>int idx = ChibiStageTable.Normalize(5); // 0</code></example>
        public static int Normalize(int stage) => stage >= 0 && stage < Stages.Length ? stage : SkyDojo;

        /// <summary>Stage definition for <paramref name="stage"/> (normalized first).</summary>
        public static ChibiStageDef Get(int stage) => Stages[Normalize(stage)];
    }
}
