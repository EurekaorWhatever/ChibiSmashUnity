using UnityEngine;

namespace ChibiClash.Netcode
{
    /// <summary>
    /// Reusable effect kinds the 108 moveset VFX notes (<see cref="ChibiMoveInfo.Entry.Vfx"/>) are grouped into.
    /// Presentation only.
    /// </summary>
    public enum MoveFx : byte
    {
        None,
        /// <summary>Slash crescent in front ("Arc slash", "Light crescent", "Wide arc").</summary>
        Arc,
        /// <summary>Crescent behind the fighter ("Backslash", "Rear arc", "Sharp reverse arc").</summary>
        BackArc,
        /// <summary>Crescent sweeping upward ("Rising arc", "Upward arc", "Hook arc").</summary>
        RisingArc,
        /// <summary>Ring + orbiting crescents around the body ("Circular slash", "Spin arc", "Spiral wind").</summary>
        Whirl,
        /// <summary>Dust puffs and a skid line along the ground ("Low dust slash", "Ground dust, skid").</summary>
        Dust,
        /// <summary>Speed lines trailing the body ("Speed trail", "Charge trail", "Kick gust").</summary>
        SpeedTrail,
        /// <summary>Fading copies of the fighter ("afterimage", "blur", "Teleport trail").</summary>
        Afterimage,
        /// <summary>Flat ring expanding along the ground ("Large shockwave", "Dual ground shockwave", "Shock ring").</summary>
        Shockwave,
        /// <summary>Upward light / wind column ("Vertical light pillars", "Wind pillar", "Spark column").</summary>
        Pillar,
        /// <summary>Crater burst at the feet ("Vertical dive, impact", "Meteor trail, large crater").</summary>
        Impact,
        /// <summary>Rune circle on the ground ("Mark circle", "Persistent rune field", "Target rune").</summary>
        Rune,
        /// <summary>Pulsing body glow ("Blue armor plate", "Jade aura", "Prism shield").</summary>
        Armor,
        /// <summary>Crossed X flash ("Counter flash", "Reversal, red flash", "X-shaped flash").</summary>
        Counter,
        /// <summary>Four-point star spark at the strike ("star burst", "Magic spark", "hit burst").</summary>
        Burst,
        /// <summary>Long straight streak forward ("Linear streak", "Wind lance", "Glowing arrow", "Rope trail").</summary>
        Streak,
        /// <summary>Streaks dropping from the sky in front ("falling stars", "spear trail", "Multi-arrow arcs").</summary>
        SkyFall,
    }

