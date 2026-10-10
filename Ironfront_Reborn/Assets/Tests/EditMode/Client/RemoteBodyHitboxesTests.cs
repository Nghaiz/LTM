using NUnit.Framework;
using UnityEditor;
using UnityEngine;

namespace Ironfront.Net.Unity.Client.Tests
{
    /// <summary>
    /// The soldier's two hitboxes on a remote body's bones (14.0.6, owner's run of 2026-10-10:
    /// "aimed dead on and it does not hit"): the local player's rounds are tested against these,
    /// so they must be the soldier's own boxes, on bones the proxy really has.
    /// </summary>
    public sealed class RemoteBodyHitboxesTests
    {
        private const string SoldierPrefab = "Assets/Prefab/Ai Character Optimizations.prefab";
        private const string ProxyPrefab = "Assets/Prefab/Remote Actor Proxy.prefab";
        private const int HitboxLayer = 8;

        [Test]
        public void TheBoxesAreTheSoldiersOwnHitboxColliders()
        {
            var soldier = AssetDatabase.LoadAssetAtPath<GameObject>(SoldierPrefab);
            Assert.IsNotNull(soldier, SoldierPrefab);

            int matched = 0;
            foreach (BoxCollider box in soldier.GetComponentsInChildren<BoxCollider>(true))
            {
                if (box.gameObject.layer != HitboxLayer) continue;

                if (box.name == RemoteBodyHitboxes.HeadBone)
                {
                    AssertNear(RemoteBodyHitboxes.HeadCentre, box.center, "head centre");
                    AssertNear(RemoteBodyHitboxes.HeadSize, box.size, "head size");
                    matched++;
                }
                else if (box.name == RemoteBodyHitboxes.BodyBone)
                {
                    AssertNear(RemoteBodyHitboxes.BodyCentre, box.center, "body centre");
                    AssertNear(RemoteBodyHitboxes.BodySize, box.size, "body size");
                    matched++;
                }
                else
                {
                    Assert.Fail($"a hitbox-layer box on '{box.name}' that remote bodies do not carry");
                }
            }

            Assert.AreEqual(2, matched, "the soldier is hit on exactly two boxes, head and body");
        }

        [Test]
        public void TheRemoteProxyHasBothBones()
        {
            var proxy = AssetDatabase.LoadAssetAtPath<GameObject>(ProxyPrefab);
            Assert.IsNotNull(proxy, ProxyPrefab);
            Assert.IsNotNull(RemoteBodyHitboxes.TryCreate(proxy.transform),
                "without both bones no round can strike a remote body, and no hit is ever reported");
        }

        [Test]
        public void ASegmentEntersALocalBoxWhereItCrossesTheFace()
        {
            var half = new Vector3(0.5f, 0.5f, 0.5f);

            Assert.IsTrue(RemoteBodyHitboxes.SegmentEntersLocalBox(
                new Vector3(-2f, 0f, 0f), new Vector3(2f, 0f, 0f), half, out float entry));
            Assert.AreEqual(0.375f, entry, 1e-4f, "enters at x = -0.5, three eighths along");

            Assert.IsFalse(RemoteBodyHitboxes.SegmentEntersLocalBox(
                new Vector3(-2f, 0.6f, 0f), new Vector3(2f, 0.6f, 0f), half, out _), "passes over the top");
            Assert.IsFalse(RemoteBodyHitboxes.SegmentEntersLocalBox(
                new Vector3(-2f, 0f, 0f), new Vector3(-0.6f, 0f, 0f), half, out _), "stops short");

            Assert.IsTrue(RemoteBodyHitboxes.SegmentEntersLocalBox(
                Vector3.zero, new Vector3(2f, 0f, 0f), half, out entry));
            Assert.AreEqual(0f, entry, "starting inside is struck at once");
        }

        [Test]
        public void TheBoxesTurnWithTheirBonesAndTheHeadIsMetFirstFromAbove()
        {
            var root = new GameObject("body");
            try
            {
                // A spine bone pointing down its own x, the way the rig hangs: the body box
                // runs from 0.4 m above the bone to 1.0 m below it.
                var body = new GameObject(RemoteBodyHitboxes.BodyBone).transform;
                body.SetParent(root.transform, false);
                body.localPosition = new Vector3(0f, 1.4f, 0f);
                body.localRotation = Quaternion.Euler(0f, 0f, -90f);

                var head = new GameObject(RemoteBodyHitboxes.HeadBone).transform;
                head.SetParent(body, false);
                head.localPosition = new Vector3(-0.5f, 0f, 0f);

                RemoteBodyHitboxes boxes = RemoteBodyHitboxes.TryCreate(root.transform);
                Assert.IsNotNull(boxes);

                Vector3 headCentre = head.TransformPoint(RemoteBodyHitboxes.HeadCentre);
                Assert.IsTrue(boxes.TryHit(headCentre + Vector3.back * 5f, headCentre + Vector3.forward * 5f,
                    out _, out bool struckHead));
                Assert.IsTrue(struckHead, "a level round through the head is a headshot");

                Vector3 waist = body.TransformPoint(new Vector3(0.6f, 0f, 0f));
                Assert.IsTrue(boxes.TryHit(waist + Vector3.back * 5f, waist + Vector3.forward * 5f,
                    out _, out struckHead));
                Assert.IsFalse(struckHead, "a round through the waist is a body hit");

                Vector3 beside = waist + Vector3.right * 1.5f;
                Assert.IsFalse(boxes.TryHit(beside + Vector3.back * 5f, beside + Vector3.forward * 5f,
                    out _, out _), "a round a metre and a half wide misses");

                // Straight down from above: the head box is entered before the body box below it.
                Vector3 above = headCentre + Vector3.up * 3f;
                Assert.IsTrue(boxes.TryHit(above, above + Vector3.down * 6f, out _, out struckHead));
                Assert.IsTrue(struckHead);
            }
            finally
            {
                Object.DestroyImmediate(root);
            }
        }

        private static void AssertNear(Vector3 expected, Vector3 actual, string what)
            => Assert.Less(Vector3.Distance(expected, actual), 1e-4f, $"{what}: expected {expected}, read {actual}");
    }
}
