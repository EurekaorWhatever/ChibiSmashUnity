using System.Text;
using UnityEngine;

namespace ChibiClash.Netcode
{
    /// <summary>
    /// Minimal IMGUI HUD for the sim test match: HP / ultimate bars from simulation state, frame counter, controls
    /// hint, damage popups (Fx.Popups, normally drawn by Hud) and the match-over banner. Each panel also shows
    /// what that player's input source is sending this frame (direction, buttons, Dash, gesture, pose verified),
    /// read from <see cref="ChibiLocalSession.OnFrame"/>. Read-only toward the simulation.
    /// </summary>
    public sealed class ChibiSimTestHud : MonoBehaviour
    {
        static readonly string[] Names = { "P1", "P2" };
        static readonly BindAction[] ChipActions = { BindAction.Skill1, BindAction.Skill2, BindAction.Skill3, BindAction.Ultimate };
        const float PanelH = 142f, ChipGap = 6f;
        /// <summary>Seconds a pressed button stays listed after it was last seen, so one-tick presses are readable.</summary>
        const float ShowFor = 0.4f;
        /// <summary>Seconds between rebuilds of the controls hint (bindings can change in Settings).</summary>
        const float HintRefresh = 1f;

        // Buttons shown in the "sends" line, in display order. Dash is highlighted (new, not yet simulated).
        static readonly ChibiButtons[] ShownButtons =
        {
            ChibiButtons.Light, ChibiButtons.Heavy, ChibiButtons.Jump, ChibiButtons.Shield, ChibiButtons.Dash,
            ChibiButtons.Skill1, ChibiButtons.Skill2, ChibiButtons.Skill3, ChibiButtons.Ultimate,
        };
        static readonly string[] ButtonNames = { "Light", "Heavy", "Jump", "Shield", "Dash", "Skill1", "Skill2", "Skill3", "Ult" };
        // Indexed by ChibiDir (Neutral, N, NE, E, SE, S, SW, W, NW).
        static readonly string[] DirGlyphs = { "neutral", "↑", "↗", "→", "↘", "↓", "↙", "←", "↖" };

        ChibiLocalLoop _loop;
        ChibiLocalSession _subscribed;
        ChibiSimView _view;           // same GameObject; source of the cinematic band and flash (lazily found)
        readonly ChibiInputPacket[] _last = new ChibiInputPacket[2];
        readonly float[,] _seen = new float[2, 9];          // last time each ShownButtons entry was pressed
        readonly Gesture[] _gesture = new Gesture[2];
        readonly bool[] _verified = new bool[2];
        readonly float[] _gestureSeen = new float[2];
        readonly int[] _sendKey = { -1, -1 };
        readonly string[] _sendText = new string[2];
        readonly StringBuilder _sb = new StringBuilder(160);
        string _controls;
        float _controlsAt = float.NegativeInfinity;

        /// <summary>Attach the HUD to a running loop and start listening to the inputs it simulates.</summary>
        /// <example><code>hud.Bind(loop);</code></example>
        public void Bind(ChibiLocalLoop loop)
        {
            _loop = loop;
            Resubscribe();
        }

        /// <summary>Follow the loop's session (it is replaced when a new match begins).</summary>
        void Resubscribe()
        {
            var cur = _loop != null ? _loop.Session : null;
            if (cur == _subscribed) return;
            if (_subscribed != null) _subscribed.OnFrame -= OnFrame;
            _subscribed = cur;
            if (_subscribed != null) _subscribed.OnFrame += OnFrame;
        }

        void Update() => Resubscribe();

        void OnDestroy()
        {
            if (_subscribed != null) _subscribed.OnFrame -= OnFrame;
            _subscribed = null;
        }

        void OnFrame(int frame, ChibiInputPacket in0, ChibiInputPacket in1)
        {
            Record(0, in0);
            Record(1, in1);
        }

        void Record(int p, ChibiInputPacket pk)
        {
            _last[p] = pk;
            float now = Time.unscaledTime;
            for (int b = 0; b < ShownButtons.Length; b++)
                if (pk.Has(ShownButtons[b])) _seen[p, b] = now;
            if (pk.Gesture != Gesture.None)
            {
                _gesture[p] = pk.Gesture;
                _verified[p] = pk.Has(ChibiButtons.PoseVerified);
                _gestureSeen[p] = now;
            }
        }