    /// <summary>
    /// Moveset presentation lookup: VFX note to effect kinds (keyword table, built once), animation kind to sheet clip.
    /// </summary>
    public static class ChibiMoveFxTable
    {
        /// <summary>Keyword (lower case) to effect, in priority order; a note gets the first two distinct matches.</summary>
        static readonly string[] Keys =
        {
            "counter", "red flash", "x-shaped", "flash",
            "armor", "aura", "shield", "prism",
            "afterimage", "blur", "teleport",
            "shockwave", "ground wave", "ground ring", "shock ring", "ground arcs", "expanding",
            "pillar", "column", "upward burst", "flares", "rocket",
            "rune", "glyph", "mark", "trap", "target", "magic circle",
            "falling star", "spear trail", "multi-arrow",
            "meteor", "comet", "crater", "impact", "downward burst", "vertical burst", "dive",
            "circular", "spin", "spiral", "whirl",
            "backslash", "back hit", "rear arc", "reverse",
            "rising", "upward", "dual arc", "hook arc",
            "crescent", "arc", "slash",
            "dust", "skid", "ground spark", "sweep",
            "speed", "trail", "gust",
            "streak", "lance", "line", "arrow", "rope", "grapple",
            "burst", "spark", "explosi", "debris", "star",
        };
        static readonly MoveFx[] Vals =
        {
            MoveFx.Counter, MoveFx.Counter, MoveFx.Counter, MoveFx.Counter,
            MoveFx.Armor, MoveFx.Armor, MoveFx.Armor, MoveFx.Armor,
            MoveFx.Afterimage, MoveFx.Afterimage, MoveFx.Afterimage,
            MoveFx.Shockwave, MoveFx.Shockwave, MoveFx.Shockwave, MoveFx.Shockwave, MoveFx.Shockwave, MoveFx.Shockwave,
            MoveFx.Pillar, MoveFx.Pillar, MoveFx.Pillar, MoveFx.Pillar, MoveFx.Pillar,
            MoveFx.Rune, MoveFx.Rune, MoveFx.Rune, MoveFx.Rune, MoveFx.Rune, MoveFx.Rune,
            MoveFx.SkyFall, MoveFx.SkyFall, MoveFx.SkyFall,
            MoveFx.Impact, MoveFx.Impact, MoveFx.Impact, MoveFx.Impact, MoveFx.Impact, MoveFx.Impact, MoveFx.Impact,
            MoveFx.Whirl, MoveFx.Whirl, MoveFx.Whirl, MoveFx.Whirl,
            MoveFx.BackArc, MoveFx.BackArc, MoveFx.BackArc, MoveFx.BackArc,
            MoveFx.RisingArc, MoveFx.RisingArc, MoveFx.RisingArc, MoveFx.RisingArc,
            MoveFx.Arc, MoveFx.Arc, MoveFx.Arc,
            MoveFx.Dust, MoveFx.Dust, MoveFx.Dust, MoveFx.Dust,
            MoveFx.SpeedTrail, MoveFx.SpeedTrail, MoveFx.SpeedTrail,
            MoveFx.Streak, MoveFx.Streak, MoveFx.Streak, MoveFx.Streak, MoveFx.Streak, MoveFx.Streak,
            MoveFx.Burst, MoveFx.Burst, MoveFx.Burst, MoveFx.Burst, MoveFx.Burst,
        };

        /// <summary>Per class, per moveset row: first / second effect and flags (bit 0 dual, bit 1 use accent colour).</summary>
        static MoveFx[][] _a, _b;
        static byte[][] _flags;

        /// <summary>Effect kinds of a VFX note: the first two distinct keyword matches; Burst when nothing matches.</summary>
        /// <example><code>ChibiMoveFxTable.Classify("Dual ground shockwave", out var a, out var b, out bool dual, out _);</code></example>
        public static void Classify(string note, out MoveFx first, out MoveFx second, out bool dual, out bool accent)
        {
            first = second = MoveFx.None;
            dual = accent = false;
            if (string.IsNullOrEmpty(note)) { first = MoveFx.Burst; return; }
            for (int k = 0; k < Keys.Length && second == MoveFx.None; k++)
            {
                if (note.IndexOf(Keys[k], System.StringComparison.OrdinalIgnoreCase) < 0) continue;
                var v = Vals[k];
                if (first == MoveFx.None) first = v;
                else if (v != first) second = v;
            }
            if (first == MoveFx.None) first = MoveFx.Burst;
            dual = note.IndexOf("dual", System.StringComparison.OrdinalIgnoreCase) >= 0;
            accent = note.IndexOf("blue", System.StringComparison.OrdinalIgnoreCase) >= 0;
        }

        /// <summary>Effects of class <paramref name="cls"/> slot <paramref name="slot"/> (None for slots without moveset data).</summary>
        public static void Get(int cls, int slot, out MoveFx first, out MoveFx second, out bool dual, out bool accent)
        {
            if (_a == null) Build();
            first = second = MoveFx.None;
            dual = accent = false;
            int k = slot - ChibiMovesetData.FirstNewSlot;
            if (cls < 0 || cls >= _a.Length || k < 0 || k >= _a[cls].Length) return;
            first = _a[cls][k];
            second = _b[cls][k];
            dual = (_flags[cls][k] & 1) != 0;
            accent = (_flags[cls][k] & 2) != 0;
        }

