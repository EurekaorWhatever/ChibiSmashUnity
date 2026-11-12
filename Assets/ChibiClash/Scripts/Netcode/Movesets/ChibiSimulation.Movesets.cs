using System;

namespace ChibiClash.Netcode
{
    /// <summary>
    /// Rules v18: input resolution (design/gdd/movesets/README.md section 1) and the generic, data-driven moveset
    /// mechanics (section 5) for slots &gt;= <see cref="ChibiMovesetData.FirstNewSlot"/>. Integers only, no RNG draws,
    /// every mutable value lives in <see cref="ChibiFighterSim"/> / <see cref="ChibiProjectileSim"/> /
    /// <see cref="ChibiZoneSim"/> (DEVELOPER-GUIDE section 3). Slots 0-6 keep their pre-v18 code paths untouched.
    /// </summary>
    public static partial class ChibiSimulation
    {
        const int FirstNew = ChibiMovesetData.FirstNewSlot;
        /// <summary>Stick directions of the resolution table.</summary>
        const int DirN = 0, DirF = 1, DirBK = 2, DirU = 3, DirD = 4;
        /// <summary><see cref="PendingHit"/>.Src: who delivers the hit (counters only catch melee, or projectiles with counter:proj).</summary>
        const int SrcOther = 0, SrcMelee = 1, SrcProj = 2;
        /// <summary>ApplyHit results.</summary>
        const int HitNormal = 0, HitCountered = 1, HitReflected = 2;

        static ChibiMove RowOf(ChibiFighterSim f) => ChibiMoveTable.Get(f.Class, f.MoveSlot);

        static int PackDir(int dx, int dy) => (dx + 1) + 3 * (dy + 1);
        static int DirXOf(int packed) => packed % 3 - 1;
        static int DirYOf(int packed) => packed / 3 - 1;

        /// <summary>The move tick AdvanceMove processed this step is inside the row's active window.</summary>
        static bool InActive(ChibiFighterSim f)
        {
            int t = f.MoveTick - 1;
            return !f.Charging && t >= f.MoveStartup && t < f.MoveStartup + f.MoveActive;
        }

        // ================================================================== input resolution (README 1)

        /// <summary>
        /// README 1.1: vertical wins on diagonals; on the ground every horizontal input is F (FreeActions turned first);
        /// in the air facing is locked (OD-1), so dx == Facing is F and dx == -Facing is BK.
        /// </summary>
        static int StickDir(ChibiFighterSim f, ChibiInputPacket input)
        {
            int dx = input.MoveX, dy = input.MoveY;
            if (dy > 0) return DirU;
            if (dy < 0) return DirD;
            if (dx == 0) return DirN;
            if (f.Grounded) return DirF;
            return dx == f.Facing ? DirF : DirBK;
        }

        /// <summary>Dash window (README 1.2): grounded and DashCd &gt;= DashCooldown - 16 (dash start .. 16 ticks after).</summary>
        static bool InDashWindow(ChibiFighterSim f) =>
            f.Grounded && f.DashCd >= ChibiSimConst.DashCooldown - ChibiMovesetConst.DashAttackWindow;

        /// <summary><c>aironce</c> spent or technique cooldown running: the row cannot start now.</summary>
        static bool CanStartRow(ChibiFighterSim f, int slot)
        {
            var mv = ChibiMoveTable.Get(f.Class, slot);
            var e = mv.Ext;
            if (e.TechIdx >= 0 && f.TechCd[e.TechIdx] > 0) return false;
            if (e.Has(ChibiMoveExt.FAirOnce) && !f.Grounded && (f.AirUsedMask & (1 << (slot - FirstNew))) != 0) return false;
            return true;
        }

        /// <summary>Rules 1-2: Light in the dash window starts Low Sweep (down, if the class has it) or the dash attack.</summary>
        static bool TryDashAttack(ChibiSimState s, int i, int dir)
        {
            var f = s.F[i];
            int slot = dir == DirD && ChibiMovesetData.DashDownSlot[f.Class] >= 0
                ? ChibiMovesetData.DashDownSlot[f.Class] : ChibiMovesetData.DashSlot[f.Class];
            if (slot < 0 || !CanStartRow(f, slot)) return false;
            if (f.DashDir != 0) f.Facing = f.DashDir;
            f.DashTicks = 0;
            StartMove(s, i, slot);
            return true;
        }

        /// <summary>
        /// The dashing branch's one new check (README 1.2): a Light edge while grounded with no move running ends the dash
        /// and starts the dash attack. Heavy / Special / skills stay ignored while dashing.
        /// </summary>
        static void DashingActions(ChibiSimState s, int i, ChibiInputPacket input, int edges)
        {
            var f = s.F[i];
            if ((edges & (int)ChibiButtons.Light) == 0 || f.MoveSlot >= 0 || !f.Grounded || f.SpearOut) return;
            TryDashAttack(s, i, StickDir(f, input));
        }

