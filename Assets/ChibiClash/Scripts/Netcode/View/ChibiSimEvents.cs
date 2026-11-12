using System.Collections.Generic;

namespace ChibiClash.Netcode
{
    /// <summary>Kind of a presentation event derived from two consecutive simulation states.</summary>
    public enum ChibiSimEventKind : byte
    {
        /// <summary>Fighter started a move. Value = move slot (0 light, 1 heavy, 2-4 skills, 5 ult), Value2 = combo index used.</summary>
        MoveStarted,
        /// <summary>Fighter lost HP. Value = amount.</summary>
        Damaged,
        /// <summary>Fighter's shield went up (On) or down.</summary>
        ShieldChanged,
        /// <summary>Fighter landed a parry (its Parries counter increased).</summary>
        Parried,
        /// <summary>Fighter entered (On) or left hitstun.</summary>
        StunChanged,
        /// <summary>Fighter was blasted off-stage and respawned at the top.</summary>
        RingOut,
        /// <summary>Fighter's HP hit zero on the tick the match ended.</summary>
        KO,
        /// <summary>Projectile slot Index became live (or now holds a new projectile, see <see cref="ChibiSimEvents.SameProjectile"/>). Value = owner fighter, Value2 = (int)<see cref="ChibiProjKind"/>.</summary>
        ProjectileSpawned,
        /// <summary>Projectile slot Index stopped being live (or was reused). Value = owner fighter, Value2 = (int)<see cref="ChibiProjKind"/> of the projectile that left.</summary>
        ProjectileGone,
        /// <summary>Fighter started a dash (DashTicks rose from 0, or re-armed while dashing). Value = DashDir (-1/+1).</summary>
        DashStarted,
        /// <summary>Ult cinematic began (CinematicRealTicks rose). Index = CinematicFighter (the ult user).</summary>
        CinematicStarted,
        /// <summary>
        /// A program move (multiphase-conversion.md section 2, <see cref="ChibiSimEvents.IsProgram"/>) ran frame Value2.
        /// Value = move slot, On = MovePhase != 0 in the current state. One event per frame run, in order, so a
        /// multi-step diff skips nothing. Not emitted for the frame on which the move ended (MoveSlot already -1).
        /// </summary>
        ProgramFrame,
        /// <summary>A program move's MovePhase changed (Grab connected, Leap Slam landed, Smash slammed, Kick lerp/hold/release). Value = new phase, Value2 = move slot.</summary>
        MovePhaseChanged,
        /// <summary>Minion slot Index became live (or now holds a new minion, see <see cref="ChibiSimEvents.SameMinion"/>). Value = owner fighter.</summary>
        MinionSpawned,
        /// <summary>Minion slot Index stopped being live (or was reused). Value = owner fighter of the minion that left.</summary>
        MinionGone,
        /// <summary>Cloud slot Index became live (or now holds a new cloud, see <see cref="ChibiSimEvents.SameCloud"/>). Value = owner fighter.</summary>
        CloudSpawned,
        /// <summary>Cloud slot Index stopped being live (or was reused). Value = owner fighter of the cloud that left.</summary>
        CloudGone,
        /// <summary>Cloud slot Index struck (its BoltTicks was set to CloudBolt this step). Value = owner fighter.</summary>
        CloudStrike,
        /// <summary>Fighter did the Super Jump pose (SuperJumpCd was re-armed, rules v15, F:375-382).</summary>
        PoseSuperJump,
        /// <summary>Fighter's ult prompt changed (rules v15). Value = new <see cref="ChibiUltPrompt"/>.</summary>
        UltPromptChanged,
        /// <summary>Training revive (rules v16): the fighter was KO'd and restored to full HP.</summary>
        Revived,
    }

    /// <summary>One presentation event. Index is the fighter (0/1) or, for projectile / minion / cloud events, the pool slot.</summary>
    public struct ChibiSimEvent
    {
        /// <summary>What happened.</summary>
        public ChibiSimEventKind Kind;
        /// <summary>Fighter index, or projectile slot for projectile events.</summary>
        public int Index;
        /// <summary>Kind-specific payload (slot, amount, owner).</summary>
        public int Value;
        /// <summary>Second payload (combo index for MoveStarted).</summary>
        public int Value2;
        /// <summary>On/off flag for ShieldChanged and StunChanged.</summary>
        public bool On;

