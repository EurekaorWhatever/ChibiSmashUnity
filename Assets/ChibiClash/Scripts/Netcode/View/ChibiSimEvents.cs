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
        /// <summary>Projectile slot Index became live. Value = owner fighter.</summary>
        ProjectileSpawned,
        /// <summary>Projectile slot Index stopped being live. Value = owner fighter.</summary>
        ProjectileGone,
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

            if (!prev.MatchOver && cur.MatchOver)
                for (int i = 0; i < 2; i++)
                    if (cur.F[i].Hp <= 0) output.Add(new ChibiSimEvent(ChibiSimEventKind.KO, i));

            int n = cur.Proj.Length < prev.Proj.Length ? cur.Proj.Length : prev.Proj.Length;
            for (int i = 0; i < n; i++)
            {
                var a = prev.Proj[i];
                var b = cur.Proj[i];
                if (a.Active && b.Active)
                {
                    // Slot freed and reused within one tick: a fresh projectile has a different owner or more life.
                    if (a.Owner != b.Owner || b.Life > a.Life)
                    {
                        output.Add(new ChibiSimEvent(ChibiSimEventKind.ProjectileGone, i, a.Owner));
                        output.Add(new ChibiSimEvent(ChibiSimEventKind.ProjectileSpawned, i, b.Owner));
                    }
                }
                else if (!a.Active && b.Active) output.Add(new ChibiSimEvent(ChibiSimEventKind.ProjectileSpawned, i, b.Owner));
                else if (a.Active && !b.Active) output.Add(new ChibiSimEvent(ChibiSimEventKind.ProjectileGone, i, a.Owner));
            }
        }

        static void DiffFighter(int i, ChibiFighterSim p, ChibiFighterSim c, List<ChibiSimEvent> output)
        {
            if (c.MoveSlot >= 0 && (c.MoveSlot != p.MoveSlot || c.MoveTick < p.MoveTick))
            {
                int combo = 0;
                int len = c.Stats.PatternLength;
                if (c.MoveSlot == 0 && len > 0) combo = (c.ComboIdx + len - 1) % len;   // StartMove advanced it past the index used
                output.Add(new ChibiSimEvent(ChibiSimEventKind.MoveStarted, i, c.MoveSlot, combo));
            }

            if (c.Hp < p.Hp) output.Add(new ChibiSimEvent(ChibiSimEventKind.Damaged, i, p.Hp - c.Hp));

            if (c.Shielding != p.Shielding) output.Add(new ChibiSimEvent(ChibiSimEventKind.ShieldChanged, i, on: c.Shielding));

            if (c.Parries > p.Parries) output.Add(new ChibiSimEvent(ChibiSimEventKind.Parried, i));

            bool ps = p.Stun > 0, cs = c.Stun > 0;
            if (ps != cs) output.Add(new ChibiSimEvent(ChibiSimEventKind.StunChanged, i, on: cs));

            if (c.Invuln == ChibiSimConst.RespawnInvuln && p.Invuln != ChibiSimConst.RespawnInvuln &&
                c.X == 0 && c.Y == ChibiSimConst.RespawnY)
                output.Add(new ChibiSimEvent(ChibiSimEventKind.RingOut, i));
        }
    }
}
