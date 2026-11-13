// Story type: Logic (fight-view clip mapping) -- evidence: automated EditMode unit test -- gate: BLOCKING
// Source: fight view shows moveset-sheet clips only (Idle_Sheet, Sheet_*, Move_*), never cutouts or the body drawing.
using System;
using System.Collections.Generic;
using ChibiClash.Netcode;
using NUnit.Framework;
using UnityEngine;

namespace ChibiClash.Tests
{
    /// <summary>FightClips: every fight state and every sim move resolves to a moveset-sheet clip for all 5 characters.</summary>
    public class FightClipsTests
    {
        static readonly FighterClass[] Classes =
            { FighterClass.Melee, FighterClass.Heavy, FighterClass.Ranged, FighterClass.Magic, FighterClass.Polearm };

        static readonly FightAnimState[] AllStates = (FightAnimState[])Enum.GetValues(typeof(FightAnimState));

        /// <summary>Clip availability from the real Resources files (texture present) of one character.</summary>
        static Func<string, bool> FilesOf(string characterId) =>
            name => Resources.Load<Texture2D>("Animations/" + characterId + "/" + name) != null;

        static bool IsCutoutName(string name)
        {
            foreach (var c in SpriteAnimLibrary.Clips) if (string.Equals(c, name, StringComparison.OrdinalIgnoreCase)) return true;
            return false;
        }

        [Test]
        public void FightIdle_AllCharacters_ResolvesToIdleSheet()
        {
            foreach (var cls in Classes)
            {
                // Arrange
                string id = CharacterRoster.Of(cls).Id;
                var has = FilesOf(id);

                // Act
                string idle = FightClips.Resolve(FightAnimState.Idle, has);
                string intro = FightClips.Resolve(FightAnimState.Intro, has);
                string respawn = FightClips.Resolve(FightAnimState.Respawn, has);

                // Assert
                Assert.AreEqual(SpriteAnimLibrary.IdleSheet, idle, id + " idle");
                Assert.AreEqual(SpriteAnimLibrary.IdleSheet, intro, id + " READY intro");
                Assert.AreEqual(SpriteAnimLibrary.IdleSheet, respawn, id + " respawn hover");
            }
        }

        [Test]
        public void AllFightStates_AllCharacters_ResolveToExistingSheetClipNeverCutout()
        {
            foreach (var cls in Classes)
            {
                string id = CharacterRoster.Of(cls).Id;
                var has = FilesOf(id);
                string ultMove = SpriteAnimLibrary.MovePrefix + FightClips.CharKey(id) + "_" + FightClips.UltimateFallbackRow;
                foreach (var s in AllStates)
                {
                    // Act
                    string clip = FightClips.Resolve(s, has, ultMove);

                    // Assert
                    Assert.IsNotNull(clip, id + " " + s + " resolves to nothing");
                    Assert.IsTrue(FightClips.IsSheetClip(clip), id + " " + s + " -> " + clip + " is not a sheet clip");
                    Assert.IsFalse(IsCutoutName(clip), id + " " + s + " -> cutout " + clip);
                    Assert.IsTrue(has(clip), id + " " + s + " -> " + clip + " has no file");
                }
            }
        }

        [Test]
        public void AllFightStates_OnlyCutoutsAvailable_NeverReturnCutout()
        {
            // Arrange: a character that only has cutouts (and the body drawing name)
            var cutouts = new HashSet<string>(SpriteAnimLibrary.Clips, StringComparer.OrdinalIgnoreCase) { "Body" };
            Func<string, bool> has = cutouts.Contains;

            foreach (var s in AllStates)
            {
                // Act
                string clip = FightClips.Resolve(s, has, "Swing");
                string move = FightClips.ResolveMove("kaia_ftilt", "swing", "kaia", has);

                // Assert
                Assert.IsNull(clip, s + " fell back to a non-sheet clip");
                Assert.IsNull(move, "move fell back to a non-sheet clip");
            }
        }

        [Test]
        public void AllFightStates_EverythingAvailable_ReturnContractClip()
        {
            // Arrange
            Func<string, bool> all = _ => true;
            var expected = new Dictionary<FightAnimState, string>
            {
                { FightAnimState.Idle, FightClips.Idle }, { FightAnimState.Run, FightClips.Run },
                { FightAnimState.Rise, FightClips.Jump }, { FightAnimState.Fall, FightClips.Fall },
                { FightAnimState.Guard, FightClips.Guard }, { FightAnimState.Hitstun, FightClips.Hit },
                { FightAnimState.Launched, FightClips.Launch }, { FightAnimState.KO, FightClips.KO },
                { FightAnimState.Ultimate, FightClips.Ultimate }, { FightAnimState.Victory, FightClips.Victory },
                { FightAnimState.Intro, FightClips.Idle }, { FightAnimState.Respawn, FightClips.Idle },
            };

            foreach (var s in AllStates)
            {
                // Act
                string clip = FightClips.Resolve(s, all, "Move_kaia_fsmash");

                // Assert
                Assert.AreEqual(expected[s], clip, s.ToString());
            }
        }

