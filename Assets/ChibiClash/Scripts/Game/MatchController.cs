using System.Collections.Generic;
using UnityEngine;
using UnityEngine.InputSystem;

namespace ChibiClash
{
    public class MatchConfig
    {
        public FighterClass[] cls = { FighterClass.Melee, FighterClass.Heavy };
        public int[] passive = new int[2];
        public int[] weapon = new int[2];     // cosmetic weapon variant per player (index into WeaponDef.ForClass)
        public bool[] cpu = { false, true };
        public int stage;
        public int timeLimit = MatchController.MatchSeconds;
        public int difficulty = 2;
        public bool training;
        public bool pve => cpu[1];
    }

    /// <summary>
    /// Runs one local match. In online play this simulation would be the rollback-netcode tick
    /// (inputs exchanged per frame, server authoritative for ranked); locally it runs directly.
    /// </summary>
    public class MatchController : MonoBehaviour
    {
        public const float IntroLength = 2.2f;
        public const int MatchSeconds = 180;   // match length is fixed

        public GameManager GM { get; private set; }
        public MatchConfig Config { get; private set; }
        public StageInfo Stage { get; private set; }
        public readonly Fighter[] F = new Fighter[2];

        public float TimeLeft { get; private set; }
        public bool Running { get; private set; }
        public bool Finished { get; private set; }
        public bool Paused;
        public int Winner { get; private set; } = -1;
        public float IntroTimer { get; private set; } = IntroLength;
        public float FinishedTime { get; private set; }
        public bool InputEnabled => Running && !Paused;

        public Fighter CinematicFighter { get; private set; }
        public float CinematicStart { get; private set; } = -10f;
        public float FlashAlpha { get; private set; }
        public Color FlashColor { get; private set; }

        public readonly List<string> RewardLog = new List<string>();
        public int CoinsEarned { get; private set; }

        float hitstopUntil, cinematicUntil;
        CameraController cam;

        public static MatchController Create(GameManager gm, MatchConfig cfg)
        {
            var go = new GameObject("Match");
            var m = go.AddComponent<MatchController>();
            m.GM = gm;
            m.Config = cfg;
            m.Setup();
            return m;
        }

        void Setup()
        {
            Fx.Init(transform);
            Stage = StageBuilder.Build(Config.stage, transform);
            TimeLeft = Config.training ? 0f : Config.timeLimit;
            for (int i = 0; i < 2; i++) F[i] = SpawnFighter(i);
            F[0].Opponent = F[1];
            F[1].Opponent = F[0];
            Physics2D.IgnoreCollision(F[0].Collider, F[1].Collider);

            var camGo = Camera.main != null ? Camera.main.gameObject : new GameObject("Main Camera", typeof(Camera));
            camGo.tag = "MainCamera";
            cam = camGo.GetComponent<CameraController>();
            if (cam == null) cam = camGo.AddComponent<CameraController>();
            cam.enabled = true;
            cam.targets = F;
        }

        Fighter SpawnFighter(int i)
        {
            var go = new GameObject("P" + (i + 1));
            go.transform.SetParent(transform, false);
            go.transform.position = Stage.spawns[i];
            go.AddComponent<Rigidbody2D>();
            go.AddComponent<BoxCollider2D>();
            var f = go.AddComponent<Fighter>();

            bool cpu = Config.cpu[i];
            FighterInput input = cpu ? new CpuInput(Config.training ? 0 : Config.difficulty) : (FighterInput)new HumanInput(i);
            var sim = new SimulatedPoseSource((cpu ? "CPU" : "P" + (i + 1)) + " simulated", () => input.SimulatedGesture, () => input.Slouching);
            // Player 1 uses the real camera when it's on and the pose model is running; everyone else uses the simulator.
            IPoseSource pose = cpu ? (IPoseSource)sim : new AutoPoseSource(i, sim, () => GM.Save.settings.useWebcam);
            var def = ClassDef.Get(Config.cls[i]);
            f.Init(this, i, def, Config.passive[i], GM.Save.Equipped(def.type), input, new GestureTracker(pose), cpu,
                   WeaponDef.Get(def.type, Config.weapon[i]));
            return f;
        }