        void OnGUI()
        {
            if (_loop == null || _loop.Session == null) return;
            Gui.Begin();
            var s = _loop.Session.State;

            if (_view == null) _view = GetComponent<ChibiSimView>();
            if (_view != null)
            {
                float ct = Time.unscaledTime - _view.CinematicStart;              // Hud.cs:18-19
                if (_view.CinematicFighter >= 0 && ct < 1.5f) Cinematic(_view, ct);
            }

            Popups();
            Panel(s.F[0], 0, new Rect(16f, 14f, 420f, PanelH));
            Panel(s.F[1], 1, new Rect(Gui.W - 436f, 14f, 420f, PanelH));

            Gui.Text(new Rect(Gui.W * 0.5f - 200f, 14f, 400f, 30f), "SIM TEST MATCH", 22, Gui.Accent);
            // Match time is game time (GameTicks), not real ticks: hitstop and cinematics do not run the clock.
            int secs = Mathf.Max(0, (ChibiSimConst.MatchTicks - s.GameTicks) / ChibiSimConst.TickRate);
            Gui.Text(new Rect(Gui.W * 0.5f - 200f, 44f, 400f, 30f), secs + "s   step " + s.GameTicks + "   frame " + s.Frame, 16, Color.white);
            Gui.Text(new Rect(16f, Gui.H - 48f, Gui.W - 32f, 40f), ControlsHint(), 13, Gui.Muted, TextAnchor.MiddleLeft);

            if (s.MatchOver)
            {
                string w = s.Winner == 0 ? "P1 WINS" : s.Winner == 1 ? "P2 WINS" : "DRAW";
                var r = new Rect(Gui.W * 0.5f - 260f, Gui.H * 0.5f - 50f, 520f, 100f);
                Gui.Rect(r, Gui.Panel, 12f);
                Gui.Text(r, "MATCH OVER — " + w, 34, Gui.Gold);
            }

            if (_view != null && _view.FlashAlpha > 0.01f)                        // Hud.cs:55-58
            {
                var c = _view.FlashColor;
                Gui.Rect(new Rect(0f, 0f, Gui.W, Gui.H), new Color(c.r, c.g, c.b, _view.FlashAlpha * 0.5f));
            }
        }

        /// <summary>Ult cinematic bars and name band, copied from Hud.Cinematic (Hud.cs:177-195).</summary>
        static void Cinematic(ChibiSimView v, float t)
        {
            float inT = Mathf.Clamp01(t / 0.2f);
            float outT = Mathf.Clamp01((1.5f - t) / 0.3f);
            float k = Mathf.Min(inT, outT);
            float bar = 80f * k;
            Gui.Rect(new Rect(0f, 0f, Gui.W, bar), Color.black);
            Gui.Rect(new Rect(0f, Gui.H - bar, Gui.W, bar), Color.black);

            var c = v.CinematicColor;
            float slide = (1f - inT) * Gui.W;
            var band = new Rect(-slide, Gui.H * 0.62f, Gui.W, 90f);
            Gui.Rect(band, new Color(c.r * 0.3f, c.g * 0.3f, c.b * 0.3f, 0.85f * k));
            Gui.Rect(new Rect(band.x, band.y, band.width, 4f), new Color(c.r, c.g, c.b, k));
            Gui.Rect(new Rect(band.x, band.yMax - 4f, band.width, 4f), new Color(c.r, c.g, c.b, k));
            Gui.Text(new Rect(band.x, band.y + 4f, band.width, 56f), v.CinematicTitle, 46, new Color(1f, 1f, 1f, k));
            Gui.Text(new Rect(band.x, band.y + 56f, band.width, 28f), v.CinematicSubtitle, 15, new Color(c.r, c.g, c.b, k));
        }

        void Panel(ChibiFighterSim f, int i, Rect r)
        {
            Gui.Rect(r, Gui.Panel, 10f);
            var def = ClassDef.Get((FighterClass)f.Class);
            Gui.Text(new Rect(r.x + 12f, r.y + 6f, r.width - 24f, 24f), Names[i] + "  " + def.heroName, 16, Color.white, TextAnchor.MiddleLeft);
            float hp = f.Stats.MaxHp > 0 ? f.HpU / ((float)f.Stats.MaxHp * ChibiCombatStats.HpScale) : 0f;   // exact fraction
            Gui.Bar(new Rect(r.x + 12f, r.y + 34f, r.width - 24f, 20f), hp, hp > 0.3f ? Gui.Green : Gui.Red, Gui.PanelLight);
            Gui.Text(new Rect(r.x + 12f, r.y + 34f, r.width - 24f, 20f), f.Hp + " / " + f.Stats.MaxHp, 12, Color.white);
            float ult = f.Ult / (float)ChibiSimConst.UltMax;
            Gui.Bar(new Rect(r.x + 12f, r.y + 62f, r.width - 24f, 12f), ult, f.Ult >= ChibiSimConst.UltMax ? Gui.Gold : Gui.Accent2, Gui.PanelLight);
            SkillRow(f, i, def, new Rect(r.x + 12f, r.y + 82f, r.width - 24f, 28f));
            Gui.Text(new Rect(r.x + 12f, r.y + 114f, r.width - 24f, 22f), SendLine(i), 12, Color.white, TextAnchor.MiddleLeft);
        }

