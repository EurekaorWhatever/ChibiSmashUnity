using System.Collections.Generic;
using UnityEngine;
using UnityEngine.InputSystem;

namespace ChibiClash
{
    /// <summary>Entry point: owns the save profile, menu flow (GDD 5) and the active match.</summary>
    public class GameManager : MonoBehaviour
    {
        enum View { Menu, Select, PreCheck, Match }
        enum Tab { Brawl, Training, Armory, Settings }

        public SaveData Save { get; private set; }

        View view = View.Menu;
        Tab tab = Tab.Brawl;
        MatchController match;
        MatchConfig pending = new MatchConfig();

        HumanInput menuInput;
        GestureTracker menuTracker;
        AutoPoseSource menuPose;

        // key rebinding (Settings)
        bool listening;
        float listenSince;
        int listenPlayer;
        BindAction listenAction;

        float preCheckT;
        bool lightOk, frameOk;
        string lightMsg = "", frameMsg = "";

        FighterClass armoryClass;
        bool bannerAll = true;
        List<SaveData.PullResult> lastPull = new List<SaveData.PullResult>();
        string toast;
        float toastUntil;

        static readonly string[] Difficulty = { "Dummy", "Easy", "Normal", "Hard" };

        void Awake()
        {
            Save = SaveData.Load();
            Bindings.Data = Save;
            Application.targetFrameRate = 120;
            menuInput = new HumanInput(0);
            var sim = new SimulatedPoseSource("P1 simulated", () => menuInput.SimulatedGesture, () => menuInput.Slouching);
            menuPose = new AutoPoseSource(0, sim, () => Save.settings.useWebcam);
            menuTracker = new GestureTracker(menuPose);
            WebcamFeed.SetEnabled(Save.settings.useWebcam);
            ResetCamera();
        }

        void ResetCamera()
        {
            var cam = Camera.main;
            if (cam == null)
            {
                var go = new GameObject("Main Camera", typeof(Camera));
                go.tag = "MainCamera";
                cam = go.GetComponent<Camera>();
            }
            cam.orthographic = true;
            cam.orthographicSize = 5f;
            cam.transform.position = new Vector3(0f, 0f, -10f);
            cam.clearFlags = CameraClearFlags.SolidColor;
            cam.backgroundColor = new Color(0.1f, 0.07f, 0.2f);
            var cc = cam.GetComponent<CameraController>();
            if (cc != null) cc.enabled = false;
        }

        void Update()
        {
            if (view == View.Match) return;
            if (listening) CaptureRebind();
            menuInput.Tick(null, Time.unscaledDeltaTime);
            menuTracker.Tick(Time.unscaledDeltaTime);
            if (view == View.PreCheck) UpdatePreCheck();
        }

        /// <summary>While a binding is selected in Settings, the next key press becomes the new binding (Esc cancels).</summary>
        void CaptureRebind()
        {
            var kb = Keyboard.current;
            if (kb != null && kb.escapeKey.wasPressedThisFrame) { listening = false; return; }
            string bound = null;
            var mouse = Mouse.current;
            if (mouse != null && Time.unscaledTime - listenSince > 0.25f)
            {
                if (mouse.leftButton.wasPressedThisFrame) bound = Bindings.MouseLeft;
                else if (mouse.rightButton.wasPressedThisFrame) bound = Bindings.MouseRight;
                else if (mouse.middleButton.wasPressedThisFrame) bound = Bindings.MouseMiddle;
            }
            if (bound == null && kb != null)
                foreach (var key in kb.allKeys)
                    if (key.wasPressedThisFrame && key.keyCode != Key.None) { bound = key.keyCode.ToString(); break; }
            if (bound == null) return;
            Bindings.Set(listenPlayer, listenAction, bound);
            Toast("P" + (listenPlayer + 1) + " " + Bindings.Names[(int)listenAction] + " = " + Bindings.BindingLabel(bound));
            listening = false;
        }

        void Toast(string s)
        {
            toast = s;
            toastUntil = Time.unscaledTime + 2.5f;
        }

        // ---------------------------------------------------------------- flow

        void StartMatch()
        {
            pending.timeLimit = MatchController.MatchSeconds;
            pending.difficulty = Save.settings.cpuDifficulty;
            if (match != null) Destroy(match.gameObject);
            match = MatchController.Create(this, pending);
            view = View.Match;
        }

        /// <summary>Skip the menus and start a match directly (editor testing / automation).</summary>
        public MatchController QuickMatch(FighterClass p1, FighterClass p2, bool vsCpu, int stage = 0, bool training = false)
        {
            pending.cls[0] = p1;
            pending.cls[1] = p2;
            pending.cpu[0] = false;
            pending.cpu[1] = vsCpu;
            pending.stage = stage;
            pending.training = training;
            StartMatch();
            return match;
        }

