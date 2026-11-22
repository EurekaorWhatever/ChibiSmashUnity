// Story type: Logic (menu / gamepad UX rules and save hardening, review 2026-11-16 batch 3, items 16-18, 22-23) --
// evidence: automated EditMode unit test -- gate: BLOCKING. Screens touched get retained screenshots in
// production/qa/evidence/ separately.
using System.Collections.Generic;
using ChibiClash.Netcode;
using NUnit.Framework;

namespace ChibiClash.Tests
{
    /// <summary>Rebind listener, binding conflicts, pad-only onboarding helpers, profile reset and save hardening.</summary>
    public class MenuPadUxTests
    {
        /// <summary>In-memory ISaveStore so nothing touches PlayerPrefs.</summary>
        sealed class MemoryStore : ISaveStore
        {
            public readonly Dictionary<string, string> Data = new Dictionary<string, string>();
            public string Get(string key) => Data.TryGetValue(key, out var v) ? v : "";
            public void Set(string key, string value) => Data[key] = value;
            public void Flush() { }
        }

        SaveData _oldData;
        MemoryStore _store;

        [SetUp]
        public void SetUp()
        {
            _oldData = Bindings.Data;
            _store = new MemoryStore();
            Bindings.Data = SaveData.Load(_store);
        }

        [TearDown]
        public void TearDown() => Bindings.Data = _oldData;

        // ================================================================== item 16: rebind listener

        static RebindStep Decide(RebindFrame f) => RebindRules.Decide(f, out _);

        [Test]
        public void Rebind_PadBackCancels()
        {
            Assert.AreEqual(RebindStep.Cancel, Decide(new RebindFrame { Elapsed = 1f, PadCancel = true }));
            Assert.AreEqual(RebindStep.Cancel, Decide(new RebindFrame { Elapsed = 1f, Escape = true }), "Esc still cancels");
        }

        [Test]
        public void Rebind_TimesOut()
        {
            Assert.AreEqual(RebindStep.Wait, Decide(new RebindFrame { Elapsed = RebindRules.TimeoutSeconds - 0.1f }));
            Assert.AreEqual(RebindStep.Cancel, Decide(new RebindFrame { Elapsed = RebindRules.TimeoutSeconds + 0.01f }));
        }

        [Test]
        public void Rebind_MouseClickOffTheRow_CancelsInsteadOfBinding()
        {
            var step = RebindRules.Decide(new RebindFrame { Elapsed = 1f, MouseButton = Bindings.MouseLeft, PointerOnRow = false }, out var b);
            Assert.AreEqual(RebindStep.Cancel, step, $"a stray click bound {b}");
        }

        [Test]
        public void Rebind_MouseClickOnTheRow_Binds_KeysBind()
        {
            Assert.AreEqual(RebindStep.Bind, RebindRules.Decide(
                new RebindFrame { Elapsed = 1f, MouseButton = Bindings.MouseRight, PointerOnRow = true }, out var m));
            Assert.AreEqual(Bindings.MouseRight, m);
            Assert.AreEqual(RebindStep.Bind, RebindRules.Decide(new RebindFrame { Elapsed = 0.1f, Key = "K" }, out var k));
            Assert.AreEqual("K", k);
            Assert.AreEqual(RebindStep.Wait, Decide(new RebindFrame { Elapsed = 0.1f, MouseButton = Bindings.MouseLeft, PointerOnRow = true }),
                "the click that opened the listener is ignored");
        }

        [Test]
        public void Rebind_EscapeWinsOverAKeyPressedTheSameFrame()
        {
            Assert.AreEqual(RebindStep.Cancel, Decide(new RebindFrame { Elapsed = 1f, Escape = true, Key = "K" }));
        }

        // ================================================================== item 17: binding conflicts

        [Test]
        public void Set_KeyHeldTwiceInAnOldSave_OnlyTheFirstHolderSwaps()
        {
            // Arrange: an old profile where P1 Dash and Block both ended up on "K".
            Bindings.Data.keybinds[(int)BindAction.Dash] = "K";
            Bindings.Data.keybinds[(int)BindAction.Block] = "K";
            string oldLeft = Bindings.Get(0, BindAction.Left);

            // Act
            Bindings.Set(0, BindAction.Left, "K", _store);

            // Assert: exactly one action got the old key, so it is not duplicated in turn.
            int holders = 0;
            for (int k = 0; k < Bindings.Count; k++) if (Bindings.Get(0, (BindAction)k) == oldLeft) holders++;
            Assert.AreEqual(1, holders);
        }

