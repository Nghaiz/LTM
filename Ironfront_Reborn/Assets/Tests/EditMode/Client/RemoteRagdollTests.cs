using NUnit.Framework;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace Ironfront.Net.Unity.Client.Tests
{
    /// <summary>
    /// Client-track E1 on the real proxy prefab: a remote body that dies falls over and stays on
    /// the ground, and the respawn that reuses it gets back an intact skeleton.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Physics is stepped here, not assumed.</b> Lane-B frames cannot grade a corpse: the
    /// rendered camera does not follow a scripted aim, so a run proves only that the fallback
    /// warning did not fire. What the owner reported was a body that vanished; what could still
    /// go wrong is a body that falls through the ground, and only a simulated fall can tell the
    /// two apart from a ragdoll that works. Mutated to build no colliders, the fall test reads
    /// "fell through the floor: hips at -177.59 m".
    /// </para>
    /// <para>
    /// <b>In a preview scene, with its own physics scene.</b> Stepping the default one needs
    /// <c>Physics.simulationMode</c> switched to Script, and that dirties the project's physics
    /// settings: the Editor re-saved <c>DynamicsManager.asset</c> on exit after the first run.
    /// </para>
    /// </remarks>
    public sealed class RemoteRagdollTests
    {
        private const string ProxyPath = "Assets/Prefab/Remote Actor Proxy.prefab";

        /// <summary>Simulated seconds for a fall to come to rest.</summary>
        private const float SettleSeconds = 6f;

        private const float StepSeconds = 0.02f;

        private Scene _scene;
        private GameObject _proxy;

        [SetUp]
        public void SetUp()
        {
            _scene = EditorSceneManager.NewPreviewScene();
        }

        [TearDown]
        public void TearDown()
        {
            EditorSceneManager.ClosePreviewScene(_scene);
        }

        [Test]
        public void TheProxyAvatarNamesEveryBoneTheRagdollNeeds()
        {
            Assert.IsNotNull(
                RemoteRagdoll.TryCreate(SpawnProxy(Vector3.zero)),
                "the proxy's avatar is missing a bone the ragdoll is built on, so every remote "
                + "death falls back to hiding the body");
        }

        [Test]
        public void AFelledProxyComesToRestOnTheGround()
        {
            GameObject floor = GameObject.CreatePrimitive(PrimitiveType.Plane);
            floor.transform.localScale = new Vector3(10f, 1f, 10f);
            SceneManager.MoveGameObjectToScene(floor, _scene);

            Animator animator = SpawnProxy(Vector3.zero);
            RemoteRagdoll ragdoll = RemoteRagdoll.TryCreate(animator);
            Transform hips = animator.GetBoneTransform(HumanBodyBones.Hips);
            float standingHips = hips.position.y;

            ragdoll.Fell(Vector3.forward * 20f, HumanBodyBones.Chest);

            PhysicsScene physics = _scene.GetPhysicsScene();
            Assert.AreNotEqual(
                Physics.defaultPhysicsScene, physics,
                "the preview scene shares the default physics scene, so it cannot be stepped here");
            for (float t = 0f; t < SettleSeconds; t += StepSeconds) physics.Simulate(StepSeconds);

            Assert.IsTrue(ragdoll.IsActive);
            Assert.IsFalse(animator.enabled, "the animator would fight the bodies for the pose");

            // Relative, not a fixed drop: the proxy's hips stand at 0.44 m, and lying flat puts
            // them at the hip capsule's radius (0.14 m) -- a 0.30 m fall, the whole of it.
            Assert.Less(
                hips.position.y, standingHips * 0.5f,
                $"the body did not fall over: hips at {hips.position.y:F2} m, standing {standingHips:F2} m");
            Assert.Greater(
                hips.position.y, -0.05f,
                $"the body fell through the floor: hips at {hips.position.y:F2} m");
        }

        [Test]
        public void RestoreRemovesEveryPartAndGivesTheAnimatorBack()
        {
            Animator animator = SpawnProxy(Vector3.zero);
            RemoteRagdoll ragdoll = RemoteRagdoll.TryCreate(animator);
            int bonesLayer = animator.GetBoneTransform(HumanBodyBones.Hips).gameObject.layer;

            ragdoll.Fell(Vector3.zero, HumanBodyBones.Hips);

            Assert.AreEqual(11, _proxy.GetComponentsInChildren<Rigidbody>().Length);
            Assert.AreEqual(10, _proxy.GetComponentsInChildren<CharacterJoint>().Length);

            ragdoll.Restore();

            Assert.IsFalse(ragdoll.IsActive);
            Assert.IsTrue(animator.enabled);
            Assert.AreEqual(0, _proxy.GetComponentsInChildren<Rigidbody>().Length);
            Assert.AreEqual(0, _proxy.GetComponentsInChildren<Joint>().Length);
            Assert.AreEqual(0, _proxy.GetComponentsInChildren<Collider>().Length);
            Assert.AreEqual(bonesLayer, animator.GetBoneTransform(HumanBodyBones.Hips).gameObject.layer);
        }

        private Animator SpawnProxy(Vector3 position)
        {
            var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(ProxyPath);
            Assert.IsNotNull(prefab, $"no prefab at {ProxyPath}");

            _proxy = Object.Instantiate(prefab, position, Quaternion.identity);
            SceneManager.MoveGameObjectToScene(_proxy, _scene);

            Animator animator = _proxy.GetComponentInChildren<Animator>();
            Assert.IsNotNull(animator, "the proxy carries no Animator");
            return animator;
        }
    }
}