        [Test]
        public void SimMoves_AllCharacters_ResolveToTheirOwnMoveSheet()
        {
            foreach (var cls in Classes)
            {
                string id = CharacterRoster.Of(cls).Id;
                var has = FilesOf(id);
                int c = (int)cls;
                for (int k = 0; k < ChibiMoveInfo.Count(c); k++)
                {
                    // Arrange
                    var info = ChibiMoveInfo.Get(c, ChibiMovesetData.FirstNewSlot + k);

                    // Act
                    string clip = FightClips.ResolveMove(info.Id, info.AnimKind, FightClips.CharKey(id), has);

                    // Assert
                    Assert.AreEqual(SpriteAnimLibrary.MovePrefix + info.Id, clip, id + " move " + info.Id);
                }
            }
        }

        [Test]
        public void LegacySlots_AllCharacters_ResolveToExistingMoveSheet()
        {
            int[] slots = { 0, 1, 2, 3, 4, ChibiSimConst.PoseSlot };
            foreach (var cls in Classes)
            {
                string id = CharacterRoster.Of(cls).Id;
                string key = FightClips.CharKey(id);
                var has = FilesOf(id);
                foreach (int slot in slots)
                {
                    // Act
                    string clip = FightClips.ResolveMove(FightClips.LegacyMoveId(cls, slot, key), null, key, has);

                    // Assert
                    Assert.IsNotNull(clip, id + " slot " + slot);
                    StringAssert.StartsWith(SpriteAnimLibrary.MovePrefix + key + "_", clip, id + " slot " + slot);
                    Assert.IsTrue(has(clip), id + " slot " + slot + " -> " + clip);
                }
                Assert.IsNull(FightClips.LegacyMoveId(cls, 5, key), id + " ultimate plays the Ultimate state clip");
            }
        }

        [Test]
        public void MissingMoveSheet_FallsBackToNearestMoveSheet()
        {
            // Arrange
            var files = new HashSet<string> { "Idle_Sheet", "Move_ren_nspec_lv1", "Move_kaia_ftilt", "Move_kaia_dtilt" };
            Func<string, bool> has = files.Contains;

            // Act / Assert
            Assert.AreEqual("Move_ren_nspec_lv1", FightClips.ResolveMove("ren_nspec_lv2", "shoot", "ren", has));
            Assert.AreEqual("Move_kaia_dtilt", FightClips.ResolveMove("kaia_new", "lowsweep", "kaia", has));
            Assert.AreEqual("Move_kaia_ftilt", FightClips.ResolveMove("kaia_jab", "jab", "kaia", has));
            Assert.AreEqual("Idle_Sheet", FightClips.ResolveMove("taro_jab", "jab", "taro", has));
        }

        [Test]
        public void StateOf_PriorityTable_PicksExpectedState()
        {
            var still = Vector2.zero;
            Assert.AreEqual(FightAnimState.Victory, FightClips.StateOf(true, FightFlags.Out, true, false, still, FightAnimState.Idle));
            Assert.AreEqual(FightAnimState.KO, FightClips.StateOf(false, FightFlags.Out | FightFlags.Hitstun, false, false, still, FightAnimState.Idle));
            Assert.AreEqual(FightAnimState.Intro, FightClips.StateOf(false, FightFlags.Intro, true, false, still, FightAnimState.Idle));
            Assert.AreEqual(FightAnimState.Respawn, FightClips.StateOf(false, FightFlags.Hover, false, false, still, FightAnimState.Idle));
            Assert.AreEqual(FightAnimState.Launched, FightClips.StateOf(false, FightFlags.Hitstun | FightFlags.Launched, false, false, still, FightAnimState.Idle));
            Assert.AreEqual(FightAnimState.Hitstun, FightClips.StateOf(false, FightFlags.Hitstun, true, true, still, FightAnimState.Idle));
            Assert.AreEqual(FightAnimState.Guard, FightClips.StateOf(false, FightFlags.None, true, true, new Vector2(3f, 0f), FightAnimState.Idle));
            Assert.AreEqual(FightAnimState.Rise, FightClips.StateOf(false, FightFlags.None, false, false, new Vector2(0f, 5f), FightAnimState.Idle));
            Assert.AreEqual(FightAnimState.Fall, FightClips.StateOf(false, FightFlags.None, false, false, new Vector2(0f, -5f), FightAnimState.Rise));
            Assert.AreEqual(FightAnimState.Rise, FightClips.StateOf(false, FightFlags.None, false, false, still, FightAnimState.Rise));
            Assert.AreEqual(FightAnimState.Fall, FightClips.StateOf(false, FightFlags.None, false, false, still, FightAnimState.Run));
            Assert.AreEqual(FightAnimState.Run, FightClips.StateOf(false, FightFlags.None, true, false, new Vector2(-3f, 0f), FightAnimState.Idle));
            Assert.AreEqual(FightAnimState.Idle, FightClips.StateOf(false, FightFlags.None, true, false, still, FightAnimState.Run));
        }

