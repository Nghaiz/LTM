using System;
using System.Reflection;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using Object = UnityEngine.Object;

namespace Ironfront.Net.Unity.Server.Tests
{
    /// <summary>
    /// The game's own adapter reads a knocked-over bot's pose off its physical ragdoll: the
    /// pelvis the snapshot sends, and the head and body bounds its hitboxes are made of.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>On the real bot prefab, through the real binding.</b> A fake source proves what
    /// <c>NetServerActor</c> does with the numbers (<c>NetServerActorSeamTests</c>); this proves the
    /// numbers exist -- that the prefab's ragdoll carries a collider on its head bone and more
    /// on the rest, which a fake would simply assume.
    /// </para>
    /// <para>
    /// EditMode runs no Awake, so it is invoked here by name, and the adapter comes from the
    /// binding's own resolver, called directly rather than installed: Install assigns twenty
    /// static seams other fixtures read. This assembly cannot reference Assembly-CSharp.
    /// </para>
    /// </remarks>
    public sealed class LiveRagdollPoseTests
    {
        private const string BotPath = "Assets/Prefab/Ai Character Optimizations.prefab";

        private GameObject _bot;

        [TearDown]
        public void TearDown()
        {
            if (_bot != null) Object.DestroyImmediate(_bot);
        }

        [Test]
        public void AKnockedOverBotsPoseIsReadOffItsRagdoll()
        {
            _bot = (GameObject)PrefabUtility.InstantiatePrefab(AssetDatabase.LoadAssetAtPath<GameObject>(BotPath));
            _bot.transform.SetPositionAndRotation(new Vector3(3f, 0f, -4f), Quaternion.identity);

            Component raggy = Find(_bot, "ActiveRaggy");
            Invoke(raggy, "Awake");

            IGameplayActorSource source = TheGamesAdapterFor(_bot);
            Assert.IsNotNull(source, "the game's binding did not resolve the bot");
            Assert.IsFalse(source.IsRagdolledAlive, "precondition: a standing bot is not a ragdoll");
            Assert.IsFalse(source.TryGetRagdollPose(out _, out _, out _));

            // What Actor.FallOver does to the rig.
            Invoke(raggy, "Ragdoll", Vector3.zero);
            Physics.SyncTransforms();

            Assert.IsTrue(source.IsRagdolledAlive, "a living bot lying as a ragdoll is not reported as one");
            Assert.IsTrue(source.TryGetRagdollPose(out Vector3 pelvis, out Bounds head, out Bounds body),
                "the ragdoll has no collider on its head bone, or none on the rest of the body");

            Transform headBone = Bone(raggy, "Head");
            Assert.IsTrue(head.Contains(headBone.position) || head.SqrDistance(headBone.position) < 0.01f,
                $"the head bounds {head} are not at the ragdoll's head {headBone.position}");
            Assert.Greater(body.size.y, 0.5f, $"the body bounds {body} are not a body");
            Assert.AreEqual(Bone(raggy, "Hips").position, pelvis, "the pelvis is not the ragdoll's hips");
            Assert.AreEqual(3f, pelvis.x, 0.5f, "the pelvis is not where the bot is");
        }

        private static IGameplayActorSource TheGamesAdapterFor(GameObject bot)
        {
            Type bindings = Type.GetType("Ironfront.Net.Unity.Bindings.IronfrontNetBindings, Assembly-CSharp");
            Assert.IsNotNull(bindings, "IronfrontNetBindings moved; this fixture reaches it by name");
            MethodInfo resolve = bindings.GetMethod(
                "ResolveActorSource", BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public);
            Assert.IsNotNull(resolve, "IronfrontNetBindings.ResolveActorSource is gone");
            return (IGameplayActorSource)resolve.Invoke(null, new object[] { bot });
        }

        private static Transform Bone(Component raggy, string bone)
        {
            MethodInfo method = raggy.GetType().GetMethod("HumanBoneTransform");
            var parsed = (HumanBodyBones)Enum.Parse(typeof(HumanBodyBones), bone);
            return (Transform)method.Invoke(raggy, new object[] { parsed });
        }

        private static void Invoke(Component target, string name, params object[] args)
        {
            MethodInfo method = target.GetType().GetMethod(
                name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            Assert.IsNotNull(method, $"{target.GetType().Name}.{name} is gone");
            method.Invoke(target, args.Length == 0 ? null : args);
        }

        private static Component Find(GameObject root, string typeName)
        {
            foreach (MonoBehaviour behaviour in root.GetComponentsInChildren<MonoBehaviour>(true))
            {
                if (behaviour != null && behaviour.GetType().Name == typeName) return behaviour;
            }

            Assert.Fail($"no {typeName} on {root.name}");
            return null;
        }
    }
}
