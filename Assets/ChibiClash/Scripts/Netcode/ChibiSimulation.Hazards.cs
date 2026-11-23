using System;

namespace ChibiClash.Netcode
{
    /// <summary>
    /// Rules v20 stage events (design/gdd/stages/README.md 3.3, 03-volcano.md, 04-bird-lair.md, 05-ocean.md, decisions
    /// ST-3 / ST-5). One event machine per stage, scheduled from game steps while the match is running and from its OWN
    /// RNG stream (<see cref="ChibiSimState.HazRng"/>), so switching hazards on or off never shifts crits or replays.
    /// Knockback tiers (percent-knockback.md 4.4): Med = magnitude 150 000 (growth 850, base 30 000), High = 187 500
    /// (growth 1000, base 35 000); the angle sets the KbX / KbY split. Every constant is in the config hash.
    /// </summary>
    public static class ChibiHazardConst
    {
        /// <summary>Event phases (<see cref="ChibiSimState.HazPhase"/>). Phase 2+ meaning depends on the stage.</summary>
        public const int Idle = 0, Telegraph = 1, PhaseFireErupt = 2, PhaseFireFade = 3, PhaseBirdSwipe = 2,
                         PhaseWhaleDescend = 2, PhaseWhaleHold = 3, PhaseWhaleRise = 4;
        /// <summary>Seed salt of the hazard stream ("STAG"): HazRng = Seed XOR salt (README 3.3).</summary>
        public const uint RngSalt = 0x53544147u;

        /// <summary>
        /// Volcano fire bursts (03-volcano.md timing; lanes at the art's fire pillars from stage-geometry.json, -10127 and
        /// +11052, half-width 700, both clear of MAIN and L1): the column rises from the lava top -4066 to 9000.
        /// </summary>
        public const int FireFirstBase = 360, FireFirstSpan = 241, FireNextBase = 300, FireNextSpan = 241,
                         FireTelegraph = 75, FireErupt = 45, FireFade = 20, FireLaneLeftX = -10127, FireLaneRightX = 11052,
                         FireLaneHalfW = 700, FireBottom = -4066, FireRise = 13066, FireRiseTicks = 6, FirePctM = 10_000,
                         FireKbX = 0, FireKbY = 150_000, FireStun = 30;
        /// <summary>
        /// Bird swipe (04-bird-lair.md): telegraph 90, gust 120 ticks (feet below 6000, accel 12 000 sub/tick to a cap of
        /// 120 000), body pass from swipe tick 30 for 80 ticks at X = -dir·17000 + dir·425·t, half-width 3000, band
        /// 1900..3700; body hit 14 % High at 30 degrees up toward dir.
        /// </summary>
        public const int BirdFirstBase = 1200, BirdFirstSpan = 301, BirdNextBase = 900, BirdNextSpan = 301,
                         BirdTelegraph = 90, BirdSwipe = 120, BirdBodyStart = 30, BirdBodyTicks = 80,
                         BirdBodyStartX = 17000, BirdBodySpeed = 425, BirdBodyHalfW = 3000, BirdBandY0 = 1900, BirdBandY1 = 3700,
                         GustTopY = 6000, GustAccel = 12_000, GustCap = 120_000, BirdPctM = 14_000,
                         BirdKbX = 162_380, BirdKbY = 93_750, BirdStun = 38;
        /// <summary>
        /// Whale descent (05-ocean.md): body X -8500..8500, 3500 tall; telegraph 120 at 12000, descend 285 at 40/tick, hold
        /// 180 at 600, rise 190 at 60/tick; first contact 12 % Med at 60 degrees below horizontal outward, then while
        /// overlapping Vy &lt;= -180 000 and |Vx| &gt;= 60 000 outward; respawn under MAIN at (0, -4300) during the event.
        /// </summary>
        public const int WhaleFirstBase = 1800, WhaleFirstSpan = 601, WhaleNextBase = 1500, WhaleNextSpan = 601,
                         WhaleTelegraph = 120, WhaleDescend = 285, WhaleHold = 180, WhaleRise = 190,
                         WhaleTopY = 12000, WhaleHoldY = 600, WhaleDescendSpeed = 40, WhaleRiseSpeed = 60,
                         WhaleHalfW = 8500, WhaleH = 3500, WhalePctM = 12_000, WhaleKbX = 75_000, WhaleKbY = -129_904,
                         WhaleStun = 30, WhalePushVy = 180_000, WhalePushVx = 60_000, WhaleRespawnX = 0, WhaleRespawnY = -4300;

