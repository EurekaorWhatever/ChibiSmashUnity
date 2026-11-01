using UnityEngine;
using UnityEngine.UI;

namespace WeaponBrawl
{
    /// <summary>
    /// One-component setup: add this to an empty GameObject in an empty scene and press Play.
    /// Builds the camera and UI, shows the character select screen, then builds the chosen stage
    /// and spawns both fighters. Runs before other scripts so input is ready for them each frame.
    /// </summary>
    [DefaultExecutionOrder(-100)]
    public class GameBootstrap : MonoBehaviour
    {
        const KeyCode ToggleOpponentAI = KeyCode.T;

        Canvas canvas;
        CharacterSelectUI selection;
        SkillBarUI skillBar;
        BattleHUD hud;
        Text hint;
        Fighter player, opponent;
        GameObject arena;
        bool opponentAI, p2Human;

        void Start()
        {
            SetupCamera();
            BuildArena(StageDatabase.All[0]);
            UIFactory.EnsureEventSystem();

            canvas = UIFactory.CreateCanvas("UI", 10);
            hud = BattleHUD.Create(canvas);
            skillBar = SkillBarUI.Create(canvas);
            hint = UIFactory.Label(canvas.transform, "Controls", "", 20, Color.white, TextAnchor.LowerLeft, FontStyle.Bold);
            hint.rectTransform.Place(Vector2.zero, Vector2.zero, new Vector2(30f, 24f), new Vector2(1150f, 60f));
            hint.gameObject.AddComponent<Outline>().effectColor = UITheme.Navy;

            selection = CharacterSelectUI.Create(canvas);
            selection.Confirmed += StartBattle;
            selection.Cancelled += Resume;
            OpenSelection();
        }

        void Update()
        {
            GameInput.Tick();
            if (selection.gameObject.activeSelf || player == null) return;

            RefreshHint();
            if (GameInput.P1.Down(GameAction.OpenMenu) || GameInput.P2.Down(GameAction.OpenMenu))
            {
                OpenSelection();
            }
            else if (Input.GetKeyDown(ToggleOpponentAI) && !p2Human)
            {
                opponentAI = !opponentAI;
                opponent.GetComponent<SimpleAI>().active = opponentAI;
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

        void StartBattle(MatchConfig match)
        {
            if (player != null) Destroy(player.gameObject);
            if (opponent != null) Destroy(opponent.gameObject);
            BuildArena(StageDatabase.All[match.stage]);

            p2Human = match.p2Human;
            opponentAI = match.opponentAI;
            player = FighterFactory.Create(match.p1Type, match.p1Variant, match.p1Passive,new Vector2(-4f, 0.1f), 0, GameInput.P1);
            opponent = FighterFactory.Create(match.p2Type, match.p2Variant, match.p2Passive,new Vector2(4f, 0.1f), 1, p2Human ? GameInput.P2 : null);
            opponent.SetFacing(-1);
            if (!p2Human) opponent.GetComponent<SimpleAI>().active = opponentAI;

            // Fighters pass through each other (hits use hurtboxes, not physics contacts).
            Physics2D.IgnoreCollision(player.GetComponent<Collider2D>(), opponent.GetComponent<Collider2D>());

            hud.Bind(player, opponent, match.matchSeconds);
            skillBar.Bind(player);
            Resume();
        }

        void SetBattleVisible(bool visible)
        {
            hud.gameObject.SetActive(visible);
            skillBar.gameObject.SetActive(visible);
            hint.gameObject.SetActive(visible);
            SetControllable(player, visible);
            SetControllable(opponent, visible);
        }

        static void SetControllable(Fighter fighter, bool on)
        {
            if (fighter == null) return;
            var controller = fighter.GetComponent<PlayerController>();
            if (controller != null) controller.enabled = on;
        }

        void RefreshHint()
        {
            var p = GameInput.P1;
            string skills = $"{p.Label(GameAction.Skill1)}/{p.Label(GameAction.Skill2)}/{p.Label(GameAction.Skill3)}";
            string ai = p2Human ? "" : $"   {ToggleOpponentAI}: CPU AI {(opponentAI ? "ON" : "OFF")}";
            hint.text =
                $"Move {p.MoveLabel}   Jump {p.Label(GameAction.Jump)}   Attack {p.Label(GameAction.Attack)} (hold)   Skills {skills}   Ultimate {p.Label(GameAction.Ultimate)}\n" +
                $"{p.Label(GameAction.OpenMenu)}: character select{ai}   F1: fill ultimate (debug)";
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
        }

        void BuildArena(StageDefinition stage)
        {
            if (arena != null) Destroy(arena);
            arena = new GameObject("Arena");
            var root = arena.transform;
            Camera.main.backgroundColor = stage.sky;

            var ground = SpriteFactory.Part(root, "Ground", SpriteFactory.Square, stage.ground, new Vector2(0f, -1.5f), new Vector2(40f, 3f), 0);
            ground.gameObject.AddComponent<BoxCollider2D>();
            SpriteFactory.Part(root, "Edge", SpriteFactory.Square, stage.edge, new Vector2(0f, -0.04f), new Vector2(40f, 0.08f), 1);

            for (int side = -1; side <= 1; side += 2)
            {
                var wall = new GameObject("Wall");
                wall.transform.SetParent(root, false);
                wall.transform.position = new Vector2(side * (Arena.HalfWidth + 0.5f), 10f);
                wall.AddComponent<BoxCollider2D>().size = new Vector2(1f, 30f);
            }

            // Background pillars for depth
            float[] xs = { -9f, -5.5f, -1.5f, 2.5f, 6f, 9.5f };
            float[] heights = { 6f, 8.5f, 5f, 9f, 6.5f, 7.5f };
            for (int i = 0; i < xs.Length; i++)
            {
                SpriteFactory.Part(root, "Pillar", SpriteFactory.Square, stage.pillar, new Vector2(xs[i], heights[i] * 0.5f), new Vector2(1.2f, heights[i]), -10);
                SpriteFactory.Part(root, "Cap", SpriteFactory.Square, stage.cap, new Vector2(xs[i], heights[i]), new Vector2(1.5f, 0.18f), -9);
            }
        }
    }
}