        static void Build()
        {
            const int classes = 5;
            var a = new MoveFx[classes][];
            var b = new MoveFx[classes][];
            var fl = new byte[classes][];
            for (int c = 0; c < classes; c++)
            {
                int n = ChibiMoveInfo.Count(c);
                a[c] = new MoveFx[n];
                b[c] = new MoveFx[n];
                fl[c] = new byte[n];
                for (int k = 0; k < n; k++)
                {
                    var e = ChibiMoveInfo.Get(c, ChibiMovesetData.FirstNewSlot + k);
                    Classify(e.Vfx, out a[c][k], out b[c][k], out bool dual, out bool accent);
                    fl[c][k] = (byte)((dual ? 1 : 0) | (accent ? 2 : 0));
                }
            }
            _b = b;
            _flags = fl;
            _a = a;
        }

        /// <summary>Sheet clip for a moveset animation kind (SpriteAnimLibrary names); "Swing" for unknown kinds.</summary>
        public static string ClipFor(string animKind)
        {
            switch (animKind)
            {
                case "jab": return "Jab";
                case "swing": return "Swing";
                case "upswing": return "Upswing";
                case "lowsweep": return "LowSweep";
                case "thrust": return "Thrust";
                case "slam": return "Slam";
                case "spin": return "Spin";
                case "shoot": return "Shoot";
                case "cast": return "Cast";
                case "dive": return "Dive";
                case "uppercut": return "Uppercut";
                case "guard": return "Guard";
                case "rush": return "Rush";
                case "launch": return "Launch";
                case "dash": return "Dash";
                default: return "Swing";
            }
        }
    }

    /// <summary>
    /// Pooled sprite effects for the moveset (one pool per match view). Every effect is a few sprites with a delay,
    /// a life, a scale / rotation / alpha curve and an optional anchor to a fighter. Built once in the constructor;
    /// Spawn and Tick never allocate. Runs on scaled time like FighterVisual, so hitstop and cinematics slow it too.
    /// </summary>
    public sealed class ChibiMoveVfx
    {
        const int PoolSize = 112;
        const float FeetY = -0.7f;                    // fighter pivot to feet (FighterVisual body base)
        const int OrderBack = 8, OrderGhost = 9, OrderFront = 52;

        enum Follow : byte { World, Body, LatchBody }
        enum Fade : byte { Out, Pulse, InOut }
        enum Anchor : byte { Center, Bottom, Back }

        sealed class Item
        {
            public Transform Tr;
            public SpriteRenderer Sr;
            public bool Live, Shown, Ghost;
            public float T, Delay, Life, A0, Rot0, RotVel;
            public Vector2 Pos, Vel, S0, S1, Offset;
            public Color Col;
            public int Fighter;
            public Follow Follow;
            public Fade Fade;
            public Anchor Anchor;
        }

        readonly Item[] _items = new Item[PoolSize];
        readonly FighterVisual[] _vis;
        int _next;

        static Sprite _crescent;
        static Texture2D _crescentTex;

        /// <summary>Soft-edged crescent (bulges toward +x), generated once; tinted per effect.</summary>
        public static Sprite Crescent
        {
            get
            {
                if (_crescent != null) return _crescent;
                const int n = 128;
                _crescentTex = new Texture2D(n, n, TextureFormat.RGBA32, false) { wrapMode = TextureWrapMode.Clamp, name = "VfxCrescent" };
                var px = new Color32[n * n];
                for (int y = 0; y < n; y++)
                    for (int x = 0; x < n; x++)
                    {
                        float u = (x + 0.5f) / n * 2f - 1f, v = (y + 0.5f) / n * 2f - 1f;
                        float outer = 0.96f - Mathf.Sqrt(u * u + v * v);                       // inside the big circle
                        float inner = Mathf.Sqrt((u + 0.42f) * (u + 0.42f) + v * v) - 0.86f;  // outside the shifted circle
                        float a = Mathf.Clamp01(Mathf.Min(outer, inner) * 40f);
                        // bright edge on the outer rim
                        byte w = (byte)(255 * Mathf.Lerp(1f, 0.75f, Mathf.Clamp01(outer * 6f)));
                        px[y * n + x] = new Color32(w, w, w, (byte)(255 * a));
                    }
                _crescentTex.SetPixels32(px);
                _crescentTex.Apply(false, true);
                _crescent = Sprite.Create(_crescentTex, new Rect(0, 0, n, n), new Vector2(0.5f, 0.5f), n);
                _crescent.name = "VfxCrescent";
                return _crescent;
            }
        }

