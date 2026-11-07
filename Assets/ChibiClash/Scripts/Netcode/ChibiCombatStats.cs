using System;

namespace ChibiClash.Netcode
{
    /// <summary>
    /// Deterministic PRNG (xorshift32). The whole state is one uint that lives inside the rolled-back
    /// simulation state, so after a rollback both peers draw the exact same sequence.
    /// Never use UnityEngine.Random or System.Random inside the simulation.
    /// </summary>
    public struct ChibiRng
    {
        public uint State;

        public ChibiRng(uint seed) { State = seed == 0 ? 0x9E3779B9u : seed; }

        public uint NextU32()
        {
            uint x = State;
            x ^= x << 13;
            x ^= x >> 17;
            x ^= x << 5;
            State = x;
            return x;
        }

        public int Range(int exclusiveMax) => (int)(NextU32() % (uint)exclusiveMax);

        /// <summary>True with probability permille/1000. Always consumes exactly one draw.</summary>
        public bool Roll(int permille) => Range(1000) < permille;
    }

    /// <summary>
    /// Integer-only combat numbers for one fighter (class base stats + the chosen passive).
    /// All ratios are permille (1000 = 100%) so there is no floating point anywhere in the simulation.
    /// Positions are in milli-units (1000 = one Unity world unit); move speeds are velocity sub-units
    /// (1/ChibiSimConst.VelSub milli-unit per tick), see docs/architecture/netcode/movement-conversion.md section 3.
    /// </summary>
    public struct ChibiCombatStats
    {
        public int Class;                 // ChibiClash.FighterClass as int
        public int Passive;               // 0 or 1
        public int MaxHp;
        public int Attack;
        public int Defense;
        public int CritRatePermille;      // base 50 = 5%
        public int CritDamagePermille;    // bonus on top of 1000: 500 = x1.5
        /// <summary>Run speed in velocity sub-units per tick: ClassData moveSpeed (u/s) x 50/3 milli/tick x 900.</summary>
        public int BaseMoveSpeed;
        public int PatternLength;         // light-attack combo length before the loop restarts

        // ---- passive modifiers (see Create) ----
        public int AttackSpeedBonusPermille;   // + faster / - slower wind-up and recovery
        public int DamageBonusPermille;
        public int MoveSpeedBonusPermille;
        public int LifestealPermille;
        public int WardPermille;               // chance to negate an incoming hit
        public int OnHitHexPermille;           // chance to hex the target (slow, weaken, sluggish)
        public int OnHitArmorBreakPermille;    // chance to reduce target defense
        public int PatienceTicks;              // idle ticks needed for the +50% opener (0 = passive absent)
        public int TailwindDuration;           // ticks of +10% move speed
        public int TailwindCooldown;           // ticks between activations (0 = passive absent)

        // Status magnitudes (shared by all classes)
        public const int HexSlowPermille = 150;
        /// <summary>Tailwind (Ranged passive 1) move-speed bonus: x1.1 (Fighter.cs:142).</summary>
        public const int TailwindPermille = 100;
        public const int HexWeakenPermille = 100;
        public const int HexSluggishPermille = 150;
        public const int ArmorBreakPermille = 150;
        public const int HexDuration = 240;           // 4 s
        public const int ArmorBreakDuration = 300;    // 5 s
        public const int PatienceBonusPermille = 500;
        public const int GuardBreakStunTicks = 60;    // 1 s stun when the guard shatters
        public const int GuardBrokenTicks = 180;      // 3 s before the guard can be raised again
        public const int GuardRegenPerTick = 4;       // guard is stored x10: 0.4 HP per tick = 24 HP/s

        /// <summary>Permille of damage that gets through a block: 400 at 10 DEF, 300 at 20, 200 at 30.</summary>
        public static int BlockedPermille(int defense)
        {
            int p = 500 - defense * 10;
            return p < 100 ? 100 : (p > 500 ? 500 : p);
        }

        /// <summary>Guard durability x10 (blocked raw damage drains it): 700 / 1000 / 1300 at 10 / 20 / 30 DEF.</summary>
        public static int GuardMax10(int defense) => (40 + 3 * defense) * 10;

        /// <summary>Archer and mage knockback is halved (balance).</summary>
        public static bool HalfKnockback(int cls) => cls == (int)FighterClass.Ranged || cls == (int)FighterClass.Magic;

