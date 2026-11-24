using UnityEngine;
using UnityEngine.InputSystem;

namespace ChibiClash
{
    /// <summary>One frame of fighter commands, filled by a human device or the CPU.</summary>
    public abstract class FighterInput
    {
        public float MoveX;
        /// <summary>Up = held "aim up" (up-tilt / up-smash / up-special, CH-3); never jumps by itself.</summary>
        public bool Down, Up, Jump, Light, Heavy, Block, Dash, Ult;
        /// <summary>Special (B) button press (CH-3).</summary>
        public bool Special;
        public readonly bool[] Skill = new bool[3];

        protected void Clear()
        {
            MoveX = 0f;
            Down = Up = Jump = Light = Heavy = Block = Dash = Ult = Special = false;
            Skill[0] = Skill[1] = Skill[2] = false;
        }

        public abstract void Tick(Fighter self, float dt);

        /// <summary>Gesture held on the simulated camera (keyboard/d-pad stand-in for real poses).</summary>
        public abstract Gesture SimulatedGesture { get; }
        public virtual bool Slouching => false;
    }

    /// <summary>
    /// Keyboard (rebindable in Settings, see Bindings) plus gamepad N for player N.
    /// Defaults P1: A/D move, W aim up, S down, Space jump, left click light, right click heavy, E special, F block, Q dash,
    /// U/I/O skills, R ultimate.
    /// Defaults P2: arrows (Up aims), RCtrl jump, . light, / heavy, \ special, RShift block, , dash, ; ' ] skills, Enter
    /// ultimate (numpad also works).
    /// Gamepad: stick move/aim, A jump, X light, Y heavy, B special, RT block, L3 dash, LB/RB/LT skills, R3 ultimate (Start pauses),
    /// d-pad poses (down cross, right/left punch, up raise).
    /// Simulated camera poses (only when no real camera tracking): P1 keys 1/2/3, P2 keys 8/9/0; P / - toggle slouch.
    /// </summary>
    public class HumanInput : FighterInput
    {
        readonly int player;
        bool slouch;

        public HumanInput(int player) { this.player = player; }

        Gamepad Pad => player < Gamepad.all.Count ? Gamepad.all[player] : null;

        public override bool Slouching => slouch;

        // Bindings can be keys or mouse buttons; Bindings resolves either.
        bool Held(Keyboard kb, BindAction a) => Bindings.Held(player, a);
        bool Pressed(Keyboard kb, BindAction a) => Bindings.Pressed(player, a);

        public override void Tick(Fighter self, float dt)
        {
            Clear();
            var kb = Keyboard.current;
            if (kb != null && Bindings.HasKeys(player))   // players 3-4 have no keyboard half: gamepad only
            {
                if (Held(kb, BindAction.Left)) MoveX -= 1f;
                if (Held(kb, BindAction.Right)) MoveX += 1f;
                Down = Held(kb, BindAction.Down);
                Up = Held(kb, BindAction.Up);
                Special = Pressed(kb, BindAction.Special);
                Jump = Pressed(kb, BindAction.Jump) || Pressed(kb, BindAction.JumpAlt);
                Light = Pressed(kb, BindAction.Light);
                Heavy = Pressed(kb, BindAction.Heavy);
                Block = Held(kb, BindAction.Block);
                Dash = Pressed(kb, BindAction.Dash);
                Skill[0] = Pressed(kb, BindAction.Skill1);
                Skill[1] = Pressed(kb, BindAction.Skill2);
                Skill[2] = Pressed(kb, BindAction.Skill3);
                Ult = Pressed(kb, BindAction.Ultimate);

                if (player == 0)
                {
                    if (kb.pKey.wasPressedThisFrame) slouch = !slouch;
                }
                else
                {
                    Light |= kb.numpad1Key.wasPressedThisFrame;
                    Heavy |= kb.numpad2Key.wasPressedThisFrame;
                    Block |= kb.numpad3Key.isPressed;
                    Dash |= kb.numpad0Key.wasPressedThisFrame;
                    Skill[0] |= kb.numpad4Key.wasPressedThisFrame;
                    Skill[1] |= kb.numpad5Key.wasPressedThisFrame;
                    Skill[2] |= kb.numpad6Key.wasPressedThisFrame;
                    Ult |= kb.numpadEnterKey.wasPressedThisFrame;
                    Special |= kb.numpadPlusKey.wasPressedThisFrame;
                    if (kb.minusKey.wasPressedThisFrame) slouch = !slouch;
                }
            }

            var gp = Pad;
            if (gp != null)
            {
                float sx = gp.leftStick.x.ReadValue();
                if (Mathf.Abs(sx) > 0.25f) MoveX = Mathf.Sign(sx);
                float sy = gp.leftStick.y.ReadValue();
                Down |= sy < -0.6f;
                Up |= sy > 0.6f;
                Jump |= gp.buttonSouth.wasPressedThisFrame;
                Light |= gp.buttonWest.wasPressedThisFrame;
                Heavy |= gp.buttonNorth.wasPressedThisFrame;
                Block |= gp.rightTrigger.isPressed;
                Special |= gp.buttonEast.wasPressedThisFrame;          // B = Special (Smash convention, CH-3)
                Dash |= gp.leftStickButton.wasPressedThisFrame;      // L3 = dash / dodge
                Skill[0] |= gp.leftShoulder.wasPressedThisFrame;
                Skill[1] |= gp.rightShoulder.wasPressedThisFrame;
                Skill[2] |= gp.leftTrigger.wasPressedThisFrame;
                // Start is Pause only (review 2026-11-16 item 19, user decision): it used to fire the ult as it paused.
                Ult |= gp.rightStickButton.wasPressedThisFrame;
            }
            MoveX = Mathf.Clamp(MoveX, -1f, 1f);
        }

