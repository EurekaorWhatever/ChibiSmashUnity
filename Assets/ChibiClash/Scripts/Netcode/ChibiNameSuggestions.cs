using System;
using System.Collections.Generic;

namespace ChibiClash.Netcode
{
    /// <summary>
    /// Random fun fighter names for the name window's SUGGEST button (and gamepad Y). Every suggestion passes
    /// <see cref="ChibiSocialRules.ValidateName"/> (length, characters, blocked words). The random source is injected,
    /// so tests are deterministic. Names are not checked for worldwide uniqueness here: that check happens when the
    /// name is saved (GameManager.ClaimAndSaveName, the Cloud Code claim).
    /// </summary>
    /// <example><code>
    /// var rng = new System.Random();
    /// nameInput = ChibiNameSuggestions.Suggest(rng.Next, nameInput);
    /// </code></example>
    public static class ChibiNameSuggestions
    {
        static readonly string[] AdjectiveWords =
        {
            "Swift", "Brave", "Lucky", "Mighty", "Sunny", "Fizzy", "Bouncy", "Clever", "Jolly", "Zippy", "Cosmic", "Pixel",
            "Sparky", "Turbo", "Fluffy", "Sneaky", "Spicy", "Shiny", "Wild", "Tiny", "Giga", "Dizzy", "Frosty", "Stormy",
            "Noble", "Rapid", "Sleepy", "Happy", "Snappy", "Magic",
        };

        static readonly string[] NounWords =
        {
            "Panda", "Tiger", "Comet", "Fox", "Bunny", "Otter", "Dragon", "Kitten", "Rocket", "Ninja", "Penguin", "Mochi",
            "Dumpling", "Wizard", "Samurai", "Koala", "Falcon", "Noodle", "Muffin", "Badger", "Phoenix", "Hamster",
            "Golem", "Raccoon", "Yeti", "Pudding", "Knight", "Taco", "Sprite", "Waffle",
        };

        /// <summary>The adjective word list (read only, for tests and tools).</summary>
        public static IReadOnlyList<string> Adjectives => AdjectiveWords;
        /// <summary>The noun word list (read only, for tests and tools).</summary>
        public static IReadOnlyList<string> Nouns => NounWords;

        /// <summary>Tries before falling back to a fixed, known-valid name.</summary>
        const int MaxAttempts = 16;

        /// <summary>
        /// A random valid fighter name, different from <paramref name="avoid"/> (case-insensitive), e.g. "ZippyMochi",
        /// "Frosty_Yeti" or "CosmicFox42".
        /// </summary>
        /// <param name="randomBelow">Random source: returns an int in [0, n) for n &gt; 0 (System.Random.Next fits).
        /// Out-of-range values are wrapped, never trusted.</param>
        /// <param name="avoid">The name shown now, so pressing SUGGEST always changes it (null: no constraint).</param>
        public static string Suggest(Func<int, int> randomBelow, string avoid = null)
        {
            if (randomBelow == null) throw new ArgumentNullException(nameof(randomBelow));
            for (int attempt = 0; attempt < MaxAttempts; attempt++)
            {
                string adj = AdjectiveWords[Pick(randomBelow, AdjectiveWords.Length)];
                string noun = NounWords[Pick(randomBelow, NounWords.Length)];
                string n;
                switch (Pick(randomBelow, 3))
                {
                    case 0: n = adj + noun; break;
                    case 1: n = adj + "_" + noun; break;
                    default: n = adj + noun + (10 + Pick(randomBelow, 90)); break;
                }
                if (IsUsable(n, avoid)) return n;
            }
            // Fixed, known-valid fallbacks (two, so one always differs from avoid).
            return IsUsable("BraveFighter10", avoid) ? "BraveFighter10" : "LuckyFighter11";
        }

        /// <summary>A suggestion from a <see cref="System.Random"/> (convenience for the game; tests pass a seeded one).</summary>
        public static string Suggest(Random random, string avoid = null)
        {
            if (random == null) throw new ArgumentNullException(nameof(random));
            return Suggest(random.Next, avoid);
        }

        static bool IsUsable(string n, string avoid) =>
            ChibiSocialRules.ValidateName(n) == null &&
            (avoid == null || !string.Equals(n, avoid.Trim(), StringComparison.OrdinalIgnoreCase));

        /// <summary>The source's value wrapped into [0, n) (a misbehaving source cannot index out of range).</summary>
        static int Pick(Func<int, int> randomBelow, int n)
        {
            int v = randomBelow(n) % n;
            return v < 0 ? v + n : v;
        }
    }
}