        void Update()
        {
            float udt = Time.unscaledDeltaTime;
            var kb = Keyboard.current;
            if (!Finished && kb != null && kb.escapeKey.wasPressedThisFrame) Paused = !Paused;

            float scale = 1f;
            if (Paused) scale = 0f;
            else
            {
                if (Time.unscaledTime < hitstopUntil) scale = 0.05f;
                if (Time.unscaledTime < cinematicUntil) scale = Mathf.Min(scale, 0.2f);
                if (Finished && Time.unscaledTime - FinishedTime < 1.2f) scale = 0.3f;
            }
            Time.timeScale = scale;

            Fx.Tick(Time.deltaTime);
            FlashAlpha = Mathf.MoveTowards(FlashAlpha, 0f, udt * 2.5f);

            if (Finished || Paused) return;
            if (!Running)
            {
                IntroTimer -= udt;
                if (IntroTimer <= 0f) Running = true;
                return;
            }

            if (Config.training)
            {
                if (kb != null && kb.tKey.wasPressedThisFrame) F[0].Ult = Fighter.UltMax;
            }
            else
            {
                TimeLeft -= Time.deltaTime;
                if (TimeLeft <= 0f)
                {
                    TimeLeft = 0f;
                    float a = F[0].Hp / F[0].MaxHp, b = F[1].Hp / F[1].MaxHp;
                    End(Mathf.Approximately(a, b) ? -1 : a > b ? 0 : 1);
                    return;
                }
            }

            foreach (var f in F)
            {
                Vector2 p = f.transform.position;
                if (Mathf.Abs(p.x) > Stage.blastX || p.y < Stage.killY || p.y > Stage.ceilingY)
                    f.RingOut(Stage.respawn);
                if (!f.IsCpu) f.Gestures.SamplePosture(Time.deltaTime);
            }
        }

        public void OnKO(Fighter f)
        {
            if (Finished) return;
            if (Config.training)
            {
                f.Revive();
                Fx.Text(f.Center + Vector2.up * 1.5f, "REVIVED", Color.white, 1f);
                return;
            }
            Fx.Text(f.Center + Vector2.up * 2f, "K.O.!", new Color(1f, 0.3f, 0.3f), 2f);
            Shake(0.8f);
            End(f.Index == 0 ? 1 : 0);
        }

        void End(int winner)
        {
            Running = false;
            Finished = true;
            Winner = winner;
            FinishedTime = Time.unscaledTime;
            AwardRewards();
        }

        public void HitStop(float t) => hitstopUntil = Mathf.Max(hitstopUntil, Time.unscaledTime + t);

        public void Shake(float amount)
        {
            if (GM.Save.settings.screenShake && cam != null) cam.Shake(amount);
        }

        public void Cinematic(Fighter f)
        {
            CinematicFighter = f;
            CinematicStart = Time.unscaledTime;
            cinematicUntil = Time.unscaledTime + 0.8f;
            FlashAlpha = 0.8f;
            FlashColor = f.Skin.ultFx;
            if (cam != null) cam.Focus(f, 0.8f);
            Shake(0.3f);
        }

        /// <summary>Gacha currency is earned only by playing: wins, PvE, quests and good posture (GDD 4.3).</summary>
        void AwardRewards()
        {
            if (Config.training) return;
            var s = GM.Save;
            var me = F[0];
            int before = s.coins;
            bool won = Winner == 0;

            int matchCoins = Config.pve ? (won ? 50 : 15) : (won ? 40 : 15);
            RewardLog.Add((Config.pve ? (won ? "PvE victory" : "PvE match") : won ? "Victory" : "Match played") + "  +" + matchCoins);
            s.coins += matchCoins;

            float posture = me.Gestures.PostureAverage;
            int postureCoins = Mathf.RoundToInt(posture * 30f);
            RewardLog.Add("Good posture " + Mathf.RoundToInt(posture * 100f) + "%  +" + postureCoins);
            s.coins += postureCoins;

            if (won) s.Progress("win", 1, RewardLog);
            if (won && Config.pve) s.Progress("pve", 1, RewardLog);
            if (posture >= 0.8f) s.Progress("posture", 1, RewardLog);
            s.Progress("combo", me.CombosLanded, RewardLog);
            s.Progress("ult", me.UltsUsed, RewardLog);
            s.Progress("parry", me.Parries, RewardLog);

            s.matches++;
            if (won) s.wins++;
            else if (Winner == 1) s.losses++;
            CoinsEarned = s.coins - before;
            s.Save();
        }

        void OnDestroy()
        {
            Time.timeScale = 1f;
            Fx.Popups.Clear();
            if (cam != null) cam.targets = null;
        }
    }
}
