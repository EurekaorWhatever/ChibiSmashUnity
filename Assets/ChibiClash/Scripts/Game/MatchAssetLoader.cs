using System;
using System.Diagnostics;

namespace ChibiClash
{
    /// <summary>
    /// Incremental match preload: both fighters' fight sheets (<see cref="SpriteAnimLibrary.PreloadStep"/>, clips.txt sheet
    /// clips only) and then the shared FX textures (<see cref="ChibiMatch.WarmSharedFx"/>), a real-time budget per
    /// <see cref="Step"/> call. GameManager drives it once per frame while the wipe covers the screen (offline) or during
    /// the handshake / READY intro (online), so no single frame stalls on the whole load. View-side only: it never
    /// touches the simulation. Progress lives in <see cref="SpriteAnimLibrary"/>, so a new loader for the same pair
    /// resumes where an earlier one stopped.
    /// </summary>
    /// <example><code>
    /// var load = new MatchAssetLoader(FighterClass.Melee, FighterClass.Magic);
    /// // once per covered frame:
    /// if (load.Step(MatchAssetLoader.WipeFrameBudget)) StartMatch();
    /// </code></example>
    public sealed class MatchAssetLoader
    {
        /// <summary>Real-time budget per frame while the wipe covers the screen (keeps each covered frame well under 50 ms).</summary>
        public const float WipeFrameBudget = 0.02f;
        /// <summary>Real-time budget per frame while a fight is already running (online READY intro): a small slice.</summary>
        public const float RunningFrameBudget = 0.006f;

        readonly string[] _ids;
        readonly bool _warmFx;
        int _stage;   // 0.._ids.Length-1 = fighters, _ids.Length = FX textures, _ids.Length + 1 = done

        /// <summary>Loads the characters that play <paramref name="c0"/> and <paramref name="c1"/> (CharacterRoster ids).</summary>
        public MatchAssetLoader(FighterClass c0, FighterClass c1, bool warmFx = true)
            : this(CharacterRoster.Of(c0).Id, CharacterRoster.Of(c1).Id, warmFx) { }

        /// <summary>Loads characters <paramref name="a"/> and <paramref name="b"/> (may be equal or empty); FX textures last when <paramref name="warmFx"/>.</summary>
        public MatchAssetLoader(string a, string b, bool warmFx = true) : this(new[] { a, b }, warmFx) { }

        /// <summary>Team play: loads every character in <paramref name="ids"/> (duplicates and empties are skipped).</summary>
        public MatchAssetLoader(string[] ids, bool warmFx = true)
        {
            var list = new System.Collections.Generic.List<string>();
            foreach (var id in ids ?? new string[0])
            {
                if (string.IsNullOrEmpty(id)) continue;
                bool dup = false;
                foreach (var x in list) dup |= string.Equals(x, id, StringComparison.OrdinalIgnoreCase);
                if (!dup) list.Add(id);
            }
            _ids = list.ToArray();
            _warmFx = warmFx;
        }

        /// <summary>True once everything is loaded.</summary>
        public bool Done => _stage >= _ids.Length + 2;

        /// <summary>
        /// Loads for up to <paramref name="budgetSeconds"/> of real time (at least one clip when anything is left, so it
        /// always progresses). Returns true when everything is loaded.
        /// </summary>
        public bool Step(float budgetSeconds)
        {
            long start = Stopwatch.GetTimestamp();
            long limit = SpriteAnimLibrary.BudgetTicks(budgetSeconds);
            int end = _ids.Length + 2;
            while (_stage < end)
            {
                float left = budgetSeconds - (float)((Stopwatch.GetTimestamp() - start) / (double)Stopwatch.Frequency);
                if (_stage < _ids.Length)
                {
                    if (!SpriteAnimLibrary.PreloadStep(_ids[_stage], left)) return false;
                }
                else if (_stage == _ids.Length && _warmFx) ChibiMatch.WarmSharedFx();
                _stage++;
                if (_stage < end && Stopwatch.GetTimestamp() - start >= limit) return false;
            }
            return true;
        }

        /// <summary>Loads everything left now (synchronous). Returns this loader.</summary>
        public MatchAssetLoader Finish()
        {
            while (!Step(float.MaxValue)) { }
            return this;
        }
    }
}