        /// <summary>Every constant above, mixed into the config hash.</summary>
        public static readonly int[] Constants =
        {
            unchecked((int)RngSalt),
            FireFirstBase, FireFirstSpan, FireNextBase, FireNextSpan, FireTelegraph, FireErupt, FireFade, FireLaneLeftX, FireLaneRightX,
            FireLaneHalfW,
            FireBottom, FireRise, FireRiseTicks, FirePctM, FireKbX, FireKbY, FireStun,
            BirdFirstBase, BirdFirstSpan, BirdNextBase, BirdNextSpan, BirdTelegraph, BirdSwipe, BirdBodyStart, BirdBodyTicks,
            BirdBodyStartX, BirdBodySpeed, BirdBodyHalfW, BirdBandY0, BirdBandY1, GustTopY, GustAccel, GustCap, BirdPctM,
            BirdKbX, BirdKbY, BirdStun,
            WhaleFirstBase, WhaleFirstSpan, WhaleNextBase, WhaleNextSpan, WhaleTelegraph, WhaleDescend, WhaleHold, WhaleRise,
            WhaleTopY, WhaleHoldY, WhaleDescendSpeed, WhaleRiseSpeed, WhaleHalfW, WhaleH, WhalePctM, WhaleKbX, WhaleKbY,
            WhaleStun, WhalePushVy, WhalePushVx, WhaleRespawnX, WhaleRespawnY,
        };
    }

    public sealed partial class ChibiSimState
    {
        // ---- rules v20 stage events. All rolled back and hashed (CopyHazards / HashHazards).
        /// <summary>Hazards enabled for this match (config !HazardsOff and the stage has an event).</summary>
        public bool HazardsOn;
        /// <summary>Event phase (<see cref="ChibiHazardConst"/> Idle / Telegraph / stage phases).</summary>
        public int HazPhase;
        /// <summary>Game steps left in the current phase, counting the current one (Idle: the drawn delay).</summary>
        public int HazTimer;
        /// <summary>Game steps already spent in the current phase (0 on its first step).</summary>
        public int HazElapsed;
        /// <summary>Volcano lane (0 left, 1 right, 2 both); Bird Lair direction (+1 / -1). Set at Telegraph start.</summary>
        public int HazArg;
        /// <summary>Events started so far.</summary>
        public int HazCount;
        /// <summary>Bit per fighter already hit by the current event (cleared at each Telegraph start).</summary>
        public int HazHitMask;
        /// <summary>The hazard RNG stream (xorshift32), seeded Seed XOR "STAG"; never the combat <see cref="Rng"/>.</summary>
        public ChibiRng HazRng;

        void CopyHazards(ChibiSimState o)
        {
            HazardsOn = o.HazardsOn; HazPhase = o.HazPhase; HazTimer = o.HazTimer; HazElapsed = o.HazElapsed;
            HazArg = o.HazArg; HazCount = o.HazCount; HazHitMask = o.HazHitMask; HazRng = o.HazRng;
        }

        void HashHazards(ref uint h)
        {
            Mix(ref h, HazardsOn ? 1 : 0); Mix(ref h, HazPhase); Mix(ref h, HazTimer); Mix(ref h, HazElapsed);
            Mix(ref h, HazArg); Mix(ref h, HazCount); Mix(ref h, HazHitMask); Mix(ref h, unchecked((int)HazRng.State));
        }
    }

    public static partial class ChibiSimulation
    {
        /// <summary>Init: the hazard stream and the first delay draw (README 3.3: the only draw before the first event).</summary>
        static void InitHazards(ChibiSimState s, in ChibiMatchConfig cfg)
        {
            int hz = ChibiStageTable.Get(s.Stage).Hazard;
            s.HazRng = new ChibiRng(cfg.Seed ^ ChibiHazardConst.RngSalt);
            s.HazardsOn = !cfg.HazardsOff && hz != ChibiStageTable.HazardNone;
            s.HazPhase = ChibiHazardConst.Idle; s.HazTimer = 0; s.HazElapsed = 0; s.HazArg = 0; s.HazCount = 0; s.HazHitMask = 0;
            if (s.HazardsOn) s.HazTimer = DrawDelay(s, hz, true);
        }

