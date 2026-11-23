using System;
using System.Collections.Generic;

namespace ChibiClash.Netcode
{
    // Pure audio rules for ChibiAudio (design/audio/sfx-events.md). Presentation only: reads ChibiSimState / events,
    // never writes them, no UnityEngine (EditMode-testable without an AudioListener). Real time is passed in as seconds.

    /// <summary>Mixer bus of a sound (sfx-events.md 5.1). AMB = stage ambience beds (dedicated looping source, 8.1).</summary>
    public enum SfxBus : byte { STG, CMB, PRJ, MOV, UI, AMB }

    /// <summary>Duck triggered when a request starts playing (sfx-events.md 5.3; StockKo = a stock lost mid-match, 8.3).</summary>
    public enum SfxDuck : byte { None, Ko, Cinematic, HitAccent, RingOut, StockKo }

    /// <summary>One row of Resources/Audio/sfx_meta.txt (generated from sfx-spec.md by tools/audio/gen_sfx.py).</summary>
    public struct SfxMeta
    {
        public string Id;
        public SfxBus Bus;
        /// <summary>Voice priority 0..100 (higher wins when stealing), max simultaneous voices of this id, cooldown per fighter.</summary>
        public int Pri, Max, CdMs, Variants;
        /// <summary>Runtime pitch jitter (+-percent) and the baked peak (dBFS).</summary>
        public float PitchPct, PeakDb;
    }

    /// <summary>Parser for the generated mix table.</summary>
    public static class SfxMetaTable
    {
        /// <summary>Parse the tab-separated table (lines starting with # are comments). Malformed lines are skipped.</summary>
        /// <example><code>var meta = SfxMetaTable.Parse(Resources.Load&lt;TextAsset&gt;("Audio/sfx_meta").text);</code></example>
        public static Dictionary<string, SfxMeta> Parse(string text)
        {
            var map = new Dictionary<string, SfxMeta>();
            if (string.IsNullOrEmpty(text)) return map;
            foreach (var raw in text.Split('\n'))
            {
                var line = raw.Trim();
                if (line.Length == 0 || line[0] == '#') continue;
                var c = line.Split('\t');
                if (c.Length < 8 || !Enum.TryParse(c[1], out SfxBus bus)) continue;
                var inv = System.Globalization.CultureInfo.InvariantCulture;
                if (!int.TryParse(c[2], out int pri) || !int.TryParse(c[3], out int max) || !int.TryParse(c[4], out int cd) ||
                    !float.TryParse(c[5], System.Globalization.NumberStyles.Float, inv, out float pitch) ||
                    !float.TryParse(c[6], System.Globalization.NumberStyles.Float, inv, out float peak) ||
                    !int.TryParse(c[7], out int variants)) continue;
                map[c[0]] = new SfxMeta { Id = c[0], Bus = bus, Pri = pri, Max = max, CdMs = cd, PitchPct = pitch, PeakDb = peak, Variants = variants };
            }
            return map;
        }
    }

    /// <summary>
    /// A sound to start (Id set) or a voice tag to stop (Id null, StopTag set). Produced by <see cref="ChibiSfxRouter"/>
    /// and <see cref="ChibiSfxMap"/>, consumed by ChibiAudio.
    /// </summary>
    public struct SfxRequest
    {
        public string Id;
        /// <summary>Fighter the sound belongs to (cooldown key, pan); -1 = none (centred).</summary>
        public int Fighter;
        /// <summary>Playback pitch multiplier (before the runtime jitter) and extra gain in dB.</summary>
        public float Pitch, GainDb;
        /// <summary>Real-time delay before it starts, seconds.</summary>
        public float Delay;
        /// <summary>Bus override; null = the id's bus from the table.</summary>
        public SfxBus? Bus;
        /// <summary>Ignore ducking (the ult cinematic and the user's ult layer).</summary>
        public bool Exempt;
        /// <summary>Stereo position in sim milli units (screen X); used when <see cref="Panned"/>.</summary>
        public int PanX;
        public bool Panned;
        /// <summary>Voice tag (0 none). Loops and hit bodies are tagged so they can be stopped / replaced.</summary>
        public int Tag;
        /// <summary>Stop every voice (and pending request) with this tag before anything else; 0 none.</summary>
        public int StopTag;
        /// <summary>Loop the clip until stopped by tag.</summary>
        public bool Loop;
        /// <summary>Duck started together with this sound.</summary>
        public SfxDuck Duck;
        /// <summary>Pan sweep (sfx-events.md 8.2, bird pass): when &gt; 0 the pan moves from PanX to PanEndX over this many seconds.</summary>
        public float PanSweepSec;
        /// <summary>End of the pan sweep, sim milli units.</summary>
        public int PanEndX;

        /// <summary>Request for <paramref name="id"/> at pitch 1, 0 dB.</summary>
        public static SfxRequest Of(string id, int fighter = -1, float pitch = 1f, float gainDb = 0f) =>
            new SfxRequest { Id = id, Fighter = fighter, Pitch = pitch, GainDb = gainDb };

        /// <summary>Stop request for <paramref name="tag"/>.</summary>
        public static SfxRequest Stop(int tag) => new SfxRequest { StopTag = tag, Fighter = -1, Pitch = 1f };
    }

    /// <summary>
    /// Rollback dedupe (sfx-events.md 6): key (Kind, Index, Value, Value2) at a sim frame; a second event with the same
    /// key within +-<see cref="Tolerance"/> frames of a stored one is a re-emission after a rollback and is dropped.
    /// </summary>
    public sealed class ChibiSfxDedupe
    {
        struct Key { public int Kind, Index, Value, Value2, Frame; public bool Used; }
        readonly Key[] _ring;
        int _next;

        /// <summary>Frame tolerance (tuning knob DedupeFrames, default 3).</summary>
        public int Tolerance = 3;

        /// <param name="capacity">Ring size (spec: last 64 played keys).</param>
        public ChibiSfxDedupe(int capacity = 64) { _ring = new Key[Math.Max(1, capacity)]; }

        /// <summary>True if this event was already played (drop it); otherwise records it and returns false.</summary>
        /// <example><code>if (dedupe.Seen((int)e.Kind, e.Index, e.Value, e.Value2, cur.Frame)) continue;</code></example>
        public bool Seen(int kind, int index, int value, int value2, int frame)
        {
            for (int i = 0; i < _ring.Length; i++)
            {
                var k = _ring[i];
                if (k.Used && k.Kind == kind && k.Index == index && k.Value == value && k.Value2 == value2 &&
                    Math.Abs(k.Frame - frame) <= Tolerance) return true;
            }
            _ring[_next] = new Key { Kind = kind, Index = index, Value = value, Value2 = value2, Frame = frame, Used = true };
            _next = (_next + 1) % _ring.Length;
            return false;
        }

        /// <summary>Forget everything (new match).</summary>
        public void Clear() { Array.Clear(_ring, 0, _ring.Length); _next = 0; }
    }

    /// <summary>Per-(id, fighter) re-trigger cooldown (spec CD column). A request inside the cooldown is dropped.</summary>
    public sealed class ChibiSfxCooldowns
    {
        readonly Dictionary<string, float>[] _last = { new Dictionary<string, float>(), new Dictionary<string, float>(), new Dictionary<string, float>() };