        /// <summary>Builds the pool under <paramref name="parent"/>; effects anchor to <paramref name="vis"/>[0/1].</summary>
        public ChibiMoveVfx(Transform parent, FighterVisual[] vis)
        {
            _vis = vis;
            var root = new GameObject("MoveVfx").transform;
            root.SetParent(parent, false);
            for (int i = 0; i < PoolSize; i++)
            {
                var sr = Art.Sprite(root, "Vfx", Art.Soft, Color.clear, Vector2.zero, Vector2.one, OrderFront);
                sr.enabled = false;
                _items[i] = new Item { Tr = sr.transform, Sr = sr };
            }
        }

        Item Take(Sprite s, int order)
        {
            Item it = null;
            for (int k = 0; k < PoolSize; k++)
            {
                var c = _items[(_next + k) % PoolSize];
                if (!c.Live) { it = c; _next = (_next + k + 1) % PoolSize; break; }
            }
            if (it == null) { it = _items[_next]; _next = (_next + 1) % PoolSize; }   // full: recycle the oldest slot
            it.Live = true; it.Shown = false; it.Ghost = false;
            it.T = 0f; it.Delay = 0f; it.Life = 0.3f; it.A0 = 1f; it.Rot0 = 0f; it.RotVel = 0f;
            it.Pos = it.Vel = it.Offset = Vector2.zero;
            it.S0 = it.S1 = Vector2.one;
            it.Col = Color.white;
            it.Fighter = -1;
            it.Follow = Follow.World; it.Fade = Fade.Out; it.Anchor = Anchor.Center;
            it.Sr.sprite = s;
            it.Sr.sortingOrder = order;
            it.Sr.enabled = false;
            return it;
        }

        Item Body(int fighter, Sprite s, int order, Follow follow, float ox, float oy)
        {
            var it = Take(s, order);
            it.Fighter = fighter;
            it.Follow = follow;
            it.Offset = new Vector2(ox, oy);
            return it;
        }

        /// <summary>Clears every live effect (match end / teardown).</summary>
        public void Clear()
        {
            for (int i = 0; i < PoolSize; i++) { _items[i].Live = false; _items[i].Sr.enabled = false; }
        }