        static int DrawDelay(ChibiSimState s, int hz, bool first)
        {
            switch (hz)
            {
                case ChibiStageTable.HazardFire:
                    return first ? ChibiHazardConst.FireFirstBase + s.HazRng.Range(ChibiHazardConst.FireFirstSpan)
                                 : ChibiHazardConst.FireNextBase + s.HazRng.Range(ChibiHazardConst.FireNextSpan);
                case ChibiStageTable.HazardBird:
                    return first ? ChibiHazardConst.BirdFirstBase + s.HazRng.Range(ChibiHazardConst.BirdFirstSpan)
                                 : ChibiHazardConst.BirdNextBase + s.HazRng.Range(ChibiHazardConst.BirdNextSpan);
                default:
                    return first ? ChibiHazardConst.WhaleFirstBase + s.HazRng.Range(ChibiHazardConst.WhaleFirstSpan)
                                 : ChibiHazardConst.WhaleNextBase + s.HazRng.Range(ChibiHazardConst.WhaleNextSpan);
            }
        }

        /// <summary>Length of a non-Idle phase in game steps, 0 when the phase does not exist on that stage (ends the event).</summary>
        public static int HazardPhaseLength(int hazard, int phase)
        {
            switch (hazard)
            {
                case ChibiStageTable.HazardFire:
                    return phase == ChibiHazardConst.Telegraph ? ChibiHazardConst.FireTelegraph :
                           phase == ChibiHazardConst.PhaseFireErupt ? ChibiHazardConst.FireErupt :
                           phase == ChibiHazardConst.PhaseFireFade ? ChibiHazardConst.FireFade : 0;
                case ChibiStageTable.HazardBird:
                    return phase == ChibiHazardConst.Telegraph ? ChibiHazardConst.BirdTelegraph :
                           phase == ChibiHazardConst.PhaseBirdSwipe ? ChibiHazardConst.BirdSwipe : 0;
                case ChibiStageTable.HazardWhale:
                    return phase == ChibiHazardConst.Telegraph ? ChibiHazardConst.WhaleTelegraph :
                           phase == ChibiHazardConst.PhaseWhaleDescend ? ChibiHazardConst.WhaleDescend :
                           phase == ChibiHazardConst.PhaseWhaleHold ? ChibiHazardConst.WhaleHold :
                           phase == ChibiHazardConst.PhaseWhaleRise ? ChibiHazardConst.WhaleRise : 0;
                default: return 0;
            }
        }

        /// <summary>
        /// Event machine advance (end of the game step, after the hazard hits; README 3.3 tick order). A phase with length
        /// L is current for exactly L running steps. Telegraph start draws the lane / direction and clears the hit mask;
        /// the event end draws the next delay. Exactly two draws per event.
        /// </summary>
        static void HazardAdvance(ChibiSimState s)
        {
            if (!s.HazardsOn || !s.Running) return;
            int hz = ChibiStageTable.Get(s.Stage).Hazard;
            s.HazElapsed++;
            if (--s.HazTimer > 0) return;
            int next = s.HazPhase + 1;
            int len = HazardPhaseLength(hz, next);
            s.HazElapsed = 0;
            if (len == 0)
            {
                s.HazPhase = ChibiHazardConst.Idle;
                s.HazTimer = DrawDelay(s, hz, false);
                return;
            }
            s.HazPhase = next;
            s.HazTimer = len;
            if (next != ChibiHazardConst.Telegraph) return;
            s.HazHitMask = 0;
            if (hz == ChibiStageTable.HazardFire) s.HazArg = s.HazRng.Range(3);
            else
            {
                int draw = s.HazRng.Range(2);                              // drawn every event: fixed draw count
                if (hz == ChibiStageTable.HazardBird) s.HazArg = s.HazCount == 0 ? (draw == 0 ? -1 : 1) : -s.HazArg;
                else s.HazArg = 0;
            }
            s.HazCount++;
        }

        /// <summary>True when fighter <paramref name="i"/> ignores hazards (Out, hovering, invulnerable).</summary>
        static bool HazardImmune(ChibiFighterSim f) => !f.InPlay || f.Invuln > 0;

        /// <summary>
        /// Hazard velocity adds (README 3.3: after FreeActions, before integration; F3 gust). Bird Lair only: every
        /// non-immune, non-held fighter with feet below GustTopY is pushed toward dir up to GustCap.
        /// </summary>
        static void HazardForces(ChibiSimState s, int i)
        {
            if (!s.HazardsOn || !s.Running || !BirdGustActive(s)) return;
            var f = s.F[i];
            if (HazardImmune(f) || f.Held || f.Y >= ChibiHazardConst.GustTopY) return;
            int dir = s.HazArg;
            if (f.Vx * dir >= ChibiHazardConst.GustCap) return;
            int v = f.Vx + dir * ChibiHazardConst.GustAccel;
            f.Vx = dir > 0 ? Math.Min(v, ChibiHazardConst.GustCap) : Math.Max(v, -ChibiHazardConst.GustCap);
        }