        /// <summary>
        /// README 1.2 precedence, this step's press edges only (D-N1, no buffer). Same-tick priority Light &gt; Heavy &gt;
        /// Special &gt; Skill1 &gt; Skill2 &gt; Skill3. Neutral Light / Heavy and neutral or horizontal skills keep the pre-v18
        /// moves and gates (reload falls through like the old normals loop, SpearOut eats Light / Heavy presses).
        /// </summary>
        static void ResolveActions(ChibiSimState s, int i, ChibiInputPacket input, int edges)
        {
            var f = s.F[i];
            int cls = f.Class;
            bool reloading = f.SetCd > 0 && (cls == (int)FighterClass.Ranged || cls == (int)FighterClass.Magic);
            bool air = !f.Grounded;
            int dir = StickDir(f, input);

            if ((edges & (int)ChibiButtons.Light) != 0)
            {
                if (f.SpearOut) return;                                                       // rules 1-7 gate
                if (!air && InDashWindow(f) && TryDashAttack(s, i, dir)) return;              // rules 1-2
                if (air)                                                                       // rule 5 (OD-2)
                {
                    int sl = ChibiMovesetData.SlotFor(cls, 0, dir, true);
                    if (sl >= 0 && CanStartRow(f, sl)) StartMove(s, i, sl);
                    return;
                }
                if (dir != DirN)                                                               // rule 3
                {
                    int sl = ChibiMovesetData.SlotFor(cls, 0, dir, false);
                    if (sl >= 0 && CanStartRow(f, sl)) StartMove(s, i, sl);
                    return;
                }
                if (!reloading) { StartMove(s, i, 0); return; }                                // rule 4
            }
            if ((edges & (int)ChibiButtons.Heavy) != 0)
            {
                if (f.SpearOut) return;
                if (!air && dir != DirN)                                                       // rule 6
                {
                    int sl = ChibiMovesetData.SlotFor(cls, 1, dir, false);
                    if (sl >= 0) { if (CanStartRow(f, sl)) StartMove(s, i, sl); return; }
                }
                if (!reloading) { StartMove(s, i, 1); return; }                                // rule 7
            }
            if ((edges & (int)ChibiButtons.Special) != 0)                                      // rule 8
            {
                int bdir = dir == DirBK ? DirF : dir;                                          // turnaround side special
                int sl = ChibiMovesetData.SlotFor(cls, 2, bdir, air);
                if (sl >= 0 && CanStartRow(f, sl))
                {
                    if (air && bdir == DirF && input.MoveX != 0) f.Facing = input.MoveX;
                    StartMove(s, i, sl);
                    return;
                }
                // No row for this direction / state, or aironce spent: the press is dropped.
            }
            for (int k = 0; k < 3; k++)                                                        // rules 9-10
            {
                int bit = (int)ChibiButtons.Skill1 << k;
                if ((edges & bit) == 0) continue;
                if (dir == DirU || dir == DirD)
                {
                    int sl = ChibiMovesetData.SlotFor(cls, 3 + k, dir, air);
                    if (sl >= 0)
                    {
                        if (!CanStartRow(f, sl)) continue;                                     // on cooldown / aironce: nothing
                        StartMove(s, i, sl);
                        return;
                    }
                    // Empty technique slot or wrong ground / air: the existing skill k (old behaviour).
                }
                if (f.Cd[k] == 0 && ChibiMoveTable.Get(cls, 2 + k).Exists) { StartMove(s, i, 2 + k); return; }
            }
        }

        // ================================================================== move start / timers

        /// <summary>Technique cooldowns tick with Cd[] (README 1.3).</summary>
        static void MovesetTimers(ChibiFighterSim f)
        {
            var c = f.TechCd;
            for (int k = 0; k < c.Length; k++) if (c[k] > 0) c[k]--;
        }

        /// <summary>
        /// README 1.3 bookkeeping for a CSV row, after the common StartMove part (fixed frames, Patience, combo reset):
        /// technique cooldown, aironce, captured stick direction, charge / hold / counter / landing state reset.
        /// </summary>
        static void StartNewMove(ChibiSimState s, int i, in ChibiMove mv)
        {
            var f = s.F[i];
            var e = mv.Ext;
            f.ChargeHeld = 0; f.Charging = e.Has(ChibiMoveExt.FCharge); f.HoldExt = 0;
            f.CounterStrike = 0; f.LandHitPending = 0;
            if (e.TechIdx >= 0 && mv.Cooldown > 0) f.TechCd[e.TechIdx] = mv.Cooldown;
            if (e.Has(ChibiMoveExt.FAirOnce) && !f.Grounded) f.AirUsedMask |= 1 << (f.MoveSlot - FirstNew);
            var pk = new ChibiInputPacket { Raw = (ushort)f.PrevInput };
            int dx = pk.MoveX, dy = pk.MoveY;
            if (e.Has(ChibiMoveExt.FRoll)) { dx = dx != 0 ? dx : -f.Facing; dy = 0; }
            else if (e.Has(ChibiMoveExt.FTeleport) && dx == 0 && dy == 0) dy = 1;          // neutral teleport goes up
            f.MoveDir = PackDir(dx, dy);
        }

