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

        /// <summary>
        /// A LIVE ragdoll -- a bot knocked over or swimming -- follows the pelvis the server's
        /// ragdoll has, rather than lying where the client's collapsed.
        /// </summary>
        /// <remarks>
        /// Leftover from the 2026-09-28 audit: such a bot was drawn standing where it fell while
        /// its real body lay metres away. Mutated to never steer, the pelvis stays at the fall.
        /// </remarks>
        [Test]
        public void ALiveRagdollFollowsTheServersPelvis()
        {
            GameObject floor = GameObject.CreatePrimitive(PrimitiveType.Plane);
            floor.transform.localScale = new Vector3(10f, 1f, 10f);
            SceneManager.MoveGameObjectToScene(floor, _scene);

            Animator animator = SpawnProxy(Vector3.zero);
            RemoteRagdoll ragdoll = RemoteRagdoll.TryCreate(animator);
            Transform hips = animator.GetBoneTransform(HumanBodyBones.Hips);
            ragdoll.Fell(Vector3.zero, HumanBodyBones.Hips);

            var target = new Vector3(2.5f, 0.2f, 1.5f);
            PhysicsScene physics = _scene.GetPhysicsScene();
            for (float t = 0f; t < 3f; t += StepSeconds)
            {
                ragdoll.Steer(target);
                physics.Simulate(StepSeconds);
            }

            float miss = Vector3.Distance(hips.position, target);
            Assert.Less(miss, 0.35f,
                $"the pelvis is {miss:F2} m from where the server's ragdoll has it ({hips.position:F2})");
        }

        [Test]
        public void AFlingTheClientNeverSawMovesTheWholeBodyAtOnce()
        {
            Animator animator = SpawnProxy(Vector3.zero);
            RemoteRagdoll ragdoll = RemoteRagdoll.TryCreate(animator);
            Transform hips = animator.GetBoneTransform(HumanBodyBones.Hips);
            Transform head = animator.GetBoneTransform(HumanBodyBones.Head);
            ragdoll.Fell(Vector3.zero, HumanBodyBones.Hips);

            float spine = Vector3.Distance(hips.position, head.position);
            var target = hips.position + new Vector3(12f, 0f, -9f);

            ragdoll.Steer(target);
            _scene.GetPhysicsScene().Simulate(StepSeconds);

            Assert.Less(Vector3.Distance(hips.position, target), 0.2f, "the body did not move with the fling");
            Assert.AreEqual(spine, Vector3.Distance(hips.position, head.position), 0.1f,
                "the move tore the body apart instead of carrying it");
        }

        [Test]
        public void ABodyInWaterFloatsAndOneOutOfItFalls()
        {
            Animator animator = SpawnProxy(new Vector3(0f, 5f, 0f));
            RemoteRagdoll ragdoll = RemoteRagdoll.TryCreate(animator);
            Transform hips = animator.GetBoneTransform(HumanBodyBones.Hips);
            ragdoll.Fell(Vector3.zero, HumanBodyBones.Hips);
            PhysicsScene physics = _scene.GetPhysicsScene();

            float before = hips.position.y;
            ragdoll.SetFloating(true);
            for (float t = 0f; t < 1f; t += StepSeconds) physics.Simulate(StepSeconds);
            Assert.AreEqual(before, hips.position.y, 0.25f, "a body in water sank or rose");

            ragdoll.SetFloating(false);
            for (float t = 0f; t < 1f; t += StepSeconds) physics.Simulate(StepSeconds);
            Assert.Less(hips.position.y, before - 2f, "a body out of water kept floating");
        }

        /// <summary>
        /// A knocked-over body far from the camera lands, then stops costing the physics step.
        /// </summary>
        [Test]
        public void AKnockedOverBodyFarFromTheCameraIsFrozenOnceItHasLanded()
        {
            RemoteRagdoll ragdoll = FellOnAFloor(out Transform hips);
            PhysicsScene physics = _scene.GetPhysicsScene();

            float t = 0f;
            for (; t < RemoteRagdoll.LandSeconds - 0.1f; t += StepSeconds)
            {
                ragdoll.SimulateWhereSeen(nearCamera: false, t);
                ragdoll.Steer(hips.position);
                physics.Simulate(StepSeconds);
            }
            Assert.IsFalse(ragdoll.IsSettled, "a body was frozen before it had landed: a fall far away would stand still");

            for (; t < RemoteRagdoll.LandSeconds + 0.5f; t += StepSeconds)
            {
                ragdoll.SimulateWhereSeen(nearCamera: false, t);
                physics.Simulate(StepSeconds);
            }
            Assert.IsTrue(ragdoll.IsSettled, "a landed body far from the camera still costs the physics step every frame");
            foreach (Rigidbody body in _proxy.GetComponentsInChildren<Rigidbody>())
            {
                Assert.IsTrue(body.isKinematic, $"{body.name} is still simulated");
                Assert.IsFalse(body.detectCollisions, $"{body.name} still collides");
            }
        }

        [Test]
        public void AKnockedOverBodyNearTheCameraKeepsSimulating()
        {
            RemoteRagdoll ragdoll = FellOnAFloor(out Transform hips);
            PhysicsScene physics = _scene.GetPhysicsScene();

            for (float t = 0f; t < RemoteRagdoll.LandSeconds * 3f; t += StepSeconds)
            {
                ragdoll.SimulateWhereSeen(nearCamera: true, t);
                physics.Simulate(StepSeconds);
            }

            Assert.IsFalse(ragdoll.IsSettled, "a body in front of the camera was frozen");
        }

        [Test]
        public void AFrozenBodyIsCarriedAfterThePelvisAndWokenByTheCamera()
        {
            RemoteRagdoll ragdoll = FellOnAFloor(out Transform hips);
            PhysicsScene physics = _scene.GetPhysicsScene();
            for (float t = 0f; t < RemoteRagdoll.LandSeconds + 0.2f; t += StepSeconds)
            {
                ragdoll.SimulateWhereSeen(nearCamera: false, t);
                physics.Simulate(StepSeconds);
            }
            Assert.IsTrue(ragdoll.IsSettled, "Setup: the body was not frozen");

            Vector3 target = hips.position + new Vector3(3f, 0f, 0f);
            ragdoll.Steer(target);
            physics.Simulate(StepSeconds);
            Assert.Less(Vector3.Distance(hips.position, target), 0.2f,
                "a frozen body stayed where it landed while the server's pelvis moved on");

            ragdoll.SimulateWhereSeen(nearCamera: true, RemoteRagdoll.LandSeconds + 1f);
            Assert.IsFalse(ragdoll.IsSettled, "the camera came near and the body stayed frozen");
            foreach (Rigidbody body in _proxy.GetComponentsInChildren<Rigidbody>())
                Assert.IsFalse(body.isKinematic, $"{body.name} stayed frozen in front of the camera");
        }

        private RemoteRagdoll FellOnAFloor(out Transform hips)
        {
            GameObject floor = GameObject.CreatePrimitive(PrimitiveType.Plane);
            floor.transform.localScale = new Vector3(10f, 1f, 10f);
            SceneManager.MoveGameObjectToScene(floor, _scene);

            Animator animator = SpawnProxy(Vector3.zero);
            RemoteRagdoll ragdoll = RemoteRagdoll.TryCreate(animator);
            hips = animator.GetBoneTransform(HumanBodyBones.Hips);
            ragdoll.Fell(Vector3.zero, HumanBodyBones.Hips);
            return ragdoll;
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