        /// <summary>True (and records the time) when <paramref name="id"/> may play for <paramref name="fighter"/> now.</summary>
        public bool TryPass(string id, int fighter, float now, int cdMs)
        {
            var d = _last[fighter == 0 ? 0 : fighter == 1 ? 1 : 2];
            if (cdMs > 0 && d.TryGetValue(id, out float t) && now - t < cdMs / 1000f) return false;
            d[id] = now;
            return true;
        }

        public void Clear() { foreach (var d in _last) d.Clear(); }
    }

    /// <summary>What the voice stealer needs to know about one pooled voice.</summary>
    public struct SfxVoiceInfo
    {
        public bool Busy;
        public SfxBus Bus;
        public int Pri;
        public float Start;
        public string Id;
    }

    /// <summary>Voice allocation and stealing (sfx-events.md 5.1).</summary>
    public static class ChibiVoiceSteal
    {
        /// <summary>Voices with this priority or more are never stolen.</summary>
        public const int Protected = 90;

        /// <summary>
        /// Index of the voice to (re)use for a new sound, or -1 to drop it. Order: the id's Max (steal the oldest
        /// voice of that id), then the bus cap (steal the lowest priority voice of the bus, oldest on ties), then a
        /// free voice, then the whole pool (same rule). A victim is never Pri &gt;= 90 and never above the new priority.
        /// </summary>
        /// <example><code>int v = ChibiVoiceSteal.Choose(voices, SfxBus.CMB, 12, "kaia_shk", 4, 65);</code></example>
        public static int Choose(SfxVoiceInfo[] voices, SfxBus bus, int busCap, string id, int idMax, int pri)
        {
            int idCount = 0, busCount = 0, free = -1;
            for (int i = 0; i < voices.Length; i++)
            {
                if (!voices[i].Busy) { if (free < 0) free = i; continue; }
                if (voices[i].Id == id) idCount++;
                if (voices[i].Bus == bus) busCount++;
            }
            if (idMax > 0 && idCount >= idMax) return Victim(voices, pri, v => v.Id == id);
            if (busCap > 0 && busCount >= busCap) return Victim(voices, pri, v => v.Bus == bus);
            if (free >= 0) return free;
            return Victim(voices, pri, v => true);
        }

        static int Victim(SfxVoiceInfo[] voices, int pri, Func<SfxVoiceInfo, bool> match)
        {
            int best = -1;
            for (int i = 0; i < voices.Length; i++)
            {
                var v = voices[i];
                if (!v.Busy || !match(v) || v.Pri >= Protected || v.Pri > pri) continue;
                if (best < 0 || v.Pri < voices[best].Pri || (v.Pri == voices[best].Pri && v.Start < voices[best].Start)) best = i;
            }
            return best;
        }
    }

    /// <summary>Static sound tables (sfx-events.md sections 2-4), keyed by class index (FighterClass order).</summary>
    public static class ChibiSfxMap
    {
        /// <summary>Lower-case character id per class (CharacterRoster: Melee Kaia, Heavy Bruna, Ranged Ren, Magic Lyra, Polearm Taro).</summary>
        public static readonly string[] Char = { "kaia", "bruna", "ren", "lyra", "taro" };

        internal static readonly string[] JabLight = { "kaia_swish", "bruna_whoosh", "ren_thwip", "lyra_ping", "taro_shk" };
        internal static readonly string[] JabFinisher = { "kaia_shrak", "bruna_swoosh", "ren_thwip", "lyra_flash", "taro_swoon" };
        internal static readonly string[] Heavy = { "kaia_shrak", "bruna_swoosh", "ren_whoosh", "lyra_flash", "taro_heavy_swing" };
        internal static readonly string[] Impact = { "kaia_shk", "bruna_thud", "ren_zing", "lyra_ting", "taro_shrak" };
        internal static readonly string[] ParryLayer = { "kaia_clink", "bruna_clang", "ren_zing", "lyra_ting", "taro_ting" };
        internal static readonly string[] UltLayer = { "kaia_ult", "bruna_smash", "ren_draw", "lyra_ult", "taro_zaa" };
        internal static readonly string[] Contact = { "kaia_thrak", "bruna_thoom", "ren_thump", "lyra_boom", "taro_boom" };

        /// <summary>Clamp a sim class int to a table index.</summary>
        public static int Cls(int c) => c < 0 ? 0 : c > 4 ? 4 : c;

        /// <summary>The move animation kinds <see cref="Move"/> understands.</summary>
        public static readonly string[] MoveKinds =
            { "jab", "swing", "upswing", "lowsweep", "thrust", "slam", "spin", "shoot", "cast", "dive", "uppercut", "guard", "rush", "launch" };

        /// <summary>
        /// Move start sounds by animation kind (sfx-events.md 3.1). <paramref name="heavy"/> = smash / charged / jab
        /// finisher / heavy slot. Appends to <paramref name="output"/>; returns false for an unknown kind (nothing added).
        /// </summary>
        /// <example><code>ChibiSfxMap.Move(FighterClass.Polearm, "launch", false, 0, list);   // taro_tchk + taro_whrr -6</code></example>
        public static bool Move(FighterClass cls, string kind, bool heavy, int fighter, List<SfxRequest> output)
        {
            int c = Cls((int)cls);
            string a = null, b = null;
            float pa = 1f, gb = 0f;
            switch (kind)
            {
                case "jab": a = heavy ? JabFinisher[c] : JabLight[c]; break;
                case "swing":
                    a = Pick(c, heavy, "kaia_swish", "kaia_shrak", "bruna_whoosh", "bruna_swoosh", "ren_thwip", "ren_whoosh",
                             "lyra_swish", "lyra_flash", "taro_swoon", "taro_heavy_swing"); break;
                case "upswing":
                    a = Pick(c, heavy, "kaia_shing", "kaia_vwoom", "bruna_whoosh", "bruna_swoosh", "ren_whoosh", "ren_whoosh",
                             "lyra_swish", "lyra_flash", "taro_swoon", "taro_heavy_swing"); break;
                case "lowsweep":
                    a = new[] { "kaia_skrrt", "bruna_whoosh", "ren_thwip", "lyra_swish", "taro_swoon" }[c];
                    pa = new[] { 1f, 0.9f, 0.9f, 0.9f, 0.95f }[c]; break;
                case "thrust":
                    a = c == 4 ? (heavy ? "taro_zaa" : "taro_shk") : new[] { "kaia_whoosh", "bruna_rush", "ren_thwip", "lyra_swish" }[c]; break;
                case "slam": a = new[] { "kaia_shrak", "bruna_swoosh", "ren_whoosh", "lyra_flash", "taro_heavy_swing" }[c]; break;
                case "spin": a = new[] { "kaia_vwoom", "bruna_whoosh", "ren_whoosh", "lyra_swish", "taro_whrr" }[c]; break;
                case "shoot": a = new[] { "kaia_whoosh", "bruna_rumble", "ren_twang", "lyra_pew", "taro_wind_lance" }[c]; break;
                case "cast": a = new[] { "kaia_tshing", "bruna_rumble", "ren_pfft", "lyra_fwoom", "taro_wind_lance" }[c]; break;
                case "dive":
                    a = new[] { "kaia_whoosh", "bruna_swoosh", "ren_whoosh", "lyra_fwoom", "taro_zaa" }[c];
                    pa = new[] { 0.9f, 1f, 0.9f, 1f, 0.9f }[c]; break;
                case "uppercut":
                    a = new[] { "kaia_vwoom", "bruna_vroom", "ren_whoosh", "lyra_flash", "taro_clang" }[c];
                    pa = c == 2 ? 1.1f : 1f; break;
                case "guard": a = new[] { "kaia_tshing", "bruna_guard", "ren_pfft", "lyra_vmm", "taro_ting" }[c]; break;
                case "rush": a = new[] { "kaia_whoosh", "bruna_rush", "ren_whoosh", "lyra_shim", "taro_zaa" }[c]; break;
                case "launch":
                    a = new[] { "kaia_vwoom", "bruna_vroom", "ren_zip", "lyra_fwoom", "taro_tchk" }[c];
                    if (c == 4) { b = "taro_whrr"; gb = -6f; }
                    break;
                default: return false;
            }
            output.Add(SfxRequest.Of(a, fighter, pa));
            if (b != null) output.Add(SfxRequest.Of(b, fighter, 1f, gb));
            return true;
        }