        /// <summary>
        /// Effect <paramref name="fx"/> for a move of fighter <paramref name="i"/>. <paramref name="startup"/> / <paramref name="active"/>
        /// are the move's phases in seconds (strike effects wait for the startup). <paramref name="c"/> is the character's Fx colour.
        /// </summary>
        public void PlayMove(int i, MoveFx fx, Color c, int facing, float startup, float active, bool dual)
        {
            float F = facing >= 0 ? 1f : -1f;
            float st = Mathf.Max(0f, startup);
            float ac = Mathf.Max(0.08f, active);
            Item it;
            switch (fx)
            {
                case MoveFx.Arc:
                case MoveFx.BackArc:
                {
                    float d = fx == MoveFx.BackArc ? -F : F;
                    it = Body(i, Crescent, OrderFront, Follow.Body, 0.75f * d, 0.15f);
                    Set(it, st, Mathf.Max(0.22f, ac + 0.08f), c, 0.95f, new Vector2(0.7f * d, 1.2f), new Vector2(1.5f * d, 2.1f), 25f * d, -260f * d);
                    it = Body(i, Crescent, OrderFront + 1, Follow.Body, 0.8f * d, 0.15f);
                    Set(it, st, Mathf.Max(0.18f, ac), Color.white, 0.7f, new Vector2(0.5f * d, 0.9f), new Vector2(1.1f * d, 1.6f), 25f * d, -260f * d);
                    break;
                }
                case MoveFx.RisingArc:
                    it = Body(i, Crescent, OrderFront, Follow.Body, 0.5f * F, 0.45f);
                    Set(it, st, Mathf.Max(0.24f, ac + 0.08f), c, 0.95f, new Vector2(0.7f * F, 1.1f), new Vector2(1.4f * F, 2.0f), -10f * F, 300f * F);
                    it = Body(i, Crescent, OrderFront + 1, Follow.Body, 0.55f * F, 0.45f);
                    Set(it, st, Mathf.Max(0.2f, ac), Color.white, 0.65f, new Vector2(0.5f * F, 0.8f), new Vector2(1.0f * F, 1.5f), -10f * F, 300f * F);
                    break;
                case MoveFx.Whirl:
                    it = Body(i, Art.Ring, OrderFront, Follow.Body, 0f, 0.1f);
                    Set(it, st, Mathf.Max(0.3f, ac + 0.1f), c, 0.7f, new Vector2(1.2f, 1.2f), new Vector2(2.5f, 2.5f), 0f, 0f);
                    for (int k = 0; k < 2; k++)
                    {
                        it = Body(i, Crescent, OrderFront + 1, Follow.Body, 0f, 0.1f);
                        Set(it, st, Mathf.Max(0.3f, ac + 0.1f), k == 0 ? c : Color.white, 0.85f, new Vector2(1.8f * F, 1.8f), new Vector2(2.3f * F, 2.3f), k * 180f, -900f * F);
                    }
                    break;
                case MoveFx.Dust:
                    for (int k = 0; k < 4; k++)
                    {
                        it = Body(i, Art.Soft, OrderFront, Follow.LatchBody, (0.4f + k * 0.35f) * F, FeetY + 0.08f);
                        Set(it, st + k * 0.03f, 0.42f, Dusty, 0.85f, new Vector2(0.3f, 0.25f), new Vector2(0.9f, 0.6f), 0f, 0f);
                        it.Vel = new Vector2(0.8f * F, 0.35f);
                    }
                    it = Body(i, Art.Rounded, OrderFront, Follow.LatchBody, 0.3f * F, FeetY + 0.03f);
                    Set(it, st, 0.3f, c, 0.85f, new Vector2(0.2f * F, 0.07f), new Vector2(1.8f * F, 0.09f), 0f, 0f);
                    it.Anchor = Anchor.Back;
                    break;
                case MoveFx.SpeedTrail:
                    for (int k = 0; k < 3; k++)
                    {
                        it = Body(i, Art.Rounded, OrderBack + 1, Follow.Body, -0.45f * F, -0.35f + k * 0.35f);
                        Set(it, k * 0.02f, st + ac + 0.05f, k == 1 ? Color.white : c, 0.75f, new Vector2(-0.2f * F, 0.05f), new Vector2(-1.7f * F, 0.07f), 0f, 0f);
                        it.Fade = Fade.InOut;
                        it.Anchor = Anchor.Back;
                    }
                    break;
                case MoveFx.Afterimage:
                {
                    float step = Mathf.Max(0.04f, (st + ac) / 4f);
                    for (int k = 0; k < 4; k++)
                    {
                        it = Body(i, null, OrderGhost, Follow.LatchBody, 0f, 0f);
                        Set(it, k * step, 0.26f, c, 0.55f, Vector2.one, Vector2.one, 0f, 0f);
                        it.Ghost = true;
                    }
                    break;
                }
                case MoveFx.Shockwave:
                    for (int side = 0; side < (dual ? 2 : 1); side++)
                    {
                        float d = side == 0 ? F : -F;
                        it = Body(i, Art.Ring, OrderFront, Follow.LatchBody, 0.9f * d, FeetY + 0.05f);
                        Set(it, st, 0.45f, c, 0.95f, new Vector2(0.4f, 0.1f), new Vector2(4f, 0.75f), 0f, 0f);
                        it = Body(i, Art.Soft, OrderBack, Follow.LatchBody, 0.9f * d, FeetY + 0.05f);
                        Set(it, st, 0.4f, c, 0.45f, new Vector2(0.6f, 0.2f), new Vector2(3.2f, 0.6f), 0f, 0f);
                        Chips(i, st, 0.9f * d, c);
                    }
                    break;
                case MoveFx.Pillar:
                    it = Body(i, Art.Soft, OrderFront, Follow.LatchBody, 0.8f * F, FeetY);
                    Set(it, st, 0.5f, c, 0.8f, new Vector2(0.5f, 0.2f), new Vector2(0.95f, 3.6f), 0f, 0f);
                    it.Anchor = Anchor.Bottom;
                    it = Body(i, Art.Rounded, OrderFront + 1, Follow.LatchBody, 0.8f * F, FeetY);
                    Set(it, st, 0.42f, Color.white, 0.8f, new Vector2(0.1f, 0.2f), new Vector2(0.16f, 3.3f), 0f, 0f);
                    it.Anchor = Anchor.Bottom;
                    break;
                case MoveFx.Impact:
                    Star(i, st, 0.5f * F, FeetY + 0.1f, c, 1.6f, Follow.LatchBody);
                    it = Body(i, Art.Ring, OrderFront, Follow.LatchBody, 0.5f * F, FeetY + 0.05f);
                    Set(it, st, 0.4f, c, 0.95f, new Vector2(0.3f, 0.08f), new Vector2(2.8f, 0.55f), 0f, 0f);
                    Chips(i, st, 0.5f * F, c);
                    break;
                case MoveFx.Rune:
                {
                    float life = st + ac + 0.45f;
                    it = Body(i, Art.Ring, OrderBack, Follow.LatchBody, 0.7f * F, FeetY + 0.04f);
                    Set(it, 0f, life, c, 0.95f, new Vector2(0.2f, 0.06f), new Vector2(2.2f, 0.55f), 0f, 0f);
                    it.Fade = Fade.Pulse;
                    it = Body(i, Art.Soft, OrderBack, Follow.LatchBody, 0.7f * F, FeetY + 0.04f);
                    Set(it, 0f, life, c, 0.4f, new Vector2(0.2f, 0.06f), new Vector2(2.0f, 0.45f), 0f, 0f);
                    for (int k = 0; k < 4; k++)
                    {
                        it = Body(i, Art.Square, OrderFront, Follow.LatchBody, (0.15f + k * 0.37f) * F, FeetY + 0.1f);
                        Set(it, 0.08f * k, 0.5f, Color.Lerp(c, Color.white, 0.5f), 0.9f, new Vector2(0.14f, 0.14f), new Vector2(0.05f, 0.05f), 45f, 180f);
                        it.Vel = new Vector2(0f, 1.6f);
                    }
                    break;
                }
                case MoveFx.Armor:
                {
                    float life = st + ac + 0.15f;
                    it = Body(i, Art.Soft, OrderBack, Follow.Body, 0f, 0.15f);
                    Set(it, 0f, life, c, 0.6f, new Vector2(2.0f, 2.6f), new Vector2(2.2f, 2.8f), 0f, 0f);
                    it.Fade = Fade.Pulse;
                    it = Body(i, Art.Ring, OrderFront, Follow.Body, 0f, 0.15f);
                    Set(it, 0f, life, c, 0.45f, new Vector2(1.7f, 2.2f), new Vector2(1.9f, 2.4f), 0f, 0f);
                    it.Fade = Fade.Pulse;
                    break;
                }
                case MoveFx.Counter:
                    Cross(i, 0f, 0.6f * F, 0.15f, c, Follow.LatchBody);
                    it = Body(i, Art.Soft, OrderFront, Follow.LatchBody, 0.4f * F, 0.15f);
                    Set(it, 0f, 0.16f, Color.white, 0.7f, new Vector2(1.2f, 1.2f), new Vector2(2.6f, 2.6f), 0f, 0f);
                    break;
                case MoveFx.Burst:
                    Star(i, st, 1.0f * F, 0.15f, c, 1.2f, Follow.LatchBody);
                    break;
                case MoveFx.Streak:
                    it = Body(i, Art.Rounded, OrderFront, Follow.LatchBody, 0.3f * F, 0.05f);
                    Set(it, st, 0.3f, c, 0.9f, new Vector2(0.2f * F, 0.12f), new Vector2(4.5f * F, 0.14f), 0f, 0f);
                    it.Anchor = Anchor.Back;
                    it = Body(i, Art.Rounded, OrderFront + 1, Follow.LatchBody, 0.3f * F, 0.05f);
                    Set(it, st, 0.24f, Color.white, 0.9f, new Vector2(0.2f * F, 0.04f), new Vector2(4.2f * F, 0.05f), 0f, 0f);
                    it.Anchor = Anchor.Back;
                    break;
                case MoveFx.SkyFall:
                    for (int k = 0; k < 4; k++)
                    {
                        it = Body(i, Art.Rounded, OrderFront, Follow.LatchBody, (1.0f + k * 0.45f) * F, 3.6f);
                        Set(it, st + k * 0.06f, 0.3f, k % 2 == 0 ? c : Color.white, 0.9f, new Vector2(0.1f, 0.9f), new Vector2(0.12f, 1.1f), 0f, 0f);
                        it.Vel = new Vector2(0f, -13f);
                    }
                    break;
            }
        }

