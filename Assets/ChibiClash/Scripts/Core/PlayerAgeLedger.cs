using System;
using System.Globalization;
using System.IO;
using System.Text;

namespace ChibiClash
{
    /// <summary>
    /// The player age record for market analysis: one CSV row for every new player, written when the player has
    /// given their age AND created their identity name (onboarding finished). A new identity on this computer
    /// (another account) is a new player and gets its own row.
    ///
    /// File: <c>&lt;persistentDataPath&gt;/player-ages.csv</c>; on Windows
    /// <c>%USERPROFILE%\AppData\LocalLow\&lt;company&gt;\&lt;product&gt;\player-ages.csv</c>
    /// (see <see cref="FilePath"/>). Columns: <see cref="Header"/>. Pure C# with an injectable path (PlayerAgeLedgerTests).
    /// </summary>
    /// <example><code>
    /// var ledger = new PlayerAgeLedger(Path.Combine(Application.persistentDataPath, PlayerAgeLedger.FileName));
    /// ledger.Append(PlayerAgeLedger.Row("Kira", 27, "TH", "WindowsPlayer", Application.version, DateTime.UtcNow));
    /// </code></example>
    public sealed class PlayerAgeLedger
    {
        /// <summary>File name inside the game's data folder.</summary>
        public const string FileName = "player-ages.csv";
        /// <summary>CSV header (fixed columns, so spreadsheets and analysis scripts can rely on them).</summary>
        public const string Header = "utc_time,player_name,age,age_group,country,platform,game_version";

        readonly string _path;

        /// <param name="path">CSV file to append to (its folder is created).</param>
        public PlayerAgeLedger(string path) { _path = path ?? throw new ArgumentNullException(nameof(path)); }

        /// <summary>The file this ledger writes.</summary>
        public string FilePath => _path;

        /// <summary>Append one row (adds the header to a new file). Returns false instead of throwing when the disk refuses.</summary>
        public bool Append(string row)
        {
            try
            {
                string dir = Path.GetDirectoryName(_path);
                if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
                bool fresh = !File.Exists(_path) || new FileInfo(_path).Length == 0;
                File.AppendAllText(_path, (fresh ? Header + "\n" : "") + row + "\n", new UTF8Encoding(false));
                return true;
            }
            catch (Exception) { return false; }   // record keeping must never break the game
        }

        /// <summary>Age group label used in reports.</summary>
        public static string Group(int age)
        {
            if (age <= 0) return "unknown";
            if (age < 13) return "under13";
            if (age < 18) return "13-17";
            if (age < 25) return "18-24";
            if (age < 35) return "25-34";
            if (age < 45) return "35-44";
            return "45+";
        }

        /// <summary>One CSV row. Every field is cleaned so a value can never break the columns (no commas, quotes, newlines or formulas).</summary>
        public static string Row(string playerName, int age, string country, string platform, string version, DateTime utc)
        {
            return string.Join(",",
                utc.ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture),
                Clean(playerName), age.ToString(CultureInfo.InvariantCulture), Group(age),
                Clean(country), Clean(platform), Clean(version));
        }

        // Letters, digits and _ - . # only: no CSV separators and no leading = + @ (spreadsheet formula injection).
        static string Clean(string v)
        {
            if (string.IsNullOrEmpty(v)) return "";
            var sb = new StringBuilder(Math.Min(v.Length, 64));
            foreach (char c in v)
            {
                if (sb.Length >= 64) break;
                if (char.IsLetterOrDigit(c) || c == '-' || c == '_' || c == '.' || c == '#') sb.Append(c);
            }
            while (sb.Length > 0 && (sb[0] == '-' || sb[0] == '#')) sb.Remove(0, 1);
            return sb.ToString();
        }
    }
}