        static string Pick(int c, bool heavy, params string[] lh) => lh[c * 2 + (heavy ? 1 : 0)];

        /// <summary>Spawn / hit / expire sounds per projectile kind (sfx-events.md 4).</summary>
        internal struct ProjRow
        {
            public string Spawn, Spawn2, Hit, HitBody, Expire;
            public float SpawnPitch, SpawnDb, Spawn2Pitch, HitPitch, HitDb, ExpirePitch, ExpireDb;
        }

        internal static ProjRow Proj(int kind)
        {
            switch ((ChibiProjKind)kind)
            {
                case ChibiProjKind.Arrow: return Row("ren_whip", "ren_zing", "core_poof", expireDb: -6f);
                case ChibiProjKind.ArrowBig:
                {
                    var r = Row("ren_bang", "ren_zing", "core_poof");
                    r.Spawn2 = "ren_whip"; r.Spawn2Pitch = 0.85f; r.HitBody = "core_hit_heavy";
                    return r;
                }
                case ChibiProjKind.Bolt: return Row("lyra_pew", "lyra_ting", "core_poof", expireDb: -6f);
                case ChibiProjKind.BoltBig: return Row("lyra_pew", "lyra_ting", "core_poof", spawnPitch: 0.8f, hitPitch: 0.85f);
                case ChibiProjKind.AuraWave: return Row("kaia_ult", "kaia_shk", "core_poof", spawnPitch: 1.2f, spawnDb: -6f, expireDb: -6f);
                case ChibiProjKind.Fireball: return Row("lyra_fireball", "lyra_boom", "lyra_boom", expireDb: -6f);
                case ChibiProjKind.Spear: return Row("taro_spear_throw", "taro_shrak", "taro_tchk");
                case ChibiProjKind.SmashWave: return Row("bruna_rumble", "bruna_bam", "core_poof", expirePitch: 0.8f);
                case ChibiProjKind.VolleyLead: return Row("ren_bang", "ren_zing", "ren_volley", spawnPitch: 1.1f);
                case ChibiProjKind.VolleyArrow: return Row("ren_volley", "ren_zing", null, hitDb: -3f);
                case ChibiProjKind.PoseFireball: return Row("lyra_fireball", "lyra_boom", "lyra_boom", spawnPitch: 1.15f, hitDb: -3f, expireDb: -9f);
                default: return Row("core_proj_spawn", "core_proj_hit", "core_poof");
            }
        }

        static ProjRow Row(string spawn, string hit, string expire, float spawnPitch = 1f, float spawnDb = 0f,
                           float hitPitch = 1f, float hitDb = 0f, float expirePitch = 1f, float expireDb = 0f) =>
            new ProjRow { Spawn = spawn, Hit = hit, Expire = expire, SpawnPitch = spawnPitch, SpawnDb = spawnDb, Spawn2Pitch = 1f,
                          HitPitch = hitPitch, HitDb = hitDb, ExpirePitch = expirePitch, ExpireDb = expireDb, HitBody = "core_proj_hit" };

        /// <summary>
        /// Countdown number for the intro (3 / 2 / 1, 0 once "FIGHT!" shows or the intro is over): the pre-fight part of
        /// the intro (IntroTicks - IntroFightTicks real ticks) split in three. The HUD may use it to draw matching numbers.
        /// </summary>
        public static int CountdownNumber(int introRealTicks)
        {
            int pre = ChibiSimConst.IntroTicks - ChibiSimConst.IntroFightTicks;
            int t = introRealTicks - ChibiSimConst.IntroFightTicks;
            if (t <= 0 || pre <= 0) return 0;
            int n = (t * 3 + pre - 1) / pre;
            return n < 1 ? 1 : n > 3 ? 3 : n;
        }
    }

    /// <summary>
    /// Turns simulation events (plus view-side derived events read from prev/cur state) into <see cref="SfxRequest"/>s:
    /// sfx-events.md sections 2-6 (hit composition, multi-hit window, string attenuation, rollback dedupe,
    /// confirmed-only match end, countdown). One instance per match; pure C#, real time passed in.
    /// </summary>
    public sealed class ChibiSfxRouter
    {
        // ---- tuning knobs (sfx-events.md 7)
        /// <summary>Whole HP at or above which a hit uses the heavy body.</summary>
        public int HeavyHitHp = 8;
        public float HitWindowSec = 0.06f, StringResetSec = 0.3f, StringStepDb = -3f, StringFloorDb = -9f;

        /// <summary>Fighter whose UI-only sounds (ult prompt) play and who hears the opponent's ult-ready at -8 dB; -1 = both local (offline).</summary>
        public int LocalPlayer = -1;
        /// <summary>Match end (KO / stings) waits for <see cref="ConfirmEnd"/> instead of playing on first sight (online).</summary>
        public bool NeedsConfirm;
        /// <summary>Sim frame of a match end waiting for confirmation, -1 none.</summary>
        public int PendingEndFrame { get; private set; } = -1;
        /// <summary>Class per fighter used instead of the state's (per-event entry with explicit classes); null = state.</summary>
        public int[] ClassOverride;

        /// <summary>Tag of the body voice of a hit on defender d (replaced by a heavier hit inside the window).</summary>
        public static int BodyTag(int d) => 100 + d;
        /// <summary>Tag of Taro's Spin Guard loop / Lyra's shield hum loop for fighter i.</summary>
        public static int SpinLoopTag(int i) => 200 + i;
        public static int ShieldLoopTag(int i) => 210 + i;