        /// <summary>Jump straight to a screen: brawl, training, armory, settings, select, precheck (editor testing / automation).</summary>
        public void ShowScreen(string screen)
        {
            if (match != null) LeaveMatch(View.Menu);
            switch (screen)
            {
                case "select": BeginSelect(true, false); return;
                case "precheck": BeginSelect(true, false); preCheckT = 0f; view = View.PreCheck; return;
            }
            view = View.Menu;
            tab = screen == "training" ? Tab.Training : screen == "armory" ? Tab.Armory : screen == "settings" ? Tab.Settings : Tab.Brawl;
        }

        void LeaveMatch(View to)
        {
            if (match != null) Destroy(match.gameObject);
            match = null;
            Time.timeScale = 1f;
            ResetCamera();
            view = to;
        }

        void BeginSelect(bool cpu, bool training)
        {
            pending.cpu[0] = false;
            pending.cpu[1] = cpu;
            pending.training = training;
            view = View.Select;
        }

        void Confirm()
        {
            if (Save.settings.preMatchCheck && !pending.training)
            {
                preCheckT = 0f;
                view = View.PreCheck;
            }
            else StartMatch();
        }

        /// <summary>5-second lighting and framing check before queueing (GDD 5).</summary>
        void UpdatePreCheck()
        {
            preCheckT += Time.unscaledDeltaTime;
            if (Save.settings.useWebcam)
            {
                float b = WebcamFeed.Brightness();
                if (b < 0f) { lightOk = false; lightMsg = "Camera not available"; }
                else if (b < 0.18f) { lightOk = false; lightMsg = "Too dark (" + Mathf.RoundToInt(b * 100f) + "%) - add light"; }
                else if (b > 0.92f) { lightOk = false; lightMsg = "Too bright (" + Mathf.RoundToInt(b * 100f) + "%) - reduce glare"; }
                else { lightOk = true; lightMsg = "Good (" + Mathf.RoundToInt(b * 100f) + "%)"; }
            }
            else { lightOk = true; lightMsg = "Simulated feed (webcam off)"; }

            var f = menuTracker.Source.Current;
            bool inFrame = f.confidence >= 0.6f;
            foreach (var p in f.p) inFrame &= p.x > 0.03f && p.x < 0.97f && p.y > 0.03f && p.y < 0.97f;
            frameOk = inFrame;
            frameMsg = inFrame ? "Upper body in frame" : "Step back - shoulders and hips must be visible";

            if (preCheckT >= 5.6f && lightOk && frameOk) StartMatch();
        }

        // ---------------------------------------------------------------- GUI

        void OnGUI()
        {
            Gui.Begin();
            if (view == View.Match && match != null)
            {
                switch (Hud.Draw(match, this))
                {
                    case Hud.Action.Resume: match.Paused = false; break;
                    case Hud.Action.Rematch: StartMatch(); break;
                    case Hud.Action.CharacterSelect: LeaveMatch(View.Select); break;
                    case Hud.Action.Menu: LeaveMatch(View.Menu); break;
                }
                return;
            }

            Background();
            switch (view)
            {
                case View.Menu: MenuView(); break;
                case View.Select: SelectView(); break;
                case View.PreCheck: PreCheckView(); break;
            }

            if (toast != null && Time.unscaledTime < toastUntil)
            {
                var r = new Rect(Gui.W * 0.5f - 260f, Gui.H - 70f, 520f, 44f);
                Gui.Rect(r, Gui.PanelLight, 12f);
                Gui.Frame(r, Gui.Gold, 2f, 12f);
                Gui.Text(r, toast, 15, Color.white);
            }
        }

        void Background()
        {
            Gui.Rect(new Rect(0f, 0f, Gui.W, Gui.H), new Color(0.09f, 0.06f, 0.18f));
            float t = Time.unscaledTime;
            for (int i = 0; i < 8; i++)
            {
                float x = (Mathf.Sin(t * 0.13f + i * 1.7f) * 0.5f + 0.5f) * Gui.W;
                float y = (Mathf.Cos(t * 0.11f + i * 2.3f) * 0.5f + 0.5f) * Gui.H;
                float s = 260f + i * 40f;
                var c = i % 2 == 0 ? Gui.Accent : Gui.Accent2;
                Gui.Glow(new Rect(x - s * 0.5f, y - s * 0.5f, s, s), new Color(c.r, c.g, c.b, 0.12f));
            }
        }

        void Header(string subtitle)
        {
            Gui.Text(new Rect(30f, 14f, 500f, 64f), "<color=#ff6aa0>CHIBI</color> <color=#5fdcff>CLASH</color>", 50, Color.white, TextAnchor.MiddleLeft);
            Gui.Text(new Rect(34f, 70f, 600f, 22f), subtitle, 14, Gui.Muted, TextAnchor.MiddleLeft, false);
            var coin = new Rect(Gui.W - 230f, 24f, 200f, 44f);
            Gui.Rect(coin, Gui.Panel, 22f);
            Gui.Frame(coin, Gui.Gold, 2f, 22f);
            Gui.Circle(new Rect(coin.x + 10f, coin.y + 9f, 26f, 26f), Gui.Gold);
            Gui.Text(new Rect(coin.x + 44f, coin.y, 150f, 44f), Save.coins + " coins", 18, Gui.Gold, TextAnchor.MiddleLeft);
        }

