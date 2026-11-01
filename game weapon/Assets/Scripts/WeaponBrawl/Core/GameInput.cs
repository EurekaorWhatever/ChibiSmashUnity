using System;
using System.Collections.Generic;
using UnityEngine;

namespace WeaponBrawl
{
    /// <summary>Xbox-layout button numbers as Unity reports them on Windows.</summary>
    public enum PadButton { A = 0, B = 1, X = 2, Y = 3, LB = 4, RB = 5, Back = 6, Start = 7 }

    public enum GameAction
    {
        Confirm, Cancel, TabPrev, TabNext, Start,
        Jump, Attack, Skill1, Skill2, Skill3, Ultimate, OpenMenu,
    }

    /// <summary>One player's bindings: gamepad first, keyboard as the fallback. Also provides menu navigation with key repeat.</summary>
    public class PlayerInputMap
    {
        class Binding
        {
            public PadButton? pad;
            public KeyCode[] keys;
        }

        const float RepeatDelay = 0.32f, RepeatInterval = 0.12f, NavThreshold = 0.5f;

        public readonly int Index;
        readonly KeyCode left, right, up, down;
        readonly string moveKeysLabel;
        readonly string axisX, axisY, dpadX, dpadY;
        readonly Dictionary<GameAction, Binding> bindings = new Dictionary<GameAction, Binding>();
        int lastNavX, lastNavY;
        float nextNavX, nextNavY;

        /// <summary>-1, 0 or 1 for this frame, with hold-to-repeat. Valid after <see cref="GameInput.Tick"/>.</summary>
        public int NavX { get; private set; }
        public int NavY { get; private set; }

        public PlayerInputMap(int index, KeyCode left, KeyCode right, KeyCode up, KeyCode down, string moveKeysLabel)
        {
            Index = index;
            this.left = left;
            this.right = right;
            this.up = up;
            this.down = down;
            this.moveKeysLabel = moveKeysLabel;
            axisX = GameInput.AxisName(index, "Horizontal");
            axisY = GameInput.AxisName(index, "Vertical");
            dpadX = GameInput.AxisName(index, "DPad X");
            dpadY = GameInput.AxisName(index, "DPad Y");
        }

        public PlayerInputMap Bind(GameAction action, PadButton? pad, params KeyCode[] keys)
        {
            bindings[action] = new Binding { pad = pad, keys = keys };
            return this;
        }

        public bool Down(GameAction action)
        {
            if (!bindings.TryGetValue(action, out var b)) return false;
            if (b.pad.HasValue && Input.GetKeyDown(PadKey(b.pad.Value))) return true;
            foreach (var k in b.keys)
                if (Input.GetKeyDown(k)) return true;
            return false;
        }

        public bool Held(GameAction action)
        {
            if (!bindings.TryGetValue(action, out var b)) return false;
            if (b.pad.HasValue && Input.GetKey(PadKey(b.pad.Value))) return true;
            foreach (var k in b.keys)
                if (Input.GetKey(k)) return true;
            return false;
        }

        public float MoveX => Axis(Input.GetKey(right), Input.GetKey(left), axisX, dpadX);
        public float MoveY => Axis(Input.GetKey(up), Input.GetKey(down), axisY, dpadY);

        /// <summary>Text for on-screen prompts, following whichever device was used last.</summary>
        public string Label(GameAction action)
        {
            if (!bindings.TryGetValue(action, out var b)) return "";
            if (GameInput.UsingPad && b.pad.HasValue) return b.pad.Value.ToString().ToUpperInvariant();
            return b.keys.Length > 0 ? GameInput.KeyLabel(b.keys[0]) : "";
        }

        public string MoveLabel => GameInput.UsingPad ? "STICK" : moveKeysLabel;

        public bool AnyPadActivity()
        {
            for (int i = 0; i < 8; i++)
                if (Input.GetKeyDown(PadKey((PadButton)i))) return true;
            return Mathf.Abs(GameInput.ReadAxis(axisX)) > NavThreshold || Mathf.Abs(GameInput.ReadAxis(axisY)) > NavThreshold ||
                   Mathf.Abs(GameInput.ReadAxis(dpadX)) > NavThreshold || Mathf.Abs(GameInput.ReadAxis(dpadY)) > NavThreshold;
        }

        public void Tick()
        {
            NavX = Repeat(Sign(MoveX), ref lastNavX, ref nextNavX);
            NavY = Repeat(Sign(MoveY), ref lastNavY, ref nextNavY);
        }

        KeyCode PadKey(PadButton button) => KeyCode.Joystick1Button0 + Index * 20 + (int)button;

        static float Axis(bool positive, bool negative, string stick, string dpad)
        {
            float keys = (positive ? 1f : 0f) - (negative ? 1f : 0f);
            if (keys != 0f) return keys;
            float d = GameInput.ReadAxis(dpad);
            return Mathf.Abs(d) > 0.1f ? d : GameInput.ReadAxis(stick);
        }

