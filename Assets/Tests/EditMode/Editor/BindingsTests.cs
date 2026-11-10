using System.Collections.Generic;
using NUnit.Framework;

namespace ChibiClash.Tests
{
    /// <summary>
    /// Story type: Logic (save migration). Evidence: automated EditMode unit test. Gate: BLOCKING.
    /// Bindings version 3 adds "Aim up" and "Special" (decision CH-3) and re-lays out the flat per-profile key list
    /// from 13 to 15 actions per player. Old saves must keep every custom key, and the old default jump key (W / UpArrow)
    /// becomes the aim-up key so that aiming up never jumps.
    /// </summary>
    public class BindingsTests
    {
        const int OldCount = 13;

        /// <summary>A version-2 save with the version-2 defaults, optionally with one custom key.</summary>
        static SaveData V2Save(int customPlayer = -1, int customAction = -1, string customKey = null)
        {
            var p1 = new List<string> { "A", "D", "S", "W", "Space", "Mouse0", "Mouse1", "F", "Q", "U", "I", "O", "R" };
            var p2 = new List<string> { "LeftArrow", "RightArrow", "DownArrow", "UpArrow", "None", "Period", "Slash",
                                        "RightShift", "Comma", "Semicolon", "Quote", "RightBracket", "Enter" };
            if (customPlayer == 0) p1[customAction] = customKey;
            if (customPlayer == 1) p2[customAction] = customKey;
            var d = new SaveData { keybindVersion = 2 };
            d.keybinds = new List<string>();
            d.keybinds.AddRange(p1);
            d.keybinds.AddRange(p2);
            return d;
        }

        static string Get(SaveData d, int player, BindAction a) => d.keybinds[player * Bindings.Count + (int)a];

        [Test]
        public void V2Defaults_UpgradeTo_AimUpOnW_JumpOnSpace()
        {
            // Arrange
            var d = V2Save();

            // Act
            Bindings.Normalize(d);

            // Assert
            Assert.AreEqual(Bindings.Version, d.keybindVersion);
            Assert.AreEqual(2 * Bindings.Count, d.keybinds.Count);
            Assert.AreEqual("W", Get(d, 0, BindAction.Up));
            Assert.AreEqual("Space", Get(d, 0, BindAction.Jump));
            Assert.AreEqual("None", Get(d, 0, BindAction.JumpAlt));
            Assert.AreEqual("E", Get(d, 0, BindAction.Special));
            Assert.AreEqual("UpArrow", Get(d, 1, BindAction.Up));
            Assert.AreEqual("RightCtrl", Get(d, 1, BindAction.Jump));
            Assert.AreEqual("Backslash", Get(d, 1, BindAction.Special));
        }

        [Test]
        public void V2CustomKeys_AreKeptForBothPlayers()
        {
            // Arrange: P2 rebound Skill 2 (index 10). Before the fix of the layout, P2's keys would shift by 2 slots.
            var d = V2Save(1, (int)BindAction.Skill2, "K");

            // Act
            Bindings.Normalize(d);

            // Assert
            Assert.AreEqual("K", Get(d, 1, BindAction.Skill2));
            Assert.AreEqual("Enter", Get(d, 1, BindAction.Ultimate));
            Assert.AreEqual("Period", Get(d, 1, BindAction.Light));
            Assert.AreEqual("R", Get(d, 0, BindAction.Ultimate));
        }

        [Test]
        public void V2CustomJumpKey_IsNotMoved()
        {
            // Arrange: P1 had rebound jump to K.
            var d = V2Save(0, (int)BindAction.Jump, "K");

            // Act
            Bindings.Normalize(d);

            // Assert: a custom jump stays; aim-up gets its default.
            Assert.AreEqual("K", Get(d, 0, BindAction.Jump));
            Assert.AreEqual("W", Get(d, 0, BindAction.Up));
        }

        [Test]
        public void V3Save_NormalizeIsIdempotent()
        {
            // Arrange
            var d = V2Save();
            Bindings.Normalize(d);
            var before = new List<string>(d.keybinds);

            // Act
            Bindings.Normalize(d);

            // Assert
            CollectionAssert.AreEqual(before, d.keybinds);
            Assert.AreEqual(OldCount + 2, Bindings.Count);
        }
    }
}
