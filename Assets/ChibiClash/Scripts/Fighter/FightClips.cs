using System;
using System.Collections.Generic;
using UnityEngine;

namespace ChibiClash
{
    /// <summary>
    /// What a fighter is doing, as far as the fight view's sheet animation cares. Derived from read-only sim state by
    /// <see cref="FightClips.StateOf"/> (loco states) or triggered by an event (KO, Ultimate).
    /// </summary>
    public enum FightAnimState : byte
    {
        Idle, Run, Rise, Fall, Guard, Hitstun, Launched, KO, Ultimate, Victory, Intro, Respawn,
    }

    /// <summary>Read-only facts about a fighter that the fight view passes to <see cref="FighterVisual.Tick(Vector2, bool, bool, FightFlags)"/>.</summary>
    [Flags]
    public enum FightFlags : byte
    {
        None = 0,
        /// <summary>In hitstun (sim Stun &gt; 0).</summary>
        Hitstun = 1 << 0,
        /// <summary>Hitstun with a strong launch in the air (tumble).</summary>
        Launched = 1 << 1,
        /// <summary>KO'd and off-screen (sim OutTicks &gt; 0).</summary>
        Out = 1 << 2,
        /// <summary>Hovering on the respawn point (sim HoverTicks &gt; 0).</summary>
        Hover = 1 << 3,
        /// <summary>READY intro still counting down (sim IntroRealTicks &gt; 0).</summary>
        Intro = 1 << 4,
    }

    /// <summary>
    /// Fight-mode clip mapping: every fight state and every move resolves to a MOVESET-SHEET clip only (Idle_Sheet,
    /// Sheet_*, Move_*), never to a costume cutout (Idle, Run, Jump, Hit, KO, Swing, ...) and never to the static body
    /// drawing. Pure static functions (clip availability is injected as a predicate) so the whole mapping is unit-testable
    /// without Unity objects. Cutout clips stay available to menus / select screens through <see cref="SpriteAnimLibrary.Get"/>.
    /// Clip-name contract with tools/art/sheet_anims.py: Sheet_Run (loop), Sheet_Jump (rise), Sheet_Fall (loop),
    /// Sheet_Guard (loop), Sheet_Hit, Sheet_Launch (loop), Sheet_KO, Sheet_Ultimate, optional Sheet_Victory (loop),
    /// Move_&lt;moveId&gt; per move.
    /// </summary>
    public static class FightClips
    {
        public const string Idle = SpriteAnimLibrary.IdleSheet;
        public const string Run = "Sheet_Run", Jump = "Sheet_Jump", Fall = "Sheet_Fall", Guard = "Sheet_Guard",
                            Hit = "Sheet_Hit", Launch = "Sheet_Launch", KO = "Sheet_KO", Ultimate = "Sheet_Ultimate",
                            Victory = "Sheet_Victory";

        /// <summary>Horizontal speed (world u/s) above which a grounded fighter runs (FighterVisual's existing threshold).</summary>
        public const float RunSpeed = 0.6f;
        /// <summary>Vertical speed dead band (world u/s) around the jump apex: inside it the airborne clip does not flip.</summary>
        public const float AirDeadband = 0.5f;
        /// <summary>Moveset row the Ultimate state falls back to when Sheet_Ultimate is missing (Move_&lt;char&gt;_fsmash).</summary>
        public const string UltimateFallbackRow = "fsmash";
        /// <summary>Most generic row of every character, the last Move_ fallback before Idle_Sheet.</summary>
        public const string GenericRow = "ftilt";

        /// <summary>Number of <see cref="FightAnimState"/> values (cache array size).</summary>
        public const int StateCount = (int)FightAnimState.Respawn + 1;

        /// <summary>Candidate sheet clips per state, best first; the first one the character has wins.</summary>
        static readonly string[][] Chains =
        {
            new[] { Idle },                              // Idle
            new[] { Run, Idle },                         // Run
            new[] { Jump, Fall, Idle },                  // Rise
            new[] { Fall, Jump, Idle },                  // Fall
            new[] { Guard, Idle },                       // Guard
            new[] { Hit, Launch, Idle },                 // Hitstun
            new[] { Launch, Hit, Fall, Idle },           // Launched
            new[] { KO, Launch, Hit, Idle },             // KO
            new[] { Ultimate, Idle },                    // Ultimate (+ Move_<char>_fsmash before Idle, see Resolve)
            new[] { Victory, Idle },                     // Victory
            new[] { Idle },                              // Intro (READY countdown)
            new[] { Idle },                              // Respawn hover
        };

        /// <summary>Read-only views of <see cref="Chains"/>, built once (callers cannot change the mapping).</summary>
        static readonly IReadOnlyList<string>[] ReadOnlyChains = BuildReadOnlyChains();

        static IReadOnlyList<string>[] BuildReadOnlyChains()
        {
            var r = new IReadOnlyList<string>[Chains.Length];
            for (int i = 0; i < Chains.Length; i++) r[i] = Array.AsReadOnly(Chains[i]);
            return r;
        }

