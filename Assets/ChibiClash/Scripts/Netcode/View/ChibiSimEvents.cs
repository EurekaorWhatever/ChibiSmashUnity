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
    }

    /// <summary>One presentation event. Index is the fighter (0/1) or, for projectile events, the projectile slot.</summary>
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

            if (!prev.MatchOver && cur.MatchOver)
                for (int i = 0; i < 2; i++)
                    if (cur.F[i].HpU <= 0) output.Add(new ChibiSimEvent(ChibiSimEventKind.KO, i));

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
        }

        /// <summary>
        /// True when two snapshots of one live slot hold the same projectile (amendment A6). SpawnFrame is the
        /// identity: a reused slot always gets the current GameTicks, which differs from any projectile already shown.
        /// Owner and Kind are also compared so a rollback diff (shown state vs re-simulated state) that spawned a
        /// different projectile on the same tick is still reported as a reuse. Both must be Active; not checked here.
        /// </summary>
        /// <example><code>bool lerp = a.Active &amp;&amp; b.Active &amp;&amp; ChibiSimEvents.SameProjectile(a, b);</code></example>
        public static bool SameProjectile(ChibiProjectileSim a, ChibiProjectileSim b) =>
            a.SpawnFrame == b.SpawnFrame && a.Owner == b.Owner && a.Kind == b.Kind;

        static void DiffFighter(int i, ChibiFighterSim p, ChibiFighterSim c, List<ChibiSimEvent> output)
        {
            if (c.MoveSlot >= 0 && (c.MoveSlot != p.MoveSlot || c.MoveTick < p.MoveTick))
            {
                int combo = 0;
                int len = c.Stats.PatternLength;
                if (c.MoveSlot == 0 && len > 0) combo = (c.ComboIdx + len - 1) % len;   // StartMove advanced it past the index used
                output.Add(new ChibiSimEvent(ChibiSimEventKind.MoveStarted, i, c.MoveSlot, combo));
            }

            // Fires on any micro-HP decrease (even 0.4 HP); Value is the loss rounded half-up to whole HP for the popup (F:438).
            if (c.HpU < p.HpU)
                output.Add(new ChibiSimEvent(ChibiSimEventKind.Damaged, i,
                    (int)(((long)p.HpU - c.HpU + ChibiCombatStats.HpScale / 2) / ChibiCombatStats.HpScale)));

            if (c.Shielding != p.Shielding) output.Add(new ChibiSimEvent(ChibiSimEventKind.ShieldChanged, i, on: c.Shielding));

            if (c.Parries > p.Parries) output.Add(new ChibiSimEvent(ChibiSimEventKind.Parried, i));

            bool ps = p.Stun > 0, cs = c.Stun > 0;
            if (ps != cs) output.Add(new ChibiSimEvent(ChibiSimEventKind.StunChanged, i, on: cs));

            if (c.Invuln == ChibiSimConst.RespawnInvuln && p.Invuln != ChibiSimConst.RespawnInvuln &&
                c.X == 0 && c.Y == ChibiSimConst.RespawnY)
                output.Add(new ChibiSimEvent(ChibiSimEventKind.RingOut, i));

            if (c.DashTicks > 0 && (p.DashTicks == 0 || c.DashTicks > p.DashTicks))
                output.Add(new ChibiSimEvent(ChibiSimEventKind.DashStarted, i, c.DashDir));
        }
    }
}