        void MenuView()
        {
            Header("2D PvP arena fighter · body-gesture ultimates · cosmetics-only gacha");
            string[] names = { "BRAWL", "TRAINING / CV SETUP", "ARMORY", "SETTINGS" };
            float tw = 250f;
            float tx = Gui.W * 0.5f - (tw * 4f + 30f) * 0.5f;
            for (int i = 0; i < 4; i++)
                if (Gui.Button(new Rect(tx + i * (tw + 10f), 104f, tw, 44f), names[i], i % 2 == 0 ? Gui.Accent : Gui.Accent2, 17, true, (int)tab == i))
                    tab = (Tab)i;
            if (tab != Tab.Settings) listening = false;

            var area = new Rect(30f, 166f, Gui.W - 60f, Gui.H - 196f);
            switch (tab)
            {
                case Tab.Brawl: BrawlTab(area); break;
                case Tab.Training: TrainingTab(area); break;
                case Tab.Armory: ArmoryTab(area); break;
                case Tab.Settings: SettingsTab(area); break;
            }
        }

        // ---------------------------------------------------------------- BRAWL

        void BrawlTab(Rect a)
        {
            float cw = (a.width - 420f - 30f) / 2f;
            var c1 = new Rect(a.x, a.y, cw, 200f);
            var c2 = new Rect(a.x + cw + 15f, a.y, cw, 200f);
            var c3 = new Rect(a.x, a.y + 215f, cw, 200f);
            var c4 = new Rect(a.x + cw + 15f, a.y + 215f, cw, 200f);
            if (ModeCard(c1, "VS CPU", "PvE · earn coins & quest progress against the computer", Gui.Accent, true)) BeginSelect(true, false);
            if (ModeCard(c2, "LOCAL VERSUS", "2 players, one keyboard or two gamepads", Gui.Accent2, true)) BeginSelect(false, false);
            ModeCard(c3, "RANKED ONLINE", "P2P rollback netcode via Unity Relay & Lobby.\nComing soon. Not connected in this build.", Gui.Muted, false);
            if (ModeCard(c4, "TRAINING", "Practice combos and gestures on a dummy", Gui.Gold, true)) BeginSelect(true, true);

            var q = new Rect(a.xMax - 420f, a.y, 420f, a.height);
            Gui.Rect(q, Gui.Panel, 16f);
            Gui.Text(new Rect(q.x + 18f, q.y + 10f, 380f, 30f), "QUESTS", 20, Gui.Gold, TextAnchor.MiddleLeft);
            Gui.Text(new Rect(q.x + 18f, q.y + 10f, 384f, 30f), "W " + Save.wins + "  L " + Save.losses, 14, Gui.Muted, TextAnchor.MiddleRight);
            float y = q.y + 50f;
            foreach (var quest in Save.quests)
            {
                Gui.Text(new Rect(q.x + 18f, y, 300f, 22f), quest.desc, 14, Color.white, TextAnchor.MiddleLeft, false);
                Gui.Text(new Rect(q.x + 18f, y, 384f, 22f), "+" + quest.reward, 14, Gui.Gold, TextAnchor.MiddleRight);
                Gui.Bar(new Rect(q.x + 18f, y + 25f, 384f, 10f), (float)quest.progress / quest.target, Gui.Accent2, new Color(0f, 0f, 0f, 0.4f));
                Gui.Text(new Rect(q.x + 18f, y + 36f, 384f, 16f), quest.progress + " / " + quest.target + (quest.completions > 0 ? "   ·   completed x" + quest.completions : ""), 11, Gui.Muted, TextAnchor.MiddleLeft, false);
                y += 62f;
            }
            Gui.Text(new Rect(q.x + 18f, q.yMax - 50f, 384f, 44f), "Coins come only from playing: wins, PvE, quests and good posture tracked by the camera.", 12, Gui.Muted, TextAnchor.MiddleLeft, false);
        }

        bool ModeCard(Rect r, string title, string desc, Color c, bool enabled)
        {
            bool hover = enabled && r.Contains(Event.current.mousePosition);
            if (hover) Gui.Glow(new Rect(r.x - 20f, r.y - 20f, r.width + 40f, r.height + 40f), new Color(c.r, c.g, c.b, 0.3f));
            Gui.Rect(r, hover ? Gui.PanelLight : Gui.Panel, 18f);
            Gui.Frame(r, c, hover ? 3f : 2f, 18f);
            Gui.Text(new Rect(r.x + 24f, r.y + 30f, r.width - 48f, 60f), title, 34, enabled ? Color.white : Gui.Muted, TextAnchor.MiddleLeft);
            Gui.Text(new Rect(r.x + 24f, r.y + 96f, r.width - 48f, 70f), desc, 15, Gui.Muted, TextAnchor.UpperLeft, false);
            if (!enabled) Gui.Text(new Rect(r.x, r.y + 14f, r.width - 24f, 24f), "LOCKED", 13, Gui.Red, TextAnchor.MiddleRight);
            return enabled && GUI.Button(r, GUIContent.none, GUIStyle.none);
        }

        // ---------------------------------------------------------------- TRAINING / CV SETUP