        /// <summary>Convenience constructor.</summary>
        public ChibiSimEvent(ChibiSimEventKind kind, int index, int value = 0, int value2 = 0, bool on = false)
        {
            Kind = kind; Index = index; Value = value; Value2 = value2; On = on;
        }
    }

    /// <summary>
    /// Pure, allocation-free diff of two simulation states into presentation events. The view calls it once per
    /// new tick with (state it last showed, state now). Each real change produces exactly one event because the
    /// caller advances "last shown" to "now" after every call.
    ///
    /// After a rollback the view should call it once with (pre-rollback shown state, re-simulated state); no
    /// special handling is needed yet (the offline session never rolls back). Integer-only, no UnityEngine.
    /// </summary>
    public static class ChibiSimEvents
    {
        /// <summary>
        /// Append the events between <paramref name="prev"/> and <paramref name="cur"/> to <paramref name="output"/>
        /// (the list is not cleared). Neither state is modified.
        /// </summary>
        /// <example><code>events.Clear(); ChibiSimEvents.Diff(shown, session.State, events); shown.CopyFrom(session.State);</code></example>
        public static void Diff(ChibiSimState prev, ChibiSimState cur, List<ChibiSimEvent> output)
        {
            for (int i = 0; i < 2; i++) DiffFighter(i, prev.F[i], cur.F[i], output);

            // Rise only (a new ult re-arms the counter upward); the per-real-tick countdown never emits.
            if (cur.CinematicRealTicks > prev.CinematicRealTicks && cur.CinematicFighter >= 0 && cur.CinematicFighter < 2)
                output.Add(new ChibiSimEvent(ChibiSimEventKind.CinematicStarted, cur.CinematicFighter));

            // Rules v20 (damage percent + stocks): a KO is any blast-line KO, emitted per fighter in DiffFighter from KosTaken.
            // HP no longer exists in the match, so the old "HpU <= 0 at MatchOver" test would never fire.

            int n = cur.Proj.Length < prev.Proj.Length ? cur.Proj.Length : prev.Proj.Length;
            for (int i = 0; i < n; i++)
            {
                var a = prev.Proj[i];
                var b = cur.Proj[i];
                if (a.Active && b.Active)
                {
                    // Slot freed and reused within one step (A6): the new projectile has a new SpawnFrame. Life is not
                    // a usable signal (the spear's return floor raises it; a reuse can have less life than the old one).
                    if (!SameProjectile(a, b))
                    {
                        output.Add(new ChibiSimEvent(ChibiSimEventKind.ProjectileGone, i, a.Owner, (int)a.Kind));
                        output.Add(new ChibiSimEvent(ChibiSimEventKind.ProjectileSpawned, i, b.Owner, (int)b.Kind));
                    }
                }
                else if (!a.Active && b.Active) output.Add(new ChibiSimEvent(ChibiSimEventKind.ProjectileSpawned, i, b.Owner, (int)b.Kind));
                else if (a.Active && !b.Active) output.Add(new ChibiSimEvent(ChibiSimEventKind.ProjectileGone, i, a.Owner, (int)a.Kind));
            }

            // Minions and clouds (minion-cloud-conversion.md, TD amendment 5): identity is (slot, SpawnTick, Owner).
            n = cur.Minions.Length < prev.Minions.Length ? cur.Minions.Length : prev.Minions.Length;
            for (int i = 0; i < n; i++)
            {
                var a = prev.Minions[i];
                var b = cur.Minions[i];
                bool gone = a.Active && (!b.Active || !SameMinion(a, b));
                bool spawned = b.Active && (!a.Active || !SameMinion(a, b));
                if (gone) output.Add(new ChibiSimEvent(ChibiSimEventKind.MinionGone, i, a.Owner));
                if (spawned) output.Add(new ChibiSimEvent(ChibiSimEventKind.MinionSpawned, i, b.Owner));
            }

            n = cur.Clouds.Length < prev.Clouds.Length ? cur.Clouds.Length : prev.Clouds.Length;
            for (int i = 0; i < n; i++)
            {
                var a = prev.Clouds[i];
                var b = cur.Clouds[i];
                bool same = a.Active && b.Active && SameCloud(a, b);
                if (a.Active && !same) output.Add(new ChibiSimEvent(ChibiSimEventKind.CloudGone, i, a.Owner));
                if (b.Active && !same) output.Add(new ChibiSimEvent(ChibiSimEventKind.CloudSpawned, i, b.Owner));
                // The sim sets BoltTicks = CloudBolt (9) on the strike step and decrements it from the next step
                // (UpdateClouds), so "now 9 and was not 9 for this cloud" is exactly one strike per period (66 > 1).
                if (b.Active && b.BoltTicks == ChibiSimConst.CloudBolt && (!same || a.BoltTicks != ChibiSimConst.CloudBolt))
                    output.Add(new ChibiSimEvent(ChibiSimEventKind.CloudStrike, i, b.Owner));
            }
        }