        // ---- rules v20 stocks loop knobs (sfx-events.md 8.3)
        /// <summary>Launch speed (velocity sub-units per tick) at or above which a hit plays core_launch_heavy: KB 100 x LsSubPerKb.</summary>
        public int HeavyLaunchSub = 100 * ChibiPercentConst.LsSubPerKb;
        /// <summary>Defender percent (milli) at or above which a strong launch counts as heavy (60 %).</summary>
        public int HeavyLaunchPctM = 60_000;
        /// <summary>Delay of core_last_stock after the KO blast that left a fighter on its last stock, seconds.</summary>
        public float LastStockDelaySec = 0.7f;

        // Derived event kinds for the dedupe ring (outside ChibiSimEventKind's range).
        const int DJump = 100, DDouble = 101, DLand = 102, DGuardBreak = 103, DUltReady = 104, DCrit = 105, DProgram = 106,
                  DStockKo = 107, DRespawn = 108, DLaunch = 109;

        public readonly ChibiSfxDedupe Dedupe = new ChibiSfxDedupe();
        readonly float[] _lastHit = { -10f, -10f, -10f, -10f }, _lastStringHit = { -10f, -10f, -10f, -10f };
        readonly int[] _lastLevel = new int[ChibiSimState.MaxFighters], _string = new int[ChibiSimState.MaxFighters];
        readonly bool[] _spinLoop = new bool[ChibiSimState.MaxFighters], _shieldLoop = new bool[ChibiSimState.MaxFighters];
        readonly int[] _contactSlot = { -1, -1, -1, -1 };
        readonly float[] _contactUntil = new float[ChibiSimState.MaxFighters];
        bool _endDone;
        int _lastCountdown, _lastIntro;
        // Scratch (no per-step allocation).
        readonly bool[] _damaged = new bool[ChibiSimState.MaxFighters], _guardBreak = new bool[ChibiSimState.MaxFighters], _crit = new bool[ChibiSimState.MaxFighters];
        readonly List<int> _goneOwner = new List<int>(8), _goneKind = new List<int>(8);
        readonly bool[] _goneUsed = new bool[64];
        readonly int[] _minionCount = new int[ChibiSimState.MaxFighters];
        readonly int[] _progFrame = new int[ChibiSimState.MaxFighters], _progSlot = new int[ChibiSimState.MaxFighters];

        /// <summary>Forget all per-match state.</summary>
        public void Reset()
        {
            Dedupe.Clear();
            for (int i = 0; i < ChibiSimState.MaxFighters; i++)
            {
                _lastHit[i] = _lastStringHit[i] = -10f; _lastLevel[i] = _string[i] = 0;
                _spinLoop[i] = _shieldLoop[i] = false; _contactSlot[i] = -1; _contactUntil[i] = 0f;
            }
            _endDone = false; PendingEndFrame = -1; _lastCountdown = 0; _lastIntro = 0;
        }

        /// <summary>A foe of <paramref name="owner"/> was hurt this batch (1v1: exactly the opponent 1 - owner).</summary>
        bool HurtFoe(ChibiSimState s, int owner)
        {
            if (s.PlayerCount <= 2) return _damaged[1 - owner];
            int nf = ChibiSimEvents.Fighters(s);
            for (int j = 0; j < nf; j++) if (_damaged[j] && ChibiSimulation.IsFoe(s, owner, j)) return true;
            return false;
        }

        int ClassOf(ChibiSimState s, int i) => ChibiSfxMap.Cls(ClassOverride != null && i >= 0 && i < ClassOverride.Length ? ClassOverride[i] : s.F[i].Class);

        static SfxRequest At(string id, int fighter, int x, float pitch = 1f, float db = 0f)
        {
            var r = SfxRequest.Of(id, fighter, pitch, db);
            r.PanX = x; r.Panned = true;
            return r;
        }

        /// <summary>
        /// Arm the slam / dive contact layer for <paramref name="fighter"/> (called by ChibiAudio.PlayMove for those kinds):
        /// it plays on the fighter's next landing or first hit while the same move slot is active (max 2 s).
        /// </summary>
        public void ArmContact(int fighter, int moveSlot, float now)
        {
            if (fighter < 0 || fighter > 1) return;
            _contactSlot[fighter] = moveSlot;
            _contactUntil[fighter] = now + 2f;
        }

        bool TakeContact(int i, ChibiSimState cur, float now)
        {
            if (_contactSlot[i] < 0) return false;
            bool ok = now <= _contactUntil[i] && cur.F[i].MoveSlot == _contactSlot[i];
            _contactSlot[i] = -1;
            return ok;
        }