        void TrainingTab(Rect a)
        {
            var feed = new Rect(a.x, a.y, 520f, 400f);
            var st = menuTracker.Stable != Gesture.None ? Gui.Green : Gui.Yellow;
            Gui.CvPanel(feed, menuTracker, st, Save.settings.useWebcam, "CV SETUP · " + menuTracker.Source.Name);

            var p = new Rect(a.x + 545f, a.y, a.width - 545f, a.height);
            Gui.Rect(p, Gui.Panel, 16f);
            float x = p.x + 20f, w = p.width - 40f, y = p.y + 14f;
            Gui.Text(new Rect(x, y, w, 28f), "COMPUTER VISION", 20, Gui.Accent2, TextAnchor.MiddleLeft);
            y += 34f;
            string status = "Camera: " + (Save.settings.useWebcam ? WebcamFeed.Status : "off") +
                            "\nTracking: " + menuPose.Status() +
                            "\nDetected: " + (menuTracker.Stable == Gesture.None ? "none" : ClassDef.GestureName(menuTracker.Stable)) +
                            "   ·   Posture " + Mathf.RoundToInt(menuTracker.Posture * 100f) + "%" +
                            "   ·   Telemetry " + menuTracker.Validator.Reason;
            Gui.Text(new Rect(x, y, w, 70f), status, 13, Color.white, TextAnchor.UpperLeft, false);
            y += 80f;

            Gui.Text(new Rect(x, y, w, 24f), (menuTracker.IsLive ? "GESTURES  (do the pose in front of the camera - punch = one arm straight out to the side)" : "GESTURES  (hold the key to simulate the pose)"), 14, Gui.Gold, TextAnchor.MiddleLeft);
            y += 30f;
            var gestures = new[] { Gesture.CrossArms, Gesture.PunchForward, Gesture.RaiseHands };
            string[] uses = { "Block · Melee & Magic ultimate", "Fireball · Ranged & Polearm ultimate", "Super jump · Heavy ultimate" };
            float gw = (w - 20f) / 3f;
            for (int i = 0; i < 3; i++)
            {
                var g = new Rect(x + i * (gw + 10f), y, gw, 190f);
                bool active = menuTracker.Stable == gestures[i];
                Gui.Rect(g, active ? new Color(0.1f, 0.35f, 0.25f, 0.9f) : Gui.PanelLight, 12f);
                Gui.Frame(g, active ? Gui.Green : Gui.Muted * 0.6f, 2f, 12f);
                Gui.Skeleton(new Rect(g.x + gw * 0.5f - 50f, g.y + 8f, 100f, 110f), SimulatedPoseSource.Template(gestures[i], false), active ? Gui.Green : Gui.Accent2, 2.5f);
                Gui.Text(new Rect(g.x, g.y + 120f, gw, 22f), "[" + (i + 1) + "] " + ClassDef.GestureName(gestures[i]), 13, Color.white);
                Gui.Text(new Rect(g.x + 6f, g.y + 144f, gw - 12f, 40f), uses[i], 11, Gui.Muted, TextAnchor.UpperCenter, false);
            }
            y += 205f;
            Gui.Text(new Rect(x, y, w, 20f), "Press P to toggle a slouching posture and watch the posture score drop.", 12, Gui.Muted, TextAnchor.MiddleLeft, false);

            if (Gui.Button(new Rect(a.x, a.y + 415f, 250f, 48f), Save.settings.useWebcam ? "CAMERA TRACKING: ON" : "CAMERA TRACKING: OFF", Gui.Accent2, 16, true, Save.settings.useWebcam))
            {
                Save.settings.useWebcam = !Save.settings.useWebcam;
                WebcamFeed.SetEnabled(Save.settings.useWebcam);
                Save.Save();
            }
            if (Gui.Button(new Rect(a.x + 270f, a.y + 415f, 250f, 48f), "PRACTICE VS DUMMY", Gui.Gold, 16))
                BeginSelect(true, true);
        }

        // ---------------------------------------------------------------- ARMORY / GACHA