        /// <summary>
        /// True when two snapshots of one live minion slot hold the same minion (TD amendment 5, like A6):
        /// SpawnTick and Owner match. Both must be Active; not checked here.
        /// </summary>
        /// <example><code>bool lerp = a.Active &amp;&amp; b.Active &amp;&amp; ChibiSimEvents.SameMinion(a, b);</code></example>
        public static bool SameMinion(ChibiMinionSim a, ChibiMinionSim b) => a.SpawnTick == b.SpawnTick && a.Owner == b.Owner;

        /// <summary>
        /// True when two snapshots of one live cloud slot hold the same cloud (TD amendment 5, like A6):
        /// SpawnTick and Owner match. Both must be Active; not checked here.
        /// </summary>
        /// <example><code>bool lerp = a.Active &amp;&amp; b.Active &amp;&amp; ChibiSimEvents.SameCloud(a, b);</code></example>
        public static bool SameCloud(ChibiCloudSim a, ChibiCloudSim b) => a.SpawnTick == b.SpawnTick && a.Owner == b.Owner;

        /// <summary>
        /// True when two snapshots of one live slot hold the same projectile (amendment A6). SpawnFrame is the
        /// identity: a reused slot always gets the current GameTicks, which differs from any projectile already shown.
        /// Owner and Kind are also compared so a rollback diff (shown state vs re-simulated state) that spawned a
        /// different projectile on the same tick is still reported as a reuse. Both must be Active; not checked here.
        /// </summary>
        /// <example><code>bool lerp = a.Active &amp;&amp; b.Active &amp;&amp; ChibiSimEvents.SameProjectile(a, b);</code></example>
        public static bool SameProjectile(ChibiProjectileSim a, ChibiProjectileSim b) =>
            a.SpawnFrame == b.SpawnFrame && a.Owner == b.Owner && a.Kind == b.Kind;

        /// <summary>Upper bound on ProgramFrame events per fighter per diff (longest program lock is the Kick's 114 frames).</summary>
        const int MaxProgramFrames = 120;

        /// <summary>
        /// True when (class, slot) is a program move: the seven skills of multiphase-conversion.md section 2 plus the three
        /// program ultimates of ultimates-conversion.md (slot 5 of Heavy, Ranged, Polearm; Sword Aura / Fire Ring are not
        /// programs, they run with MoveSlot -1). Mirrors
        /// ChibiSimulation.ProgramOf (private); keep the two in sync (pinned by IsProgram_MatchesSimProgramTable).
        /// </summary>
        /// <example><code>if (ChibiSimEvents.IsProgram(f.Class, f.MoveSlot)) { /* frame-driven effects */ }</code></example>
        public static bool IsProgram(int cls, int slot)
        {
            switch ((FighterClass)cls)
            {
                case FighterClass.Melee: return slot == 4;                  // Thrust Barrage
                case FighterClass.Heavy: return slot == 2 || slot == 3 || slot == 5;     // Grab, Tornado Spin, ULT Smash
                case FighterClass.Ranged: return slot == 2 || slot == 4 || slot == 5;    // Backstep Shot, Arrow Barrage, ULT Thousand Arrows
                case FighterClass.Polearm: return slot == 2 || slot == 3 || slot == 5;   // Leap Slam, Spin Guard, ULT Kick
                default: return false;
            }
        }