        /// <summary>The candidate chain of <paramref name="s"/>, best first (read-only: the mapping cannot be changed through it).</summary>
        public static IReadOnlyList<string> Candidates(FightAnimState s) => ReadOnlyChains[(int)s];

        /// <summary>True for a moveset-sheet clip name: Idle_Sheet, Sheet_*, Move_*. Cutout names and null are false.</summary>
        public static bool IsSheetClip(string name)
        {
            if (string.IsNullOrEmpty(name)) return false;
            return string.Equals(name, Idle, StringComparison.OrdinalIgnoreCase)
                || name.StartsWith(SpriteAnimLibrary.SheetPrefix, StringComparison.OrdinalIgnoreCase)
                || name.StartsWith(SpriteAnimLibrary.MovePrefix, StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>Sheet clips that loop while their state lasts; every other sheet clip plays once and holds its last frame.</summary>
        public static bool LoopsClip(string name) =>
            name == Idle || name == Run || name == Fall || name == Guard || name == Launch || name == Victory;

        /// <summary>
        /// States that cancel a playing move clip: a hit / launch interrupts the move in the sim, KO and victory end it.
        /// </summary>
        public static bool Overrides(FightAnimState s) =>
            s == FightAnimState.Hitstun || s == FightAnimState.Launched || s == FightAnimState.KO || s == FightAnimState.Victory;

        /// <summary>
        /// Fight state from read-only facts. Priority: victory &gt; KO (out) &gt; READY intro &gt; respawn hover &gt; launched
        /// &gt; hitstun &gt; guard (grounded) &gt; airborne rise / fall &gt; run &gt; idle. Inside the apex dead band the
        /// previous airborne state is kept (no flicker); leaving the ground with no vertical speed counts as falling.
        /// </summary>
        public static FightAnimState StateOf(bool victory, FightFlags flags, bool grounded, bool blocking, Vector2 velocity,
                                             FightAnimState previous)
        {
            if (victory) return FightAnimState.Victory;
            if ((flags & FightFlags.Out) != 0) return FightAnimState.KO;
            if ((flags & FightFlags.Intro) != 0) return FightAnimState.Intro;
            if ((flags & FightFlags.Hover) != 0) return FightAnimState.Respawn;
            if ((flags & FightFlags.Launched) != 0) return FightAnimState.Launched;
            if ((flags & FightFlags.Hitstun) != 0) return FightAnimState.Hitstun;
            if (blocking && grounded) return FightAnimState.Guard;
            if (!grounded)
            {
                if (velocity.y > AirDeadband) return FightAnimState.Rise;
                if (velocity.y < -AirDeadband) return FightAnimState.Fall;
                return previous == FightAnimState.Rise ? FightAnimState.Rise : FightAnimState.Fall;
            }
            return Mathf.Abs(velocity.x) > RunSpeed ? FightAnimState.Run : FightAnimState.Idle;
        }

        /// <summary>
        /// Sheet clip for state <paramref name="s"/>: the first candidate <paramref name="has"/> accepts. For Ultimate,
        /// <paramref name="moveFallback"/> (a Move_ clip, e.g. the character's fsmash) is tried before Idle_Sheet.
        /// Null only when the character has none of them (not even Idle_Sheet); never a cutout name.
        /// </summary>
        public static string Resolve(FightAnimState s, Func<string, bool> has, string moveFallback = null)
        {
            var chain = Chains[(int)s];
            for (int k = 0; k < chain.Length; k++)
            {
                string c = chain[k];
                if (s == FightAnimState.Ultimate && c == Idle && IsSheetClip(moveFallback) && has(moveFallback)) return moveFallback;
                if (has(c)) return c;
            }
            return null;
        }

        /// <summary>Lower-case character key used in move ids ("Bruna" -&gt; "bruna").</summary>
        public static string CharKey(string characterId) => string.IsNullOrEmpty(characterId) ? "" : characterId.ToLowerInvariant();

        /// <summary>Representative moveset row of an animation kind (movesets.csv anim_kind), for a missing Move_ clip.</summary>
        public static string KindRow(string animKind)
        {
            switch (animKind == null ? "" : animKind.ToLowerInvariant())
            {
                case "jab": return "jab";
                case "upswing": return "utilt";
                case "lowsweep": return "dtilt";
                case "slam": return "dsmash";
                case "spin": return "nair";
                case "shoot": return "nspec";
                case "cast":
                case "guard": return "dspec";
                case "dive": return "dair";
                case "uppercut":
                case "launch": return "uspec";
                case "rush":
                case "dash": return "dash";
                default: return GenericRow;   // swing, thrust, unknown
            }
        }

        /// <summary>
        /// Sheet clip for move <paramref name="moveId"/> (e.g. "ren_nspec_lv2"): Move_&lt;moveId&gt;, else its family
        /// (charge level 1 / ground version / base id), else the character's row for <paramref name="animKind"/>, else
        /// the character's ftilt, else Idle_Sheet. Null only when the character has none of these; never a cutout.
        /// Allocates (string building): callers cache the result per move id.
        /// </summary>
        public static string ResolveMove(string moveId, string animKind, string charKey, Func<string, bool> has)
        {
            if (!string.IsNullOrEmpty(moveId))
            {
                string exact = SpriteAnimLibrary.MovePrefix + moveId;
                if (has(exact)) return exact;
                string family = FamilyOf(moveId);
                if (family != null)
                {
                    string lv1 = SpriteAnimLibrary.MovePrefix + family + "_lv1";
                    if (has(lv1)) return lv1;
                    string fam = SpriteAnimLibrary.MovePrefix + family;
                    if (has(fam)) return fam;
                }
            }
            if (!string.IsNullOrEmpty(charKey))
            {
                string kind = SpriteAnimLibrary.MovePrefix + charKey + "_" + KindRow(animKind);
                if (has(kind)) return kind;
                string generic = SpriteAnimLibrary.MovePrefix + charKey + "_" + GenericRow;
                if (has(generic)) return generic;
            }
            return has(Idle) ? Idle : null;
        }

        /// <summary>Base id of a variant id: "x_nspec_lv2" -&gt; "x_nspec", "x_rebound_air" -&gt; "x_rebound", "x_grapple2" -&gt; "x_grapple"; null if none.</summary>
        static string FamilyOf(string id)
        {
            int lv = id.LastIndexOf("_lv", StringComparison.Ordinal);
            if (lv > 0) return id.Substring(0, lv);
            if (id.EndsWith("_air", StringComparison.Ordinal)) return id.Substring(0, id.Length - 4);
            int end = id.Length;
            while (end > 0 && char.IsDigit(id[end - 1])) end--;
            return end < id.Length && end > 0 ? id.Substring(0, end) : null;
        }

        /// <summary>
        /// Moveset row standing in for the pre-v18 slots (0 neutral light, 1 neutral heavy, 2-4 class skills, 6 pose
        /// fireball) per class, FighterClass order. Slot 5 (ultimate) is null: it plays the Ultimate state clip.
        /// Rows picked by the closest sheet pose: Kaia Dash-Thrust = Shadow Lunge, Parry = Riposte, Barrage = Blade Flurry;
        /// Bruna Grab = Iron Uppercut, Tornado = Neutral Air spin; Ren Backstep shot = Focus Shot, Choke = Forward Tilt,
        /// Arrow Barrage = Skyward Volley; Lyra Fireball = Arcane Shot, Minions = Glyph Zone, Cloud = Starfall Trap;
        /// Taro Leap Slam = Falling Spear, Spin Guard = Crescent Reaper, Throw Spear = Wind Lance.
        /// </summary>
        static readonly string[][] LegacyRows =
        {
            new[] { "jab", "fsmash", "sspec", "riposte", "nspec", null, "nspec" },                 // Melee (Kaia)
            new[] { "jab", "fsmash", "uppercut", "nair", "fsmash", null, "nspec" },                // Heavy (Bruna)
            new[] { "jab", "fsmash", "nspec_lv1", "ftilt", "volley", null, "nspec_lv1" },          // Ranged (Ren)
            new[] { "jab", "fsmash", "nspec", "dspec", "starfall", null, "nspec" },                // Magic (Lyra)
            new[] { "jab", "fsmash", "fallingspear", "reaper", "nspec", null, "nspec" },           // Polearm (Taro)
        };

        /// <summary>Move id ("bruna_uppercut") the fight view plays for pre-v18 slot <paramref name="slot"/>; null for the ultimate / out of range.</summary>
        public static string LegacyMoveId(FighterClass cls, int slot, string charKey)
        {
            int c = (int)cls;
            if (c < 0 || c >= LegacyRows.Length || slot < 0 || slot >= LegacyRows[c].Length || string.IsNullOrEmpty(charKey)) return null;
            string row = LegacyRows[c][slot];
            return row == null ? null : charKey + "_" + row;
        }

        /// <summary>
        /// Fight state a legacy cutout clip request stands for ("KO" -&gt; KO, "Ultimate" -&gt; Ultimate, "Hit" -&gt; Hitstun...),
        /// so old PlayClip / PlayClipLocked call sites still show the right SHEET clip. False for move cutouts (Swing,
        /// Skill1...): in fight mode those requests are ignored because the move's own Move_ clip is already playing.
        /// </summary>
        public static bool TryStateForLegacyClip(string clip, out FightAnimState s)
        {
            switch (clip)
            {
                case "KO": s = FightAnimState.KO; return true;
                case "Ultimate": s = FightAnimState.Ultimate; return true;
                case "Hit": s = FightAnimState.Hitstun; return true;
                case "Launch": s = FightAnimState.Launched; return true;
                case "Guard": s = FightAnimState.Guard; return true;
                case "Victory": s = FightAnimState.Victory; return true;
                case "Jump": s = FightAnimState.Rise; return true;
                case "Run": s = FightAnimState.Run; return true;
                case "Idle": s = FightAnimState.Idle; return true;
                default: s = FightAnimState.Idle; return false;
            }
        }
    }
}
