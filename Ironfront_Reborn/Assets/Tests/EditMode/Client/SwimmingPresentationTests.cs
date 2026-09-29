using System;
using Ironfront.Net.Protocol;
using Ironfront.Net.Replication.Combat;
using Ironfront.Net.Unity.Client.Hud;
using NUnit.Framework;
using UnityEditor;
using UnityEditor.Animations;
using UnityEngine;
using Object = UnityEngine.Object;

namespace Ironfront.Net.Unity.Client.Tests
{
    /// <summary>
    /// A body in water swims (owner ruling 2026-09-29): the original's own swim animation at the
    /// surface for every swimmer, bots included, and a breath bar the local player watches drain.
    /// </summary>
    public sealed class SwimmingPresentationTests
    {
        private const string ActorControllerPath = "Assets/AnimatorController/Actor.controller";
        private const string RagdollLayer = "Ragdoll Layer";
        private const string ProxyPrefabPath = "Assets/Prefab/Remote Actor Proxy.prefab";

        [Test]
        public void OnlyALiveBodyInWaterAndOutOfASeatSwims()
        {
            Assert.IsTrue(SwimPresentation.Swims(alive: true, inWater: true, seated: false));
            Assert.IsFalse(SwimPresentation.Swims(alive: false, inWater: true, seated: false),
                "a corpse in water was drawn swimming");
            Assert.IsFalse(SwimPresentation.Swims(alive: true, inWater: true, seated: true),
                "a crew member in a boat was drawn swimming");
            Assert.IsFalse(SwimPresentation.Swims(alive: true, inWater: false, seated: false));
        }

        [Test]
        public void ABotSwimmingAsARagdollIsDrawnSwimming()
        {
            // The server swims a bot as a buoyant ragdoll: its snapshot says alive, ragdolled, in water.
            Assert.IsTrue(RemoteActorRegistry.Swims(Entry(
                    ActorStateFlags.IsAlive | ActorStateFlags.IsRagdoll | ActorStateFlags.IsInWater)),
                "a swimming bot was drawn as a limp ragdoll bobbing at the surface");
            Assert.IsFalse(RemoteActorRegistry.Swims(Entry(ActorStateFlags.IsRagdoll | ActorStateFlags.IsInWater)),
                "a corpse floating in water was drawn swimming");
            Assert.IsFalse(RemoteActorRegistry.Swims(Entry(
                ActorStateFlags.IsAlive | ActorStateFlags.IsInWater | ActorStateFlags.IsSeated)));
        }

        [Test]
        public void ASwimmingBotFacesWhereItIsGoing()
        {
            // A ragdolled bot's yaw on the wire is the one it fell with; drawn by it, it swims sideways.
            Quaternion north = Quaternion.identity;

            Quaternion turned = RemoteActorRegistry.SwimmingHeading(north, new Vector3(2f, 0f, 0f), deltaSeconds: 1f);
            Assert.AreEqual(90f, turned.eulerAngles.y, 0.5f, "a bot swimming east kept facing north");

            Quaternion treading = RemoteActorRegistry.SwimmingHeading(north, new Vector3(0.1f, 0f, 0f), deltaSeconds: 1f);
            Assert.AreEqual(0f, Quaternion.Angle(north, treading), 1e-3f, "a bot treading water spun on the spot");
        }

        [Test]
        public void TheActorControllerStillCarriesTheOriginalsSwim()
        {
            var controller = AssetDatabase.LoadAssetAtPath<AnimatorController>(ActorControllerPath);
            Assert.IsNotNull(controller, ActorControllerPath);

            AnimatorControllerLayer layer = Array.Find(controller.layers, l => l.name == RagdollLayer);
            Assert.IsNotNull(layer, "the swim clips live on the " + RagdollLayer + ", which is gone");
            foreach (string state in new[] { "Swim Idle", "Swim Forward" })
            {
                Assert.IsTrue(Array.Exists(layer.stateMachine.states, s => s.state.name == state),
                    RagdollLayer + " has no '" + state + "': every swimmer would be drawn standing");
            }
            foreach (string parameter in new[] { "ragdolled", "swim", "swim forward" })
            {
                Assert.IsTrue(Array.Exists(controller.parameters,
                        p => p.name == parameter && p.type == AnimatorControllerParameterType.Bool),
                    "Actor.controller has no bool '" + parameter + "' for SwimPresentation to set");
            }
        }