        /// <summary>
        /// Process one diff batch: <paramref name="events"/>[first..] were produced by ChibiSimEvents.Diff(prev, cur).
        /// Appends requests to <paramref name="output"/>. Neither state is modified.
        /// </summary>
        /// <example><code>router.Step(shown, now, events, first, Time.unscaledTime, requests);</code></example>
        public void Step(ChibiSimState prev, ChibiSimState cur, List<ChibiSimEvent> events, int first, float now, List<SfxRequest> output)
        {
            int frame = cur.Frame;
            int nf = ChibiSimEvents.Fighters(cur);
            for (int i = 0; i < ChibiSimState.MaxFighters; i++)
            {
                _damaged[i] = _guardBreak[i] = _crit[i] = false; _minionCount[i] = 0; _progFrame[i] = -1;
            }
            _goneOwner.Clear(); _goneKind.Clear();
            Array.Clear(_goneUsed, 0, _goneUsed.Length);
            for (int k = first; k < events.Count; k++)
            {
                var e = events[k];
                if (e.Kind == ChibiSimEventKind.Damaged && e.Value > 0 && e.Index >= 0 && e.Index < nf) _damaged[e.Index] = true;
                if (e.Kind == ChibiSimEventKind.ProjectileGone) { _goneOwner.Add(e.Value); _goneKind.Add(e.Value2); }
            }

            // ---- derived events (sfx-events.md 2.2), before the hits that they modify
            bool critStop = cur.HitstopRealTicks == ChibiSimConst.HitstopCrit && cur.HitstopRealTicks > prev.HitstopRealTicks;
            for (int i = 0; i < nf; i++)
            {
                var p = prev.F[i];
                var c = cur.F[i];
                int cls = ClassOf(cur, i);
                if (critStop && _damaged[i] && !Dedupe.Seen(DCrit, i, 0, 0, frame)) _crit[i] = true;
                if (c.GuardBrokenTicks == ChibiCombatStats.GuardBrokenTicks && p.GuardBrokenTicks != c.GuardBrokenTicks &&
                    !Dedupe.Seen(DGuardBreak, i, 0, 0, frame))
                {
                    _guardBreak[i] = true;
                    var r = At("core_guard_break", i, c.X); r.Duck = SfxDuck.HitAccent;
                    output.Add(r);
                    {
                        int gb = ChibiSimView.Attacker(cur, i);   // who broke the guard (1v1: 1 - i)
                        if (gb >= 0 && ClassOf(cur, gb) == (int)FighterClass.Heavy) output.Add(At("bruna_krash", gb, c.X));
                    }
                }
                if (p.Grounded && !c.Grounded && c.Vy > 0 && !Dedupe.Seen(DJump, i, 0, 0, frame))
                    output.Add(At("core_jump", i, c.X));
                else if (!c.Grounded && c.AirJumps < p.AirJumps && !Dedupe.Seen(DDouble, i, c.AirJumps, 0, frame))
                    output.Add(At("core_double_jump", i, c.X));
                if (!p.Grounded && c.Grounded && !Dedupe.Seen(DLand, i, 0, 0, frame))
                {
                    if (TakeContact(i, cur, now)) output.Add(At(ChibiSfxMap.Contact[cls], i, c.X));
                    else
                    {
                        output.Add(At("core_land", i, c.X));
                        if (cls == (int)FighterClass.Heavy) output.Add(At("bruna_armor", i, c.X));
                    }
                }
                if (p.Ult < ChibiSimConst.UltMax && c.Ult >= ChibiSimConst.UltMax && !Dedupe.Seen(DUltReady, i, 0, 0, frame))
                {
                    var r = SfxRequest.Of("core_ult_ready", i, 1f, LocalPlayer >= 0 && i != LocalPlayer ? -8f : 0f);
                    output.Add(r);
                }
                StocksLoop(prev, cur, i, frame, output);
                // Loops follow the (possibly re-simulated) state: stop when the move / shield is gone (6.4).
                if (_spinLoop[i] && !(cls == (int)FighterClass.Polearm && c.MoveSlot == 3))
                {
                    _spinLoop[i] = false; output.Add(SfxRequest.Stop(SpinLoopTag(i)));
                }
                if (_shieldLoop[i] && !c.Shielding)
                {
                    _shieldLoop[i] = false; output.Add(SfxRequest.Stop(ShieldLoopTag(i)));
                }
            }

            // ---- simulation events
            for (int k = first; k < events.Count; k++)
            {
                var e = events[k];
                if (e.Kind == ChibiSimEventKind.ProgramFrame)
                {
                    // Cadence only, at most the latest qualifying frame per batch (sfx-events.md 2, ProgramFrame row).
                    if (e.Index >= 0 && e.Index < nf && ProgramQualifies(ClassOf(cur, e.Index), e.Value, e.Value2))
                    {
                        _progFrame[e.Index] = e.Value2; _progSlot[e.Index] = e.Value;
                    }
                    continue;
                }
                if (Dedupe.Seen((int)e.Kind, e.Index, e.Value, e.Value2, frame)) continue;
                Event(prev, cur, e, now, output);
            }
            for (int i = 0; i < nf; i++)
            {
                if (_progFrame[i] < 0) continue;
                int start = cur.GameTicks - cur.F[i].MoveTick;   // key = move start + move frame (sfx-events.md 6.1)
                if (Dedupe.Seen(DProgram, i, _progSlot[i], _progFrame[i], start + _progFrame[i])) continue;
                int cls = ClassOf(cur, i);
                if (cls == (int)FighterClass.Melee) output.Add(At("kaia_swish", i, cur.F[i].X));
                else if (cls == (int)FighterClass.Heavy)
                    output.Add(At("bruna_whoosh", i, cur.F[i].X, (_progFrame[i] / ChibiSimConst.TornadoPeriod) % 2 == 1 ? 1.06f : 1f));
            }

            // Match end without a KO event (time-up) still gets its sting.
            if (!prev.MatchOver && cur.MatchOver && !_endDone && PendingEndFrame < 0) MatchEnded(cur, output);
        }

        /// <summary>
        /// Rules v20 percent / stocks loop (sfx-events.md 8.3), read from prev/cur only. KO and respawn are keyed on the KO
        /// number (KosTaken) with no frame drift, so a rollback that re-shows the same KO a few frames later stays silent.
        ///   - KosTaken rose (stock lost, or a training KO): core_ko_blast at the KO point (StockKo duck); when it leaves the
        ///     fighter on its last stock, core_last_stock follows. The FINAL stock plays nothing here: the confirmed match
        ///     end plays core_final_stock (which carries its own blast).
        ///   - HoverTicks rose from 0 (respawn platform): core_respawn_chime.
        ///   - Percent rose and the launch speed jumped to HeavyLaunchSub or more at HeavyLaunchPctM or more:
        ///     core_launch_heavy, pitched down up to 10 % for stronger launches.
        /// </summary>
        void StocksLoop(ChibiSimState prev, ChibiSimState cur, int i, int frame, List<SfxRequest> output)
        {
            var p = prev.F[i];
            var c = cur.F[i];
            bool ko = c.KosTaken > p.KosTaken;
            if (ko && !Dedupe.Seen(DStockKo, i, c.KosTaken, 0, 0))
            {
                bool final = !cur.Training && c.Stocks <= 0;
                if (!final)
                {
                    var r = At("core_ko_blast", i, c.X); r.Duck = SfxDuck.StockKo;
                    output.Add(r);
                    if (!cur.Training && c.Stocks == 1)
                    {
                        var w = SfxRequest.Of("core_last_stock", i); w.Delay = LastStockDelaySec;
                        output.Add(w);
                    }
                }
            }
            if (p.HoverTicks == 0 && c.HoverTicks > 0 && !Dedupe.Seen(DRespawn, i, c.KosTaken, 0, 0))
                output.Add(At("core_respawn_chime", i, c.X));
            if (!ko && c.PctM > p.PctM && c.PctM >= HeavyLaunchPctM)
            {
                double vNow = Speed(c.Vx, c.Vy), vWas = Speed(p.Vx, p.Vy);
                if (vNow >= HeavyLaunchSub && vNow - vWas >= HeavyLaunchSub * 0.5 && !Dedupe.Seen(DLaunch, i, c.PctM, 0, frame))
                {
                    float strength = (float)Math.Min(1.0, (vNow - HeavyLaunchSub) / HeavyLaunchSub);
                    output.Add(At("core_launch_heavy", i, c.X, 1f - 0.1f * strength));
                }
            }
        }

        static double Speed(int vx, int vy) => Math.Sqrt((double)vx * vx + (double)vy * vy);

        static bool ProgramQualifies(int cls, int slot, int f)
        {
            if (cls == (int)FighterClass.Melee && slot == 4)
                return f % ChibiSimConst.ThrustPeriod == 0 && f <= ChibiSimConst.ThrustPeriod * (ChibiSimConst.ThrustHits - 1);
            if (cls == (int)FighterClass.Heavy && slot == 3)
                return f % ChibiSimConst.TornadoPeriod == 0 && f <= ChibiSimConst.TornadoLastHit;
            return false;
        }

        void MatchEnded(ChibiSimState cur, List<SfxRequest> output)
        {
            if (NeedsConfirm) { PendingEndFrame = cur.Frame; return; }
            EmitEnd(cur.Winner, cur.F[0].HpU <= 0 || cur.F[1].HpU <= 0, output);
        }

        /// <summary>
        /// Play the match-end sounds once: core_final_stock (the final stock was taken: finisher blast, crowd and chord)
        /// then, 0.9 s later, the victory or draw sting (winner -1 = draw). Online, call it once the end frame is confirmed
        /// (ConfirmedState.MatchOver). Rules v20: core_final_stock replaces core_ko (sfx-events.md 8.3).
        /// </summary>
        public void ConfirmEnd(int winner, bool ko, List<SfxRequest> output) => EmitEnd(winner, ko, output);

