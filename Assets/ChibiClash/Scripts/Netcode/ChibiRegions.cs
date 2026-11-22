using System;
using System.Collections.Generic;
using System.Globalization;

namespace ChibiClash.Netcode
{
    /// <summary>
    /// Where players are, for matchmaking that prefers the closest opponent (docs/architecture/netcode/online-rooms.md
    /// "Regional matchmaking"). Every country maps to a UN M49 sub-region and a continent; sub-regions also list their
    /// geographic neighbours. Proximity tiers, best first:
    ///   0 same country  (Thailand - Thailand)
    ///   1 same sub-region  (Thailand - Vietnam, Malaysia, Singapore: South-eastern Asia)
    ///   2 neighbouring sub-region  (Thailand - China, Japan, India, Australia)
    ///   3 same continent  (Thailand - Turkey)
    ///   4 anywhere in the world
    ///
    /// Privacy and security: the location is the device's REGION SETTING (a 2-letter country code), never GPS and never
    /// an IP lookup, so no permission is needed and nothing more precise than a country ever leaves the device. A peer's
    /// code is untrusted: anything not in the table counts as "unknown" (tier 4). Faking the code only changes who the
    /// faker is matched with. Pure C#, no Unity API; unit-tested in ChibiRegionsTests.
    /// </summary>
    /// <example><code>
    /// string me = ChibiRegions.DeviceCountry();                 // "TH" (or "" when the device has no region)
    /// int tier = ChibiRegions.Tier(me, lobbyCountry);           // 0..4
    /// int pick = ChibiRegions.PickClosest(me, countries, ChibiRegions.MaxTierAfter(secondsWaited));
    /// </code></example>
    public static class ChibiRegions
    {
        /// <summary>Tier for the same country.</summary>
        public const int TierCountry = 0, TierSubregion = 1, TierNeighbour = 2, TierContinent = 3, TierWorld = 4;
        /// <summary>The search widens one tier every this many seconds of waiting (sub-region at once, worldwide after 3 steps).</summary>
        public const float WidenEverySeconds = 8f;

        // UN M49 sub-regions: id, continent, display name, member countries (ISO 3166-1 alpha-2).
        static readonly string[][] Table =
        {
            new[] { "NAF", "Africa", "Northern Africa", "DZ EG LY MA SD TN EH" },
            new[] { "EAF", "Africa", "Eastern Africa", "IO BI KM DJ ER ET TF KE MG MW MU YT MZ RE RW SC SO SS UG TZ ZM ZW" },
            new[] { "MAF", "Africa", "Middle Africa", "AO CM CF TD CG CD GQ GA ST" },
            new[] { "SAF", "Africa", "Southern Africa", "BW SZ LS NA ZA" },
            new[] { "WAF", "Africa", "Western Africa", "BJ BF CV CI GM GH GN GW LR ML MR NE NG SH SN SL TG" },
            new[] { "CAR", "Americas", "Caribbean", "AI AG AW BS BB BQ VG KY CU CW DM DO GD GP HT JM MQ MS PR BL KN LC MF VC SX TT TC VI" },
            new[] { "CAM", "Americas", "Central America", "BZ CR SV GT HN MX NI PA" },
            new[] { "SAM", "Americas", "South America", "AR BO BV BR CL CO EC FK GF GY PY PE GS SR UY VE" },
            new[] { "NAM", "Americas", "Northern America", "BM CA GL PM US" },
            new[] { "CAS", "Asia", "Central Asia", "KZ KG TJ TM UZ" },
            new[] { "EAS", "Asia", "Eastern Asia", "CN HK MO KP JP MN KR TW" },
            new[] { "SEA", "Asia", "South-eastern Asia", "BN KH ID LA MY MM PH SG TH TL VN" },
            new[] { "SAS", "Asia", "Southern Asia", "AF BD BT IN IR MV NP PK LK" },
            new[] { "WAS", "Asia", "Western Asia", "AM AZ BH CY GE IQ IL JO KW LB OM QA SA PS SY TR AE YE" },
            new[] { "EEU", "Europe", "Eastern Europe", "BY BG CZ HU PL MD RO RU SK UA" },
            new[] { "NEU", "Europe", "Northern Europe", "AX DK EE FO FI GG IS IE IM JE LV LT NO SJ SE GB" },
            new[] { "SEU", "Europe", "Southern Europe", "AL AD BA HR GI GR VA IT MT ME MK PT SM RS SI ES XK" },
            new[] { "WEU", "Europe", "Western Europe", "AT BE FR DE LI LU MC NL CH" },
            new[] { "ANZ", "Oceania", "Australia and New Zealand", "AU CX CC HM NZ NF" },
            new[] { "MEL", "Oceania", "Melanesia", "FJ NC PG SB VU" },
            new[] { "MIC", "Oceania", "Micronesia", "GU KI MH FM NR MP PW UM" },
            new[] { "POL", "Oceania", "Polynesia", "AS CK PF NU PN WS TK TO TV WF" },
        };