        /// <summary>
        /// Hazard hit tests (after combat, before the advance): fire columns, bird body, whale crush and push, fighters in
        /// index order, one hit per fighter per event (HazHitMask). Unblockable, never countered, reflected or warded, no
        /// combat RNG draw.
        /// </summary>
        static void HazardHits(ChibiSimState s)
        {
            if (!s.HazardsOn || !s.Running) return;
            int hz = ChibiStageTable.Get(s.Stage).Hazard;
            for (int i = 0; i < s.Count; i++)   // rules v25: hazards hit every fighter, allies included
            {
                var f = s.F[i];
                if (HazardImmune(f)) continue;
                bool hitBefore = (s.HazHitMask & (1 << i)) != 0;
                if (hz == ChibiStageTable.HazardFire && s.HazPhase == ChibiHazardConst.PhaseFireErupt)
                {
                    int top = FireColumnTop(s);
                    bool inLane = (s.HazArg != 1 && Overlaps(f, ChibiHazardConst.FireLaneLeftX - ChibiHazardConst.FireLaneHalfW,
                                       ChibiHazardConst.FireLaneLeftX + ChibiHazardConst.FireLaneHalfW, ChibiHazardConst.FireBottom, top)) ||
                                  (s.HazArg != 0 && Overlaps(f, ChibiHazardConst.FireLaneRightX - ChibiHazardConst.FireLaneHalfW,
                                       ChibiHazardConst.FireLaneRightX + ChibiHazardConst.FireLaneHalfW, ChibiHazardConst.FireBottom, top));
                    if (inLane && !hitBefore)
                    {
                        s.HazHitMask |= 1 << i;
                        HazardHit(f, s, i, ChibiHazardConst.FirePctM, ChibiHazardConst.FireKbX, ChibiHazardConst.FireKbY,
                                  f.X >= 0 ? 1 : -1, ChibiHazardConst.FireStun);
                    }
                }
                else if (hz == ChibiStageTable.HazardBird && BirdBodyActive(s))
                {
                    int bx = BirdBodyX(s);
                    if (!hitBefore && Overlaps(f, bx - ChibiHazardConst.BirdBodyHalfW, bx + ChibiHazardConst.BirdBodyHalfW,
                                               ChibiHazardConst.BirdBandY0, ChibiHazardConst.BirdBandY1))
                    {
                        s.HazHitMask |= 1 << i;
                        HazardHit(f, s, i, ChibiHazardConst.BirdPctM, ChibiHazardConst.BirdKbX, ChibiHazardConst.BirdKbY,
                                  s.HazArg, ChibiHazardConst.BirdStun);
                    }
                }
                else if (hz == ChibiStageTable.HazardWhale && s.HazPhase >= ChibiHazardConst.PhaseWhaleDescend)
                {
                    int by = WhaleBottom(s);
                    if (!Overlaps(f, -ChibiHazardConst.WhaleHalfW, ChibiHazardConst.WhaleHalfW, by, by + ChibiHazardConst.WhaleH))
                        continue;
                    int outward = f.X >= 0 ? 1 : -1;                       // deterministic tie rule: X 0 -> +X
                    if (!hitBefore)
                    {
                        s.HazHitMask |= 1 << i;
                        HazardHit(f, s, i, ChibiHazardConst.WhalePctM, ChibiHazardConst.WhaleKbX, ChibiHazardConst.WhaleKbY,
                                  outward, ChibiHazardConst.WhaleStun);
                    }
                    if (!f.Held)
                    {
                        f.Vy = Math.Min(f.Vy, -ChibiHazardConst.WhalePushVy);
                        f.Vx = outward * Math.Max(Math.Abs(f.Vx), ChibiHazardConst.WhalePushVx);
                        f.Grounded = false;
                    }
                }
            }
        }