        /// <summary>Build stats from the GDD tables. 'passive' is the player's 1-of-2 pick.</summary>
        public static ChibiCombatStats Create(FighterClass cls, int passive)
        {
            passive = passive == 0 ? 0 : 1;
            var s = new ChibiCombatStats { Class = (int)cls, Passive = passive, CritRatePermille = 50, CritDamagePermille = 500 };

            switch (cls)
            {
                case FighterClass.Melee:
                    s.Attack = 20; s.MaxHp = 500; s.Defense = 20; s.BaseMoveSpeed = 114000; s.PatternLength = 4;
                    if (passive == 0) s.AttackSpeedBonusPermille = 150;   // +15% attack speed
                    else s.LifestealPermille = 50;                        // 5% life steal
                    break;
                case FighterClass.Heavy:
                    s.Attack = 35; s.MaxHp = 600; s.Defense = 30; s.BaseMoveSpeed = 94500; s.PatternLength = 5;
                    s.DamageBonusPermille = 200;                          // both options: +20% damage
                    if (passive == 0) s.AttackSpeedBonusPermille = -150;  // ...with -15% attack speed
                    else s.MoveSpeedBonusPermille = -150;                 // ...or -15% move speed
                    break;
                case FighterClass.Ranged:
                    s.Attack = 15; s.MaxHp = 400; s.Defense = 10; s.BaseMoveSpeed = 109500; s.PatternLength = 4;
                    if (passive == 0) s.CritRatePermille += 200;          // +20% crit rate
                    else { s.TailwindDuration = 600; s.TailwindCooldown = 1800; } // +10% speed 10s, 30s cd
                    break;
                case FighterClass.Magic:
                    s.Attack = 20; s.MaxHp = 375; s.Defense = 10; s.BaseMoveSpeed = 103500; s.PatternLength = 4;
                    if (passive == 0) s.OnHitHexPermille = 100;           // 10% (GDD text is garbled: "1a 0%")
                    else s.WardPermille = 200;                            // 20% to block damage
                    break;
                default: // Polearm
                    s.Attack = 25; s.MaxHp = 500; s.Defense = 20; s.BaseMoveSpeed = 105000; s.PatternLength = 4;
                    if (passive == 0) s.OnHitArmorBreakPermille = 100;    // 10% to reduce defense 15%
                    else s.PatienceTicks = 180;                           // 3 s idle -> next hit +50%
                    break;
            }
            return s;
        }

        // ------------------------------------------------------------------ derived numbers

        /// <summary>
        /// Run speed in velocity sub-units per tick. Modifiers MULTIPLY like the original (Fighter.cs:139-143):
        /// base x (1000 + passive bonus) x (1000 - hex) x (1000 + tailwind) / 1000^k, rounded half away from zero once.
        /// </summary>
        public static int EffectiveMoveSpeed(in ChibiCombatStats s, bool hexed, bool tailwind)
        {
            long num = s.BaseMoveSpeed, den = 1;
            if (s.MoveSpeedBonusPermille != 0) { num *= 1000 + s.MoveSpeedBonusPermille; den *= 1000; }
            if (hexed) { num *= 1000 - HexSlowPermille; den *= 1000; }
            if (tailwind) { num *= 1000 + TailwindPermille; den *= 1000; }
            long q = (Math.Abs(num) + den / 2) / den;
            return (int)(num < 0 ? -q : q);
        }

        public static int AttackSpeedPermille(in ChibiCombatStats s, bool hexed)
        {
            int p = 1000 + s.AttackSpeedBonusPermille - (hexed ? HexSluggishPermille : 0);
            return p < 300 ? 300 : p;
        }

        /// <summary>Scale a frame count by attack speed, rounding up so a move is never faster than its data.</summary>
        public static int ScaleFrames(int frames, int attackSpeedPermille)
        {
            if (frames <= 0) return 0;
            return (frames * 1000 + attackSpeedPermille - 1) / attackSpeedPermille;
        }

        public static int EffectiveDefense(in ChibiCombatStats s, bool armorBroken)
        {
            return armorBroken ? s.Defense * (1000 - ArmorBreakPermille) / 1000 : s.Defense;
        }

        /// <summary>GDD rule: every 10 defense removes 1 point of incoming damage; a hit always deals at least 1.</summary>
        public static int MitigateDefense(int rawDamage, int defense)
        {
            int d = rawDamage - defense / 10;
            return d < 1 ? 1 : d;
        }

        /// <summary>
        /// Outgoing damage before the defender's mitigation. Order of operations (identical on both peers):
        ///   1. attack x move multiplier        2. passive damage bonus        3. hexed attacker -10%
        ///   4. ONE crit roll (always consumed, so the RNG stream never depends on the outcome)
        /// All divisions are C# integer divisions (truncate toward zero), which are defined identically on every platform.
        /// </summary>
        public static int RollDamage(in ChibiCombatStats atk, int multPermille, bool weakened, ref ChibiRng rng, out bool crit)
        {
            long raw = (long)atk.Attack * multPermille / 1000;
            raw = raw * (1000 + atk.DamageBonusPermille) / 1000;
            if (weakened) raw = raw * (1000 - HexWeakenPermille) / 1000;
            crit = rng.Roll(atk.CritRatePermille);
            if (crit) raw = raw * (1000 + atk.CritDamagePermille) / 1000;
            return (int)raw;
        }
    }
}
