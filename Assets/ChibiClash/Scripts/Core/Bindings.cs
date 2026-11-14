using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.InputSystem.Controls;

namespace ChibiClash
{
    /// <summary>Rebindable actions. New actions are appended (Up, Special: version 3) so saved indices stay meaningful.</summary>
    public enum BindAction { Left, Right, Down, Jump, JumpAlt, Light, Heavy, Block, Dash, Skill1, Skill2, Skill3, Ultimate, Up, Special }

    /// <summary>
    /// Rebindable controls for both players, stored in the save profile as strings: a keyboard Key enum name
    /// ("A", "Space", "RightShift"...) or a mouse button ("Mouse0" left, "Mouse1" right, "Mouse2" middle).
    /// A missing or unreadable entry falls back to the default, so old saves keep working.
    /// Version 3 (character movesets, decision CH-3): "Aim up" (held direction for up-tilt / up-smash / up-special,
    /// Brawlhalla-style: W aims, Space jumps) and "Special" (the B button).
    /// </summary>
    public static class Bindings
    {
        public const int Count = 15;
        /// <summary>2 = light/heavy default to the mouse. 3 = Up + Special added; W becomes aim-up, Space jumps.</summary>
        public const int Version = 3;
        /// <summary>Actions per player before version 3 (layout of older saves).</summary>
        const int CountV2 = 13;

        public const string MouseLeft = "Mouse0", MouseRight = "Mouse1", MouseMiddle = "Mouse2";

        public static readonly string[] Names =
        {
            "Move left", "Move right", "Drop / fast-fall", "Jump", "Jump (alt)", "Light attack", "Heavy attack",
            "Block", "Dash", "Skill 1", "Skill 2", "Skill 3", "Ultimate", "Aim up", "Special"
        };

        static readonly string[][] Defaults =
        {
            new[] { "A", "D", "S", "Space", "None", MouseLeft, MouseRight, "F", "Q", "U", "I", "O", "R", "W", "E" },
            new[] { "LeftArrow", "RightArrow", "DownArrow", "RightCtrl", "None", "Period", "Slash",
                    "RightShift", "Comma", "Semicolon", "Quote", "RightBracket", "Enter", "UpArrow", "Backslash" },
        };

        /// <summary>The active profile; set by GameManager.</summary>
        public static SaveData Data;

        static int Index(int player, BindAction a) => player * Count + (int)a;

        public static string Default(int player, BindAction a) => Defaults[player][(int)a];

        public static string Get(int player, BindAction a)
        {
            var list = Data?.keybinds;
            int i = Index(player, a);
            if (list != null && i < list.Count && IsValid(list[i])) return list[i];
            return Default(player, a);
        }

        static bool IsMouse(string s) => s == MouseLeft || s == MouseRight || s == MouseMiddle;
        static bool IsValid(string s) => IsMouse(s) || TryKey(s, out _);

        /// <summary>
        /// Binding string -> parsed <see cref="Key"/> as int, or -1 when the string is not a Key name.
        /// <see cref="Held"/> / <see cref="Pressed"/> run ~100 times a frame, and Enum.TryParse allocates on every call
        /// (~2.7 KB/frame measured 2026-11-14), so each distinct string is parsed once. The mapping is pure, so it never
        /// needs invalidating when bindings change. Main thread only.
        /// </summary>
        static readonly Dictionary<string, int> parsedKeys = new Dictionary<string, int>();

        /// <summary>Same result as <c>Enum.TryParse(s, out Key k)</c>, without allocating after the first call per string.</summary>
        /// <example><code>if (TryKey("Space", out Key k)) control = Keyboard.current[k];</code></example>
        static bool TryKey(string s, out Key k)
        {
            k = Key.None;
            if (s == null) return false;
            if (!parsedKeys.TryGetValue(s, out int v))
            {
                v = Enum.TryParse(s, out Key p) ? (int)p : -1;
                parsedKeys[s] = v;
            }
            if (v < 0) return false;
            k = (Key)v;
            return true;
        }

        static ButtonControl Control(string s)
        {
            if (IsMouse(s))
            {
                var m = Mouse.current;
                if (m == null) return null;
                return s == MouseLeft ? m.leftButton : s == MouseRight ? m.rightButton : m.middleButton;
            }
            var kb = Keyboard.current;
            if (kb == null || !TryKey(s, out Key k) || k == Key.None) return null;
            return kb[k];
        }

        public static bool Held(int player, BindAction a) => Control(Get(player, a))?.isPressed ?? false;
        public static bool Pressed(int player, BindAction a) => Control(Get(player, a))?.wasPressedThisFrame ?? false;

