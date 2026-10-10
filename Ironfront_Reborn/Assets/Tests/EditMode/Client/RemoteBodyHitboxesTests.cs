using System;
using System.Collections.Generic;
using System.Linq;
using Ironfront.Net.Protocol;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;

namespace Ironfront.Net.Unity.Client.Tests
{
    /// <summary>
    /// The soldier's hitboxes on a remote body's bones (14.0.6, owner's run of 2026-10-10: "aimed
    /// dead on and it does not hit"): the local player's rounds are tested against these, so they
    /// must be the soldier's own colliders, on bones the proxy really has.
    /// </summary>
    public sealed class RemoteBodyHitboxesTests
    {
        private const string SoldierPrefab = "Assets/Prefab/Ai Character Optimizations.prefab";
        private const string ProxyPrefab = "Assets/Prefab/Remote Actor Proxy.prefab";
        private static readonly int[] HitboxLayers = { 8, 10, 16 };

        /// <summary>
        /// Both directions: every collider the soldier is hit on is in the table, and every shape in
        /// the table is a collider the soldier is hit on. A rig change that adds, moves or drops a
        /// hitbox fails here rather than quietly changing what a remote body can be hit on.
        /// </summary>
        [Test]
        public void TheShapesAreTheSoldiersOwnHitboxColliders()
        {
            var soldier = AssetDatabase.LoadAssetAtPath<GameObject>(SoldierPrefab);
            Assert.IsNotNull(soldier, SoldierPrefab);

            Type hitboxType = AppDomain.CurrentDomain.GetAssemblies()
                .Select(assembly => assembly.GetType("Hitbox", false))
                .First(type => type != null);

            var unmatched = new List<RemoteBodyHitboxes.Shape>(RemoteBodyHitboxes.Shapes);
            var strays = new List<string>();
            foreach (Collider collider in soldier.GetComponentsInChildren<Collider>(true))
            {
                if (!HitboxLayers.Contains(collider.gameObject.layer)) continue;
                Component hitbox = collider.GetComponent(hitboxType);
                if (hitbox == null) continue;

                float multiplier = (float)hitboxType.GetField("multiplier").GetValue(hitbox);
                HitboxType part = multiplier >= 2f ? HitboxType.Head
                    : multiplier >= 0.85f ? HitboxType.Body
                    : HitboxType.Limb;

                int index = unmatched.FindIndex(shape => Matches(shape, collider, part));
                if (index < 0)
                {
                    strays.Add($"{collider.GetType().Name} on '{collider.name}' (x{multiplier})");
                    continue;
                }
                unmatched.RemoveAt(index);
            }

            Assert.IsEmpty(strays, "hitbox colliders on the soldier that remote bodies do not carry");
            Assert.IsEmpty(unmatched.Select(shape => shape.Bone),
                "shapes remote bodies are hit on that the soldier no longer carries");
        }

        [Test]
        public void TheRemoteProxyHasEveryBone()
        {
            var proxy = AssetDatabase.LoadAssetAtPath<GameObject>(ProxyPrefab);
            Assert.IsNotNull(proxy, ProxyPrefab);
            Assert.IsNotNull(RemoteBodyHitboxes.TryCreate(proxy.transform),
                "without every bone no round can strike a remote body, and no hit is ever reported");
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
        public void ASegmentEntersALocalCapsuleByItsSideOrItsCap()
        {
            // Spine from x = -0.2 to +0.2, radius 0.1.
            Assert.IsTrue(RemoteBodyHitboxes.SegmentEntersLocalCapsule(
                new Vector3(0f, 0f, -1f), new Vector3(0f, 0f, 1f), 0.2f, 0.1f, out float entry));
            Assert.AreEqual(0.45f, entry, 1e-4f, "the side at z = -0.1");

            Assert.IsTrue(RemoteBodyHitboxes.SegmentEntersLocalCapsule(
                new Vector3(-1f, 0f, 0f), new Vector3(1f, 0f, 0f), 0.2f, 0.1f, out entry));
            Assert.AreEqual(0.35f, entry, 1e-4f, "the cap at x = -0.3");

            Assert.IsFalse(RemoteBodyHitboxes.SegmentEntersLocalCapsule(
                new Vector3(0.35f, 0f, -1f), new Vector3(0.35f, 0f, 1f), 0.2f, 0.1f, out _),
                "past the end of the rounded cap");
            Assert.IsFalse(RemoteBodyHitboxes.SegmentEntersLocalCapsule(
                new Vector3(0f, 0.11f, -1f), new Vector3(0f, 0.11f, 1f), 0.2f, 0.1f, out _),
                "a millimetre wide of the side");

            Assert.IsTrue(RemoteBodyHitboxes.SegmentEntersLocalCapsule(
                new Vector3(0.1f, 0f, 0f), new Vector3(1f, 0f, 0f), 0.2f, 0.1f, out entry));
            Assert.AreEqual(0f, entry, "starting inside is struck at once");
        }

        [Test]
        public void ARoundFromAboveMeetsTheHeadFirst()
        {
            var proxy = AssetDatabase.LoadAssetAtPath<GameObject>(ProxyPrefab);
            GameObject body = UnityEngine.Object.Instantiate(proxy);
            try
            {
                RemoteBodyHitboxes boxes = RemoteBodyHitboxes.TryCreate(body.transform);
                Assert.IsNotNull(boxes);

                Transform head = Find(body.transform, RemoteBodyHitboxes.HeadBone);
                Vector3 centre = head.TransformPoint(RemoteBodyHitboxes.Shapes[0].Centre);

                Assert.IsTrue(boxes.TryHit(centre + Vector3.up * 3f, centre + Vector3.down * 3f,
                    out _, out HitboxType part));
                Assert.AreEqual(HitboxType.Head, part);

                Vector3 beside = centre + Vector3.right * 2f;
                Assert.IsFalse(boxes.TryHit(beside + Vector3.forward * 3f, beside + Vector3.back * 3f,
                    out _, out _), "a round two metres wide misses");
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(body);
            }
        }

        private static bool Matches(RemoteBodyHitboxes.Shape shape, Collider collider, HitboxType part)
        {
            if (shape.Bone != collider.name || shape.Part != part) return false;
            switch (collider)
            {
                case BoxCollider box:
                    return !shape.IsCapsule && Near(shape.Centre, box.center) && Near(shape.Size, box.size);
                case SphereCollider sphere:
                    return shape.IsSphere && Near(shape.Centre, sphere.center)
                           && Mathf.Abs(shape.Radius - sphere.radius) < 1e-4f;
                case CapsuleCollider capsule:
                    return shape.IsCapsule && !shape.IsSphere && capsule.direction == 0 && Near(shape.Centre, capsule.center)
                           && Mathf.Abs(shape.Radius - capsule.radius) < 1e-4f
                           && Mathf.Abs(shape.Height - capsule.height) < 1e-4f;
                default:
                    return false;
            }
        }

        private static bool Near(Vector3 a, Vector3 b) => Vector3.Distance(a, b) < 1e-4f;

        private static Transform Find(Transform parent, string name)
        {
            foreach (Transform child in parent.GetComponentsInChildren<Transform>(true))
                if (child.name == name) return child;
            return null;
        }
    }
}