        static void DiffFighter(int i, ChibiFighterSim p, ChibiFighterSim c, List<ChibiSimEvent> output)
        {
            bool started = c.MoveSlot >= 0 && (c.MoveSlot != p.MoveSlot || c.MoveTick < p.MoveTick);
            if (started)
            {
                int combo = 0;
                int len = c.Stats.PatternLength;
                if (c.MoveSlot == 0 && len > 0) combo = (c.ComboIdx + len - 1) % len;   // StartMove advanced it past the index used
                output.Add(new ChibiSimEvent(ChibiSimEventKind.MoveStarted, i, c.MoveSlot, combo));
            }

            if (c.MoveSlot >= 0 && IsProgram(c.Class, c.MoveSlot))
            {
                // Frames run = [from, MoveTick - 1]; a new move ran frame 0 inline on its press tick (frame-0 rule).
                int from = started ? 0 : p.MoveTick;
                int to = c.MoveTick - 1;
                if (to - from >= MaxProgramFrames) from = to - MaxProgramFrames + 1;
                bool on = c.MovePhase != 0;
                for (int k = from; k <= to; k++)
                    output.Add(new ChibiSimEvent(ChibiSimEventKind.ProgramFrame, i, c.MoveSlot, k, on));
                int prevPhase = started ? 0 : p.MovePhase;
                if (c.MovePhase != prevPhase)
                    output.Add(new ChibiSimEvent(ChibiSimEventKind.MovePhaseChanged, i, c.MovePhase, c.MoveSlot));
            }

            // Rules v20: damage is a PERCENT increase (PctM, milli-percent). Fires on any increase; Value is the gain rounded
            // half-up to whole percent for the popup / sounds (a tiny burn tick rounds to 0 and stays silent, like before).
            // A KO resets PctM to 0 (a decrease), which is the KO event below, never a Damaged.
            if (c.PctM > p.PctM)
                output.Add(new ChibiSimEvent(ChibiSimEventKind.Damaged, i, (c.PctM - p.PctM + 500) / 1000));

            if (c.Shielding != p.Shielding) output.Add(new ChibiSimEvent(ChibiSimEventKind.ShieldChanged, i, on: c.Shielding));

            if (c.Parries > p.Parries) output.Add(new ChibiSimEvent(ChibiSimEventKind.Parried, i));

            bool ps = p.Stun > 0, cs = c.Stun > 0;
            if (ps != cs) output.Add(new ChibiSimEvent(ChibiSimEventKind.StunChanged, i, on: cs));

            // Rules v20: every KO crosses an active blast line (KosTaken counts them, training included). The old HP-era
            // ring-out signature (respawn invuln at (0, RespawnY)) no longer exists, so RingOut is not emitted any more.
            if (c.KosTaken > p.KosTaken) output.Add(new ChibiSimEvent(ChibiSimEventKind.KO, i));

            if (c.DashTicks > 0 && (p.DashTicks == 0 || c.DashTicks > p.DashTicks))
                output.Add(new ChibiSimEvent(ChibiSimEventKind.DashStarted, i, c.DashDir));

            // Rules v15: SuperJumpCd is set to its full value only on the pose tick and otherwise only counts down.
            if (c.SuperJumpCd > p.SuperJumpCd) output.Add(new ChibiSimEvent(ChibiSimEventKind.PoseSuperJump, i));
            if (c.UltPrompt != p.UltPrompt) output.Add(new ChibiSimEvent(ChibiSimEventKind.UltPromptChanged, i, c.UltPrompt));
            // Training: a KO costs no stock ("revive"); in versus the same KO is just the KO event above.
            if (c.KosTaken > p.KosTaken && c.Stocks == p.Stocks) output.Add(new ChibiSimEvent(ChibiSimEventKind.Revived, i));
        }
    }
}