        [Test]
        public void SheetOnlyLookup_CutoutNames_ReturnNull()
        {
            foreach (var cls in Classes)
            {
                // Arrange
                string id = CharacterRoster.Of(cls).Id;

                // Act / Assert
                Assert.IsNotNull(SpriteAnimLibrary.GetSheet(id, SpriteAnimLibrary.IdleSheet), id + " Idle_Sheet");
                foreach (var cut in SpriteAnimLibrary.Clips)
                    Assert.IsNull(SpriteAnimLibrary.GetSheet(id, cut), id + " cutout " + cut + " leaked through GetSheet");
            }
        }

        /// <summary>Fixed view tick for the EditMode visual tests (Time.deltaTime is 0 there).</summary>
        const float TestDt = 1f / 60f;

        [Test]
        public void FightVisual_AllCharactersAllStates_ShowsTheStatesContractClip()
        {
            // (flags, grounded) -> the fight state the visual must be in; velocity (2, 0) grounded / (2, 4) airborne
            var cases = new (FightFlags flags, bool grounded, FightAnimState state)[]
            {
                (FightFlags.Intro, true, FightAnimState.Intro), (FightFlags.Intro, false, FightAnimState.Intro),
                (FightFlags.None, true, FightAnimState.Run), (FightFlags.None, false, FightAnimState.Rise),
                (FightFlags.Hitstun, true, FightAnimState.Hitstun), (FightFlags.Hitstun, false, FightAnimState.Hitstun),
                (FightFlags.Hitstun | FightFlags.Launched, true, FightAnimState.Launched),
                (FightFlags.Hitstun | FightFlags.Launched, false, FightAnimState.Launched),
                (FightFlags.Out, true, FightAnimState.KO), (FightFlags.Out, false, FightAnimState.KO),
                (FightFlags.Hover, true, FightAnimState.Respawn), (FightFlags.Hover, false, FightAnimState.Respawn),
            };
            foreach (var cls in Classes)
            {
                // Arrange
                var go = new GameObject("FightClipsTests");
                try
                {
                    var v = go.AddComponent<FighterVisual>();
                    v.Build(ClassDef.Get(cls), Skin.Default(cls), 10, WeaponDef.Get(cls, 0));
                    v.EnableSheetOnly();
                    string id = v.CharacterId;
                    var has = FilesOf(id);
                    string ultMove = SpriteAnimLibrary.MovePrefix + FightClips.CharKey(id) + "_" + FightClips.UltimateFallbackRow;
                    var body = SpriteAnimLibrary.Body(id);
                    AssertStateClip(v, body, FightAnimState.Intro, has, ultMove, "first frame");

                    // Act / Assert: every fact combination shows exactly its state's contract clip (Sheet_Hit for
                    // Hitstun, Sheet_Launch for Launched, Sheet_KO for KO... when the character has the file)
                    foreach (var c in cases)
                    {
                        v.Tick(new Vector2(2f, c.grounded ? 0f : 4f), c.grounded, false, c.flags, TestDt, 0f);
                        AssertStateClip(v, body, c.state, has, ultMove, c.flags + " grounded " + c.grounded);
                    }
                    v.Tick(Vector2.zero, true, true, FightFlags.None, TestDt, 0f);
                    AssertStateClip(v, body, FightAnimState.Guard, has, ultMove, "guard");
                    v.Tick(Vector2.zero, true, false, FightFlags.None, TestDt, 0f);
                    AssertStateClip(v, body, FightAnimState.Idle, has, ultMove, "idle");

                    // legacy cutout requests and unknown moves never show a cutout or the body drawing
                    v.PlayNormal(false, 0, 0.3f); v.PlaySwing(0.3f); v.PlayRecoil(0.3f); v.Hurt();
                    v.PlayClipLocked("Skill1", 0f); v.PlayClipLocked("KO", 0f); v.PlayClipLocked("Ultimate", 0f);
                    v.Tick(Vector2.zero, true, false, FightFlags.None, TestDt, 0f);
                    AssertSheet(v, body, "legacy requests");
                    v.PlayMoveClip("nope_unknown", "Swing", 0.1f, 0.1f, 0.1f);
                    v.Tick(Vector2.zero, true, false, FightFlags.None, TestDt, 0f);
                    AssertSheet(v, body, "unknown move");
                    v.SetVictory(true);
                    v.Tick(Vector2.zero, true, false, FightFlags.None, TestDt, 0f);
                    AssertStateClip(v, body, FightAnimState.Victory, has, ultMove, "victory");
                }
                finally
                {
                    UnityEngine.Object.DestroyImmediate(go);
                }
            }
        }