        [Test]
        public void ASwimmerPlaysTheSwimAndABodyOnLandDoesNot()
        {
            var body = new GameObject("swimmer");
            try
            {
                var animator = body.AddComponent<Animator>();
                animator.runtimeAnimatorController =
                    AssetDatabase.LoadAssetAtPath<RuntimeAnimatorController>(ActorControllerPath);
                animator.Rebind();
                int layer = animator.GetLayerIndex(RagdollLayer);

                SwimPresentation.Apply(animator, swimming: true, moving: true);
                Assert.AreEqual(1f, animator.GetLayerWeight(layer), "the swim layer was left at weight 0");
                Assert.IsTrue(animator.GetBool("ragdolled") && animator.GetBool("swim") && animator.GetBool("swim forward"));

                SwimPresentation.Apply(animator, swimming: true, moving: false);
                Assert.IsFalse(animator.GetBool("swim forward"), "a swimmer treading water was drawn stroking");

                SwimPresentation.Apply(animator, swimming: false, moving: true);
                Assert.AreEqual(0f, animator.GetLayerWeight(layer), "a body out of the water kept the swim layer");
                Assert.IsFalse(animator.GetBool("swim") || animator.GetBool("swim forward") || animator.GetBool("ragdolled"));

                SwimPresentation.Apply(animator, swimming: false, moving: false, ragdolledOtherwise: true);
                Assert.IsTrue(animator.GetBool("ragdolled"), "a knocked-over body out of the water lost its ragdoll flag");
            }
            finally
            {
                Object.DestroyImmediate(body);
            }
        }

        [TestCase(false, TestName = "ATreadingSwimmerHasItsHeadOutAndItsBodyUnderTheSurface")]
        [TestCase(true, TestName = "AStrokingSwimmerHasItsHeadOutAndItsBodyUnderTheSurface")]
        public void ASwimmerIsDrawnWithItsHeadOutAndItsBodyUnder(bool stroking)
        {
            // Owner report 2026-09-30: a swimmer treading water was drawn with two thirds of its body
            // out of it. Placed by its head, both of the original's clips put the chest and hips under.
            const float water = 18f;
            var body = (GameObject)PrefabUtility.InstantiatePrefab(
                AssetDatabase.LoadAssetAtPath<GameObject>(ProxyPrefabPath));
            try
            {
                var animator = body.GetComponentInChildren<Animator>();
                animator.cullingMode = AnimatorCullingMode.AlwaysAnimate;
                animator.Rebind();
                SwimPresentation.Apply(animator, swimming: true, moving: stroking);
                for (int i = 0; i < 40; i++) animator.Update(0.1f);

                Transform head = animator.GetBoneTransform(HumanBodyBones.Head);
                float headAboveRoot = head.position.y - body.transform.position.y;
                if (!stroking)
                {
                    Assert.AreEqual(SwimPresentation.IdleHeadAboveRoot, headAboveRoot, 0.05f,
                        "Swim Idle no longer stands the head where IdleHeadAboveRoot says it does");
                }

                Vector3 at = body.transform.position;
                body.transform.position = new Vector3(at.x, SwimPresentation.RootHeight(water, headAboveRoot), at.z);

                Assert.AreEqual(water + SwimPresentation.HeadAboveSurface, head.position.y, 1e-3f);
                Assert.Less(animator.GetBoneTransform(HumanBodyBones.Chest).position.y, water,
                    "the swimmer's chest is out of the water");
                Assert.Less(animator.GetBoneTransform(HumanBodyBones.Hips).position.y, water,
                    "the swimmer's hips are out of the water");
            }
            finally
            {
                Object.DestroyImmediate(body);
            }
        }

        [Test]
        public void TheBreathBarDrainsInWaterRefillsOnLandAndStartsFullAfterADeath()
        {
            var host = new GameObject("breath");
            try
            {
                BreathHud hud = host.AddComponent<BreathHud>();
                Assert.AreEqual(1f, hud.Fraction);

                hud.Advance(alive: true, inWater: true, deltaSeconds: BreathClock.CapacitySeconds * 0.5f);
                Assert.AreEqual(0.5f, hud.Fraction, 1e-4f, "the bar did not drain in water");

                hud.Advance(alive: true, inWater: false, deltaSeconds: BreathClock.RefillSeconds * 0.25f);
                Assert.AreEqual(0.75f, hud.Fraction, 1e-4f, "the bar did not refill on land");

                hud.Advance(alive: true, inWater: true, deltaSeconds: BreathClock.CapacitySeconds);
                Assert.AreEqual(0f, hud.Fraction, 1e-4f);

                hud.Advance(alive: false, inWater: true, deltaSeconds: 0.1f);
                Assert.AreEqual(1f, hud.Fraction, "a player who died in the water came back with no breath");
            }
            finally
            {
                Object.DestroyImmediate(host);
            }
        }

        private static ActorSnapshotEntry Entry(ActorStateFlags flags) => new ActorSnapshotEntry
        {
            ActorId    = 3,
            StateFlags = flags,
        };
    }
}
