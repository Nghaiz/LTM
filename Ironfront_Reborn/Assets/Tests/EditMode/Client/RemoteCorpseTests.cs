using Ironfront.Net.Protocol;
using NUnit.Framework;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace Ironfront.Net.Unity.Client.Tests
{
    /// <summary>
    /// A remote death leaves a corpse that outlives the actor: a copy of the body, thrown as a
    /// ragdoll, with its own materials and its weapon out of the hand (owner report 2026-09-29: a
    /// body shot dead just disappeared).
    /// </summary>
    /// <remarks>
    /// On the real proxy prefab, in a preview scene with its own physics, for the reason
    /// <see cref="RemoteRagdollTests"/> gives. The corpse root is created in the active scene and
    /// moved into the preview one, or <c>PhysicsScene.Simulate</c> would never move it.
    /// </remarks>
    public sealed class RemoteCorpseTests
    {
        private const string ProxyPath = "Assets/Prefab/Remote Actor Proxy.prefab";

        private const float StepSeconds = 0.02f;

        private Scene _scene;
        private GameObject _proxy;
        private RemoteCorpse _corpse;

        [SetUp]
        public void SetUp()
        {
            _scene = EditorSceneManager.NewPreviewScene();
        }

        [TearDown]
        public void TearDown()
        {
            _corpse?.Destroy();
            EditorSceneManager.ClosePreviewScene(_scene);
        }

        [Test]
        public void ACorpseIsACopyThatOutlivesTheBodyItCameFrom()
        {
            Animator animator = SpawnProxy(Vector3.zero);
            _corpse = Copy(animator);

            Object.DestroyImmediate(_proxy);

            Assert.IsFalse(_corpse.IsGone, "the corpse went with the proxy it was copied from");
            Assert.IsNotNull(_corpse.Root.GetComponentInChildren<SkinnedMeshRenderer>(),
                "the corpse copied no body to draw");
            Assert.IsEmpty(_corpse.Root.GetComponentsInChildren<MonoBehaviour>(),
                "the corpse copied scripts: an instantiated weapon would register itself as a live one");
        }

        [Test]
        public void ACorpseFallsAndComesToRestOnTheGround()
        {
            AddFloor();
            Animator animator = SpawnProxy(Vector3.zero);
            float standingHips = animator.GetBoneTransform(HumanBodyBones.Hips).position.y;
            _corpse = Copy(animator);
            Transform hips = FindCopy(_corpse.Root.transform, animator.GetBoneTransform(HumanBodyBones.Hips).name);

            _corpse.Fell(Vector3.forward * 20f, HumanBodyBones.Chest, new Vector3(3f, 0f, 0f), 0.55f);
            Simulate(6f);

            Assert.Less(hips.position.y, standingHips * 0.5f,
                $"the corpse did not fall over: hips at {hips.position.y:F2} m, standing {standingHips:F2} m");
            Assert.Greater(hips.position.y, -0.05f, $"the corpse fell through the floor: hips at {hips.position.y:F2} m");
            Assert.Greater(hips.position.x, 0.2f,
                "a body shot while running must carry its momentum into the fall, not drop on the spot");
            Assert.IsTrue(_corpse.IsResting, "the corpse is still moving six seconds after the death");
        }

        [Test]
        public void TheDeathPoseHoldsAndThenGivesWay()
        {
            Animator animator = SpawnProxy(Vector3.zero);
            _corpse = Copy(animator);
            _corpse.Fell(Vector3.zero, HumanBodyBones.Hips, Vector3.zero, 0.5f);

            ConfigurableJoint[] joints = _corpse.Root.GetComponentsInChildren<ConfigurableJoint>();
            Assert.AreEqual(10, joints.Length, "the corpse is not jointed like the body it copies");
            foreach (ConfigurableJoint joint in joints)
            {
                Assert.Greater(joint.slerpDrive.positionSpring, 0f, $"{joint.name} does not hold the death pose at first");
            }

            _corpse.TickCrumple(0.5f);
            foreach (ConfigurableJoint joint in joints)
            {
                Assert.AreEqual(0f, joint.slerpDrive.positionSpring, 1e-4f, $"{joint.name} still holds the pose after the crumple");
            }
        }

        [Test]
        public void ACorpseOwnsItsMaterials()
        {
            Animator animator = SpawnProxy(Vector3.zero);
            SkinnedMeshRenderer body = _proxy.GetComponentInChildren<SkinnedMeshRenderer>();
            _corpse = Copy(animator);
            SkinnedMeshRenderer copy = _corpse.Root.GetComponentInChildren<SkinnedMeshRenderer>();

            Assert.AreNotSame(body.sharedMaterial, copy.sharedMaterial,
                "the corpse shares the proxy's material, so the next occupant's team colour repaints it");
        }

        [Test]
        public void TheWeaponLeavesTheHand()
        {
            AddFloor();
            Animator animator = SpawnProxy(Vector3.zero);
            Transform hand = animator.GetBoneTransform(HumanBodyBones.RightHand);
            Transform mount = FindCopy(hand, "Weapon Mount");
            Assert.IsNotNull(mount, "Setup: the proxy has no Weapon Mount under its right hand.");
            GameObject rifle = GameObject.CreatePrimitive(PrimitiveType.Cube);
            Object.DestroyImmediate(rifle.GetComponent<Collider>());
            rifle.transform.SetParent(mount, false);
            rifle.transform.localScale = new Vector3(0.08f, 0.1f, 0.7f);

            _corpse = Copy(animator);
            Assert.IsNotNull(_corpse.DroppedWeapon, "the corpse kept the weapon welded to its hand");
            Transform handCopy = FindCopy(_corpse.Root.transform, hand.name);
            Assert.IsFalse(_corpse.DroppedWeapon.transform.IsChildOf(handCopy), "the dropped weapon still hangs from the hand");

            _corpse.Fell(Vector3.zero, HumanBodyBones.Hips, Vector3.zero, 0f);
            Simulate(3f);
            Assert.Greater(_corpse.DroppedWeapon.position.y, -0.1f, "the dropped weapon fell through the floor");
        }

        [Test]
        public void ADestroyedCorpseLeavesNothingBehind()
        {
            Animator animator = SpawnProxy(Vector3.zero);
            _corpse = Copy(animator);
            _corpse.Destroy();
            Assert.IsTrue(_corpse.IsGone);
        }

        [Test]
        public void AnAliveEntryDrawnJustAfterTheDeathIsNotARespawn()
        {
            // S_DEATH lands at once; the snapshot entry is the interpolator's, a moment in the
            // past, so it still says alive. Taken as a respawn, it stood the body back up and,
            // when the dead entry followed, left a second corpse (rig, 2026-09-29).
            var body = new GameObject("Remote Actor");
            SceneManager.MoveGameObjectToScene(body, _scene);
            RemoteActorView view = body.AddComponent<RemoteActorView>();

            view.Apply(Entry(alive: true));
            view.HandOverToCorpse();

            view.Apply(Entry(alive: true));
            Assert.IsTrue(view.HasCorpseThisLife,
                "the interpolator's last alive entry was taken for a respawn, so the dead one will leave a second corpse");

            view.Apply(Entry(alive: false));
            Assert.IsTrue(view.HasCorpseThisLife, "the death's own entry forgot the corpse it already left");

            view.Apply(Entry(alive: true));
            Assert.IsFalse(view.HasCorpseThisLife, "an alive entry after a dead one is the respawn, and it was missed");
        }

        [Test]
        public void AnAliveEntryLongAfterTheDeathIsARespawnEvenUnseenDead()
        {
            // A body out of interest range can die and respawn with no dead entry ever drawn.
            Assert.Less(RemoteActorView.StaleAliveSeconds, ProtocolConstants.RESPAWN_SECONDS,
                "a stale entry must be told apart well before the server allows a respawn");
            Assert.IsFalse(RemoteActorView.IsRespawn(true, false, RemoteActorView.StaleAliveSeconds - 0.01f));
            Assert.IsTrue(RemoteActorView.IsRespawn(true, false, RemoteActorView.StaleAliveSeconds));
            Assert.IsTrue(RemoteActorView.IsRespawn(true, true, 0f));
            Assert.IsFalse(RemoteActorView.IsRespawn(false, true, 10f));
        }

        private static ActorSnapshotEntry Entry(bool alive) => new ActorSnapshotEntry
        {
            ActorId    = 7,
            StateFlags = alive ? ActorStateFlags.IsAlive : ActorStateFlags.IsRagdoll,
            Health     = (byte)(alive ? 100 : 0),
            Team       = 1,
        };

        private RemoteCorpse Copy(Animator animator)
        {
            RemoteCorpse corpse = RemoteCorpse.TryCreate(animator, 7, 0, 0f);
            Assert.IsNotNull(corpse, "the proxy's body could not be copied into a corpse");
            SceneManager.MoveGameObjectToScene(corpse.Root, _scene);
            return corpse;
        }

        private void Simulate(float seconds)
        {
            PhysicsScene physics = _scene.GetPhysicsScene();
            for (float t = 0f; t < seconds; t += StepSeconds)
            {
                _corpse?.TickCrumple(t);
                physics.Simulate(StepSeconds);
            }
        }

        private void AddFloor()
        {
            GameObject floor = GameObject.CreatePrimitive(PrimitiveType.Plane);
            floor.transform.localScale = new Vector3(10f, 1f, 10f);
            SceneManager.MoveGameObjectToScene(floor, _scene);
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

        private static Transform FindCopy(Transform root, string name)
        {
            if (root.name == name) return root;
            for (int i = 0; i < root.childCount; i++)
            {
                Transform found = FindCopy(root.GetChild(i), name);
                if (found != null) return found;
            }
            return null;
        }
    }
}