        /// <summary>
        /// <c>charge</c> release (README 5): bonus = BONUS x held / MAX permille on MoveMult; <c>chargelvl</c> swaps in the row
        /// with the highest MIN &lt;= ChargeHeld (same MoveTick, its mult keeps the Patience factor).
        /// </summary>
        static void ReleaseCharge(ChibiFighterSim f, ref ChibiMove mv)
        {
            var e = mv.Ext;
            f.Charging = false;
            if (e.ChargeMax > 0)
                f.MoveMult = (int)((long)f.MoveMult * (1000 + e.ChargeBonus * f.ChargeHeld / e.ChargeMax) / 1000);
            if (!e.Has(ChibiMoveExt.FChargeLevel)) return;
            int best = f.MoveSlot, bestMin = int.MinValue;
            for (int k = e.ChargeGroupBase; k < e.ChargeGroupBase + e.ChargeGroupCount; k++)
            {
                int mn = ChibiMoveTable.Get(f.Class, k).Ext.ChargeLevelMin;
                if (mn <= f.ChargeHeld && mn > bestMin) { best = k; bestMin = mn; }
            }
            if (best == f.MoveSlot) return;
            var nm = ChibiMoveTable.Get(f.Class, best);
            if (mv.Mult > 0) f.MoveMult = (int)((long)f.MoveMult * nm.Mult / mv.Mult);
            f.MoveSlot = best;
            f.MoveStartup = nm.Startup; f.MoveActive = nm.Active; f.MoveRecovery = nm.Recovery;
            f.MoveLockEnd = nm.Startup + nm.Active + nm.Recovery;
            mv = nm;
        }

        // ================================================================== advance (README 5)

        /// <summary>
        /// One tick of a CSV row: charge wait, self velocity (default / dive / roll / recoil), invuln window, first-active-tick
        /// events (restorejump, teleport, zone), hit arming (multi / rehit) and shots, hold extension. Fixed frames.
        /// Returns true when the row forced Vy = 0 this tick (FLevel), like AdvanceMove.
        /// </summary>
        static bool AdvanceNewMove(ChibiSimState s, int i, bool dashing)
        {
            var f = s.F[i];
            var mv = RowOf(f);
            var e = mv.Ext;
            f.Shielding = false;
            bool held = (f.PrevInput & e.Trigger) != 0;
            if (f.Charging)
            {
                if (held && f.ChargeHeld < e.ChargeMax)
                {
                    f.ChargeHeld++;                                     // MoveTick does not advance, no self velocity
                    if (!dashing && f.Grounded) f.Vx = Approach(f.Vx, 0, ChibiSimConst.MoveFriction);
                    return false;
                }
                ReleaseCharge(f, ref mv);
                e = mv.Ext;
            }

            int t = f.MoveTick, aS = f.MoveStartup, aE = aS + f.MoveActive;
            const int vs = ChibiSimConst.VelSub;
            bool level = false;
            if (e.Has(ChibiMoveExt.FDive))
            {
                if (t < aS) { f.Vx = 0; f.Vy = 0; }                     // hover during startup
                else if (t < aE) { f.Vx = f.Facing * mv.SelfVx * vs; f.Vy = mv.SelfVy * vs; }
                else if (!dashing && f.Grounded) f.Vx = Approach(f.Vx, 0, ChibiSimConst.MoveFriction);
            }
            else if (e.Has(ChibiMoveExt.FRoll))
            {
                if (t < aE) f.Vx = DirXOf(f.MoveDir) * mv.SelfVx * vs;
                else if (!dashing && f.Grounded) f.Vx = Approach(f.Vx, 0, ChibiSimConst.MoveFriction);
            }
            else
            {
                if (t == 0 && mv.SelfVy != 0 && !e.Has(ChibiMoveExt.FTeleport)) { f.Vy = mv.SelfVy * vs; f.Grounded = false; }
                if (mv.SelfVx != 0 && t < aE)
                {
                    f.Vx = mv.SelfVx * vs * f.Facing;
                    if ((mv.Flags & ChibiMove.FLevel) != 0) { f.Vy = 0; level = true; }
                }
                else if (e.Has(ChibiMoveExt.FRecoil) && t == aE)
                {
                    f.Vx = e.RecoilVx * vs * f.Facing;
                    f.Vy = e.RecoilVy * vs;
                    if (f.Vy > 0) f.Grounded = false;
                }
                else if (!dashing && f.Grounded) f.Vx = Approach(f.Vx, 0, ChibiSimConst.MoveFriction);
            }
            if (e.Has(ChibiMoveExt.FInvulnWindow) && t == e.InvulnFrom)
                f.Invuln = Math.Max(f.Invuln, e.InvulnTo - e.InvulnFrom + 1);

            if (t >= aS && t < aE)
            {
                int k = t - aS;
                if (k == 0)
                {
                    if (e.Has(ChibiMoveExt.FRestoreJump)) f.AirJumps = Math.Max(f.AirJumps, 1);
                    if (e.Has(ChibiMoveExt.FTeleport)) Teleport(s, f, mv);
                    if (e.Has(ChibiMoveExt.FZone)) SpawnZone(s, i, mv);
                }
                if (e.Has(ChibiMoveExt.FRoll) && t == aE - 1) f.Facing = -DirXOf(f.MoveDir);
                bool arm = k == 0 || (mv.Rehit > 0 && k % mv.Rehit == 0 && (e.MultiHits == 0 || k / mv.Rehit < e.MultiHits));
                if (arm)
                {
                    f.MoveHit = false;
                    if (mv.ProjSpeed > 0 && !e.Has(ChibiMoveExt.FZone)) SpawnMoveShot(s, i, mv);
                }
                if (e.Has(ChibiMoveExt.FHold) && t == aE - 1 && held && f.HoldExt < e.HoldMax)
                {
                    f.HoldExt++; f.MoveActive++; f.MoveLockEnd++;       // the last active tick repeats
                }
            }

            f.MoveTick++;
            if (f.MoveTick >= f.MoveLockEnd) f.MoveSlot = -1;
            return level;
        }

