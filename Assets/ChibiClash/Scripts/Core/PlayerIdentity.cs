namespace ChibiClash
{
    /// <summary>
    /// Age group the player gave on first launch. Only the GROUP is stored, never the age or a birth date (data
    /// minimisation: the game needs one yes/no decision from it, nothing more).
    /// </summary>
    public enum AgeBand
    {
        /// <summary>Not asked yet (new profile or a profile from before the age question).</summary>
        Unknown = 0,
        /// <summary>Under 13: online friends, invites and matchmaking stay off (child-safety default, e.g. COPPA).</summary>
        Child = 1,
        /// <summary>13 to 17.</summary>
        Teen = 2,
        /// <summary>18 or older.</summary>
        Adult = 3,
    }

    /// <summary>
    /// Player identity rules for first-launch onboarding: age question, then the player name (the name rules live in
    /// Netcode.ChibiSocialRules). Pure C#, unit-tested in PlayerIdentityTests.
    /// </summary>
    /// <example><code>
    /// var band = PlayerIdentity.BandForAge(age);       // Unknown when the answer is not a plausible age
    /// if (band != AgeBand.Unknown) save.ageBand = (int)band;
    /// bool canGoOnline = PlayerIdentity.OnlineAllowed((AgeBand)save.ageBand);
    /// </code></example>
    public static class PlayerIdentity
    {
        /// <summary>Youngest age that may use online friends, invites and matchmaking without a parent's consent.</summary>
        public const int OnlineMinAge = 13;
        /// <summary>Answers outside 1..MaxAge are treated as typos and asked again.</summary>
        public const int MaxAge = 120;

        /// <summary>The age group for an answer, or Unknown when it is not a plausible age.</summary>
        public static AgeBand BandForAge(int age)
        {
            if (age < 1 || age > MaxAge) return AgeBand.Unknown;
            if (age < OnlineMinAge) return AgeBand.Child;
            return age < 18 ? AgeBand.Teen : AgeBand.Adult;
        }

        /// <summary>Parse the typed answer (digits only, surrounding spaces allowed) into an age group (Unknown = ask again).</summary>
        public static AgeBand BandForAnswer(string typed)
        {
            string s = (typed ?? "").Trim();
            if (s.Length == 0 || s.Length > 3) return AgeBand.Unknown;
            int age = 0;
            foreach (char c in s)
            {
                if (c < '0' || c > '9') return AgeBand.Unknown;
                age = age * 10 + (c - '0');
            }
            return BandForAge(age);
        }

        /// <summary>A stored value that is not a known group (never asked, or an edited profile) means: ask.</summary>
        public static bool NeedsAge(int storedBand) => storedBand < (int)AgeBand.Child || storedBand > (int)AgeBand.Adult;

        /// <summary>Online play (Lobby, Relay, friends, invites) is offered only to players 13 and older.</summary>
        public static bool OnlineAllowed(int storedBand) => storedBand == (int)AgeBand.Teen || storedBand == (int)AgeBand.Adult;
    }
}