        /// <summary>The pending end was rolled back (the live state is no longer MatchOver).</summary>
        public void CancelPendingEnd() => PendingEndFrame = -1;

        void EmitEnd(int winner, bool ko, List<SfxRequest> output)
        {
            if (_endDone) return;
            _endDone = true;
            PendingEndFrame = -1;
            if (ko)
            {
                var r = SfxRequest.Of("core_final_stock"); r.Duck = SfxDuck.Ko;
                output.Add(r);
            }
            var s = SfxRequest.Of(winner < 0 ? "core_draw_sting" : "core_victory_sting");
            s.Delay = ko ? 0.9f : 0f;
            output.Add(s);
        }

        /// <summary>
        /// Countdown (sfx-events.md 2.2): ticks for 3 / 2 / 1 (pitch 1.0 / 1.06 / 1.12) and GO when IntroRealTicks
        /// reaches 0 after an intro. Call every rendered frame with the live state's IntroRealTicks.
        /// </summary>
        public void Intro(int introRealTicks, List<SfxRequest> output)
        {
            int n = ChibiSfxMap.CountdownNumber(introRealTicks);
            if (n != 0 && n != _lastCountdown)
            {
                var r = SfxRequest.Of("core_countdown_tick", -1, n == 3 ? 1f : n == 2 ? 1.06f : 1.12f);
                output.Add(r);
            }
            _lastCountdown = n;
            if (introRealTicks == 0 && _lastIntro > 0) output.Add(SfxRequest.Of("core_countdown_go"));
            _lastIntro = introRealTicks;
        }

        void Event(ChibiSimState prev, ChibiSimState cur, in ChibiSimEvent e, float now, List<SfxRequest> o)
        {
            int i = e.Index;
            bool fighter = i == 0 || i == 1;
            int cls = fighter ? ClassOf(cur, i) : 0;
            int x = fighter ? cur.F[i].X : 0;
            switch (e.Kind)
            {
                case ChibiSimEventKind.MoveStarted:
                    if (fighter) MoveStarted(cur, i, cls, e.Value, e.Value2, o);
                    break;
                case ChibiSimEventKind.Damaged:
                    if (fighter && e.Value > 0) Hit(prev, cur, i, e.Value, now, o);
                    break;
                case ChibiSimEventKind.ShieldChanged:
                    if (!fighter || !e.On) break;
                    if (cls == (int)FighterClass.Magic)
                    {
                        o.Add(At("lyra_vmm", i, x));
                        var loop = At("lyra_vmm_loop", i, x, 1f, -3f);
                        loop.Loop = true; loop.Tag = ShieldLoopTag(i); loop.Delay = 0.5f;
                        o.Add(loop);
                        _shieldLoop[i] = true;
                    }
                    else if (cls == (int)FighterClass.Heavy) o.Add(At("bruna_guard", i, x, 1f, -6f));
                    else o.Add(At("core_shield_up", i, x));
                    break;
                case ChibiSimEventKind.Parried:
                    if (!fighter) break;
                    o.Add(At("core_parry", i, x));
                    o.Add(At(ChibiSfxMap.ParryLayer[cls], i, x));
                    break;
                case ChibiSimEventKind.RingOut:
                    // Rules v20: KO and respawn are derived from Stocks / KosTaken / HoverTicks in StocksLoop (8.3), so the
                    // RingOut event (whatever its v20 definition) plays nothing here and can never double them.
                    break;
                case ChibiSimEventKind.KO:
                    if (!_endDone && PendingEndFrame < 0) MatchEnded(cur, o);
                    break;
                case ChibiSimEventKind.ProjectileSpawned:
                {
                    var row = ChibiSfxMap.Proj(e.Value2);
                    int px = i >= 0 && i < cur.Proj.Length ? cur.Proj[i].X : 0;
                    var r = At(row.Spawn, e.Value, px, row.SpawnPitch, row.SpawnDb);
                    if (e.Value2 == (int)ChibiProjKind.AuraWave) r.Bus = SfxBus.PRJ;   // kaia_ult row: AuraWave spawn on PRJ
                    o.Add(r);
                    if (row.Spawn2 != null) o.Add(At(row.Spawn2, e.Value, px, row.Spawn2Pitch));
                    break;
                }
                case ChibiSimEventKind.ProjectileGone:
                {
                    int owner = e.Value;
                    // A projectile that left on the batch its owner's opponent was hurt is the hit (2.1); no expire sound.
                    if (owner >= 0 && owner < ChibiSimState.MaxFighters && HurtFoe(cur, owner)) break;
                    var row = ChibiSfxMap.Proj(e.Value2);
                    if (row.Expire == null) break;
                    int px = i >= 0 && i < prev.Proj.Length ? prev.Proj[i].X : 0;
                    o.Add(At(row.Expire, owner, px, row.ExpirePitch, row.ExpireDb));
                    break;
                }
                case ChibiSimEventKind.DashStarted:
                case ChibiSimEventKind.EscapeDash:   // rules v24 (ED-1): the escape plays the class dash sound
                    if (!fighter) break;
                    switch ((FighterClass)cls)
                    {
                        case FighterClass.Melee: o.Add(At("kaia_dash", i, x)); break;
                        case FighterClass.Magic: o.Add(At("lyra_shim", i, x)); break;
                        case FighterClass.Polearm: o.Add(At("taro_tchk", i, x)); break;
                        case FighterClass.Heavy: o.Add(At("core_dash", i, x, 0.85f)); o.Add(At("bruna_armor", i, x)); break;
                        default: o.Add(At("core_dash", i, x)); break;
                    }
                    break;
                case ChibiSimEventKind.CinematicStarted:
                {
                    if (!fighter) break;
                    var r = SfxRequest.Of("core_ult_cinematic", i); r.Exempt = true; r.Duck = SfxDuck.Cinematic;
                    o.Add(r);
                    var l = SfxRequest.Of(ChibiSfxMap.UltLayer[cls], i); l.Exempt = true;
                    o.Add(l);
                    break;
                }
                case ChibiSimEventKind.MovePhaseChanged:
                    if (fighter) Phase(cls, i, x, e.Value, e.Value2, o);
                    break;
                case ChibiSimEventKind.MinionSpawned:
                {
                    int own = e.Value == 1 ? 1 : 0;
                    int n = _minionCount[own]++;
                    if (n > 2) break;
                    int mx = i >= 0 && i < cur.Minions.Length ? cur.Minions[i].X : 0;
                    var r = At("lyra_summon", e.Value, mx, 1f, n == 0 ? 0f : -6f);
                    r.Delay = 0.04f * n;
                    o.Add(r);
                    break;
                }
                case ChibiSimEventKind.MinionGone:
                    o.Add(At("core_poof", e.Value, i >= 0 && i < prev.Minions.Length ? prev.Minions[i].X : 0));
                    break;
                case ChibiSimEventKind.CloudSpawned:
                    o.Add(At("lyra_fwoom", e.Value, i >= 0 && i < cur.Clouds.Length ? cur.Clouds[i].MilliX : 0, 0.8f));
                    break;
                case ChibiSimEventKind.CloudGone:
                    o.Add(At("core_poof", e.Value, i >= 0 && i < prev.Clouds.Length ? prev.Clouds[i].MilliX : 0, 0.9f));
                    break;
                case ChibiSimEventKind.CloudStrike:
                    o.Add(At("lyra_thunder", e.Value, i >= 0 && i < cur.Clouds.Length ? cur.Clouds[i].MilliX : 0));
                    break;
                case ChibiSimEventKind.PoseSuperJump:
                    if (fighter) o.Add(At("core_super_jump", i, x));
                    break;
                case ChibiSimEventKind.UltPromptChanged:
                    if (!fighter || (LocalPlayer >= 0 && i != LocalPlayer)) break;
                    if (e.Value == (int)ChibiUltPrompt.Preparing) o.Add(SfxRequest.Of("core_ult_prompt", i));
                    else if (e.Value == (int)ChibiUltPrompt.Failed) o.Add(SfxRequest.Of("core_ult_fail", i));
                    break;
                case ChibiSimEventKind.Revived:
                    if (fighter) o.Add(At("core_revive", i, x));
                    break;
            }
        }