        /// <summary>
        /// <c>teleport:D</c> (README 5) on the first active tick: direction from StartMove (neutral = up), diagonal axes
        /// D x 707 / 1000; clamped inside the blast zone by TeleportMargin; below the main floor top inside the floor span
        /// snaps to the top (Grounded). Platforms ignored. The row's self_vy applies now instead of at tick 0.
        /// </summary>
        static void Teleport(ChibiSimState s, ChibiFighterSim f, in ChibiMove mv)
        {
            var st = ChibiStageTable.Get(s.Stage);
            int dx = DirXOf(f.MoveDir), dy = DirYOf(f.MoveDir), d = mv.Ext.TeleportDist;
            if (dx != 0 && dy != 0) d = d * ChibiMovesetConst.TeleportDiag / 1000;
            // SEC-105 / rules v20: never into a blast zone: inside every edge (active or solid) by TeleportMargin.
            int nx = f.X + dx * d, ny = f.Y + dy * d;
            st.ClampInside(ref nx, ref ny, ChibiMovesetConst.TeleportMargin);
            f.X = nx; f.Y = ny; f.XRem = 0; f.YRem = 0;
            if (ny < 0 && Math.Abs(nx) < st.HalfWidth + ChibiStageTable.SupportHalfW)
            {
                f.Y = 0;
                f.Grounded = true;
                if (f.Vy < 0) f.Vy = 0;
            }
            if (mv.SelfVy != 0) { f.Vy = mv.SelfVy * ChibiSimConst.VelSub; f.Grounded = false; }
        }

        /// <summary>
        /// Projectile row shot (README 4.3): spawn offset box_x / box_y, velocity fan[ShotIdx] / pv / (Facing x speed, 0),
        /// no barrage jitter. Remembers the source slot for bounce / tether / spike / pull.
        /// </summary>
        static void SpawnMoveShot(ChibiSimState s, int i, in ChibiMove mv)
        {
            var f = s.F[i];
            var e = mv.Ext;
            int vx, vy;
            if (e.FanVx != null)
            {
                int n = Math.Min(f.ShotIdx, e.FanVx.Length - 1);
                vx = f.Facing * e.FanVx[n]; vy = e.FanVy[n];
            }
            else if (e.Has(ChibiMoveExt.FExplicitPv)) { vx = f.Facing * e.ProjVx; vy = e.ProjVy; }
            else { vx = f.Facing * mv.ProjSpeed; vy = 0; }
            int slot = SpawnProjectile(s, new ProjSpec
            {
                Owner = i, Kind = mv.ProjKind, X = f.X + f.Facing * mv.ProjOffX, Y = f.Y + mv.ProjOffY, Vx = vx, Vy = vy,
                Radius = mv.ProjR, Life = mv.ProjLife, Mult = f.MoveMult, KbX = mv.KbX, KbY = mv.KbY, Stun = mv.Stun,
                Flags = mv.ProjFlags,
            });
            if (slot >= 0) s.Proj[slot].SrcSlot = f.MoveSlot;
            f.ShotIdx++;
        }

        // ================================================================== fighter hooks around Physics

        /// <summary>Tether pull (README 5 <c>tether</c>), written last before Physics like program velocity.</summary>
        static void MovesetPrePhysics(ChibiSimState s, int i)
        {
            var f = s.F[i];
            if (f.PullTicks <= 0) return;
            if (f.Stun > 0 || !f.InPlay) { f.PullTicks = 0; return; }
            int dx = f.AnchorX - f.X, dy = f.AnchorY - f.Y;
            int m = Math.Max(Math.Abs(dx), Math.Abs(dy));
            if (m <= ChibiMovesetConst.PullArrive) { f.PullTicks = 0; f.Vx /= 2; f.Vy /= 2; return; }
            f.Vx = (int)((long)dx * f.PullSpeed / m);
            f.Vy = (int)((long)dy * f.PullSpeed / m);
            if (f.Vy > 0) f.Grounded = false;
            if (--f.PullTicks == 0) { f.Vx /= 2; f.Vy /= 2; }
        }

        /// <summary>After Physics: landing clears aironce; a dive that lands during its active window goes to recovery and fires its landing hit.</summary>
        static void MovesetPostPhysics(ChibiSimState s, int i)
        {
            var f = s.F[i];
            if (f.Grounded) f.AirUsedMask = 0;
            if (f.MoveSlot < FirstNew || f.Charging || !f.Grounded) return;
            var mv = RowOf(f);
            if (!mv.Ext.Has(ChibiMoveExt.FDive) || !InActive(f)) return;
            f.MoveTick = f.MoveStartup + f.MoveActive;                  // recovery = landing lag
            if (mv.Ext.Has(ChibiMoveExt.FLandHit)) f.LandHitPending = 1;
        }