        /// <summary>
        /// "P1 sends: → Light Dash · Gesture: Cross Arms ✓" (rich text). Rebuilt only when what is visible changes,
        /// so a steady state allocates nothing.
        /// </summary>
        string SendLine(int p)
        {
            float now = Time.unscaledTime;
            var pk = _last[p];
            int key = (int)pk.Direction;
            for (int b = 0; b < ShownButtons.Length; b++)
                if (pk.Has(ShownButtons[b]) || now - _seen[p, b] < ShowFor) key |= 1 << (4 + b);
            bool gest = _gesture[p] != Gesture.None && now - _gestureSeen[p] < ShowFor;
            if (gest) key |= ((int)_gesture[p] << 13) | (_verified[p] ? 1 << 15 : 0);
            if (key == _sendKey[p] && _sendText[p] != null) return _sendText[p];

            _sb.Clear().Append(Names[p]).Append(" sends: ");
            bool any = false;
            if (pk.Direction != ChibiDir.Neutral) { _sb.Append(DirGlyphs[(int)pk.Direction]).Append(' '); any = true; }
            for (int b = 0; b < ShownButtons.Length; b++)
            {
                if ((key & (1 << (4 + b))) == 0) continue;
                if (ShownButtons[b] == ChibiButtons.Dash) _sb.Append("<color=#").Append(ColorUtility.ToHtmlStringRGB(Gui.Gold)).Append(">Dash</color> ");
                else _sb.Append(ButtonNames[b]).Append(' ');
                any = true;
            }
            if (!any) _sb.Append(p == 1 ? "nothing " : "neutral ");
            _sb.Append("· Gesture: ");
            if (gest)
            {
                _sb.Append(ClassDef.GestureName(_gesture[p]));
                if (_verified[p]) _sb.Append(" <color=#").Append(ColorUtility.ToHtmlStringRGB(Gui.Green)).Append(">✓</color>");
                else _sb.Append(" <color=#").Append(ColorUtility.ToHtmlStringRGB(Gui.Muted)).Append(">(keys)</color>");
            }
            else _sb.Append("<color=#").Append(ColorUtility.ToHtmlStringRGB(Gui.Muted)).Append(">none</color>");

            _sendKey[p] = key;
            return _sendText[p] = _sb.ToString();
        }

        /// <summary>Controls hint built from P1's live bindings; rebuilt at most once per <see cref="HintRefresh"/>.</summary>
        string ControlsHint()
        {
            float now = Time.unscaledTime;
            if (_controls != null && now - _controlsAt < HintRefresh) return _controls;
            _controlsAt = now;
            string L(BindAction a) => Bindings.Label(0, a);
            _controls = "P1: " + L(BindAction.Left) + "/" + L(BindAction.Right) + " move · " + L(BindAction.Jump) + " jump · "
                + L(BindAction.Light) + " light · " + L(BindAction.Heavy) + " heavy · " + L(BindAction.Block) + " shield · "
                + L(BindAction.Dash) + " dash · " + L(BindAction.Skill1) + "/" + L(BindAction.Skill2) + "/" + L(BindAction.Skill3)
                + " skills · " + L(BindAction.Ultimate) + " ult · 1/2/3 gestures (no camera)   |   P2: training dummy";
            return _controls;
        }

        /// <summary>One chip per skill plus the ultimate, labelled with P1's bound keys: READY in green, or the seconds left to wait.</summary>
        static void SkillRow(ChibiFighterSim f, int i, ClassDef def, Rect r)
        {
            float w = (r.width - 3f * ChipGap) / 4f;
            for (int k = 0; k < 4; k++)
            {
                var chip = new Rect(r.x + k * (w + ChipGap), r.y, w, r.height);
                string key = i == 0 ? Bindings.Label(0, ChipActions[k]) + " " : "";
                string label;
                bool ready;
                if (k < 3)
                {
                    var mv = ChibiMoveTable.Get(f.Class, 2 + k);
                    if (!mv.Exists) continue;
                    string name = def.skillNames != null && k < def.skillNames.Length ? def.skillNames[k] : "Skill " + (k + 1);
                    ready = f.Cd[k] <= 0;
                    float cdFrac = mv.Cooldown > 0 ? f.Cd[k] / (float)mv.Cooldown : 0f;
                    Gui.Rect(chip, Gui.PanelLight, 6f);
                    if (!ready) Gui.Bar(new Rect(chip.x, chip.yMax - 4f, chip.width, 4f), 1f - cdFrac, Gui.Muted, Gui.PanelLight);
                    label = key + name + "\n" + (ready ? "READY" : (f.Cd[k] / (float)ChibiSimConst.TickRate).ToString("0.0") + "s");
                }
                else
                {
                    ready = f.Ult >= ChibiSimConst.UltMax;
                    Gui.Rect(chip, Gui.PanelLight, 6f);
                    label = key + "ULT\n" + (ready ? "READY" : Mathf.FloorToInt(100f * f.Ult / ChibiSimConst.UltMax) + "%");
                }
                Gui.Text(chip, label, 10, ready ? Gui.Green : Gui.Muted);
            }
        }

        static void Popups()
        {
            foreach (var p in Fx.Popups)
            {
                var g = Gui.WorldToGui(p.pos);
                float a = 1f - Mathf.Clamp01((p.t - 0.6f) / 0.5f);
                float pop = 1f + Mathf.Max(0f, 0.3f - p.t) * 1.5f;
                Gui.Text(new Rect(g.x - 150f, g.y - 20f, 300f, 40f), p.text, Mathf.RoundToInt(20f * p.size * pop), new Color(p.color.r, p.color.g, p.color.b, a));
            }
        }
    }
}
