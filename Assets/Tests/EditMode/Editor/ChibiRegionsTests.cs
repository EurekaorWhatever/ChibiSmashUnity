// Story type: Logic (regional matchmaking: closest players first) -- evidence: automated EditMode unit test -- gate: BLOCKING
// Spec: docs/architecture/netcode/online-rooms.md ("Regional matchmaking").
using System.Collections.Generic;
using System.Linq;
using ChibiClash.Netcode;
using NUnit.Framework;

namespace ChibiClash.Tests
{
    /// <summary>ChibiRegions: country table, proximity tiers, widening search, candidate ranking, untrusted codes.</summary>
    public class ChibiRegionsTests
    {
        // ================================================================== table

        [Test]
        public void Table_EveryCountryHasOneSubregionAndContinent()
        {
            var all = ChibiRegions.AllCountries.ToList();
            Assert.GreaterOrEqual(all.Count, 240, "every country in the world is covered");
            foreach (var cc in all)
            {
                Assert.AreEqual(2, cc.Length, cc);
                Assert.IsNotEmpty(ChibiRegions.Subregion(cc), cc);
                Assert.IsNotEmpty(ChibiRegions.Continent(cc), cc);
            }
        }

        [Test]
        public void Neighbours_AreSymmetricAndKnown()
        {
            var subs = new HashSet<string>(ChibiRegions.AllSubregions);
            foreach (var s in subs)
                foreach (var n in ChibiRegions.NeighboursOf(s))
                {
                    Assert.IsTrue(subs.Contains(n), s + " -> unknown " + n);
                    CollectionAssert.Contains(ChibiRegions.NeighboursOf(n).ToList(), s, s + " -> " + n + " is not mutual");
                }
        }

        [TestCase("TH", "SEA", "Asia")] [TestCase("US", "NAM", "Americas")] [TestCase("BR", "SAM", "Americas")]
        [TestCase("DE", "WEU", "Europe")] [TestCase("NG", "WAF", "Africa")] [TestCase("AU", "ANZ", "Oceania")]
        [TestCase("JP", "EAS", "Asia")] [TestCase("IN", "SAS", "Asia")] [TestCase("EG", "NAF", "Africa")]
        public void Subregion_KnownCountries(string cc, string sub, string continent)
        {
            Assert.AreEqual(sub, ChibiRegions.Subregion(cc));
            Assert.AreEqual(continent, ChibiRegions.Continent(cc));
        }

        // ================================================================== tiers (Thailand examples from the brief)

        [TestCase("TH", "TH", ChibiRegions.TierCountry)]
        [TestCase("TH", "VN", ChibiRegions.TierSubregion)]
        [TestCase("TH", "MY", ChibiRegions.TierSubregion)]
        [TestCase("TH", "CN", ChibiRegions.TierNeighbour)]
        [TestCase("TH", "IN", ChibiRegions.TierNeighbour)]
        [TestCase("TH", "AU", ChibiRegions.TierNeighbour)]
        [TestCase("TH", "TR", ChibiRegions.TierContinent)]
        [TestCase("TH", "US", ChibiRegions.TierWorld)]
        [TestCase("FR", "ES", ChibiRegions.TierNeighbour)]
        [TestCase("US", "CA", ChibiRegions.TierSubregion)]
        [TestCase("US", "MX", ChibiRegions.TierNeighbour)]
        [TestCase("US", "AR", ChibiRegions.TierContinent)]
        [TestCase("ma", "es", ChibiRegions.TierNeighbour)]
        public void Tier_ClosenessOrder(string a, string b, int expected)
        {
            Assert.AreEqual(expected, ChibiRegions.Tier(a, b));
            Assert.AreEqual(expected, ChibiRegions.Tier(b, a), "tiers are symmetric");
        }