        void ArmoryTab(Rect a)
        {
            for (int i = 0; i < 5; i++)
            {
                var def = ClassDef.All[i];
                if (Gui.Button(new Rect(a.x, a.y + i * 56f, 180f, 48f), def.heroName + " · " + def.className, def.color, 15, true, armoryClass == def.type))
                    armoryClass = def.type;
            }

            var grid = new Rect(a.x + 200f, a.y, 600f, a.height);
            var skins = Skin.All.FindAll(s => s.cls == armoryClass);
            var equipped = Save.Equipped(armoryClass);
            var cdef = ClassDef.Get(armoryClass);
            for (int i = 0; i < skins.Count; i++)
            {
                var s = skins[i];
                var r = new Rect(grid.x + (i % 3) * 200f, grid.y + (i / 3) * 236f, 188f, 224f);
                bool owned = Save.owned.Contains(s.id);
                var rc = Skin.RarityColor(s.rarity);
                Gui.Rect(r, owned ? Gui.PanelLight : Gui.Panel, 14f);
                Gui.Frame(r, s == equipped ? Gui.Green : rc, s == equipped ? 3f : 2f, 14f);
                if (owned) Gui.Pfp(new Rect(r.x + 44f, r.y + 14f, 100f, 100f), cdef, s);
                else
                {
                    Gui.Circle(new Rect(r.x + 44f, r.y + 14f, 100f, 100f), new Color(0.05f, 0.05f, 0.1f));
                    Gui.Text(new Rect(r.x + 44f, r.y + 14f, 100f, 100f), "?", 50, Gui.Muted);
                }
                Gui.Text(new Rect(r.x, r.y + 120f, r.width, 24f), s.name, 16, Color.white);
                Gui.Text(new Rect(r.x, r.y + 142f, r.width, 20f), s.rarity.ToString().ToUpper() + (s.goldCinematic ? " · GOLD CINEMATIC" : ""), 11, rc);
                if (owned)
                {
                    if (s == equipped) Gui.Text(new Rect(r.x, r.y + 172f, r.width, 36f), "EQUIPPED", 14, Gui.Green);
                    else if (Gui.Button(new Rect(r.x + 34f, r.y + 172f, 120f, 36f), "EQUIP", Gui.Accent2, 14)) Save.Equip(s);
                }
                else Gui.Text(new Rect(r.x, r.y + 172f, r.width, 36f), "Pull to unlock", 12, Gui.Muted);
            }

            var g = new Rect(a.x + 820f, a.y, a.width - 820f, a.height);
            Gui.Rect(g, Gui.Panel, 16f);
            Gui.Frame(g, Gui.Gold, 2f, 16f);
            float x = g.x + 18f, w = g.width - 36f, y = g.y + 12f;
            Gui.Text(new Rect(x, y, w, 30f), "COSMETIC BANNER", 20, Gui.Gold);
            y += 32f;
            Gui.Text(new Rect(x, y, w, 50f), "Skins & ultimate cinematics only. Stats are identical on every skin - nothing is paywalled.", 12, Gui.Muted, TextAnchor.UpperCenter, false);
            y += 52f;
            Gui.Text(new Rect(x, y, w, 20f), "Common 60% · Rare 28% · Epic 10% · Legendary 2%", 11, Color.white);
            y += 28f;
            if (Gui.Button(new Rect(x, y, w * 0.5f - 5f, 34f), "ALL CLASSES", Gui.Accent2, 12, true, bannerAll)) bannerAll = true;
            if (Gui.Button(new Rect(x + w * 0.5f + 5f, y, w * 0.5f - 5f, 34f), ClassDef.Get(armoryClass).heroName.ToUpper() + " ONLY", Gui.Accent2, 12, true, !bannerAll)) bannerAll = false;
            y += 46f;
            FighterClass? banner = bannerAll ? (FighterClass?)null : armoryClass;
            if (Gui.Button(new Rect(x, y, w, 46f), "PULL x1  ·  " + SaveData.PullCost, Gui.Accent, 16, Save.coins >= SaveData.PullCost))
                DoPull(1, banner);
            y += 54f;
            if (Gui.Button(new Rect(x, y, w, 46f), "PULL x10  ·  " + SaveData.TenPullCost, Gui.Gold, 16, Save.coins >= SaveData.TenPullCost))
                DoPull(10, banner);
            y += 60f;
            foreach (var p in lastPull)
            {
                if (y > g.yMax - 20f) break;
                var cls = ClassDef.Get(p.skin.cls).heroName;
                Gui.Text(new Rect(x, y, w, 18f), cls + " · " + p.skin.name + (p.duplicate ? "  (dup +20)" : "  NEW!"), 12, Skin.RarityColor(p.skin.rarity), TextAnchor.MiddleLeft, false);
                y += 18f;
            }
        }

        void DoPull(int n, FighterClass? banner)
        {
            lastPull = Save.Pull(n, banner);
            foreach (var p in lastPull)
                if (p.skin.rarity >= Rarity.Epic && !p.duplicate)
                    Toast(p.skin.rarity.ToString().ToUpper() + "! " + ClassDef.Get(p.skin.cls).heroName + " · " + p.skin.name);
        }

        // ---------------------------------------------------------------- SETTINGS