        void MoveStarted(ChibiSimState cur, int i, int cls, int slot, int combo, List<SfxRequest> o)
        {
            int x = cur.F[i].X;
            switch (slot)
            {
                case 0:
                {
                    int len = cur.F[i].Stats.PatternLength;
                    bool finisher = combo >= 2 && (len <= 0 || combo == len - 1);
                    o.Add(At(finisher ? ChibiSfxMap.JabFinisher[cls] : ChibiSfxMap.JabLight[cls], i, x));
                    return;
                }
                case 1: o.Add(At(ChibiSfxMap.Heavy[cls], i, x)); return;
                case 2:
                    switch ((FighterClass)cls)
                    {
                        case FighterClass.Melee: o.Add(At("kaia_whoosh", i, x)); break;
                        case FighterClass.Heavy: o.Add(At("bruna_armor", i, x)); break;
                        case FighterClass.Ranged: o.Add(At("core_dash", i, x)); o.Add(At("ren_pfft", i, x)); break;
                        case FighterClass.Magic: o.Add(At("lyra_fwoom", i, x)); break;
                        default: o.Add(At("taro_tchk", i, x)); break;
                    }
                    return;
                case 3:
                    switch ((FighterClass)cls)
                    {
                        case FighterClass.Melee: o.Add(At("kaia_tshing", i, x)); break;
                        case FighterClass.Ranged: o.Add(At("ren_zip", i, x)); break;
                        case FighterClass.Magic: o.Add(At("lyra_fwoom", i, x)); break;
                        case FighterClass.Polearm:
                        {
                            o.Add(SfxRequest.Stop(SpinLoopTag(i)));
                            var r = At("taro_whrr_loop", i, x); r.Loop = true; r.Tag = SpinLoopTag(i);
                            o.Add(r);
                            _spinLoop[i] = true;
                            break;
                        }
                        // Heavy Tornado Spin: ProgramFrame cadence.
                    }
                    return;
                case 4:
                    switch ((FighterClass)cls)
                    {
                        case FighterClass.Ranged: o.Add(At("ren_draw", i, x)); break;
                        case FighterClass.Magic: o.Add(At("lyra_fwoom", i, x, 0.8f)); break;
                        case FighterClass.Polearm: o.Add(At("taro_heavy_swing", i, x)); break;
                        // Melee Thrust Barrage: ProgramFrame cadence. Heavy has no slot 4.
                    }
                    return;
                case 5:
                    // Ult layers come with CinematicStarted (every ult emits it); the per-id cooldown absorbs a repeat.
                    // Heavy ULT Smash and Polearm Kick speak through MovePhaseChanged.
                    if (cls == (int)FighterClass.Ranged) o.Add(At("ren_draw", i, x));
                    return;
                // 6 = Pose Fireball: the PoseFireball projectile carries the sound. 7+ = new moves: ChibiAudio.PlayMove.
            }
        }

        void Phase(int cls, int i, int x, int phase, int slot, List<SfxRequest> o)
        {
            if (cls == (int)FighterClass.Heavy && slot == 2 && phase == 1)
            {
                o.Add(At("bruna_thud", i, x)); o.Add(At("bruna_armor", i, x));
            }
            else if (cls == (int)FighterClass.Heavy && slot == 5 && phase == 1)
            {
                o.Add(At("bruna_smash", i, x)); o.Add(At("bruna_kra_thud", i, x, 1f, -4f));
            }
            else if (cls == (int)FighterClass.Polearm && slot == 2 && phase == 1) o.Add(At("taro_boom", i, x));
            else if (cls == (int)FighterClass.Polearm && slot == 5)
            {
                // ChibiSimulation Kick phases (private there): 2 lerp, 3 holding, 4 released.
                if (phase == 2) o.Add(At("taro_zaa", i, x));
                else if (phase == 3) o.Add(At("taro_shrak", i, x));
                else if (phase == 4) o.Add(At("taro_crash", i, x));
            }
        }

        /// <summary>Hit composition (sfx-events.md 2.1) with the per-defender window and string attenuation (5.2).</summary>
        void Hit(ChibiSimState prev, ChibiSimState cur, int d, int hp, float now, List<SfxRequest> o)
        {
            int a = ChibiSimView.Attacker(cur, d);
            if (a < 0) a = d;   // four players: a hazard hit has no attacker; the victim's class picks the hit layer
            int acls = ClassOf(cur, a);
            int x = cur.F[d].X;
            bool heavy = hp >= HeavyHitHp;
            bool crit = _crit[d];
            int level = crit ? 2 : heavy ? 1 : 0;

            if (now - _lastHit[d] < HitWindowSec)
            {
                if (level <= _lastLevel[d]) return;   // inside the window and not heavier: nothing
                _lastLevel[d] = level;
                o.Add(SfxRequest.Stop(BodyTag(d)));
                var body = At(heavy ? "core_hit_heavy" : "core_hit_light", d, x); body.Tag = BodyTag(d);
                o.Add(body);
                if (crit) { var c = At("core_hit_crit", d, x); c.Duck = SfxDuck.HitAccent; o.Add(c); }
                return;
            }
            _string[d] = now - _lastStringHit[d] <= StringResetSec ? _string[d] + 1 : 0;
            _lastStringHit[d] = now;
            _lastHit[d] = now;
            _lastLevel[d] = level;

            if (_guardBreak[d]) return;   // the guard break replaced the block / hit sound on this frame
            if (prev.F[d].Shielding && cur.F[d].Shielding)
            {
                o.Add(acls == (int)FighterClass.Heavy ? At("bruna_clang", a, x, 1f, -4f) : At("core_block", d, x));
                return;
            }

            int proj = -1;
            for (int k = 0; k < _goneOwner.Count; k++)
                if (_goneOwner[k] == a && !_goneUsed[k]) { proj = _goneKind[k]; _goneUsed[k] = true; break; }

            bool attenuate = _string[d] > 0 && !heavy;
            float weaponDb = attenuate ? Math.Max(StringStepDb * _string[d], StringFloorDb) : 0f;
            string bodyId, weaponId;
            float weaponPitch = 1f;
            if (proj >= 0)
            {
                var row = ChibiSfxMap.Proj(proj);
                bodyId = row.HitBody; weaponId = row.Hit; weaponPitch = row.HitPitch; weaponDb += row.HitDb;
            }
            else
            {
                bodyId = heavy ? "core_hit_heavy" : "core_hit_light";
                weaponId = ChibiSfxMap.Impact[acls];
                if (acls == (int)FighterClass.Heavy && heavy) weaponId = "bruna_bam";
                else if (acls == (int)FighterClass.Polearm && heavy && cur.F[a].MoveSlot == 1) weaponId = "taro_crash";
            }
            var b = At(attenuate ? "core_hit_light" : bodyId, d, x, 1f, attenuate ? -6f : 0f);
            b.Tag = BodyTag(d);
            o.Add(b);
            o.Add(At(weaponId, a, x, weaponPitch, weaponDb));
            if (crit) { var c = At("core_hit_crit", d, x); c.Duck = SfxDuck.HitAccent; o.Add(c); }
            if (TakeContact(a, cur, now)) o.Add(At(ChibiSfxMap.Contact[acls], a, x));
        }
    }