        // ================================================================== hit detection

        /// <summary>Knockback direction (README 4.4): centered = positional (tie Facing), back = -Facing, else Facing; pull negates.</summary>
        static int RowDir(ChibiFighterSim f, ChibiFighterSim o, in ChibiMove mv)
        {
            int dir;
            if ((mv.Flags & ChibiMove.FCentered) != 0) { int sx = Sign(o.X - f.X); dir = sx != 0 ? sx : f.Facing; }
            else if (mv.Ext.Has(ChibiMoveExt.FBack)) dir = -f.Facing;
            else dir = f.Facing;
            return mv.Ext.Has(ChibiMoveExt.FPull) ? -dir : dir;
        }

        /// <summary>Row hitbox world rectangle (README 4.1; back uses -Facing, centered spans X +- w/2).</summary>
        static void RowBox(ChibiFighterSim f, in ChibiMove mv, out int x0, out int x1, out int y0, out int y1)
        {
            int face = mv.Ext != null && mv.Ext.Has(ChibiMoveExt.FBack) ? -f.Facing : f.Facing;
            if ((mv.Flags & ChibiMove.FCentered) != 0) { x0 = f.X - mv.BoxW / 2; x1 = x0 + mv.BoxW; }
            else if (face > 0) { x0 = f.X + mv.BoxX; x1 = x0 + mv.BoxW; }
            else { x1 = f.X - mv.BoxX; x0 = x1 - mv.BoxW; }
            y0 = f.Y + mv.BoxY; y1 = y0 + mv.BoxH;
        }

        /// <summary>Circle vs hurtbox, strict like Overlaps (README 4.2).</summary>
        static bool CircleHitsStrict(ChibiFighterSim t, int cx, int cy, int r)
        {
            int hx0 = t.X - ChibiSimConst.HurtHalfW, hx1 = t.X + ChibiSimConst.HurtHalfW;
            int hy0 = t.Y, hy1 = t.Y + ChibiSimConst.HurtH;
            long qx = (long)ChibiFixed.Clamp(cx, hx0, hx1) - cx, qy = (long)ChibiFixed.Clamp(cy, hy0, hy1) - cy;
            return qx * qx + qy * qy < (long)r * r;
        }

        static bool RowHits(ChibiFighterSim f, ChibiFighterSim o, in ChibiMove mv)
        {
            if (mv.Ext.Radius > 0)
                return CircleHitsStrict(o, f.X + f.Facing * mv.BoxX, f.Y + mv.BoxY, mv.Ext.Radius);
            RowBox(f, mv, out int x0, out int x1, out int y0, out int y1);
            return Overlaps(o, x0, x1, y0, y1);
        }

        /// <summary>Spike vs a grounded victim pops up (KbY = -KbY / 2); trip vs an airborne victim is the L horizontal tier.</summary>
        static void AdjustKb(ref PendingHit h, ChibiFighterSim victim, ChibiMoveExt e)
        {
            if (e.Has(ChibiMoveExt.FSpike) && victim.Grounded) h.KbY = -h.KbY / 2;
            if (e.Has(ChibiMoveExt.FTrip) && victim.Grounded) h.Trip = true;   // rules v20: grounded trip = set knockback
            if (e.Has(ChibiMoveExt.FTrip) && !victim.Grounded)
            {
                h.KbX = ChibiMovesetConst.TripAirKbX; h.KbY = ChibiMovesetConst.TripAirKbY; h.Stun = ChibiMovesetConst.TripAirStun;
            }
        }

        /// <summary>
        /// Melee probe of a CSV row: landing hit, counter riposte, then the row box / circle on the active ticks (hit-once
        /// per arm; multi re-arms with LINK kb on the non-final hits). A dive that connects pops up and goes to recovery.
        /// </summary>
        static PendingHit NewMoveProbe(ChibiSimState s, int i)
        {
            var f = s.F[i];
            var o = s.F[1 - i];
            var none = default(PendingHit);
            var mv = RowOf(f);
            var e = mv.Ext;
            bool unblockable = (mv.Flags & ChibiMove.FUnblockable) != 0;
            if (f.LandHitPending != 0)
            {
                f.LandHitPending = 0;
                int lx0 = f.X - e.LandW / 2;
                if (!Overlaps(o, lx0, lx0 + e.LandW, f.Y, f.Y + e.LandH)) return none;
                int sx = Sign(o.X - f.X);
                int lm = mv.Mult > 0 ? (int)((long)e.LandMult * f.MoveMult / mv.Mult) : e.LandMult;
                return new PendingHit
                {
                    Valid = true, Mult = lm, KbX = e.LandKbX, KbY = e.LandKbY, Stun = e.LandStun,
                    Dir = sx != 0 ? sx : f.Facing, Unblockable = unblockable, Src = SrcMelee,
                };
            }
            if (f.CounterStrike != 0)
            {
                f.CounterStrike = 0;
                if (!RowHits(f, o, mv)) return none;
                var c = new PendingHit
                {
                    Valid = true, Mult = f.MoveMult, KbX = mv.KbX, KbY = mv.KbY, Stun = mv.Stun, Dir = RowDir(f, o, mv),
                    Unblockable = true, Src = SrcMelee,
                };
                AdjustKb(ref c, o, e);
                return c;
            }
            if (f.MoveHit || mv.ProjSpeed > 0 || mv.Mult <= 0) return none;
            if (e.Has(ChibiMoveExt.FZone) || e.Has(ChibiMoveExt.FCounter) || e.Has(ChibiMoveExt.FRoll)) return none;
            if (!InActive(f) || !RowHits(f, o, mv)) return none;

            f.MoveHit = true;
            int k = f.MoveTick - 1 - f.MoveStartup;
            bool link = e.Has(ChibiMoveExt.FMulti) && mv.Rehit > 0 && k / mv.Rehit < e.MultiHits - 1;
            var h = new PendingHit
            {
                Valid = true, Mult = f.MoveMult,
                KbX = link ? ChibiMovesetConst.LinkKbX : mv.KbX, KbY = link ? ChibiMovesetConst.LinkKbY : mv.KbY,
                Stun = link ? mv.Rehit + ChibiMovesetConst.LinkStunPad : mv.Stun,
                Dir = RowDir(f, o, mv), Unblockable = unblockable, Src = SrcMelee,
            };
            if (!link) AdjustKb(ref h, o, e);
            if (e.Has(ChibiMoveExt.FDive))
            {
                f.Vy = ChibiMovesetConst.DivePopVy * ChibiSimConst.VelSub;   // no dive suicide: pop up, recover in the air
                f.MoveTick = f.MoveStartup + f.MoveActive;
            }
            return h;
        }