        /// <summary>Hit spark of a moveset hit at world point <paramref name="at"/>: star + glow in the character's colour.</summary>
        public void PlayHit(Vector2 at, Color c)
        {
            var it = Take(Art.Soft, OrderFront);
            it.Pos = at;
            Set(it, 0f, 0.2f, c, 0.8f, new Vector2(0.6f, 0.6f), new Vector2(1.6f, 1.6f), 0f, 0f);
            for (int k = 0; k < 2; k++)
            {
                it = Take(Art.Square, OrderFront + 2);
                it.Pos = at;
                Set(it, 0f, 0.18f, Color.white, 1f, new Vector2(0.1f, 0.4f), new Vector2(0.16f, 1.5f), k * 90f + 20f, 0f);
            }
        }

        static readonly Color Dusty = new Color(0.86f, 0.8f, 0.72f);
        static readonly Color Rock = new Color(0.55f, 0.5f, 0.45f);

        void Chips(int i, float delay, float ox, Color c)
        {
            for (int k = 0; k < 3; k++)
            {
                var it = Body(i, Art.Square, OrderFront, Follow.LatchBody, ox + (k - 1) * 0.3f, FeetY + 0.1f);
                Set(it, delay, 0.4f, Rock, 1f, new Vector2(0.14f, 0.14f), new Vector2(0.08f, 0.08f), 20f * k, 400f);
                it.Vel = new Vector2((k - 1) * 1.6f, 3.2f);
            }
        }

