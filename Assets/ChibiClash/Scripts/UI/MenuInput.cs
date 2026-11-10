using UnityEngine;
using UnityEngine.InputSystem;

namespace ChibiClash
{
    public enum NavCmd { None, Up, Down, Left, Right, Confirm, Back, PrevTab, NextTab, Alt }

    /// <summary>
    /// Menu navigation from the keyboard and gamepads, polled once per frame (directions auto-repeat while held).
    /// Sources: 0 = P1 keys (WASD, Space/F/J confirm, Esc back, Q/E tabs, R random),
    /// 1 = P2 keys (arrows, Enter confirm, Backspace back, PgUp/PgDn tabs), 2..5 = gamepads in connection order
    /// (stick/d-pad, A/Start confirm, B/Select back, LB/RB tabs, Y random).
    /// </summary>
    public static class MenuInput
    {
        const int Sources = 6;
        const int Cmds = 10;
        const float RepeatDelay = 0.38f, RepeatRate = 0.1f;

        static readonly bool[,] fired = new bool[Sources, Cmds];
        static readonly bool[,] held = new bool[Sources, Cmds];
        static readonly float[,] next = new float[Sources, Cmds];
        static int polledFrame = -1;

        /// <summary>The last menu input came from a gamepad (prompts show pad glyphs).</summary>
        public static bool UsingGamepad { get; private set; }
        /// <summary>Some confirm button is held (press-in animation of the focused widget).</summary>
        public static bool ConfirmHeld { get; private set; }

        public static void Poll()
        {
            if (polledFrame == Time.frameCount) return;
            polledFrame = Time.frameCount;

            bool anyKey = false, anyPad = false;
            var kb = Keyboard.current;
            for (int s = 0; s < 2; s++)
                for (int c = 1; c < Cmds; c++)
                    Step(s, c, kb != null && KeyHeld(kb, s, (NavCmd)c), ref anyKey);

            var pads = Gamepad.all;
            for (int s = 2; s < Sources; s++)
            {
                var pad = s - 2 < pads.Count ? pads[s - 2] : null;
                for (int c = 1; c < Cmds; c++)
                    Step(s, c, pad != null && PadHeld(pad, (NavCmd)c), ref anyPad);
            }

            if (anyPad) UsingGamepad = true;
            else if (anyKey) UsingGamepad = false;

            bool confirm = false;
            for (int s = 0; s < Sources; s++) confirm |= held[s, (int)NavCmd.Confirm];
            ConfirmHeld = confirm;

            var mouse = Mouse.current;
            if (mouse != null && (mouse.delta.ReadValue().sqrMagnitude > 4f || mouse.leftButton.wasPressedThisFrame || mouse.rightButton.wasPressedThisFrame))
                Gui.NavMode = false;
        }

        static void Step(int s, int c, bool down, ref bool any)
        {
            bool was = held[s, c];
            held[s, c] = down;
            fired[s, c] = false;
            if (!down) return;
            float now = Time.unscaledTime;
            if (!was)
            {
                fired[s, c] = true;
                next[s, c] = now + RepeatDelay;
                any = true;
            }
            else if (c <= (int)NavCmd.Right && now >= next[s, c])
            {
                fired[s, c] = true;
                next[s, c] = now + RepeatRate;
            }
        }

        static bool K(Keyboard kb, Key k) => kb[k].isPressed;

        static bool KeyHeld(Keyboard kb, int s, NavCmd c)
        {
            bool p1 = s == 0;
            switch (c)
            {
                case NavCmd.Up: return p1 ? K(kb, Key.W) : K(kb, Key.UpArrow);
                case NavCmd.Down: return p1 ? K(kb, Key.S) : K(kb, Key.DownArrow);
                case NavCmd.Left: return p1 ? K(kb, Key.A) : K(kb, Key.LeftArrow);
                case NavCmd.Right: return p1 ? K(kb, Key.D) : K(kb, Key.RightArrow);
                case NavCmd.Confirm: return p1 ? K(kb, Key.Space) || K(kb, Key.F) || K(kb, Key.J) : K(kb, Key.Enter) || K(kb, Key.NumpadEnter);
                case NavCmd.Back: return p1 ? K(kb, Key.Escape) : K(kb, Key.Backspace);
                case NavCmd.PrevTab: return p1 ? K(kb, Key.Q) : K(kb, Key.PageUp);
                case NavCmd.NextTab: return p1 ? K(kb, Key.E) : K(kb, Key.PageDown);
                case NavCmd.Alt: return p1 ? K(kb, Key.R) : K(kb, Key.Delete);
                default: return false;
            }
        }

        static bool PadHeld(Gamepad pad, NavCmd c)
        {
            var st = pad.leftStick.ReadValue();
            switch (c)
            {
                case NavCmd.Up: return pad.dpad.up.isPressed || st.y > 0.55f;
                case NavCmd.Down: return pad.dpad.down.isPressed || st.y < -0.55f;
                case NavCmd.Left: return pad.dpad.left.isPressed || st.x < -0.55f;
                case NavCmd.Right: return pad.dpad.right.isPressed || st.x > 0.55f;
                case NavCmd.Confirm: return pad.buttonSouth.isPressed || pad.startButton.isPressed;
                case NavCmd.Back: return pad.buttonEast.isPressed || pad.selectButton.isPressed;
                case NavCmd.PrevTab: return pad.leftShoulder.isPressed;
                case NavCmd.NextTab: return pad.rightShoulder.isPressed;
                case NavCmd.Alt: return pad.buttonNorth.isPressed;
                default: return false;
            }
        }

        /// <summary>Fired this frame on any keyboard half or gamepad.</summary>
        public static bool Any(NavCmd c)
        {
            for (int s = 0; s < Sources; s++)
                if (fired[s, (int)c]) return true;
            return false;
        }

        /// <summary>Any menu command fired this frame from any keyboard half or gamepad.</summary>
        public static bool AnyFired()
        {
            for (int c = 1; c < Cmds; c++)
                if (Any((NavCmd)c)) return true;
            return false;
        }

        /// <summary>
        /// Fired this frame for a player: P1 = WASD keys + first gamepad, P2 = arrow keys + second gamepad.
        /// When solo (one human), P1 accepts every source.
        /// </summary>
        public static bool Player(int player, NavCmd c, bool solo)
        {
            if (solo) return player == 0 && Any(c);
            return fired[player, (int)c] || fired[2 + player, (int)c];
        }

        /// <summary>A gamepad's Start was pressed this frame (pause toggle in a match).</summary>
        public static bool PadStartPressed()
        {
            foreach (var pad in Gamepad.all)
                if (pad.startButton.wasPressedThisFrame) return true;
            return false;
        }
    }
}