        /// <summary>Fill missing entries with defaults and upgrade old saves (light/heavy move from J/K to the mouse).</summary>
        public static void Normalize(SaveData d)
        {
            if (d.keybindVersion < 3 && d.keybinds.Count > 0) UpgradeToV3(d);
            while (d.keybinds.Count < 2 * Count)
            {
                int i = d.keybinds.Count;
                d.keybinds.Add(Defaults[i / Count][i % Count]);
            }
            if (d.keybindVersion < 2)
            {
                if (d.keybinds[Index(0, BindAction.Light)] == "J") d.keybinds[Index(0, BindAction.Light)] = MouseLeft;
                if (d.keybinds[Index(0, BindAction.Heavy)] == "K") d.keybinds[Index(0, BindAction.Heavy)] = MouseRight;
            }
            d.keybindVersion = Version;
        }

        /// <summary>
        /// v2 saves hold 13 actions per player in one flat list. Re-lay them out at 15 per player (keeping every custom
        /// key), then move the old default jump key to the new aim-up action: P1 W/Space becomes aim-up W + jump Space,
        /// P2 UpArrow becomes aim-up UpArrow + jump RightCtrl. Custom jump keys are kept as they were.
        /// </summary>
        static void UpgradeToV3(SaveData d)
        {
            var old = new System.Collections.Generic.List<string>(d.keybinds);
            d.keybinds.Clear();
            for (int p = 0; p < 2; p++)
            {
                for (int a = 0; a < Count; a++)
                {
                    int src = p * CountV2 + a;
                    d.keybinds.Add(a < CountV2 && src < old.Count ? old[src] : Defaults[p][a]);
                }
                int jump = Index(p, BindAction.Jump), alt = Index(p, BindAction.JumpAlt), up = Index(p, BindAction.Up);
                if (p == 0 && d.keybinds[jump] == "W")
                {
                    // Jump moves to the alt key if one is bound (default Space), else to Space; the alt slot is freed.
                    string a2 = d.keybinds[alt];
                    d.keybinds[jump] = string.IsNullOrEmpty(a2) || a2 == "None" || a2 == "W" ? "Space" : a2;
                    d.keybinds[alt] = "None";
                    d.keybinds[up] = "W";
                }
                else if (p == 1 && d.keybinds[jump] == "UpArrow")
                {
                    d.keybinds[jump] = "RightCtrl";
                    d.keybinds[up] = "UpArrow";
                }
            }
        }

        public static void ResetDefaults(SaveData d)
        {
            d.keybinds.Clear();
            Normalize(d);
        }

        /// <summary>Bind a key or mouse button. If another action of the same player already uses it, the two swap.</summary>
        public static void Set(int player, BindAction a, string binding)
        {
            if (Data == null) return;
            Normalize(Data);
            string old = Get(player, a);
            for (int k = 0; k < Count; k++)
            {
                var other = (BindAction)k;
                if (other != a && binding != "None" && Get(player, other) == binding)
                    Data.keybinds[Index(player, other)] = old;
            }
            Data.keybinds[Index(player, a)] = binding;
            Data.Save();
        }

        public static string Label(int player, BindAction a) => BindingLabel(Get(player, a));

        public static string BindingLabel(string s)
        {
            switch (s)
            {
                case MouseLeft: return "Mouse L";
                case MouseRight: return "Mouse R";
                case MouseMiddle: return "Mouse M";
            }
            return Enum.TryParse(s, out Key k) ? KeyLabel(k) : s;
        }

        public static string KeyLabel(Key k)
        {
            switch (k)
            {
                case Key.None: return "-";
                case Key.LeftArrow: return "←";
                case Key.RightArrow: return "→";
                case Key.UpArrow: return "↑";
                case Key.DownArrow: return "↓";
                case Key.LeftShift: return "L-Shift";
                case Key.RightShift: return "R-Shift";
                case Key.LeftCtrl: return "L-Ctrl";
                case Key.RightCtrl: return "R-Ctrl";
                case Key.LeftAlt: return "L-Alt";
                case Key.RightAlt: return "R-Alt";
                case Key.Comma: return ",";
                case Key.Period: return ".";
                case Key.Slash: return "/";
                case Key.Backslash: return "\\";
                case Key.Semicolon: return ";";
                case Key.Quote: return "'";
                case Key.LeftBracket: return "[";
                case Key.RightBracket: return "]";
                case Key.Minus: return "-";
                case Key.Equals: return "=";
                case Key.Backquote: return "`";
                case Key.Enter: return "Enter";
                case Key.NumpadEnter: return "Num-Enter";
            }
            string s = k.ToString();
            if (s.StartsWith("Digit")) return s.Substring(5);
            if (s.StartsWith("Numpad")) return "Num" + s.Substring(6);
            return s;
        }
    }
}
