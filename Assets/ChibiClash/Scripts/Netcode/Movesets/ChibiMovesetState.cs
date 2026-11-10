namespace ChibiClash.Netcode
{
    // Rules v18 moveset state (design/gdd/movesets/README.md section 5). Every field below is rollback state: copied by
    // CopyFrom and mixed into ComputeChecksum (the reflection coverage tests in ChibiSyncVerificationTests check both).

    public sealed partial class ChibiFighterSim
    {
        /// <summary>Technique cooldowns (README 1.3): index k*2 + (U ? 0 : 1) for Skill k + Up / Down. Ticked with Cd[].</summary>
        public readonly int[] TechCd = new int[ChibiMovesetConst.TechCdSlots];
        /// <summary>Ticks the trigger was held at move tick 0 of a charge move (README 5 <c>charge</c>).</summary>
        public int ChargeHeld;
        /// <summary>A charge move is waiting at tick 0 for the trigger to be released (or MAX).</summary>
        public bool Charging;
        /// <summary>Active ticks added by <c>hold</c> so far.</summary>
        public int HoldExt;
        /// <summary>Bit (slot - 7) per <c>aironce</c> move started airborne; cleared on landing, on a stunning hit, on respawn.</summary>
        public int AirUsedMask;
        /// <summary>Stick direction captured at StartMove, packed (dx + 1) + 3 (dy + 1): roll and teleport direction.</summary>
        public int MoveDir;
        /// <summary>1 = a counter triggered last tick: this tick runs the one-tick unblockable riposte probe.</summary>
        public int CounterStrike;
        /// <summary>1 = the dive landed this tick: the melee probe runs the <c>landhit</c> box.</summary>
        public int LandHitPending;
        /// <summary>Tether pull ticks left, pull speed (sub/tick) and the anchor (feet milli) (README 5 <c>tether</c>).</summary>
        public int PullTicks, PullSpeed, AnchorX, AnchorY;

        /// <summary>Clears every moveset field (InitFighter).</summary>
        public void ResetMoveset()
        {
            for (int k = 0; k < TechCd.Length; k++) TechCd[k] = 0;
            ChargeHeld = 0; Charging = false; HoldExt = 0; AirUsedMask = 0; MoveDir = 0; CounterStrike = 0; LandHitPending = 0;
            PullTicks = 0; PullSpeed = 0; AnchorX = 0; AnchorY = 0;
        }

        /// <summary>
        /// SEC-106 (rules v19): per-move runtime flags that must never outlive the move that set them. Cleared by
        /// Interrupt and at every StartMove (all slots, ult included), so a stale charge / riposte / hold / landing hit /
        /// tether pull cannot leak into the next action.
        /// </summary>
        public void ClearMoveRuntime()
        {
            Charging = false; CounterStrike = 0; HoldExt = 0; LandHitPending = 0; PullTicks = 0;
        }

        void CopyMoveset(ChibiFighterSim o)
        {
            for (int k = 0; k < TechCd.Length; k++) TechCd[k] = o.TechCd[k];
            ChargeHeld = o.ChargeHeld; Charging = o.Charging; HoldExt = o.HoldExt; AirUsedMask = o.AirUsedMask;
            MoveDir = o.MoveDir; CounterStrike = o.CounterStrike; LandHitPending = o.LandHitPending;
            PullTicks = o.PullTicks; PullSpeed = o.PullSpeed; AnchorX = o.AnchorX; AnchorY = o.AnchorY;
        }

        void HashMoveset(ref uint h)
        {
            for (int k = 0; k < TechCd.Length; k++) ChibiSimState.Mix(ref h, TechCd[k]);
            ChibiSimState.Mix(ref h, ChargeHeld); ChibiSimState.Mix(ref h, Charging ? 1 : 0); ChibiSimState.Mix(ref h, HoldExt);
            ChibiSimState.Mix(ref h, AirUsedMask); ChibiSimState.Mix(ref h, MoveDir); ChibiSimState.Mix(ref h, CounterStrike);
            ChibiSimState.Mix(ref h, LandHitPending); ChibiSimState.Mix(ref h, PullTicks); ChibiSimState.Mix(ref h, PullSpeed);
            ChibiSimState.Mix(ref h, AnchorX); ChibiSimState.Mix(ref h, AnchorY);
        }
    }

    public sealed partial class ChibiProjectileSim
    {
        /// <summary>
        /// Move slot (&gt;= 7) of the owner's class that spawned this shot, for its bounce / tether / spike / pull data;
        /// -1 for every other projectile and after a reflect (a reflected shot is a plain shot).
        /// </summary>
        public int SrcSlot;
        /// <summary>Bounces so far (README 5 <c>bounce</c>).</summary>
        public int Bounces;
        /// <summary>Ticks until a bouncing shot that hit the enemy may hit again (clears HasHit at 0).</summary>
        public int Rearm;
        /// <summary>Times this shot was reflected by a reflect row (SEC-104: only the first two scale its Mult).</summary>
        public int Reflects;

        /// <summary>Spawn defaults (SpawnProjectile writes every field).</summary>
        public void ResetMoveset() { SrcSlot = -1; Bounces = 0; Rearm = 0; Reflects = 0; }

        void CopyMoveset(ChibiProjectileSim o) { SrcSlot = o.SrcSlot; Bounces = o.Bounces; Rearm = o.Rearm; Reflects = o.Reflects; }

        /// <summary>Mixes the moveset fields (called by ChibiSimState.ComputeChecksum for live slots).</summary>
        public void HashMoveset(ref uint h)
        {
            ChibiSimState.Mix(ref h, SrcSlot); ChibiSimState.Mix(ref h, Bounces); ChibiSimState.Mix(ref h, Rearm);
            ChibiSimState.Mix(ref h, Reflects);   // rules v19
        }
    }

    /// <summary>
    /// One stationary zone (README 5 <c>zone:*</c>, rules v18): a world rectangle computed once at spawn, hit data from
    /// the owner's move row <see cref="Slot"/>. Pool of <see cref="ChibiMovesetConst.ZonePool"/>, updated after the
    /// projectiles in slot order.
    /// </summary>
    public sealed class ChibiZoneSim
    {
        public bool Active;
        public int Owner, Slot;
        /// <summary>World rectangle [X0, X1) x [Y0, Y1), milli.</summary>
        public int X0, X1, Y0, Y1;
        /// <summary>Updates done (0 on the first update, the step after the spawn).</summary>
        public int Age;
        /// <summary>MoveMult snapshot at spawn (charge / Patience included).</summary>
        public int Mult;
        /// <summary>Owner's Facing at spawn: knockback direction on a tie.</summary>
        public int Facing;
        /// <summary>GameTicks of the spawning step: the first update is the next game step.</summary>
        public int SpawnTick;

        public void CopyFrom(ChibiZoneSim o)
        {
            Active = o.Active; Owner = o.Owner; Slot = o.Slot; X0 = o.X0; X1 = o.X1; Y0 = o.Y0; Y1 = o.Y1; Age = o.Age;
            Mult = o.Mult; Facing = o.Facing; SpawnTick = o.SpawnTick;
        }

        public void Hash(ref uint h)
        {
            ChibiSimState.Mix(ref h, Active ? 1 : 0);
            if (!Active) return;
            ChibiSimState.Mix(ref h, Owner); ChibiSimState.Mix(ref h, Slot); ChibiSimState.Mix(ref h, X0);
            ChibiSimState.Mix(ref h, X1); ChibiSimState.Mix(ref h, Y0); ChibiSimState.Mix(ref h, Y1);
            ChibiSimState.Mix(ref h, Age); ChibiSimState.Mix(ref h, Mult); ChibiSimState.Mix(ref h, Facing);
            ChibiSimState.Mix(ref h, SpawnTick);
        }
    }

    public sealed partial class ChibiSimState
    {
        /// <summary>Moveset zone pool (rules v18). A full pool drops the zone and counts it in DroppedSummons.</summary>
        public readonly ChibiZoneSim[] Zones = NewZones();

        static ChibiZoneSim[] NewZones()
        {
            var z = new ChibiZoneSim[ChibiMovesetConst.ZonePool];
            for (int i = 0; i < z.Length; i++) z[i] = new ChibiZoneSim();
            return z;
        }

        void CopyMoveset(ChibiSimState o)
        {
            for (int i = 0; i < Zones.Length; i++) Zones[i].CopyFrom(o.Zones[i]);
        }

        void HashMoveset(ref uint h)
        {
            for (int i = 0; i < Zones.Length; i++) Zones[i].Hash(ref h);
        }
    }
}