        /// <summary>The visual is in <paramref name="state"/> and shows a frame of that state's contract clip.</summary>
        static void AssertStateClip(FighterVisual v, Sprite body, FightAnimState state, Func<string, bool> has, string ultMove,
                                    string when)
        {
            string expected = FightClips.Resolve(state, has, ultMove);
            Assert.IsNotNull(expected, v.CharacterId + " " + when + ": no contract clip for " + state);
            Assert.AreEqual(state, v.FightState, v.CharacterId + " " + when + ": state");
            Assert.AreEqual(expected, v.StateClip(state), v.CharacterId + " " + when + ": StateClip(" + state + ")");
            AssertSheet(v, body, when);
            Assert.AreEqual(expected, v.BodyRenderer.sprite.texture.name,
                            v.CharacterId + " " + when + ": shows a frame of the wrong clip for " + state);
        }

        static void AssertSheet(FighterVisual v, Sprite body, string when)
        {
            var sr = v.BodyRenderer;
            Assert.IsTrue(sr.enabled, v.CharacterId + " " + when + ": body renderer hidden");
            Assert.AreNotSame(body, sr.sprite, v.CharacterId + " " + when + ": static body drawing shown");
            Assert.IsNotNull(sr.sprite, v.CharacterId + " " + when + ": no sprite");
            Assert.IsTrue(FightClips.IsSheetClip(sr.sprite.texture.name),
                          v.CharacterId + " " + when + ": frame from non-sheet texture " + sr.sprite.texture.name);
        }

        [Test]
        public void Candidates_CallerTriesToModify_MappingIsReadOnly()
        {
            // Arrange
            var chain = FightClips.Candidates(FightAnimState.Hitstun);

            // Act / Assert
            Assert.AreEqual(FightClips.Hit, chain[0]);
            Assert.Throws<NotSupportedException>(() => ((IList<string>)chain)[0] = "Run");
            Assert.AreEqual(FightClips.Hit, FightClips.Candidates(FightAnimState.Hitstun)[0]);
            Assert.AreEqual(FightClips.Hit, FightClips.Resolve(FightAnimState.Hitstun, _ => true));
        }

        // ---- Depend on the technical artist's Sheet_* clips (fail until each file lands in Resources/Animations/<Id>/).

        static IEnumerable<TestCaseData> ArtistClips()
        {
            var contract = new[]
            {
                (FightAnimState.Run, FightClips.Run), (FightAnimState.Rise, FightClips.Jump), (FightAnimState.Fall, FightClips.Fall),
                (FightAnimState.Guard, FightClips.Guard), (FightAnimState.Hitstun, FightClips.Hit),
                (FightAnimState.Launched, FightClips.Launch), (FightAnimState.KO, FightClips.KO),
                (FightAnimState.Ultimate, FightClips.Ultimate),
            };
            foreach (var cls in Classes)
                foreach (var (s, clip) in contract)
                    yield return new TestCaseData(CharacterRoster.Of(cls).Id, s, clip).SetName("ArtistSheetClip_" + CharacterRoster.Of(cls).Id + "_" + clip + "_IsUsedForState");
        }

        [Category("ArtistSheetClips")]
        [TestCaseSource(nameof(ArtistClips))]
        public void ArtistSheetClip_Present_IsUsedForState(string id, FightAnimState state, string clip)
        {
            // Arrange
            var has = FilesOf(id);

            // Act
            string resolved = FightClips.Resolve(state, has);

            // Assert
            Assert.IsTrue(has(clip), id + ": " + clip + ".png not delivered yet");
            Assert.AreEqual(clip, resolved, id + " " + state);
        }
    }
}
