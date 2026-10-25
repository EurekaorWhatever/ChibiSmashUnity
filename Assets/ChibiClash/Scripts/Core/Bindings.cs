using System;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.InputSystem.Controls;

namespace ChibiClash
{
    public enum BindAction { Left, Right, Down, Jump, JumpAlt, Light, Heavy, Block, Dash, Skill1, Skill2, Skill3, Ultimate }

    /// <summary>
    /// Rebindable controls for both players, stored in the save profile as strings: a keyboard Key enum name
    /// ("A", "Space", "RightShift"...) or a mouse button ("Mouse0" left, "Mouse1" right, "Mouse2" middle).
    /// A missing or unreadable entry falls back to the default, so old saves keep working.
    /// </summary>
    public static class Bindings
    {
        public const int Count = 13;
        public const int Version = 2;           // 2 = light/heavy default to the mouse

        public const string MouseLeft = "Mouse0", MouseRight = "Mouse1", MouseMiddle = "Mouse2";

        public static readonly string[] Names =
        {
            "Move left", "Move right", "Drop / fast-fall", "Jump", "Jump (alt)", "Light attack", "Heavy attack",
            "Block", "Dash", "Skill 1", "Skill 2", "Skill 3", "Ultimate"
        };

        static readonly string[][] Defaults =
        {
            new[] { "A", "D", "S", "W", "Space", MouseLeft, MouseRight, "F", "Q", "U", "I", "O", "R" },
            new[] { "LeftArrow", "RightArrow", "DownArrow", "UpArrow", "None", "Period", "Slash",
                    "RightShift", "Comma", "Semicolon", "Quote", "RightBracket", "Enter" },
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
        static bool IsValid(string s) => IsMouse(s) || Enum.TryParse(s, out Key _);

        static ButtonControl Control(string s)
        {
            if (IsMouse(s))
            {
                var m = Mouse.current;
                if (m == null) return null;
                return s == MouseLeft ? m.leftButton : s == MouseRight ? m.rightButton : m.middleButton;
            }
            var kb = Keyboard.current;
            if (kb == null || !Enum.TryParse(s, out Key k) || k == Key.None) return null;
            return kb[k];
        }

        public static bool Held(int player, BindAction a) => Control(Get(player, a))?.isPressed ?? false;
        public static bool Pressed(int player, BindAction a) => Control(Get(player, a))?.wasPressedThisFrame ?? false;

        /// <summary>Fill missing entries with defaults and upgrade old saves (light/heavy move from J/K to the mouse).</summary>
        public static void Normalize(SaveData d)
        {
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