        // Geographic neighbours of each sub-region (symmetric; checked by a unit test). Crosses continents where the
        // players are close in practice (North Africa - Southern Europe, South-east Asia - Australia, ...).
        static readonly string[] Neighbours =
        {
            "NAF: WAS SEU WAF EAF MAF",
            "EAF: MAF SAF NAF WAS",
            "MAF: EAF SAF WAF NAF",
            "SAF: EAF MAF",
            "WAF: NAF MAF",
            "CAR: CAM NAM SAM",
            "CAM: NAM CAR SAM",
            "SAM: CAM CAR",
            "NAM: CAM CAR",
            "CAS: EAS SAS WAS EEU",
            "EAS: SEA CAS MIC",
            "SEA: EAS SAS ANZ MEL",
            "SAS: SEA CAS WAS",
            "WAS: SAS CAS NAF EEU SEU EAF",
            "EEU: NEU WEU SEU CAS WAS",
            "NEU: WEU EEU",
            "SEU: WEU EEU NAF WAS",
            "WEU: NEU SEU EEU",
            "ANZ: MEL POL SEA MIC",
            "MEL: ANZ MIC POL SEA",
            "MIC: MEL POL ANZ EAS",
            "POL: MEL MIC ANZ",
        };

        static readonly Dictionary<string, int> CountryToRow = new Dictionary<string, int>(StringComparer.Ordinal);
        static readonly Dictionary<string, HashSet<string>> NeighbourSets = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);

