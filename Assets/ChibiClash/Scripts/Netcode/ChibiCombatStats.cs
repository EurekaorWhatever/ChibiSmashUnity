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
        /// <summary>Micro-HP per HP: HP and guard are stored exactly in micro-HP (damage-conversion.md section 1b, D9-1 = a).</summary>
        public const int HpScale = 1_000_000;
        /// <summary>Nano-HP per HP: intermediate damage unit, exact for every permille factor (section 1b).</summary>
        public const long NanoPerHp = 1_000_000_000L;
        /// <summary>Guard regen 24 HP/s = 0.4 HP = 400 000 micro-HP per tick, exact at 60 Hz (section 2, F:202-203).</summary>
        public const int GuardRegenPerTickU = 400_000;

        /// <summary>Guard max in micro-HP from the BASE defense: (40 + 3·DEF) HP (F:90): 70 / 100 / 130 HP at 10 / 20 / 30.</summary>
        public static int GuardMaxU(int defense) => (40 + 3 * defense) * HpScale;

        /// <summary>Defense in hundredths, x0.85 when armor-broken (F:148): 2000 / 3000 / 1000, broken 1700 / 2550 / 850.</summary>
        public static int DefenseH(in ChibiCombatStats s, bool armorBroken) =>
            s.Defense * (armorBroken ? (1000 - ArmorBreakPermille) / 10 : 100);

        /// <summary>Block fraction x100 000: clamp(50000 - 10·DefH, 10000, 50000) = clamp(0.5 - DEF·0.01, 0.1, 0.5) (CA:52-55).</summary>
        public static int BlockedPcm(int defH)
        {
            int p = 50000 - 10 * defH;
            return p < 10000 ? 10000 : (p > 50000 ? 50000 : p);
        }

        /// <summary>
        /// Raw damage in nano-HP (section 1c, CA:43-49): Attack·Mult, x(1 + damage bonus), x0.9 if weakened, then ONE crit
        /// draw (always consumed) and x(1 + crit damage) on a crit. Every step is exact (no rounding). Also the guard drain.
        /// </summary>
        /// <param name="multPermille">Move multiplier incl. finisher and Patience.</param>
        public static long RawDamageNano(in ChibiCombatStats atk, int multPermille, bool weakened, ref ChibiRng rng, out bool crit)
        {
            long n = (long)atk.Attack * NanoPerHp * multPermille / 1000;
            n = n * (1000 + atk.DamageBonusPermille) / 1000;
            if (weakened) n = n * (1000 - HexWeakenPermille) / 1000;
            crit = rng.Roll(atk.CritRatePermille);
            if (crit) n = n * (1000 + atk.CritDamagePermille) / 1000;
            return n;
        }

        /// <summary>
        /// Final damage in micro-HP (section 1c, CA:34): blocked fraction on the exact raw value, then
        /// max(1 HP, X - floor(DEF/10)), one half-up rounding nano -> micro. Never below 1 HP.
        /// </summary>
        public static int FinalDamageMicro(long rawN, int defH, bool blocked)
        {
            long x = blocked ? rawN * BlockedPcm(defH) / 100000 : rawN;
            long dmg = x - (defH / 1000) * NanoPerHp;
            if (dmg < NanoPerHp) dmg = NanoPerHp;
            return (int)((dmg + 500) / 1000);
        }

        /// <summary>Life steal heal in micro-HP: dmg·permille/1000, half-up (Blood Edge 5 %: 18 HP -> 0.9 HP, section 5).</summary>
        public static int LifestealMicro(int dmgU, int permille) => (int)(((long)dmgU * permille + 500) / 1000);

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

        /// <summary>
        /// Attack speed in parts per million for the normals (slots 0/1). Modifiers MULTIPLY like the original
        /// (Fighter.cs:123-133): (1000 + class passive bonus) x (hexed ? 850 : 1000). Range 722 500 .. 1 150 000.
        /// Source: normals-conversion.md section 1 (spPpm). Skills and the ult never read attack speed (multiphase-conversion.md fact 7, rules v12).
        /// </summary>
        public static int AttackSpeedPpm(in ChibiCombatStats s, bool hexed)
        {
            int classP = 1000 + s.AttackSpeedBonusPermille;
            if (classP < 300) classP = 300;
            return classP * (hexed ? 1000 - HexSluggishPermille : 1000);
        }

        /// <summary>
        /// Light/heavy timing from the original's dur = baseMs / AttackSpeed (normals-conversion.md sections 1a-1c):
        /// k_strike = ceil(24·baseMs·1000 / spPpm), k_lock = ceil(60·baseMs·1000 / spPpm);
        /// Startup = k_strike - 1, Active = 1, Recovery = k_lock - k_strike - 1, so the hit probe runs on tick k_strike and
        /// the next normal is accepted on tick k_lock. Example (base light, 240 ms): 5/1/8, hit tick 6, accepted on 15.
        /// </summary>
        /// <param name="baseMs">240 (light) or 420 (heavy).</param>
        /// <param name="spPpm">Multiplicative attack speed from <see cref="AttackSpeedPpm"/>.</param>
        public static void NormalFrames(int baseMs, int spPpm, out int startup, out int recovery)
        {
            int kStrike = (24 * baseMs * 1000 + spPpm - 1) / spPpm;
            int kLock = (60 * baseMs * 1000 + spPpm - 1) / spPpm;
            startup = kStrike - 1;
            recovery = kLock - kStrike - 1;
            if (recovery < 0) recovery = 0;
        }
    }
}