        // ================================================================== ApplyHit hooks

        /// <summary>
        /// <c>counter</c> (README 5), checked first in ApplyHit: a non-unblockable melee hit (or projectile with
        /// counter:proj) during the active ticks is cancelled; the defender turns, gets invuln 20, jumps to recovery with
        /// a 20-tick lock and strikes next tick with the row box, unblockable.
        /// </summary>
        static bool CounterCatches(ChibiSimState s, int a, int d, in PendingHit h)
        {
            var D = s.F[d];
            if (h.Unblockable || D.MoveSlot < FirstNew || D.CounterStrike != 0 || !InActive(D)) return false;
            var e = RowOf(D).Ext;
            if (!e.Has(ChibiMoveExt.FCounter)) return false;
            if (!(h.Src == SrcMelee || (h.Src == SrcProj && e.Has(ChibiMoveExt.FCounterProj)))) return false;
            var A = s.F[a];
            if (h.Src == SrcMelee) A.MoveHit = true;
            Hitstop(s, ChibiSimConst.HitstopParry);
            if (A.X != D.X) D.Facing = A.X > D.X ? 1 : -1;
            D.Invuln = Math.Max(D.Invuln, ChibiMovesetConst.CounterInvuln);
            D.MoveTick = D.MoveStartup + D.MoveActive;
            D.MoveLockEnd = D.MoveTick + ChibiMovesetConst.CounterLock;
            D.CounterStrike = 1;
            return true;
        }

        /// <summary>
        /// <c>reflect</c> melee half (README 5): a non-unblockable melee hit from the front with Mult &lt;= W during the
        /// active ticks gets the parry-skill reflect (the caller's existing Parry path).
        /// </summary>
        static bool ReflectsMelee(ChibiSimState s, int a, int d, in PendingHit h)
        {
            var D = s.F[d];
            if (h.Src != SrcMelee || h.Unblockable || D.MoveSlot < FirstNew || !InActive(D)) return false;
            var e = RowOf(D).Ext;
            if (!e.Has(ChibiMoveExt.FReflect) || h.Mult > e.ReflectMaxMult) return false;
            int sx = Sign(s.F[a].X - D.X);
            return sx == 0 || sx == D.Facing;
        }

        /// <summary>
        /// <c>armor</c> (README 5): during move ticks A..B a non-unblockable hit sets no stun and no Interrupt; with KBP &gt; 0
        /// the launch velocity is scaled by KBP / 1000 (still gated by KbMinSq), else the velocity is unchanged. Damage already
        /// ran. Rules v20 (percent-knockback.md 3.2 step 7): a hit with knockback &gt;= ArmorBreakKbM breaks the armor (false).
        /// </summary>
        static bool ArmorAbsorbs(ChibiSimState s, int d, in PendingHit h, int kbM, int vx, int vy)
        {
            var D = s.F[d];
            if (h.Unblockable || D.MoveSlot < FirstNew || D.Charging) return false;
            var e = RowOf(D).Ext;
            if (!e.Has(ChibiMoveExt.FArmor)) return false;
            int t = D.MoveTick - 1;
            int to = e.ArmorTo < 0 ? D.MoveStartup + D.MoveActive - 1 : e.ArmorTo;
            if (t < e.ArmorFrom || t > to) return false;
            if (kbM >= ChibiPercentConst.ArmorBreakKbM) return false;
            if (e.ArmorKbPermille > 0)
                SetKnockback(D, (int)((long)vx * e.ArmorKbPermille / 1000), (int)((long)vy * e.ArmorKbPermille / 1000));
            return true;
        }

        // ================================================================== projectile hooks

