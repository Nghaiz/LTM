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
        public void ABodyAtRestIsFrozenOutOfTheSimulation()
        {
            // A corpse lies for 30 s; awake and interpolated it cost every physics step and every
            // frame for nothing, and merely put to sleep it was woken by the heap it lay in (Forest
            // Lake, 100 bots, 2026-10-02). See RemoteRagdoll.Settle.
            AddFloor();
            Animator animator = SpawnProxy(Vector3.zero);
            _corpse = Copy(animator);

            _corpse.Fell(Vector3.forward * 20f, HumanBodyBones.Chest, new Vector3(3f, 0f, 0f), 0.55f);
            Simulate(6f);

            Assert.IsTrue(_corpse.IsSettled, "a body that came to rest six seconds ago was never settled");
            Assert.IsTrue(_corpse.IsAsleep);
            foreach (Rigidbody body in _corpse.Root.GetComponentsInChildren<Rigidbody>())
            {
                Assert.IsTrue(body.isKinematic, $"{body.name} is still simulated while it lies still");
                Assert.IsFalse(body.detectCollisions, $"{body.name} still collides while it lies still");
                Assert.AreEqual(RigidbodyInterpolation.None, body.interpolation,
                    $"{body.name} is still interpolated while it lies still");
            }
        }

        [Test]
        public void ASettledBodyIsBoundedOnceRatherThanEveryFrame()
        {
            AddFloor();
            Animator animator = SpawnProxy(Vector3.zero);
            _corpse = Copy(animator);
            _corpse.Fell(Vector3.forward * 20f, HumanBodyBones.Chest, new Vector3(3f, 0f, 0f), 0.55f);
            SkinnedMeshRenderer[] skins = _corpse.Root.GetComponentsInChildren<SkinnedMeshRenderer>();
            Assert.IsNotEmpty(skins, "Setup: the corpse copied no skinned body");
            foreach (SkinnedMeshRenderer skin in skins)
                Assert.IsTrue(skin.updateWhenOffscreen, "a falling body must be re-bounded as it flies");

            Simulate(6f);
            Assert.IsTrue(_corpse.IsSettled, "Setup: the body did not settle");

            foreach (SkinnedMeshRenderer skin in skins)
            {
                Assert.IsFalse(skin.updateWhenOffscreen, $"{skin.name} is still re-bounded every frame while it lies still");
                Transform space = skin.rootBone != null ? skin.rootBone : skin.transform;
                foreach (Transform bone in skin.bones)
                {
                    if (bone == null) continue;
                    Assert.IsTrue(skin.localBounds.Contains(space.InverseTransformPoint(bone.position)),
                        $"{bone.name} lies outside {skin.name}'s fixed bounds, so the body would be culled while on screen");
                }
            }

            _corpse.ThrowByBlast(8f, _corpse.ChestPosition + new Vector3(0.5f, -0.3f, 0f), 6f, 6f);
            foreach (SkinnedMeshRenderer skin in skins)
                Assert.IsTrue(skin.updateWhenOffscreen, "a body thrown by a blast must be re-bounded as it flies again");
        }

        [Test]
        public void ABodyKilledStandingStillStillFalls()
        {
            // A death a snapshot reported without S_DEATH has no impulse, and a bot standing still
            // has no momentum to carry: nothing touches the new bodies but gravity.
            AddFloor();
            Animator animator = SpawnProxy(Vector3.zero);
            float standingHips = animator.GetBoneTransform(HumanBodyBones.Hips).position.y;
            _corpse = Copy(animator);
            Transform hips = FindCopy(_corpse.Root.transform, animator.GetBoneTransform(HumanBodyBones.Hips).name);

            _corpse.Fell(Vector3.zero, HumanBodyBones.Hips, Vector3.zero, 0.55f);
            bool asleepAtOnce = _corpse.IsAsleep;
            Simulate(3f);

            Assert.Less(hips.position.y, standingHips * 0.6f,
                $"the body never fell: hips at {hips.position.y:F2} m (asleep before the first step: {asleepAtOnce})");
        }

        [Test]
        public void ABodyIsNeverSettledInTheAir()
        {
            // Every part of a body tossed straight up is still for a moment at the top of the arc.
            // Settled there, it would hang in the air until something touched it.
            AddFloor();
            Animator animator = SpawnProxy(Vector3.zero);
            _corpse = Copy(animator, diedAt: -10f);
            Transform hips = FindCopy(_corpse.Root.transform, animator.GetBoneTransform(HumanBodyBones.Hips).name);

            _corpse.Fell(Vector3.zero, HumanBodyBones.Hips, Vector3.up * 8f, 0f);
            float highest = hips.position.y;
            float asleepAt = float.NaN;
            PhysicsScene physics = _scene.GetPhysicsScene();
            for (float t = 0f; t < 6f && float.IsNaN(asleepAt); t += StepSeconds)
            {
                _corpse.TickSettle(t);
                if (_corpse.IsAsleep) asleepAt = hips.position.y;
                physics.Simulate(StepSeconds);
                highest = Mathf.Max(highest, hips.position.y);
            }

            Assert.Greater(highest, 2f, "Setup: the body was not thrown up at all");
            Assert.IsFalse(float.IsNaN(asleepAt), "the body never settled after it landed");
            Assert.Less(asleepAt, 0.6f, $"the body was settled in the air, hips at {asleepAt:F2} m");
        }

        [Test]
        public void ABlastWakesASettledBody()
        {
            AddFloor();
            Animator animator = SpawnProxy(Vector3.zero);
            _corpse = Copy(animator);
            Transform hips = FindCopy(_corpse.Root.transform, animator.GetBoneTransform(HumanBodyBones.Hips).name);
            _corpse.Fell(Vector3.zero, HumanBodyBones.Hips, Vector3.zero, 0f);
            Simulate(6f);
            Assert.IsTrue(_corpse.IsSettled, "Setup: the body did not settle");
            Vector3 lying = hips.position;

            _corpse.ThrowByBlast(8f, lying + new Vector3(0.5f, -0.3f, 0f), 6f, 6f);

            Assert.IsFalse(_corpse.IsAsleep, "a blast beside a settled body left it frozen");
            Assert.IsFalse(_corpse.IsSettled);
            foreach (Rigidbody body in _corpse.Root.GetComponentsInChildren<Rigidbody>())
            {
                Assert.IsFalse(body.isKinematic, $"{body.name} stays frozen after the blast");
                Assert.IsTrue(body.detectCollisions, $"{body.name} flies through the ground after the blast");
                Assert.AreEqual(RemoteRagdoll.InterpolationAt(body.position, Camera.main), body.interpolation,
                    $"{body.name} flies with the interpolation the camera's distance does not call for");
            }
            PhysicsScene physics = _scene.GetPhysicsScene();
            for (int i = 0; i < 25; i++) physics.Simulate(StepSeconds);
            Assert.Greater(Vector3.Distance(hips.position, lying), 0.3f, "the blast did not move the settled body");
        }

        [Test]
        public void AShiveringBodyIsSettledThoughItsPartsNeverSlowDown()
        {
            // A ragdoll on uneven ground shivers: fast parts going nowhere. Judged by speed, most
            // corpses of a 100-bot match never came to rest (census, 2026-10-02).
            AddFloor();
            Animator animator = SpawnProxy(Vector3.zero);
            _corpse = Copy(animator);
            _corpse.Fell(Vector3.zero, HumanBodyBones.Hips, Vector3.zero, 0f);
            Rigidbody forearm = Part(animator, HumanBodyBones.RightLowerArm);

            float t = RunWhileAwake(8f, step => forearm.linearVelocity = new Vector3(step % 2 == 0 ? 0.8f : -0.8f, 0f, 0f), out _);

            Assert.IsTrue(_corpse.IsAsleep, "a body shivering on the spot was never settled");
            Assert.Less(t, RemoteCorpse.TwitchSettleSeconds,
                $"a body shivering on the spot was settled only at {t:F2} s, as a creep rather than as still");
        }

        [Test]
        public void ACreepingBodyIsSettledOnceItHasLainLongEnough()
        {
            // Sliding down a slope, or shoved by the bodies around it: no fall, but more than a shiver.
            AddFloor();
            Animator animator = SpawnProxy(Vector3.zero);
            _corpse = Copy(animator);
            _corpse.Fell(Vector3.zero, HumanBodyBones.Hips, Vector3.zero, 0f);
            Rigidbody[] parts = _corpse.Root.GetComponentsInChildren<Rigidbody>();

            // The whole body, 4 mm a step: 0.1 m in StillSeconds, between StillMetres and
            // TwitchMetres. Moving one part alone does not creep: its joints pull it straight back.
            float t = RunWhileAwake(10f, _ =>
            {
                foreach (Rigidbody part in parts) part.position += new Vector3(0.004f, 0f, 0f);
            }, out float firstAsleep);

            Assert.IsTrue(_corpse.IsAsleep, "a creeping body was never settled");
            Assert.GreaterOrEqual(firstAsleep, RemoteCorpse.TwitchSettleSeconds,
                $"a creeping body was settled at {firstAsleep:F2} s, before it had lain long");
            Assert.Less(t, RemoteCorpse.TwitchSettleSeconds + RemoteCorpse.StillSeconds + 0.3f,
                $"the creeping body was settled only at {t:F2} s");
        }

        // Steps the scene until the corpse sleeps, calling `nudge` after each step from t = 1 s on:
        // after the step, so the nudge is what the settle check reads -- set before, the step's own
        // friction would have taken it out first.
        private float RunWhileAwake(float seconds, System.Action<int> nudge, out float firstAsleep)
        {
            PhysicsScene physics = _scene.GetPhysicsScene();
            firstAsleep = float.NaN;
            float t = 0f;
            for (int step = 0; t < seconds && !_corpse.IsAsleep; step++, t += StepSeconds)
            {
                physics.Simulate(StepSeconds);
                if (t > 1f) nudge(step);
                _corpse.TickSettle(t);
                if (_corpse.IsAsleep && float.IsNaN(firstAsleep)) firstAsleep = t;
            }
            return t;
        }

        private Rigidbody Part(Animator animator, HumanBodyBones bone)
            => FindCopy(_corpse.Root.transform, animator.GetBoneTransform(bone).name).GetComponent<Rigidbody>();

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

        private RemoteCorpse Copy(Animator animator, float diedAt = 0f)
        {
            RemoteCorpse corpse = RemoteCorpse.TryCreate(animator, 7, 0, diedAt);
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
                _corpse?.TickSettle(t);
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