        void SettingsTab(Rect a)
        {
            var s = Save.settings;
            var p = new Rect(a.x, a.y, 620f, a.height);
            Gui.Rect(p, Gui.Panel, 16f);
            float x = p.x + 24f, y = p.y + 20f;

            Gui.Text(new Rect(x, y, 200f, 40f), "Match time", 16, Color.white, TextAnchor.MiddleLeft);
            Gui.Text(new Rect(x + 220f, y, 350f, 40f), MatchController.MatchSeconds + " seconds  <size=12><color=#b5aed0>(fixed for every match)</color></size>", 16, Color.white, TextAnchor.MiddleLeft);
            y += 56f;

            Gui.Text(new Rect(x, y, 200f, 40f), "CPU difficulty", 16, Color.white, TextAnchor.MiddleLeft);
            for (int i = 1; i <= 3; i++)
                if (Gui.Button(new Rect(x + 220f + (i - 1) * 120f, y, 110f, 40f), Difficulty[i], Gui.Accent2, 15, true, s.cpuDifficulty == i)) s.cpuDifficulty = i;
            y += 56f;

            if (Toggle(x, ref y, "Camera tracking (webcam)", ref s.useWebcam)) WebcamFeed.SetEnabled(s.useWebcam);
            Toggle(x, ref y, "CV overlay in match", ref s.showCvOverlay);
            Toggle(x, ref y, "Pre-match camera check", ref s.preMatchCheck);
            Toggle(x, ref y, "Screen shake", ref s.screenShake);

            y += 10f;
            if (Gui.Button(new Rect(x, y, 260f, 44f), "RESET PROFILE", Gui.Red, 15))
            {
                PlayerPrefs.DeleteKey("ChibiClash.Save.v1");
                Save = SaveData.Load();
                Bindings.Data = Save;
                WebcamFeed.SetEnabled(Save.settings.useWebcam);
                Toast("Profile reset");
            }
            if (GUI.changed) Save.Save();

            var c = new Rect(a.x + 650f, a.y, a.width - 650f, a.height);
            Gui.Rect(c, Gui.Panel, 16f);
            Gui.Text(new Rect(c.x + 20f, c.y + 10f, 300f, 30f), "KEY BINDINGS", 20, Gui.Gold, TextAnchor.MiddleLeft);
            if (Gui.Button(new Rect(c.xMax - 170f, c.y + 10f, 150f, 30f), "RESET KEYS", Gui.Red, 12))
            {
                Bindings.ResetDefaults(Save);
                Save.Save();
                listening = false;
                Toast("Keys reset to defaults");
            }
            float colW = (c.width - 60f) * 0.5f;
            for (int pn = 0; pn < 2; pn++)
            {
                float bx = c.x + 20f + pn * (colW + 20f), by = c.y + 50f;
                var pc = pn == 0 ? Gui.Accent : Gui.Accent2;
                Gui.Text(new Rect(bx, by, colW, 22f), pn == 0 ? "PLAYER 1" : "PLAYER 2", 15, pc, TextAnchor.MiddleLeft);
                by += 26f;
                for (int k = 0; k < Bindings.Count; k++)
                {
                    var act = (BindAction)k;
                    bool mine = listening && listenPlayer == pn && listenAction == act;
                    Gui.Text(new Rect(bx, by, colW - 106f, 26f), Bindings.Names[k], 13, Color.white, TextAnchor.MiddleLeft, false);
                    if (Gui.Button(new Rect(bx + colW - 104f, by, 104f, 26f), mine ? "key or click" : Bindings.Label(pn, act), mine ? Gui.Gold : pc, 12, true, mine))
                    {
                        listening = true; listenSince = Time.unscaledTime; listenPlayer = pn; listenAction = act;
                    }
                    by += 29f;
                }
            }
            Gui.Text(new Rect(c.x + 20f, c.yMax - 52f, c.width - 40f, 46f),
                "Click a binding, then press a key or click a mouse button (Esc cancels). If it's already used, the two actions swap.\n" +
                "Camera poses: cross arms = block · arm out to the side = fireball · both hands up = super jump (keyboard poses: 1/2/3 and 8/9/0).",
                11, Gui.Muted, TextAnchor.UpperLeft, false);
        }

        bool Toggle(float x, ref float y, string label, ref bool value)
        {
            Gui.Text(new Rect(x, y, 260f, 40f), label, 16, Color.white, TextAnchor.MiddleLeft);
            bool changed = false;
            if (Gui.Button(new Rect(x + 340f, y, 110f, 40f), "ON", Gui.Green, 15, true, value) && !value) { value = true; changed = true; }
            if (Gui.Button(new Rect(x + 460f, y, 110f, 40f), "OFF", Gui.Red, 15, true, !value) && value) { value = false; changed = true; }
            y += 52f;
            if (changed) Save.Save();
            return changed;
        }

        // ---------------------------------------------------------------- CHARACTER SELECT

        void SelectView()
        {
            Header(pending.training ? "Training · pick your fighter" : pending.cpu[1] ? "VS CPU (PvE) · pick your fighters" : "Local Versus · pick your fighters");
            for (int i = 0; i < 2; i++) PlayerColumn(i, new Rect(30f + i * (Gui.W - 60f) * 0.5f + (i == 1 ? 10f : 0f), 104f, (Gui.W - 60f) * 0.5f - 10f, 500f));

            float y = Gui.H - 110f;
            Gui.Text(new Rect(30f, y, 100f, 44f), "STAGE", 18, Gui.Gold, TextAnchor.MiddleLeft);
            for (int s = 0; s < StageBuilder.Names.Length; s++)
                if (Gui.Button(new Rect(120f + s * 190f, y, 180f, 44f), StageBuilder.Names[s], Gui.Gold, 15, true, pending.stage == s))
                    pending.stage = s;

            if (Gui.Button(new Rect(Gui.W - 470f, y - 6f, 180f, 56f), "BACK", Gui.Muted, 18)) view = View.Menu;
            if (Gui.Button(new Rect(Gui.W - 270f, y - 6f, 240f, 56f), pending.training ? "START TRAINING" : "FIGHT!", Gui.Accent, 22))
                Confirm();
        }