        static ChibiMoveExt ProjExt(ChibiSimState s, ChibiProjectileSim p) =>
            p.SrcSlot >= FirstNew ? ChibiMoveTable.Get(s.F[p.Owner].Class, p.SrcSlot).Ext : null;

        /// <summary>Bounce re-arm timer (README 5 <c>bounce</c>): HasHit clears 10 ticks after an enemy hit.</summary>
        static void ProjTimers(ChibiProjectileSim p)
        {
            if (p.Rearm > 0 && --p.Rearm == 0) p.Flags &= ~ChibiProjectileSim.FHasHit;
        }

        /// <summary>Highest stage solid top (main floor, platforms) the centre crossed downward this update, or int.MinValue.</summary>
        static int CrossedTop(ChibiSimState s, int x, int prevY, int y)
        {
            var st = ChibiStageTable.Get(s.Stage);
            int best = int.MinValue;
            if (prevY >= 0 && y <= 0 && Math.Abs(x) < st.HalfWidth) best = 0;
            if (st.HasFloor && st.FloorY > best && prevY >= st.FloorY && y <= st.FloorY && Math.Abs(x) < st.FloorHalfW)
                best = st.FloorY;                                                                         // v21 floor
            var plats = st.Platforms;
            int t = s.GameTicks;
            for (int k = 0; k < plats.Length; k++)
            {
                int top = plats[k].TopAt(t);
                if (top > best && prevY >= top && y <= top && Math.Abs(x - plats[k].CxAt(t)) < plats[k].HalfW) best = top;
            }
            return best;
        }

        static void Bounce(ChibiSimState s, ChibiProjectileSim p, ChibiMoveExt e)
        {
            p.Bounces++;
            p.Mult = e.BounceMult;
            if (p.Bounces >= e.BounceMax) Despawn(s, p);
        }

        static void Anchor(ChibiSimState s, int owner, int x, int y, ChibiMoveExt e)
        {
            var f = s.F[owner];
            if (!f.InPlay) return;
            f.PullTicks = e.TetherMax; f.PullSpeed = e.TetherSpeed; f.AnchorX = x; f.AnchorY = y;
        }

        /// <summary>
        /// After the move: a bouncing shot falling onto a stage top bounces (Vy = -Vy); a grapple touching a top while moving
        /// down anchors there (both tether kinds) and despawns. Returns true when the projectile is gone.
        /// </summary>
        static bool ProjStage(ChibiSimState s, ChibiProjectileSim p, int prevY)
        {
            var e = ProjExt(s, p);
            if (e == null || p.Vy >= 0) return false;
            bool bounce = e.Has(ChibiMoveExt.FBounce);
            bool tether = e.Has(ChibiMoveExt.FTetherSelf) || e.Has(ChibiMoveExt.FTetherFoe);
            if (!bounce && !tether) return false;
            int top = CrossedTop(s, p.X, prevY, p.Y);
            if (top == int.MinValue) return false;
            if (bounce)
            {
                p.Vy = -p.Vy; p.Y = top; p.YRem = 0;
                Bounce(s, p, e);
                return !p.Active;
            }
            Anchor(s, p.Owner, p.X, top, e);
            Despawn(s, p);
            return true;
        }

        /// <summary>
        /// <c>reflect</c> projectile half (README 5): an enemy shot overlapping a reflecting row's box during its active
        /// ticks changes owner and reverses, Mult x P / 1000, Life &gt;= 30. It becomes a plain shot (SrcSlot -1). A reflected
        /// spear frees its thrower's SpearOut. Returns true when reflected (no hit this update).
        /// </summary>
        static bool ProjReflected(ChibiSimState s, ChibiProjectileSim p)
        {
            var r = s.F[1 - p.Owner];
            if (r.MoveSlot < FirstNew || !InActive(r)) return false;
            var mv = RowOf(r);
            if (!mv.Ext.Has(ChibiMoveExt.FReflect)) return false;
            RowBox(r, mv, out int x0, out int x1, out int y0, out int y1);
            if (!ChibiFixed.CircleOverlapsAabb(p.X, p.Y, p.Radius, x0, y0, x1 - 1, y1 - 1)) return false;
            if (p.Kind == ChibiProjKind.Spear) s.F[p.Owner].SpearOut = false;
            p.Owner = 1 - p.Owner;
            p.Vx = -p.Vx; p.Vy = -p.Vy;
            if (p.Reflects < ChibiMovesetConst.MaxScaledReflects)                       // SEC-104: growth capped
                p.Mult = (int)((long)p.Mult * mv.Ext.ReflectPermille / 1000);
            p.Reflects++;
            p.Life = Math.Max(p.Life, ChibiMovesetConst.ReflectLifeFloor);
            p.Flags &= ~(ChibiProjectileSim.FHasHit | ChibiProjectileSim.FReturning);
            p.ReturnTicks = 0;
            p.SrcSlot = -1; p.Bounces = 0; p.Rearm = 0;   // Reflects is kept: it bounds the growth across owners
            return true;
        }