    /// <summary>
    /// Stage audio rules (sfx-events.md 8.1 / 8.2): the ambience bed of a stage and the hazard sounds keyed on the sim's
    /// hazard event machine. One instance per built stage (ChibiDesignStage); pure C#, reads only.
    ///
    /// Dedupe: a sound plays once per (HazCount, phase). Rollback can re-show a phase (or an older event): the tracker
    /// remembers the newest event number and a bit per phase already sounded, and ignores older event numbers. A phase
    /// first seen more than <see cref="LateTicks"/> into its length is marked but not played (no stale sounds after a
    /// stage is built mid-event or a long catch-up).
    /// </summary>
    public sealed class ChibiStageSfx
    {
        /// <summary>Pseudo phase of the bird's body pass (starts BirdBodyStart ticks into the swipe).</summary>
        public const int PhaseBirdBody = 7;
        /// <summary>Game ticks into a phase after which its sound is skipped (tuning knob HazardLateTicks).</summary>
        public int LateTicks = 20;

        int _count = -1, _mask;

        /// <summary>Ambience bed id of sim stage <paramref name="stage"/> (3..7), null for stages without a bed (Classic 0..2).</summary>
        /// <example><code>ChibiStageSfx.AmbienceId(ChibiStageTable.Ocean)   // "stage_ocean_amb"</code></example>
        public static string AmbienceId(int stage)
        {
            switch (stage)
            {
                case ChibiStageTable.Mountain: return "stage_mountain_amb";
                case ChibiStageTable.Plain: return "stage_plain_amb";
                case ChibiStageTable.Volcano: return "stage_volcano_amb";
                case ChibiStageTable.BirdLair: return "stage_birdlair_amb";
                case ChibiStageTable.Ocean: return "stage_ocean_amb";
                default: return null;
            }
        }

        /// <summary>Forget what played (new match on the same stage object).</summary>
        public void Reset() { _count = -1; _mask = 0; }

        /// <summary>Hazard sounds for the displayed state <paramref name="s"/> (call every rendered frame).</summary>
        /// <example><code>stageSfx.Step(shownState, requests);</code></example>
        public void Step(ChibiSimState s, List<SfxRequest> output)
        {
            if (s == null) return;
            Step(ChibiStageTable.Get(s.Stage).Hazard, s.HazCount, s.HazPhase, s.HazElapsed, s.HazArg, output);
        }

        /// <summary>
        /// Raw-value form: <paramref name="hazard"/> = ChibiStageTable.Hazard*, <paramref name="count"/> = HazCount,
        /// <paramref name="phase"/> = HazPhase, <paramref name="elapsed"/> = HazElapsed, <paramref name="arg"/> = HazArg
        /// (fire lane 0 / 1 / 2 both, bird direction +1 / -1).
        /// </summary>
        public void Step(int hazard, int count, int phase, int elapsed, int arg, List<SfxRequest> output)
        {
            if (hazard == ChibiStageTable.HazardNone || phase == ChibiHazardConst.Idle) return;
            if (count < _count) return;                       // rolled back to an event that already sounded
            if (count > _count) { _count = count; _mask = 0; }
            Fire(hazard, phase, elapsed, arg, output);
            if (hazard == ChibiStageTable.HazardBird && phase == ChibiHazardConst.PhaseBirdSwipe && elapsed >= ChibiHazardConst.BirdBodyStart)
                Fire(hazard, PhaseBirdBody, elapsed - ChibiHazardConst.BirdBodyStart, arg, output);
        }

        void Fire(int hazard, int phase, int elapsed, int arg, List<SfxRequest> o)
        {
            int bit = 1 << phase;
            if ((_mask & bit) != 0) return;
            _mask |= bit;
            if (elapsed > LateTicks) return;
            switch (hazard)
            {
                case ChibiStageTable.HazardFire:
                    if (phase == ChibiHazardConst.Telegraph) Lanes("stage_volcano_burst_warn", arg, o);
                    else if (phase == ChibiHazardConst.PhaseFireErupt) Lanes("stage_volcano_burst", arg, o);
                    break;
                case ChibiStageTable.HazardBird:
                {
                    int dir = arg >= 0 ? 1 : -1;
                    if (phase == ChibiHazardConst.Telegraph) o.Add(At("stage_birdlair_screech", -dir * ChibiHazardConst.BirdBodyStartX));
                    else if (phase == ChibiHazardConst.PhaseBirdSwipe) o.Add(At("stage_birdlair_gust", dir * ChibiHazardConst.BirdBodyStartX / 4));
                    else if (phase == PhaseBirdBody)
                    {
                        // The body flies from -dir x 17000 to +dir x 17000: the pan follows it (doppler across the field).
                        var r = At("stage_birdlair_pass", -dir * ChibiHazardConst.BirdBodyStartX);
                        r.PanEndX = dir * ChibiHazardConst.BirdBodyStartX;
                        r.PanSweepSec = ChibiHazardConst.BirdBodyTicks / (float)ChibiSimConst.TickRate;
                        o.Add(r);
                    }
                    break;
                }
                case ChibiStageTable.HazardWhale:
                    if (phase == ChibiHazardConst.Telegraph) o.Add(SfxRequest.Of("stage_ocean_whale_call"));
                    else if (phase == ChibiHazardConst.PhaseWhaleDescend) o.Add(SfxRequest.Of("stage_ocean_whale_descend"));
                    else if (phase == ChibiHazardConst.PhaseWhaleRise) o.Add(SfxRequest.Of("stage_ocean_whale_rise"));
                    break;
            }
        }

        static void Lanes(string id, int lanes, List<SfxRequest> o)
        {
            if (lanes != 1) o.Add(At(id, ChibiHazardConst.FireLaneLeftX));
            if (lanes != 0) o.Add(At(id, ChibiHazardConst.FireLaneRightX));
        }

        static SfxRequest At(string id, int x)
        {
            var r = SfxRequest.Of(id);
            r.PanX = x; r.Panned = true;
            return r;
        }
    }
}
