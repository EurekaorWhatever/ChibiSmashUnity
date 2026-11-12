// Story type: Logic (rules v20 percent + stocks, design/gdd/percent-knockback.md) -- shared test helpers.
using ChibiClash.Netcode;

namespace ChibiClash.Tests
{
    /// <summary>
    /// Rules v20 removed HP from the sim state. Pre-v20 tests detected a landed hit as "the victim's HP dropped"; that is
    /// exactly "the victim's percent rose" (every hit adds at least Pct(1 HP) = 200 milli-percent). <see cref="HpLike"/>
    /// = -PctM keeps those assertions readable with their original direction (it falls when the fighter is damaged).
    /// Differences are in milli-percent (5 HP = 1000).
    /// </summary>
    public static class ChibiPercentTestKit
    {
        /// <summary>-PctM: decreases exactly when the fighter takes damage, increases on lifesteal.</summary>
        public static int HpLike(this ChibiFighterSim f) => -f.PctM;

        /// <summary>A launch as the spec defines it (percent-knockback.md 4.2-4.6).</summary>
        public struct Launch { public int KbM, Vx, Vy, Stun; public bool Set; }

        /// <summary>
        /// Spec oracle for one unblocked hit: set knockback (magnitude &lt; 30 000) keeps (dir·KbX, KbY) and the legacy stun;
        /// otherwise KB from <see cref="ChibiSimulation.KnockbackM"/> (pinned by exact-value tests), launch speed KB x 1.53
        /// sub, split by KbX / KbY over the magnitude, hitstun min(90, max(legacy, KB x 0.4)).
        /// </summary>
        public static Launch Expected(int pctAfterM, int hitPctM, int weight, int attackerClass, int kbX, int kbY, int dir,
                                      int legacyStun, bool ult = false)
        {
            long kx = kbX, ky = kbY;
            int mag = (int)ChibiFixed.ISqrt(kx * kx + ky * ky);
            var l = new Launch { KbM = ChibiSimulation.KnockbackM(pctAfterM, hitPctM, weight, mag, ult, ChibiSimulation.ClassKbP(attackerClass)) };
            if (mag < ChibiPercentConst.SetKbMag) { l.Set = true; l.Vx = dir * kbX; l.Vy = kbY; l.Stun = legacyStun; return l; }
            long ls = (long)l.KbM * ChibiPercentConst.LsSubPerKb / 1000;
            l.Vx = dir * (int)(ls * System.Math.Abs(kx) / mag);
            l.Vy = (int)(ls * ky / mag);
            l.Stun = ChibiSimulation.HitstunFor(l.KbM, mag, legacyStun);
            return l;
        }

        /// <summary>
        /// Oracle from the victim right after the hit step: its percent now is "after", the hit's percent is now minus
        /// <paramref name="pctBefore"/> (valid when nothing else changed the percent that step).
        /// </summary>
        public static Launch ExpectedOn(ChibiFighterSim victim, int pctBefore, ChibiFighterSim attacker, int kbX, int kbY,
                                        int dir, int legacyStun, bool ult = false) =>
            Expected(victim.PctM, victim.PctM - pctBefore, victim.Weight, attacker.Class, kbX, kbY, dir, legacyStun, ult);
    }
}