        /// <summary>Projectile hit of a CSV row: positional dir for a vertical shot, pull, spike / trip.</summary>
        static void ProjAdjustHit(ChibiSimState s, ChibiProjectileSim p, ref PendingHit h)
        {
            var e = ProjExt(s, p);
            if (e == null) return;
            var victim = s.F[1 - p.Owner];
            if (p.Vx == 0) { int sx = Sign(victim.X - p.X); h.Dir = sx != 0 ? sx : 1; }
            if (e.Has(ChibiMoveExt.FPull)) h.Dir = -h.Dir;
            AdjustKb(ref h, victim, e);
        }

        /// <summary>
        /// After a projectile hit: a bouncing shot reverses (Vx = -Vx, Vy = |Vy|), re-arms after 10 ticks and counts a
        /// bounce; a self tether anchors on the enemy and despawns. Returns true when handled (skip the normal despawn).
        /// </summary>
        static bool ProjAfterHit(ChibiSimState s, ChibiProjectileSim p)
        {
            var e = ProjExt(s, p);
            if (e == null) return false;
            if (e.Has(ChibiMoveExt.FBounce))
            {
                p.Vx = -p.Vx; p.Vy = Math.Abs(p.Vy);
                p.Rearm = ChibiMovesetConst.ProjRearm;
                Bounce(s, p, e);
                return true;
            }
            if (e.Has(ChibiMoveExt.FTetherSelf))
            {
                Anchor(s, p.Owner, p.X, p.Y, e);
                Despawn(s, p);
                return true;
            }
            return false;
        }

        /// <summary>Expiry: a self tether that hit nothing anchors in the air where it ended.</summary>
        static void ProjExpire(ChibiSimState s, ChibiProjectileSim p)
        {
            var e = ProjExt(s, p);
            if (e != null && e.Has(ChibiMoveExt.FTetherSelf)) Anchor(s, p.Owner, p.X, p.Y, e);
        }

        // ================================================================== zones (README 5 zone:*)

        static void InitZones(ChibiSimState s)
        {
            for (int n = 0; n < s.Zones.Length; n++) s.Zones[n].Active = false;
        }

        /// <summary>Spawns the row's zone on its first active tick: one per (owner, slot), a new one replaces the old; full pool drops it.</summary>
        static void SpawnZone(ChibiSimState s, int i, in ChibiMove mv)
        {
            var f = s.F[i];
            for (int n = 0; n < s.Zones.Length; n++)
                if (s.Zones[n].Active && s.Zones[n].Owner == i && s.Zones[n].Slot == f.MoveSlot) s.Zones[n].Active = false;
            ChibiZoneSim z = null;
            for (int n = 0; n < s.Zones.Length; n++) if (!s.Zones[n].Active) { z = s.Zones[n]; break; }
            if (z == null) { s.DroppedSummons++; return; }
            RowBox(f, mv, out int x0, out int x1, out int y0, out int y1);
            z.Active = true; z.Owner = i; z.Slot = f.MoveSlot;
            z.X0 = x0; z.X1 = x1; z.Y0 = y0; z.Y1 = y1;
            z.Age = 0; z.Mult = f.MoveMult; z.Facing = f.Facing; z.SpawnTick = s.GameTicks;
        }

        /// <summary>
        /// One update of every zone, slots in index order, after the projectiles: slow while overlapping; contact (armed
        /// after A, life B) and timed (live from A for B ticks) hit once on the first overlap and despawn; periodic pulses
        /// at ages 0, A, 2A ... below B. Hits go through ApplyHit from the owner (blockable, not counterable).
        /// </summary>
        static void UpdateZones(ChibiSimState s)
        {
            for (int n = 0; n < s.Zones.Length; n++)
            {
                var z = s.Zones[n];
                if (!z.Active || z.SpawnTick == s.GameTicks) continue;
                var mv = ChibiMoveTable.Get(s.F[z.Owner].Class, z.Slot);
                var e = mv.Ext;
                var o = s.F[1 - z.Owner];
                int age = z.Age++;
                bool over = Overlaps(o, z.X0, z.X1, z.Y0, z.Y1);
                if (e.ZoneSlow > 0 && over && o.InPlay) o.SlowTicks = Math.Max(o.SlowTicks, e.ZoneSlow);
                bool live, once = true;
                switch (e.ZoneKind)
                {
                    case 1: if (age >= e.ZoneB) { z.Active = false; continue; } live = age >= e.ZoneA; break;
                    case 2: if (age >= e.ZoneA + e.ZoneB) { z.Active = false; continue; } live = age >= e.ZoneA; break;
                    default:
                        if (age >= e.ZoneB) { z.Active = false; continue; }
                        live = e.ZoneA > 0 && age % e.ZoneA == 0; once = false;
                        break;
                }
                if (!live || !over || o.Invuln > 0 || !o.InPlay) continue;
                int sx = Sign(o.X - (z.X0 + z.X1) / 2);
                var h = new PendingHit
                {
                    Valid = true, Mult = z.Mult, KbX = mv.KbX, KbY = mv.KbY, Stun = mv.Stun, Dir = sx != 0 ? sx : z.Facing,
                    Unblockable = (mv.Flags & ChibiMove.FUnblockable) != 0, Src = SrcOther,
                };
                AdjustKb(ref h, o, e);
                ApplyHit(s, z.Owner, 1 - z.Owner, h);
                if (once) z.Active = false;
            }
        }
    }
}