        [TestCase("")] [TestCase(null)] [TestCase("XX")] [TestCase("THA")] [TestCase("<b>")] [TestCase("T\nH")]
        public void Tier_UnknownOrForgedCode_IsWorld(string forged)
        {
            Assert.AreEqual(ChibiRegions.TierWorld, ChibiRegions.Tier("TH", forged));
            Assert.AreEqual("", ChibiRegions.NormalizeCountry(forged));
        }

        // ================================================================== widening

        [Test]
        public void MaxTierAfter_WidensStepByStep_ThenWorldwide()
        {
            float step = ChibiRegions.WidenEverySeconds;
            Assert.AreEqual(ChibiRegions.TierSubregion, ChibiRegions.MaxTierAfter(0f));
            Assert.AreEqual(ChibiRegions.TierSubregion, ChibiRegions.MaxTierAfter(step - 0.1f));
            Assert.AreEqual(ChibiRegions.TierNeighbour, ChibiRegions.MaxTierAfter(step));
            Assert.AreEqual(ChibiRegions.TierContinent, ChibiRegions.MaxTierAfter(step * 2f));
            Assert.AreEqual(ChibiRegions.TierWorld, ChibiRegions.MaxTierAfter(step * 3f));
            Assert.AreEqual(ChibiRegions.TierWorld, ChibiRegions.MaxTierAfter(10000f));
        }

        [Test]
        public void MaxTierAfter_UnknownCountryOrBadClock_Safe()
        {
            Assert.AreEqual(ChibiRegions.TierWorld, ChibiRegions.MaxTierAfter(0f, knownCountry: false));
            Assert.AreEqual(ChibiRegions.TierSubregion, ChibiRegions.MaxTierAfter(-50f));
            Assert.AreEqual(ChibiRegions.TierSubregion, ChibiRegions.MaxTierAfter(float.NaN));
        }

        // ================================================================== picking

        [Test]
        public void PickClosest_PrefersSameCountry_OverOlderFartherPlayers()
        {
            var waiting = new List<string> { "US", "VN", "TH", "TH" };   // oldest first

            Assert.AreEqual(2, ChibiRegions.PickClosest("TH", waiting, ChibiRegions.TierWorld), "first Thai player");
        }

        [Test]
        public void PickClosest_RespectsTheCurrentSearchRadius()
        {
            var waiting = new List<string> { "US", "JP" };

            Assert.AreEqual(-1, ChibiRegions.PickClosest("TH", waiting, ChibiRegions.TierSubregion), "nobody near yet");
            Assert.AreEqual(1, ChibiRegions.PickClosest("TH", waiting, ChibiRegions.TierNeighbour), "Japan once widened");
            Assert.AreEqual(1, ChibiRegions.PickClosest("TH", waiting, ChibiRegions.TierWorld), "still the closer one");
        }

        [Test]
        public void RankCandidates_ClosestFirst_OldestFirstWithinATier()
        {
            var waiting = new List<string> { "BR", "SG", "TH", "XX", "VN", "TH" };

            var order = ChibiRegions.RankCandidates("TH", waiting, ChibiRegions.TierWorld);

            CollectionAssert.AreEqual(new[] { 2, 5, 1, 4, 0, 3 }, order);
        }

        [Test]
        public void RankCandidates_EmptyOrNull_NoCandidates()
        {
            Assert.IsEmpty(ChibiRegions.RankCandidates("TH", new List<string>(), ChibiRegions.TierWorld));
            Assert.IsEmpty(ChibiRegions.RankCandidates("TH", null, ChibiRegions.TierWorld));
            Assert.AreEqual(-1, ChibiRegions.PickClosest("TH", null, ChibiRegions.TierWorld));
        }

        [Test]
        public void DeviceCountry_IsKnownCodeOrEmpty()
        {
            string cc = ChibiRegions.DeviceCountry();
            Assert.IsTrue(cc.Length == 0 || ChibiRegions.Subregion(cc).Length > 0, cc);
        }
    }
}