        public override Gesture SimulatedGesture
        {
            get
            {
                var kb = Keyboard.current;
                if (kb != null && Bindings.HasKeys(player))   // players 3-4 never read P2's 8/9/0 pose keys
                {
                    if (player == 0)
                    {
                        if (kb.digit1Key.isPressed) return Gesture.CrossArms;
                        if (kb.digit2Key.isPressed) return Gesture.PunchForward;
                        if (kb.digit3Key.isPressed) return Gesture.RaiseHands;
                    }
                    else
                    {
                        if (kb.digit8Key.isPressed || kb.numpad7Key.isPressed) return Gesture.CrossArms;
                        if (kb.digit9Key.isPressed || kb.numpad8Key.isPressed) return Gesture.PunchForward;
                        if (kb.digit0Key.isPressed || kb.numpad9Key.isPressed) return Gesture.RaiseHands;
                    }
                }
                var gp = Pad;
                if (gp != null)
                {
                    if (gp.dpad.down.isPressed) return Gesture.CrossArms;
                    if (gp.dpad.left.isPressed || gp.dpad.right.isPressed) return Gesture.PunchForward;
                    if (gp.dpad.up.isPressed) return Gesture.RaiseHands;
                }
                return Gesture.None;
            }
        }
    }

    /// <summary>Simple CPU opponent for PvE and training. Difficulty 0 = training dummy.</summary>
    public class CpuInput : FighterInput
    {
        readonly int difficulty;
        float think;
        float wantMove;
        bool wantBlock;
        float gestureDelay = -1f;
        bool gestureWillSucceed;
        Gesture pose;

        public CpuInput(int difficulty) { this.difficulty = difficulty; }

        public override Gesture SimulatedGesture => pose;

        public override void Tick(Fighter self, float dt)
        {
            Clear();
            if (difficulty <= 0 || self.Opponent == null) return;
            var opp = self.Opponent;
            Vector2 d = opp.transform.position - self.transform.position;
            float skill = difficulty / 3f;

            UpdateUltGesture(self, dt);

            // Recover to the stage when knocked off.
            var stage = self.Match.Stage;
            if (Mathf.Abs(self.transform.position.x) > stage.halfWidth - 0.3f || self.transform.position.y < stage.groundY - 0.5f)
            {
                MoveX = -Mathf.Sign(self.transform.position.x);
                if (self.Velocity.y < 0f) Jump = true;
                if (self.Velocity.y < -2f) Skill[0] = self.SkillReady(0) && self.Def.type == FighterClass.Melee;
                return;
            }

            think -= dt;
            if (think <= 0f)
            {
                think = Random.Range(0.1f, 0.3f) / Mathf.Max(0.4f, skill);
                float preferred = self.Def.rangedNormals ? 4.5f : self.Def.reach * 0.8f;
                float dist = Mathf.Abs(d.x);
                if (dist > preferred + 0.4f) wantMove = Mathf.Sign(d.x);
                else if (self.Def.rangedNormals && dist < preferred - 1.5f) wantMove = -Mathf.Sign(d.x);
                else wantMove = 0f;
                wantBlock = opp.IsAttacking && dist < 2.5f && Random.value < 0.15f + 0.35f * skill;
                if (d.y > 1.5f && Random.value < 0.6f) Jump = true;
            }

            MoveX = wantMove;
            Block = wantBlock;
            if (wantBlock) return;

            bool facingOpp = Mathf.Sign(d.x) == self.Facing || Mathf.Abs(d.x) < 0.2f;
            if (wantMove == 0f && !facingOpp) MoveX = Mathf.Sign(d.x) * 0.2f;

            float attackChance = (0.06f + 0.12f * skill) * dt * 60f;
            bool inMelee = Mathf.Abs(d.x) < self.Def.reach + 0.4f && Mathf.Abs(d.y) < 1.2f;
            bool inLine = Mathf.Abs(d.y) < 1f;
            if ((self.Def.rangedNormals ? inLine : inMelee) && facingOpp && Random.value < attackChance)
            {
                if (Random.value < 0.75f) Light = true; else Heavy = true;
            }

            if (Random.value < 0.012f * skill * dt * 60f)
            {
                int s = Random.Range(0, self.Def.SkillCount);
                if (self.SkillReady(s) && (inMelee || self.Def.rangedNormals || Mathf.Abs(d.x) < 5f)) Skill[s] = true;
            }

            if (self.Ult >= 100f && !self.UltWindow && Random.value < 0.02f * dt * 60f) Ult = true;
        }

        void UpdateUltGesture(Fighter self, float dt)
        {
            if (self.UltWindow)
            {
                if (gestureDelay < 0f)
                {
                    gestureDelay = Random.Range(0.6f, 2.2f);
                    gestureWillSucceed = Random.value < 0.6f + 0.12f * difficulty;
                }
                gestureDelay -= dt;
                if (gestureDelay <= 0f && gestureWillSucceed) pose = self.Def.ultGesture;
            }
            else
            {
                gestureDelay = -1f;
                pose = Gesture.None;
            }
        }
    }
}
