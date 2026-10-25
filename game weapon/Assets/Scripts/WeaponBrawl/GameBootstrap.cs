using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;

namespace WeaponBrawl
{
    /// <summary>
    /// One-component setup: add this to an empty GameObject in an empty scene and press Play.
    /// Builds the camera, arena and UI, shows weapon + passive selection, then spawns the player
    /// and a CPU opponent.
    /// </summary>
    public class GameBootstrap : MonoBehaviour
    {
        [SerializeField] bool opponentAI = true;
        [SerializeField] float matchSeconds = 180f;

        Canvas canvas;
        PassiveSelectionUI selection;
        SkillBarUI skillBar;
        BattleHUD hud;
        Text hint;
        Fighter player, opponent;

        void Start()
        {
            SetupCamera();
            BuildArena();
            UIFactory.EnsureEventSystem();

            canvas = UIFactory.CreateCanvas("UI", 10);
            hud = BattleHUD.Create(canvas);
            skillBar = SkillBarUI.Create(canvas);
            hint = UIFactory.Label(canvas.transform, "Controls", "", 20, new Color(1f, 1f, 1f, 0.6f), TextAnchor.LowerLeft);
            hint.rectTransform.Place(Vector2.zero, Vector2.zero, new Vector2(30f, 24f), new Vector2(1000f, 60f));

            selection = PassiveSelectionUI.Create(canvas);
            selection.Confirmed += StartBattle;
            selection.Cancelled += Resume;
            OpenSelection();
        }

        void Update()
        {
            if (selection.gameObject.activeSelf || player == null) return;
            if (Input.GetKeyDown(Controls.ChangeWeapon)) OpenSelection();
            if (Input.GetKeyDown(Controls.ToggleDummyAI) && opponent != null)
            {
                opponentAI = !opponentAI;
                opponent.GetComponent<SimpleAI>().active = opponentAI;
                RefreshHint();
            }
        }

        void OpenSelection()
        {
            selection.CanCancel = player != null;
            selection.Open();
            SetBattleVisible(false);
            Time.timeScale = 0f;
        }

        void Resume()
        {
            SetBattleVisible(true);
            Time.timeScale = 1f;
        }

        void StartBattle(WeaponType type, int passiveIndex)
        {
            if (player != null) Destroy(player.gameObject);
            if (opponent != null) Destroy(opponent.gameObject);

            player = FighterFactory.Create(type, passiveIndex, new Vector2(-4f, 0.1f), true);

            var others = new List<WeaponDefinition>();
            foreach (var d in WeaponDatabase.All)
                if (d.type != type) others.Add(d);
            var opponentDef = others[Random.Range(0, others.Count)];
            opponent = FighterFactory.Create(opponentDef.type, Random.Range(0, 2), new Vector2(4f, 0.1f), false);
            opponent.SetFacing(-1);
            opponent.GetComponent<SimpleAI>().active = opponentAI;

            // Fighters pass through each other (hits use hurtboxes, not physics contacts).
            Physics2D.IgnoreCollision(player.GetComponent<Collider2D>(), opponent.GetComponent<Collider2D>());

            hud.Bind(player, opponent, matchSeconds);
            skillBar.Bind(player);
            RefreshHint();
            Resume();
        }

        void SetBattleVisible(bool visible)
        {
            hud.gameObject.SetActive(visible);
            skillBar.gameObject.SetActive(visible);
            hint.gameObject.SetActive(visible);
            if (player != null) player.GetComponent<PlayerController>().enabled = visible;
        }

        void RefreshHint()
        {
            hint.text =
                $"Move A/D   Jump Space   Attack {Controls.Attack} (hold)   Skills {Controls.Skills[0]}/{Controls.Skills[1]}/{Controls.Skills[2]}   Ultimate {Controls.Ultimate}\n" +
                $"{Controls.ChangeWeapon}: change weapon/passive   {Controls.ToggleDummyAI}: CPU AI {(opponentAI ? "ON" : "OFF")}   {Controls.DebugFillUltimate}: fill ultimate (debug)";
        }

        static void SetupCamera()
        {
            var cam = Camera.main;
            if (cam == null)
            {
                cam = new GameObject("Main Camera").AddComponent<Camera>();
                cam.tag = "MainCamera";
            }
            cam.orthographic = true;
            cam.orthographicSize = 6.5f;
            cam.transform.position = new Vector3(0f, 3.8f, -10f);
            cam.clearFlags = CameraClearFlags.SolidColor;
            cam.backgroundColor = new Color(0.07f, 0.08f, 0.12f);
        }

        static void BuildArena()
        {
            var arena = new GameObject("Arena").transform;

            var ground = SpriteFactory.Part(arena, "Ground", SpriteFactory.Square, new Color(0.18f, 0.2f, 0.26f), new Vector2(0f, -1.5f), new Vector2(40f, 3f), 0);
            ground.gameObject.AddComponent<BoxCollider2D>();
            SpriteFactory.Part(arena, "Edge", SpriteFactory.Square, new Color(0.35f, 0.4f, 0.55f), new Vector2(0f, -0.04f), new Vector2(40f, 0.08f), 1);

            for (int side = -1; side <= 1; side += 2)
            {
                var wall = new GameObject("Wall");
                wall.transform.SetParent(arena, false);
                wall.transform.position = new Vector2(side * (Arena.HalfWidth + 0.5f), 10f);
                wall.AddComponent<BoxCollider2D>().size = new Vector2(1f, 30f);
            }

            // Background pillars for depth
            var pillar = new Color(0.11f, 0.12f, 0.17f);
            float[] xs = { -9f, -5.5f, -1.5f, 2.5f, 6f, 9.5f };
            float[] heights = { 6f, 8.5f, 5f, 9f, 6.5f, 7.5f };
            for (int i = 0; i < xs.Length; i++)
                SpriteFactory.Part(arena, "Pillar", SpriteFactory.Square, pillar, new Vector2(xs[i], heights[i] * 0.5f), new Vector2(1.2f, heights[i]), -10);
        }
    }
}