        [Test]
        public void Set_KeyUsedByOtherPlayer_Swaps()
        {
            string p1Old = Bindings.Get(0, BindAction.Left);               // A
            string p2Left = Bindings.Get(1, BindAction.Left);              // LeftArrow
            var r = Bindings.Set(0, BindAction.Left, p2Left, _store);
            Assert.AreEqual(BindResult.SwappedOtherPlayer, r);
            Assert.AreEqual(p2Left, Bindings.Get(0, BindAction.Left));
            Assert.AreEqual(p1Old, Bindings.Get(1, BindAction.Left), "P2 gets P1's old key");
        }

        [Test]
        public void Set_KeyUsedBySamePlayer_StillSwaps()
        {
            string jump = Bindings.Get(0, BindAction.Jump);
            string oldLeft = Bindings.Get(0, BindAction.Left);
            Assert.AreEqual(BindResult.Swapped, Bindings.Set(0, BindAction.Left, jump, _store));
            Assert.AreEqual(oldLeft, Bindings.Get(0, BindAction.Jump));
        }

        [TestCase("Digit1")] [TestCase("Digit3")] [TestCase("Digit8")] [TestCase("Digit0")]
        [TestCase("Numpad7")] [TestCase("Numpad9")] [TestCase("P")] [TestCase("Minus")]
        public void Set_PoseOrPostureKey_IsRefused(string key)
        {
            string before = Bindings.Get(1, BindAction.Light);
            Assert.IsTrue(Bindings.IsReserved(key));
            Assert.AreEqual(BindResult.Reserved, Bindings.Set(1, BindAction.Light, key, _store));
            Assert.AreEqual(before, Bindings.Get(1, BindAction.Light));
        }

        [Test]
        public void Set_FreeKey_Binds()
        {
            Assert.IsFalse(Bindings.IsReserved("K"));
            Assert.AreEqual(BindResult.Bound, Bindings.Set(0, BindAction.Dash, "K", _store));
            Assert.AreEqual("K", Bindings.Get(0, BindAction.Dash));
        }

        // ================================================================== item 18: pad-only onboarding

        [Test]
        public void StepAge_FromBlank_ThenClampsToValidAges()
        {
            Assert.AreEqual("1", PlayerIdentity.StepAge("", 1));
            Assert.AreEqual("1", PlayerIdentity.StepAge("", -1), "blank never goes below a valid age");
            Assert.AreEqual("30", PlayerIdentity.StepAge("20", 10));
            Assert.AreEqual("1", PlayerIdentity.StepAge("5", -10));
            Assert.AreEqual(PlayerIdentity.MaxAge.ToString(), PlayerIdentity.StepAge("115", 10));
            Assert.AreEqual("13", PlayerIdentity.StepAge("12x", 1), "junk keeps its digits");
        }

        [Test]
        public void DefaultFighterName_IsAlwaysValid_AndVaries()
        {
            var seen = new HashSet<string>();
            for (uint seed = 0; seed < 200; seed++)
            {
                string n = ChibiSocialRules.DefaultFighterName(seed * 2654435761u);
                Assert.IsNull(ChibiSocialRules.ValidateName(n), $"seed {seed}: '{n}' is not a valid name");
                seen.Add(n);
            }
            Assert.Greater(seen.Count, 100, "names vary with the seed");
        }

        // ================================================================== item 22: reset keeps identity

