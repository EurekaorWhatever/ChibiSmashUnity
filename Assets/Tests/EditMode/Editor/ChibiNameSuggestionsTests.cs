// Story type: Logic (random fighter name suggestions) -- evidence: automated EditMode unit test -- gate: BLOCKING
// Spec: production/HANDOFF-2026-11-22.md "NEXT" item 4 (Names: offer random name suggestions). Every suggestion must pass
// the same validation as a typed name (length, characters, blocked words). Random sources are seeded: deterministic.
using System.Collections.Generic;
using ChibiClash.Netcode;
using NUnit.Framework;

namespace ChibiClash.Tests
{
    /// <summary>ChibiNameSuggestions: valid, varied, deterministic for a given source, never the current name.</summary>
    public class ChibiNameSuggestionsTests
    {
        const int Seeds = 500;

        [Test]
        public void Suggest_ManySeeds_AlwaysPassesNameValidation()
        {
            for (int seed = 0; seed < Seeds; seed++)
            {
                string n = ChibiNameSuggestions.Suggest(new System.Random(seed));
                Assert.IsNull(ChibiSocialRules.ValidateName(n), "seed " + seed + ": " + n);
            }
        }

        [Test]
        public void Suggest_SameSeed_SameName()
        {
            Assert.AreEqual(ChibiNameSuggestions.Suggest(new System.Random(42)), ChibiNameSuggestions.Suggest(new System.Random(42)));
        }

        [Test]
        public void Suggest_DifferentSeeds_AreVaried()
        {
            var seen = new HashSet<string>();
            for (int seed = 0; seed < 50; seed++) seen.Add(ChibiNameSuggestions.Suggest(new System.Random(seed)));
            Assert.That(seen.Count, Is.GreaterThanOrEqualTo(40), "50 seeds should give mostly different names");
        }

        [Test]
        public void Suggest_NeverReturnsTheNameToAvoid()
        {
            for (int seed = 0; seed < Seeds; seed++)
            {
                string first = ChibiNameSuggestions.Suggest(new System.Random(seed));
                string next = ChibiNameSuggestions.Suggest(new System.Random(seed), first.ToUpperInvariant());
                Assert.AreNotEqual(first.ToLowerInvariant(), next.ToLowerInvariant(), "seed " + seed);
            }
        }

        [Test]
        public void Suggest_StuckSourceAvoidingItsOnlyName_FallsBackToAValidOtherName()
        {
            string stuck = ChibiNameSuggestions.Suggest(n => 0);
            string other = ChibiNameSuggestions.Suggest(n => 0, stuck);
            Assert.AreNotEqual(stuck, other);
            Assert.IsNull(ChibiSocialRules.ValidateName(other), other);
            string third = ChibiNameSuggestions.Suggest(n => 0, other);
            Assert.AreNotEqual(other, third);
            Assert.IsNull(ChibiSocialRules.ValidateName(third), third);
        }

        [Test]
        public void Suggest_OutOfRangeSource_StillValid()
        {
            Assert.IsNull(ChibiSocialRules.ValidateName(ChibiNameSuggestions.Suggest(n => int.MaxValue)));
            Assert.IsNull(ChibiSocialRules.ValidateName(ChibiNameSuggestions.Suggest(n => -7)));
            Assert.IsNull(ChibiSocialRules.ValidateName(ChibiNameSuggestions.Suggest(n => int.MinValue)));
        }

        [Test]
        public void WordLists_HaveNoBlockedWords()
        {
            foreach (var w in ChibiNameSuggestions.Adjectives) Assert.IsFalse(ChibiSocialRules.ContainsBlockedWord(w), w);
            foreach (var w in ChibiNameSuggestions.Nouns) Assert.IsFalse(ChibiSocialRules.ContainsBlockedWord(w), w);
        }

        [Test]
        public void Suggest_NullSource_Throws()
        {
            Assert.Throws<System.ArgumentNullException>(() => ChibiNameSuggestions.Suggest((System.Func<int, int>)null));
        }
    }
}