        static int Sign(float v) => v > NavThreshold ? 1 : v < -NavThreshold ? -1 : 0;

        static int Repeat(int dir, ref int last, ref float next)
        {
            float now = Time.unscaledTime;
            if (dir == 0)
            {
                last = 0;
                return 0;
            }
            if (dir != last)
            {
                last = dir;
                next = now + RepeatDelay;
                return dir;
            }
            if (now < next) return 0;
            next = now + RepeatInterval;
            return dir;
        }
    }

    /// <summary>Input for both players. Call <see cref="Tick"/> once per frame before anything reads it.</summary>
    public static class GameInput
    {
        public static readonly PlayerInputMap P1 = new PlayerInputMap(0, KeyCode.A, KeyCode.D, KeyCode.W, KeyCode.S, "WASD")
            .Bind(GameAction.Confirm, PadButton.A, KeyCode.J, KeyCode.Return)
            .Bind(GameAction.Cancel, PadButton.B, KeyCode.X, KeyCode.Backspace, KeyCode.Escape)
            .Bind(GameAction.TabPrev, PadButton.LB, KeyCode.Q)
            .Bind(GameAction.TabNext, PadButton.RB, KeyCode.E)
            .Bind(GameAction.Start, PadButton.Start, KeyCode.Return)
            .Bind(GameAction.Jump, PadButton.A, KeyCode.Space, KeyCode.W)
            .Bind(GameAction.Attack, PadButton.X, KeyCode.J)
            .Bind(GameAction.Skill1, PadButton.LB, KeyCode.Q)
            .Bind(GameAction.Skill2, PadButton.RB, KeyCode.E)
            .Bind(GameAction.Skill3, PadButton.Y, KeyCode.F)
            .Bind(GameAction.Ultimate, PadButton.B, KeyCode.R)
            .Bind(GameAction.OpenMenu, PadButton.Back, KeyCode.Tab);

        public static readonly PlayerInputMap P2 = new PlayerInputMap(1, KeyCode.LeftArrow, KeyCode.RightArrow, KeyCode.UpArrow, KeyCode.DownArrow, "ARROWS")
            .Bind(GameAction.Confirm, PadButton.A, KeyCode.M)
            .Bind(GameAction.Cancel, PadButton.B, KeyCode.N)
            .Bind(GameAction.Start, PadButton.Start, KeyCode.RightShift)
            .Bind(GameAction.Jump, PadButton.A, KeyCode.UpArrow)
            .Bind(GameAction.Attack, PadButton.X, KeyCode.M)
            .Bind(GameAction.Skill1, PadButton.LB, KeyCode.K)
            .Bind(GameAction.Skill2, PadButton.RB, KeyCode.L)
            .Bind(GameAction.Skill3, PadButton.Y, KeyCode.Semicolon)
            .Bind(GameAction.Ultimate, PadButton.B, KeyCode.O)
            .Bind(GameAction.OpenMenu, PadButton.Back);

        static bool axesChecked, axesAvailable;

        /// <summary>True when a gamepad was the last device used; prompts switch to pad buttons.</summary>
        public static bool UsingPad { get; private set; }

        public static void Tick()
        {
            P1.Tick();
            P2.Tick();
            if (P1.AnyPadActivity() || P2.AnyPadActivity()) UsingPad = true;
            else if (Input.anyKeyDown) UsingPad = false;
        }

        public static string AxisName(int player, string axis) => $"P{player + 1} {axis}";

        /// <summary>Reads a per-player axis. Those axes are added to the Input Manager by Editor/InputAxesInstaller.cs.</summary>
        public static float ReadAxis(string name)
        {
            if (!axesChecked)
            {
                axesChecked = true;
                try
                {
                    Input.GetAxisRaw(AxisName(0, "Horizontal"));
                    axesAvailable = true;
                }
                catch (Exception)
                {
                    axesAvailable = false;
                    Debug.LogWarning("WeaponBrawl: gamepad axes are missing, so sticks won't move. Run Tools > Weapon Brawl > Install Gamepad Axes.");
                }
            }
            return axesAvailable ? Input.GetAxisRaw(name) : 0f;
        }

        public static string KeyLabel(KeyCode key)
        {
            switch (key)
            {
                case KeyCode.Return: return "ENTER";
                case KeyCode.Backspace: return "BKSP";
                case KeyCode.Escape: return "ESC";
                case KeyCode.Space: return "SPACE";
                case KeyCode.Semicolon: return ";";
                case KeyCode.RightShift: return "R-SHIFT";
                case KeyCode.UpArrow: return "UP";
                default: return key.ToString().ToUpperInvariant();
            }
        }
    }
}