        [Test]
        public void ResetProgress_KeepsIdentity_DropsProgress()
        {
            var d = SaveData.Load(_store);
            d.playerName = "Kira_7"; d.age = 21; d.ageBand = (int)AgeBand.Adult; d.ageRecorded = true;
            d.accountId = "acc-1"; d.accountAsked = true;
            d.profiles.Add(new AccountProfile { key = "acc-1", name = "Kira_7", ageBand = (int)AgeBand.Adult, age = 21, ageRecorded = true });
            d.coins = 5000; d.wins = 9; d.owned.Add("Melee_inferno"); d.settings.cpuDifficulty = 3;
            d.keybinds[0] = "K";
            d.Save(_store);

            var r = d.ResetProgress(_store);
            Assert.AreEqual("Kira_7", r.playerName);
            Assert.AreEqual(21, r.age);
            Assert.AreEqual((int)AgeBand.Adult, r.ageBand);
            Assert.IsTrue(r.ageRecorded, "no second row in the age record");
            Assert.AreEqual("acc-1", r.accountId);
            Assert.IsTrue(r.accountAsked);
            Assert.AreEqual(1, r.profiles.Count);
            Assert.AreEqual(new SaveData().coins, r.coins);
            Assert.AreEqual(0, r.wins);
            Assert.IsFalse(r.owned.Contains("Melee_inferno"));
            Assert.AreEqual(new Settings().cpuDifficulty, r.settings.cpuDifficulty);
            Assert.AreEqual(Bindings.Default(0, BindAction.Left), r.keybinds[0]);

            var reloaded = SaveData.Load(_store);
            Assert.AreEqual("Kira_7", reloaded.playerName, "the reset profile was written to the store");
            Assert.AreEqual(new SaveData().coins, reloaded.coins);
        }

        [Test]
        public void ResetProgress_KeepsTheCameraOffWhenThePlayerTurnedItOff()
        {
            var d = SaveData.Load(_store);
            d.settings.useWebcam = false;                               // Load switched it on by default; the player turned it off
            var r = d.ResetProgress(_store);
            Assert.IsFalse(r.settings.useWebcam, "a reset must not switch the camera back on");
        }

        // ================================================================== item 23: save hardening

        [Test]
        public void Load_NewerSchema_IsNotStampedDown()
        {
            _store.Data[SaveData.Key] = "{\"schemaVersion\":" + (SaveData.SchemaVersion + 5) + "}";
            Assert.AreEqual(SaveData.SchemaVersion + 5, SaveData.Load(_store).schemaVersion);
        }

        [Test]
        public void Equipped_OwnedSkin_IsReturned()
        {
            var d = new SaveData(); d.Repair();
            d.owned.Add("Melee_inferno");
            d.equipped[(int)FighterClass.Melee] = "Melee_inferno";
            Assert.AreEqual("Melee_inferno", d.Equipped(FighterClass.Melee).id);
        }

        [Test]
        public void Load_StampsSchemaVersion()
        {
            _store.Data[SaveData.Key] = "{\"coins\":10}";
            var d = SaveData.Load(_store);
            Assert.AreEqual(SaveData.SchemaVersion, d.schemaVersion);
        }

        [Test]
        public void Repair_NullLists_DoNotThrow()
        {
            var d = new SaveData { owned = null, equipped = null, quests = null, profiles = null, keybinds = null, settings = null };
            Assert.DoesNotThrow(() => d.Repair());
            Assert.IsNotNull(d.owned);
            Assert.IsNotNull(d.profiles);
            Assert.AreEqual(ClassDef.All.Length, d.equipped.Count);
        }

        [Test]
        public void Repair_ClampsCoins()
        {
            var a = new SaveData { coins = -50 }; a.Repair();
            var b = new SaveData { coins = int.MaxValue }; b.Repair();
            Assert.AreEqual(0, a.coins);
            Assert.AreEqual(SaveData.MaxCoins, b.coins);
        }

        [Test]
        public void Progress_NullQuestEntry_DoesNotThrow()
        {
            var d = new SaveData(); d.Repair();
            d.quests.Insert(0, null);
            Assert.DoesNotThrow(() => d.Progress("win", 1, null));
        }

        [Test]
        public void Equipped_NotOwnedOrMissing_FallsBackToDefault()
        {
            var d = new SaveData(); d.Repair();
            d.equipped[(int)FighterClass.Melee] = "Melee_celestial";        // edited save: never pulled
            Assert.AreEqual(Skin.Default(FighterClass.Melee).id, d.Equipped(FighterClass.Melee).id);
            d.equipped.Clear();
            Assert.AreEqual(Skin.Default(FighterClass.Heavy).id, d.Equipped(FighterClass.Heavy).id);
        }

        [Test]
        public void Pull_EmptyPool_DoesNotThrow_AndCostsNothing()
        {
            var d = new SaveData(); d.Repair();
            int before = d.coins;
            List<SaveData.PullResult> res = null;
            Assert.DoesNotThrow(() => res = d.Pull(1, (FighterClass)99, _store));
            Assert.AreEqual(0, res.Count);
            Assert.AreEqual(before, d.coins, "no skin, no charge");
        }
    }
}
