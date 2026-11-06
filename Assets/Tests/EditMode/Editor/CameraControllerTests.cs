using NUnit.Framework;
using UnityEngine;

namespace ChibiClash.Tests
{
    /// <summary>Regression test for the camera freezing after Rematch: the old match's OnDestroy must not clear the new match's targets.</summary>
    public class CameraControllerTests
    {
        GameObject go;
        CameraController cam;
        Fighter[] oldF, newF;

        [SetUp]
        public void SetUp()
        {
            // a Camera is attached so Awake is safe even if it runs; in EditMode it normally doesn't
            go = new GameObject("CameraControllerTests", typeof(Camera));
            cam = go.AddComponent<CameraController>();
            oldF = new Fighter[2];
            newF = new Fighter[2];
        }

        [TearDown]
        public void TearDown()
        {
            Object.DestroyImmediate(go);
        }

        [Test]
        public void ReleaseTargets_OtherOwner_KeepsTargets()
        {
            cam.targets = newF;
            cam.ReleaseTargets(oldF);
            Assert.AreSame(newF, cam.targets);
        }

        [Test]
        public void ReleaseTargets_SameOwner_ClearsTargets()
        {
            cam.targets = newF;
            cam.ReleaseTargets(newF);
            Assert.IsNull(cam.targets);
        }

        [Test]
        public void ReleaseTargets_NoTargets_StaysNull()
        {
            cam.targets = null;
            cam.ReleaseTargets(oldF);
            Assert.IsNull(cam.targets);
        }
    }
}