        void PlayerColumn(int i, Rect r)
        {
            bool cpu = pending.cpu[i];
            Gui.Rect(r, Gui.Panel, 16f);
            var pc = i == 0 ? Gui.Accent : Gui.Accent2;
            Gui.Frame(r, pc, 2f, 16f);
            string who = cpu ? (pending.training ? "TRAINING DUMMY" : "CPU · " + Difficulty[Save.settings.cpuDifficulty]) : "PLAYER " + (i + 1);
            Gui.Text(new Rect(r.x + 18f, r.y + 8f, 300f, 30f), who, 20, pc, TextAnchor.MiddleLeft);
            if (cpu && Gui.Button(new Rect(r.xMax - 128f, r.y + 8f, 110f, 30f), "RANDOM", pc, 13))
            {
                pending.cls[i] = (FighterClass)Random.Range(0, 5);
                pending.passive[i] = Random.Range(0, 2);
                pending.weapon[i] = Random.Range(0, WeaponDef.ForClass(pending.cls[i]).Count);
            }

            float bw = (r.width - 36f - 16f) / 5f;
            for (int k = 0; k < 5; k++)
            {
                var d = ClassDef.All[k];
                if (Gui.Button(new Rect(r.x + 18f + k * (bw + 4f), r.y + 46f, bw, 40f), d.heroName, d.color, 14, true, pending.cls[i] == d.type))
                {
                    pending.cls[i] = d.type;
                    pending.passive[i] = 0;
                    pending.weapon[i] = 0;
                }
            }

            var def = ClassDef.Get(pending.cls[i]);
            var skin = Save.Equipped(def.type);
            var weapon = WeaponDef.Get(def.type, pending.weapon[i]);
            float x = r.x + 18f, w = r.width - 36f, y = r.y + 98f;
            Gui.Pfp(new Rect(x, y, 96f, 96f), def, skin);
            Gui.Text(new Rect(x + 110f, y, w - 110f, 30f), def.heroName.ToUpper() + "  <size=14><color=#b5aed0>" + def.className + " · " + weapon.name + " · skin: " + skin.name + "</color></size>", 22, Color.white, TextAnchor.MiddleLeft);
            StatBar(x + 110f, y + 34f, "ATK", def.attack, 40f, def.attack.ToString("0"));
            StatBar(x + 110f, y + 52f, "HP", def.health, 600f, def.health.ToString("0"));
            StatBar(x + 110f, y + 70f, "DEF", def.defense, 30f, def.defense.ToString("0") + "  (-" + Mathf.FloorToInt(def.defense / 10f) + " dmg/hit, blocks " + Mathf.RoundToInt((1f - CombatAuthority.BlockedFraction(def.defense)) * 100f) + "%)");
            Gui.Text(new Rect(x + 110f, y + 86f, w - 110f, 18f), "Crit " + Mathf.RoundToInt(def.critRate * 100f) + "% ×" + (1f + def.critDmg).ToString("0.0") + "  ·  " + def.patternLength + "-hit attack pattern  ·  speed " + def.moveSpeed.ToString("0.0"), 11, Gui.Muted, TextAnchor.MiddleLeft, false);
            if (weapon.Sprite != null)
                GUI.DrawTexture(new Rect(x + w - 58f, y - 2f, 54f, 106f), weapon.Sprite.texture, ScaleMode.ScaleToFit);
            y += 110f;

            var variants = WeaponDef.ForClass(def.type);
            if (variants.Count > 1)
            {
                Gui.Text(new Rect(x, y, 200f, 28f), "WEAPON (cosmetic)", 13, Gui.Gold, TextAnchor.MiddleLeft);
                float bw2 = (w - 210f) / variants.Count;
                for (int v = 0; v < variants.Count; v++)
                    if (Gui.Button(new Rect(x + 210f + v * bw2, y, bw2 - 4f, 28f), variants[v].name, def.color, 12, true, pending.weapon[i] == v))
                        pending.weapon[i] = v;
                y += 34f;
            }

            Gui.Text(new Rect(x, y, w, 20f), "PASSIVE (choose 1)", 13, Gui.Gold, TextAnchor.MiddleLeft);
            y += 22f;
            for (int p = 0; p < 2; p++)
            {
                var pr = new Rect(x + p * (w * 0.5f + 4f), y, w * 0.5f - 4f, 54f);
                bool sel = pending.passive[i] == p;
                if (Gui.Button(pr, "", def.color, 12, true, sel)) pending.passive[i] = p;
                var tc = sel ? new Color(0.08f, 0.06f, 0.12f) : Color.white;
                Gui.Text(new Rect(pr.x + 8f, pr.y + 4f, pr.width - 16f, 20f), def.passiveNames[p], 14, tc, TextAnchor.MiddleCenter, true, false);
                Gui.Text(new Rect(pr.x + 8f, pr.y + 24f, pr.width - 16f, 28f), def.passiveDescs[p], 11, tc, TextAnchor.UpperCenter, false, false);
            }
            y += 64f;

            Gui.Text(new Rect(x, y, w, 20f), "SKILLS", 13, Gui.Gold, TextAnchor.MiddleLeft);
            y += 22f;
            for (int s = 0; s < def.SkillCount; s++)
            {
                string key = cpu ? "•" : Bindings.Label(i, (BindAction)((int)BindAction.Skill1 + s));
                Gui.Text(new Rect(x, y, w, 20f), "<b>" + key + "  " + def.skillNames[s] + "</b>  <color=#b5aed0>" + def.skillDescs[s] + " · " + def.skillCooldowns[s] + "s</color>", 13, Color.white, TextAnchor.MiddleLeft, false);
                y += 22f;
            }
            y += 6f;
            Gui.Text(new Rect(x, y, w, 20f), "ULTIMATE", 13, Gui.Gold, TextAnchor.MiddleLeft);
            y += 22f;
            Gui.Text(new Rect(x, y, w, 40f), "<b>" + def.ultName + "</b> - " + def.ultDesc + "\n<color=#5fdcff>Gesture: " + ClassDef.GestureName(def.ultGesture) + "</color>", 13, Color.white, TextAnchor.UpperLeft, false);
        }

