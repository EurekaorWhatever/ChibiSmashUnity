using System.Collections.Generic;

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
        /// <summary>Under 13.</summary>
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

        /// <summary>
        /// Every mode is open to every age (product decision 2026-11-09: the age is asked for market analysis, not to
        /// restrict play). Online needs only an answered age question. Set to true to bring back a 13+ online gate.
        /// </summary>
        public const bool RequireTeenForOnline = false;

        /// <summary>Online play (matchmaking, rooms, friends): open to all ages once the age question was answered.</summary>
        public static bool OnlineAllowed(int storedBand)
        {
            if (NeedsAge(storedBand)) return false;
            return !RequireTeenForOnline || storedBand == (int)AgeBand.Teen || storedBand == (int)AgeBand.Adult;
        }

        /// <summary>The typed age as a number (digits only, 1..MaxAge), or 0 when it is not a plausible age.</summary>
        public static int ParseAge(string typed) => BandForAnswer(typed) == AgeBand.Unknown ? 0 : int.Parse((typed ?? "").Trim());

        /// <summary>Profile key of the no-account player.</summary>
        public const string GuestKey = "guest";
        /// <summary>Profiles remembered per computer (oldest dropped first).</summary>
        public const int MaxProfiles = 8;

        static string KeyFor(string accountId) => string.IsNullOrEmpty(accountId) ? GuestKey : accountId;

        static AccountProfile FindProfile(SaveData s, string key)
        {
            foreach (var p in s.profiles) if (p != null && p.key == key) return p;
            return null;
        }

        /// <summary>
        /// Switch the active identity when the signed-in account changes (sign in, sign out, another Google account).
        /// The current name / age group are stored under the old account; the new account's are loaded. The account's
        /// server-side name ("Kira#4821" -> "Kira") wins, so the same account shows the same name on every computer.
        /// A brand-new account signed in from the guest keeps the guest's age group and name (first link of this device);
        /// switching between two accounts never carries anything over (the new account is named again).
        /// </summary>
        /// <example><code>PlayerIdentity.SwitchProfile(save, account.PlayerId, serverName); save.Save();</code></example>
        public static void SwitchProfile(SaveData s, string newAccountId, string serverName)
        {
            if (s == null) return;
            if (s.profiles == null) s.profiles = new List<AccountProfile>();
            string oldKey = KeyFor(s.accountId), newKey = KeyFor(newAccountId);

            var old = FindProfile(s, oldKey);
            if (old == null) { old = new AccountProfile { key = oldKey }; s.profiles.Add(old); }
            old.name = Netcode.ChibiSocialRules.CleanSavedName(s.playerName);
            old.ageBand = NeedsAge(s.ageBand) ? 0 : s.ageBand;
            old.age = s.age;
            old.ageRecorded = s.ageRecorded;

            var next = FindProfile(s, newKey);
            bool firstLinkFromGuest = next == null && oldKey == GuestKey && newKey != GuestKey;
            s.accountId = newAccountId ?? "";
            s.playerName = next != null ? next.name : firstLinkFromGuest ? old.name : "";
            s.ageBand = next != null ? next.ageBand : firstLinkFromGuest ? old.ageBand : 0;
            s.age = next != null ? next.age : firstLinkFromGuest ? old.age : 0;
            // A different account is a new player (its own row in the age record); the guest's first account is the same person.
            s.ageRecorded = next != null ? next.ageRecorded : firstLinkFromGuest && old.ageRecorded;

            string server = Netcode.ChibiSocialRules.CleanSavedName(Netcode.ChibiSocialRules.BaseName(serverName));
            if (newKey != GuestKey && server.Length > 0) s.playerName = server;

            while (s.profiles.Count > MaxProfiles) s.profiles.RemoveAt(0);
        }
    }
}