        static ChibiRegions()
        {
            for (int r = 0; r < Table.Length; r++)
                foreach (string cc in Table[r][3].Split(' '))
                    CountryToRow[cc] = r;
            foreach (string line in Neighbours)
            {
                int colon = line.IndexOf(':');
                var set = new HashSet<string>(line.Substring(colon + 1).Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries), StringComparer.Ordinal);
                NeighbourSets[line.Substring(0, colon)] = set;
            }
        }

        /// <summary>Every known country code (tests and tools).</summary>
        public static IEnumerable<string> AllCountries => CountryToRow.Keys;
        /// <summary>Every sub-region id (tests and tools).</summary>
        public static IEnumerable<string> AllSubregions { get { foreach (var row in Table) yield return row[0]; } }
        /// <summary>Neighbour sub-region ids of <paramref name="subregion"/> (empty when unknown).</summary>
        public static IEnumerable<string> NeighboursOf(string subregion) =>
            subregion != null && NeighbourSets.TryGetValue(subregion, out var s) ? s : (IEnumerable<string>)Array.Empty<string>();

        /// <summary>Upper-case a 2-letter code and keep it only if it is a known country; "" otherwise (untrusted input).</summary>
        public static string NormalizeCountry(string code)
        {
            if (code == null) return "";
            string s = code.Trim().ToUpperInvariant();
            return s.Length == 2 && CountryToRow.ContainsKey(s) ? s : "";
        }

        /// <summary>Sub-region id of a country ("TH" -> "SEA"), "" when unknown.</summary>
        public static string Subregion(string country) => CountryToRow.TryGetValue(NormalizeCountry(country), out int r) ? Table[r][0] : "";
        /// <summary>Continent of a country ("TH" -> "Asia"), "" when unknown.</summary>
        public static string Continent(string country) => CountryToRow.TryGetValue(NormalizeCountry(country), out int r) ? Table[r][1] : "";
        /// <summary>Readable sub-region name ("TH" -> "South-eastern Asia"), "" when unknown.</summary>
        public static string SubregionName(string country) => CountryToRow.TryGetValue(NormalizeCountry(country), out int r) ? Table[r][2] : "";

        /// <summary>
        /// How close two players are: 0 same country .. 4 anywhere. Unknown codes on either side are tier 4, so an
        /// unknown or forged location never jumps ahead of real nearby players.
        /// </summary>
        public static int Tier(string countryA, string countryB)
        {
            string a = NormalizeCountry(countryA), b = NormalizeCountry(countryB);
            if (a.Length == 0 || b.Length == 0) return TierWorld;
            if (a == b) return TierCountry;
            string sa = Subregion(a), sb = Subregion(b);
            if (sa == sb) return TierSubregion;
            if (NeighbourSets.TryGetValue(sa, out var n) && n.Contains(sb)) return TierNeighbour;
            return Continent(a) == Continent(b) ? TierContinent : TierWorld;
        }

        /// <summary>
        /// The widest tier accepted after waiting <paramref name="secondsWaited"/>: same sub-region (or country) at first,
        /// then neighbours, continent and finally the whole world, one step every <see cref="WidenEverySeconds"/>.
        /// A player with no known country searches worldwide at once (nobody is "near" them).
        /// </summary>
        public static int MaxTierAfter(float secondsWaited, bool knownCountry = true)
        {
            if (!knownCountry) return TierWorld;
            if (float.IsNaN(secondsWaited) || secondsWaited < 0f) secondsWaited = 0f;
            int steps = (int)(secondsWaited / WidenEverySeconds);
            return Math.Min(TierWorld, TierSubregion + steps);
        }

        /// <summary>
        /// Index of the closest candidate within <paramref name="maxTier"/>, or -1. Ties keep the earlier candidate, so
        /// pass candidates oldest-waiting first (fair: the player who waited longest is matched first).
        /// </summary>
        public static int PickClosest(string myCountry, IReadOnlyList<string> candidateCountries, int maxTier)
        {
            int best = -1, bestTier = int.MaxValue;
            if (candidateCountries == null) return -1;
            for (int i = 0; i < candidateCountries.Count; i++)
            {
                int t = Tier(myCountry, candidateCountries[i]);
                if (t > maxTier || t >= bestTier) continue;
                best = i;
                bestTier = t;
            }
            return best;
        }

        /// <summary>Order of candidate indices to try: closest tier first, oldest first within a tier, only within maxTier.</summary>
        public static List<int> RankCandidates(string myCountry, IReadOnlyList<string> candidateCountries, int maxTier)
        {
            var order = new List<int>();
            if (candidateCountries == null) return order;
            for (int tier = TierCountry; tier <= Math.Min(maxTier, TierWorld); tier++)
                for (int i = 0; i < candidateCountries.Count; i++)
                    if (Tier(myCountry, candidateCountries[i]) == tier) order.Add(i);
            return order;
        }

        /// <summary>
        /// This device's country from the operating system's region setting (no permission, no GPS, no network). Returns
        /// "" when the platform reports no real region (e.g. the invariant culture), which means "search worldwide".
        /// </summary>
        /// <remarks>
        /// NetworkRoomManager switches the process to the invariant culture at startup (Thai Buddhist calendar TLS fix), and
        /// on Mono RegionInfo.CurrentRegion follows the current culture, so reading it afterwards gives no country. The
        /// startup hook therefore calls <see cref="CaptureDeviceCountry(string)"/> FIRST; this returns that captured value.
        /// </remarks>
        public static string DeviceCountry() => _capturedCountry ?? ChooseCountry(ReadRegionIso(), CultureInfo.CurrentCulture.Name);

        // The country read before the culture switch (null = not captured in this Play session). Reset by
        // NetworkRoomManager's SubsystemRegistration hook through ResetCapturedCountry.
        static string _capturedCountry;

        /// <summary>
        /// Remember the device country now, while the OS culture <paramref name="osCultureName"/> (e.g. "th-TH") is still
        /// known. Called by NetworkRoomManager just before it switches to the invariant culture.
        /// </summary>
        public static void CaptureDeviceCountry(string osCultureName) => CaptureDeviceCountry(ReadRegionIso(), osCultureName);

        /// <summary>Capture from explicit values (testable): the region's ISO code wins when it is a known country.</summary>
        public static void CaptureDeviceCountry(string regionIso, string osCultureName) =>
            _capturedCountry = ChooseCountry(regionIso, osCultureName);

        /// <summary>Forget the captured country (each Play session captures it again).</summary>
        public static void ResetCapturedCountry() => _capturedCountry = null;

        /// <summary>
        /// The device country from the region's 2-letter code, else from the culture name's region part ("th-TH" -> "TH",
        /// "zh-Hant-TW" -> "TW"). "" when neither is a known country (e.g. the invariant culture, region "IV").
        /// </summary>
        public static string ChooseCountry(string regionIso, string cultureName)
        {
            string r = NormalizeCountry(regionIso);
            if (r.Length > 0) return r;
            if (string.IsNullOrEmpty(cultureName)) return "";
            int dash = cultureName.LastIndexOf('-');
            return dash < 0 ? "" : NormalizeCountry(cultureName.Substring(dash + 1));
        }

        static string ReadRegionIso()
        {
            try { return RegionInfo.CurrentRegion.TwoLetterISORegionName; }
            catch (Exception) { return ""; }   // some platforms have no region data
        }
    }
}