        void Star(int i, float delay, float ox, float oy, Color c, float size, Follow follow)
        {
            var it = Body(i, Art.Soft, OrderFront, follow, ox, oy);
            Set(it, delay, 0.25f, c, 0.85f, new Vector2(0.4f, 0.4f) * size, new Vector2(1.3f, 1.3f) * size, 0f, 0f);
            for (int k = 0; k < 2; k++)
            {
                it = Body(i, Art.Square, OrderFront + 2, follow, ox, oy);
                Set(it, delay, 0.22f, Color.white, 1f, new Vector2(0.08f, 0.3f) * size, new Vector2(0.12f, 1.1f) * size, k * 90f, 0f);
            }
        }

        void Cross(int i, float delay, float ox, float oy, Color c, Follow follow)
        {
            for (int k = 0; k < 2; k++)
            {
                var it = Body(i, Art.Square, OrderFront + 2, follow, ox, oy);
                Set(it, delay, 0.26f, k == 0 ? Color.white : c, 1f, new Vector2(0.1f, 0.3f), new Vector2(0.2f, 2.4f), k == 0 ? 45f : -45f, 0f);
            }
        }

        static void Set(Item it, float delay, float life, Color c, float a0, Vector2 s0, Vector2 s1, float rot0, float rotVel)
        {
            it.Delay = delay; it.Life = Mathf.Max(0.05f, life); it.Col = c; it.A0 = a0;
            it.S0 = s0; it.S1 = s1; it.Rot0 = rot0; it.RotVel = rotVel;
        }