        void StatBar(float x, float y, string label, float v, float max, string text)
        {
            Gui.Text(new Rect(x, y, 40f, 16f), label, 11, Gui.Muted, TextAnchor.MiddleLeft);
            Gui.Bar(new Rect(x + 40f, y + 3f, 220f, 10f), v / max, Gui.Accent2, new Color(0f, 0f, 0f, 0.4f));
            Gui.Text(new Rect(x + 270f, y, 200f, 16f), text, 11, Color.white, TextAnchor.MiddleLeft, false);
        }

        // ---------------------------------------------------------------- PRE-MATCH CHECK

        void PreCheckView()
        {
            Header("Pre-match camera check");
            var feed = new Rect(Gui.W * 0.5f - 420f, 120f, 440f, 340f);
            bool ok = lightOk && frameOk;
            Gui.CvPanel(feed, menuTracker, preCheckT < 5f ? Gui.Yellow : ok ? Gui.Green : Gui.Red, Save.settings.useWebcam, "CAMERA CHECK");

            var p = new Rect(feed.xMax + 20f, 120f, 380f, 340f);
            Gui.Rect(p, Gui.Panel, 16f);
            float x = p.x + 20f, w = p.width - 40f;
            Gui.Text(new Rect(x, p.y + 14f, w, 30f), preCheckT < 5f ? "CHECKING... " + Mathf.CeilToInt(5f - preCheckT) : ok ? "ALL CHECKS PASSED" : "CHECK FAILED", 22, preCheckT < 5f ? Gui.Yellow : ok ? Gui.Green : Gui.Red, TextAnchor.MiddleLeft);
            Gui.Bar(new Rect(x, p.y + 52f, w, 10f), preCheckT / 5f, preCheckT < 5f ? Gui.Yellow : ok ? Gui.Green : Gui.Red, new Color(0f, 0f, 0f, 0.4f));
            Check(x, p.y + 80f, w, "Lighting", lightOk, lightMsg);
            Check(x, p.y + 140f, w, "Framing", frameOk, frameMsg);
            Check(x, p.y + 200f, w, "Posture", menuTracker.Posture >= 0.6f, menuTracker.Posture >= 0.6f ? "Nice and upright" : "Sit up straight for bonus coins");

            if (preCheckT >= 5f && !ok)
            {
                if (Gui.Button(new Rect(x, p.yMax - 60f, 160f, 44f), "RETRY", Gui.Yellow, 16)) preCheckT = 0f;
                if (Gui.Button(new Rect(x + 180f, p.yMax - 60f, 160f, 44f), "PLAY ANYWAY", Gui.Muted, 14)) StartMatch();
            }
            if (Gui.Button(new Rect(Gui.W * 0.5f - 90f, 490f, 180f, 48f), "BACK", Gui.Muted, 16)) view = View.Select;
            Gui.Text(new Rect(0f, 550f, Gui.W, 40f), "Ranked queues require this check to pass. Casual matches may skip it.", 13, Gui.Muted, TextAnchor.MiddleCenter, false);
        }

        void Check(float x, float y, float w, string label, bool ok, string msg)
        {
            Gui.Circle(new Rect(x, y + 4f, 18f, 18f), preCheckT < 5f && !ok ? Gui.Yellow : ok ? Gui.Green : Gui.Red);
            Gui.Text(new Rect(x + 28f, y, w - 28f, 26f), label, 17, Color.white, TextAnchor.MiddleLeft);
            Gui.Text(new Rect(x + 28f, y + 26f, w - 28f, 22f), msg, 12, Gui.Muted, TextAnchor.MiddleLeft, false);
        }

        void OnDestroy()
        {
            MoveNetPoseSource.Shared.Dispose();
            WebcamFeed.SetEnabled(false);
            Time.timeScale = 1f;
        }
    }
}