        /// <summary>
        /// One hazard hit (README 3.3, percent-knockback.md 6 Hazards row): percent added, knockback from the formula with
        /// the hazard's percent as d and its tier magnitude, launch fields, stun and Interrupt. No ult, no hitstop.
        /// </summary>
        static void HazardHit(ChibiFighterSim f, ChibiSimState s, int i, int pctM, int kbX, int kbY, int dir, int stun)
        {
            AddPct(f, pctM);
            long kx = kbX, ky = kbY;
            int mag = (int)ChibiFixed.ISqrt(kx * kx + ky * ky);
            int kb = KnockbackM(f.PctM, pctM, f.Weight, mag, false, 1000);
            long ls = (long)kb * ChibiPercentConst.LsSubPerKb / 1000;
            int st = HitstunFor(kb, mag, stun);
            f.Stun = Math.Max(f.Stun, st);
            Interrupt(s, i);
            f.AirUsedMask = 0;
            f.LaunchDecX = (int)((long)ChibiPercentConst.LaunchDecay * Math.Abs(kx) / mag);
            f.LaunchDecY = (int)((long)ChibiPercentConst.LaunchDecay * ky / mag);
            f.LaunchGravP = LaunchGravityP(kb);
            f.StartLaunchAge(StrongKb(kbX, kbY));        // rules v24 (ED-1)
            SetKnockback(f, dir * (int)(ls * Math.Abs(kx) / mag), (int)(ls * ky / mag));
            if (f.Vy > 0) f.Grounded = false;
        }

        /// <summary>Ocean: during Telegraph..Rise the respawn point moves under MAIN (05-ocean.md).</summary>
        static void HazardRespawnOverride(ChibiSimState s, ref int x, ref int y)
        {
            if (!s.HazardsOn || s.HazPhase == ChibiHazardConst.Idle) return;
            if (ChibiStageTable.Get(s.Stage).Hazard != ChibiStageTable.HazardWhale) return;
            x = ChibiHazardConst.WhaleRespawnX; y = ChibiHazardConst.WhaleRespawnY;
        }

        static void MixHazardHash(ref uint h)
        {
            var c = ChibiHazardConst.Constants;
            for (int k = 0; k < c.Length; k++) ChibiSimState.Mix(ref h, c[k]);
        }

        // ================================================================== read-only helpers (view / HUD / CPU)

        /// <summary>Volcano: current fire column top Y (milli) during Erupt, else the lava top (FireBottom).</summary>
        public static int FireColumnTop(ChibiSimState s)
        {
            if (s.HazPhase != ChibiHazardConst.PhaseFireErupt) return ChibiHazardConst.FireBottom;
            int k = Math.Min(s.HazElapsed + 1, ChibiHazardConst.FireRiseTicks);
            return ChibiHazardConst.FireBottom + ChibiHazardConst.FireRise * k / ChibiHazardConst.FireRiseTicks;
        }

        /// <summary>Volcano: lane <paramref name="lane"/> (0 left at FireLaneLeftX, 1 right at FireLaneRightX) is part of the current event.</summary>
        public static bool FireLaneOn(ChibiSimState s, int lane) =>
            s.HazPhase != ChibiHazardConst.Idle && (s.HazArg == 2 || s.HazArg == lane);

        /// <summary>Bird Lair: the gust is blowing (the whole swipe phase). Direction = <see cref="ChibiSimState.HazArg"/>.</summary>
        public static bool BirdGustActive(ChibiSimState s) =>
            ChibiStageTable.Get(s.Stage).Hazard == ChibiStageTable.HazardBird && s.HazPhase == ChibiHazardConst.PhaseBirdSwipe;

        /// <summary>Bird Lair: the body is crossing the arena this step.</summary>
        public static bool BirdBodyActive(ChibiSimState s) =>
            BirdGustActive(s) && s.HazElapsed >= ChibiHazardConst.BirdBodyStart &&
            s.HazElapsed < ChibiHazardConst.BirdBodyStart + ChibiHazardConst.BirdBodyTicks;

        /// <summary>Bird Lair: body centre X (milli) for the current swipe step (meaningful while <see cref="BirdBodyActive"/>).</summary>
        public static int BirdBodyX(ChibiSimState s)
        {
            int t = Math.Max(0, s.HazElapsed - ChibiHazardConst.BirdBodyStart);
            return -s.HazArg * ChibiHazardConst.BirdBodyStartX + s.HazArg * ChibiHazardConst.BirdBodySpeed * t;
        }

        /// <summary>Ocean: whale body bottom Y (milli); 12000 (off the top) while idle or telegraphing.</summary>
        public static int WhaleBottom(ChibiSimState s)
        {
            switch (s.HazPhase)
            {
                case ChibiHazardConst.PhaseWhaleDescend:
                    return ChibiHazardConst.WhaleTopY - ChibiHazardConst.WhaleDescendSpeed * s.HazElapsed;
                case ChibiHazardConst.PhaseWhaleHold: return ChibiHazardConst.WhaleHoldY;
                case ChibiHazardConst.PhaseWhaleRise:
                    return ChibiHazardConst.WhaleHoldY + ChibiHazardConst.WhaleRiseSpeed * s.HazElapsed;
                default: return ChibiHazardConst.WhaleTopY;
            }
        }
    }
}