        Vector2 FighterPos(int i) => i >= 0 && i < _vis.Length && _vis[i] != null ? (Vector2)_vis[i].transform.position : Vector2.zero;

        /// <summary>Advances every live effect by <paramref name="dt"/> (scaled) seconds.</summary>
        public void Tick(float dt)
        {
            for (int n = 0; n < PoolSize; n++)
            {
                var it = _items[n];
                if (!it.Live) continue;
                it.T += dt;
                if (it.T < it.Delay) continue;
                float local = it.T - it.Delay;
                float u = local / it.Life;
                if (u >= 1f)
                {
                    it.Live = false;
                    it.Sr.enabled = false;
                    continue;
                }
                if (!it.Shown)
                {
                    it.Shown = true;
                    if (it.Ghost)
                    {
                        var src = it.Fighter >= 0 && it.Fighter < _vis.Length && _vis[it.Fighter] != null ? _vis[it.Fighter].BodyRenderer : null;
                        if (src == null || src.sprite == null) { it.Live = false; continue; }
                        it.Sr.sprite = src.sprite;
                        it.Pos = src.transform.position;
                        it.Tr.rotation = src.transform.rotation;
                        Vector3 ls = src.transform.lossyScale;
                        it.S0 = it.S1 = new Vector2(ls.x, ls.y);
                    }
                    else if (it.Follow == Follow.LatchBody) it.Pos = FighterPos(it.Fighter) + it.Offset;
                    it.Sr.enabled = true;
                }
                float e = 1f - (1f - u) * (1f - u);
                Vector2 s = Vector2.LerpUnclamped(it.S0, it.S1, e);
                Vector2 p = it.Follow == Follow.Body ? FighterPos(it.Fighter) + it.Offset : it.Pos;
                p += it.Vel * local;
                if (it.Anchor == Anchor.Bottom) p.y += s.y * 0.5f;
                else if (it.Anchor == Anchor.Back) p.x += s.x * 0.5f;
                it.Tr.position = new Vector3(p.x, p.y, 0f);
                it.Tr.localScale = new Vector3(s.x, s.y, 1f);
                if (!it.Ghost) it.Tr.localRotation = Quaternion.Euler(0f, 0f, it.Rot0 + it.RotVel * local);
                float a;
                switch (it.Fade)
                {
                    case Fade.Pulse: a = it.A0 * (0.75f + 0.25f * Mathf.Sin(local * 14f)) * Mathf.Clamp01((1f - u) * 4f) * Mathf.Clamp01(local * 12f); break;
                    case Fade.InOut: a = it.A0 * Mathf.Sin(u * Mathf.PI); break;
                    default: a = it.A0 * (1f - u * u); break;
                }
                it.Sr.color = new Color(it.Col.r, it.Col.g, it.Col.b, a);
            }
        }
    }
}
