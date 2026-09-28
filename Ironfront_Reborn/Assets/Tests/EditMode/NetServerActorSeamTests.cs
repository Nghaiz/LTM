using NUnit.Framework;
using UnityEngine;

namespace Ironfront.Net.Unity.Server.Tests
{
    /// <summary>
    /// Pins the three replicated fields <c>NetServerActor</c> reads off the gameplay actor.
    /// </summary>
    /// <remarks>
    /// The weapon id is the reason this suite exists. It was a serialized field the snapshot
    /// read and nothing ever wrote, so every actor in every <c>S_SPAWN</c> and every
    /// <c>S_WEAPON_FIRE</c> reported weapon 0 — a legal value meaning "unknown", which is why
    /// nothing anywhere reported an error. A test could not have caught it before: this type
    /// lived in <c>Assembly-CSharp</c>, which no test assembly can reference.
    /// </remarks>
    public sealed class NetServerActorSeamTests
    {
        private sealed class FakeGameplayActor : IGameplayActorSource
        {
            internal bool HoldsAWeapon = true;
            internal byte HeldWeaponNetworkId = 7;
            internal Vector3 Velocity = Vector3.zero;

            public bool Exists { get; set; } = true;
            public float Health { get; set; } = 100f;
            public bool IsDead { get; set; }
            public bool IsSubmerged { get; set; }
            public bool IsCrouching { get; set; }
            public int SeatAnimation { get; set; }
            public bool IsRagdolledAlive { get; set; }
            public bool IsInWater { get; set; }
            internal Vector3 RagdollPelvis;
            internal Bounds RagdollHead;
            internal Bounds RagdollBody;

            public bool TryGetRagdollPose(out Vector3 pelvis, out Bounds head, out Bounds body)
            {
                pelvis = RagdollPelvis;
                head = RagdollHead;
                body = RagdollBody;
                return IsRagdolledAlive;
            }

            public string DescribeSubmersion() => "fake";

            /// <summary>Stagger the seam carried since phase-V2. Recorded, not simulated.</summary>
            internal float BalanceDamageTaken;

            public void ApplyBalanceDamage(float balanceDamage) => BalanceDamageTaken += balanceDamage;

            public bool TryGetActiveWeaponNetworkId(out byte networkId)
            {
                networkId = HoldsAWeapon ? HeldWeaponNetworkId : (byte)0;
                return HoldsAWeapon;
            }

            /// <summary>Every slot this seam was asked for, in order. The edge is what is under
            /// test, so the COUNT matters as much as the values.</summary>
            internal readonly System.Collections.Generic.List<int> SwitchedSlots =
                new System.Collections.Generic.List<int>();

            public void SwitchWeapon(int slot) => SwitchedSlots.Add(slot);

            /// <summary>How many times the body was armed. The join path calls this once.</summary>
            internal int LoadoutsEquipped;

            public void EquipLoadout() => LoadoutsEquipped++;

            /// <summary>Every direction the carried weapon was fired along. Ledger X-42.</summary>
            internal readonly System.Collections.Generic.List<Vector3> FiredDirections =
                new System.Collections.Generic.List<Vector3>();

            /// <summary>
            /// Every origin the authority supplied with those shots, in the same order.
            /// </summary>
            /// <remarks>
            /// Separate from <see cref="FiredDirections"/> rather than folded in, because the two
            /// became independent the moment the origin stopped being read off the weapon: a shot
            /// can now be fired along the right direction from the wrong place, which is the
            /// defect this pair exists to tell apart.
            /// </remarks>
            internal readonly System.Collections.Generic.List<Vector3> FiredOrigins =
                new System.Collections.Generic.List<Vector3>();

            public bool FireCarriedWeapon(
                float originX, float originY, float originZ,
                float directionX, float directionY, float directionZ)
            {
                if (!HoldsAWeapon) return false;

                FiredOrigins.Add(new Vector3(originX, originY, originZ));
                FiredDirections.Add(new Vector3(directionX, directionY, directionZ));
                return true;
            }

            public bool ReleaseCarriedThrowable(
                float originX, float originY, float originZ,
                float directionX, float directionY, float directionZ)
                => FireCarriedWeapon(
                    originX, originY, originZ, directionX, directionY, directionZ);

            /// <summary>Every aim the carried weapon was handed, in order.</summary>
            internal readonly System.Collections.Generic.List<(Vector3 Eye, Vector3 Forward, bool Aim)> Steers =
                new System.Collections.Generic.List<(Vector3, Vector3, bool)>();

            public void SteerCarriedWeapon(
                float eyeX, float eyeY, float eyeZ,
                float forwardX, float forwardY, float forwardZ,
                bool aimHeld)
                => Steers.Add((new Vector3(eyeX, eyeY, eyeZ), new Vector3(forwardX, forwardY, forwardZ), aimHeld));

            /// <summary>What the carried weapon answers when a trigger is offered: an unlocked
            /// Javelin keeps it.</summary>
            internal bool WithholdsTrigger;

            /// <summary>Every direction a trigger was offered along.</summary>
            internal readonly System.Collections.Generic.List<Vector3> OfferedTriggers =
                new System.Collections.Generic.List<Vector3>();

            public bool TryWithholdCarriedTrigger(float forwardX, float forwardY, float forwardZ)
            {
                OfferedTriggers.Add(new Vector3(forwardX, forwardY, forwardZ));
                return WithholdsTrigger;
            }

            /// <summary>How many times the authority's weapon state was mirrored in.</summary>
            internal int Mirrors;

            /// <summary>The last triple the mirror was handed, so a test can grade it.</summary>
            internal (int AmmoInClip, bool Unholstered, float Elapsed) LastMirror;

            public void MirrorAuthorityWeaponState(
                int ammoInClip, bool unholstered, float elapsedSinceLastShot)
            {
                Mirrors++;
                LastMirror = (ammoInClip, unholstered, elapsedSinceLastShot);
            }

            public bool FireMountedWeapon() => false;

            public bool DeclareMountedWeapon() => false;

            public void GetVelocity(out float x, out float y, out float z)
            {
                x = Velocity.x;
                y = Velocity.y;
                z = Velocity.z;
            }
        }

        private GameObject _gameObject;

        [TearDown]
        public void TearDown()
        {
            // DestroyImmediate, not Destroy: an EditMode test has no frame boundary for a
            // deferred destroy to land on, and OnDisable is what unregisters the actor.
            if (_gameObject != null) Object.DestroyImmediate(_gameObject);
            NetServerBindings.Clear();
        }

        /// <summary>
        /// Builds the actor and performs the binding <c>Awake</c> would have.
        /// </summary>
        /// <remarks>
        /// Unity does not run <c>Awake</c> on <c>AddComponent</c> outside play mode, so
        /// registering a resolver and trusting the component to call it would leave every
        /// assertion below measuring the no-actor fallback — green, and proving nothing. The
        /// resolver is still registered so the production path is the one under test; the
        /// explicit bind is only standing in for the callback EditMode never fires.
        /// </remarks>
        private NetServerActor CreateActor(IGameplayActorSource source)
        {
            NetServerBindings.ActorSourceResolver = _ => source;
            _gameObject = new GameObject(nameof(NetServerActorSeamTests));

            var actor = _gameObject.AddComponent<NetServerActor>();
            actor.BindGameplaySource(NetServerBindings.ResolveActorSource(_gameObject));
            return actor;
        }

        [Test]
        public void WeaponIdIsTheIdOfTheWeaponTheActorIsHolding()
        {
            var gameplay = new FakeGameplayActor { HoldsAWeapon = true, HeldWeaponNetworkId = 7 };
            NetServerActor actor = CreateActor(gameplay);

            Assert.AreEqual(7, actor.WeaponId);
        }

        [Test]
        public void WeaponIdFallsBackToTheSerializedIdWhenNothingIsHeld()
        {
            // Holstered everything. "Holding nothing" and "holding weapon 0" are different
            // facts, and only the first falls back.
            var gameplay = new FakeGameplayActor { HoldsAWeapon = false };
            NetServerActor actor = CreateActor(gameplay);
            actor.WeaponId = 3;

            Assert.AreEqual(3, actor.WeaponId);
        }

        [Test]
        public void WeaponIdFallsBackForAReplicatedObjectThatIsNotAnActor()
        {
            // A prop or a bare rig: nothing resolves, so the serialized id is the only copy.
            NetServerActor actor = CreateActor(null);
            actor.WeaponId = 5;

            Assert.AreEqual(5, actor.WeaponId);
        }

        [Test]
        public void HealthReadsAndWritesTheGameplayActorRatherThanACopy()
        {
            var gameplay = new FakeGameplayActor { Health = 42f };
            NetServerActor actor = CreateActor(gameplay);

            Assert.AreEqual(42f, actor.Health, "the snapshot read a second, stale copy");

            actor.Health = 17f;
            Assert.AreEqual(17f, gameplay.Health, "the write did not reach the gameplay actor");
        }

        [Test]
        public void IsAliveIsTheInverseOfTheGameplayDeadFlag()
        {
            var gameplay = new FakeGameplayActor { IsDead = false };
            NetServerActor actor = CreateActor(gameplay);

            Assert.IsTrue(actor.IsAlive);

            // Killed through Actor.Damage. With a plain auto-property here the snapshot would
            // keep reporting a corpse as alive, and every client would render a standing body
            // that is still a valid hitscan target.
            gameplay.IsDead = true;
            Assert.IsFalse(actor.IsAlive);

            actor.IsAlive = true;
            Assert.IsFalse(gameplay.IsDead, "the respawn did not clear the gameplay dead flag");
        }

        [Test]
        public void ADestroyedGameplayActorFallsBackToTheLocalFields()
        {
            // The Unity null check the seam preserves. A plain interface reference stays
            // non-null over a destroyed component; Exists is what still reports the truth.
            var gameplay = new FakeGameplayActor { Health = 42f, Exists = true };
            NetServerActor actor = CreateActor(gameplay);
            Assert.AreEqual(42f, actor.Health);

            gameplay.Exists = false;

            Assert.AreEqual(NetServerActor.DefaultSpawnHealth, actor.Health,
                "a destroyed gameplay actor was still being dereferenced");
            Assert.IsTrue(actor.IsAlive);
        }

        /// <summary>
        /// A held switch bit reaches the seam ONCE, and releasing it re-arms the next press.
        /// </summary>
        /// <remarks>
        /// C_INPUT repeats each frame seven times for redundancy, so "call the seam on every
        /// arrival" would flip a ToggleableItem in and out at tick rate. This is the test that
        /// would go red if the edge were removed.
        /// </remarks>
        [Test]
        public void AHeldWeaponSwitchReachesTheSeamOnceAndAReleaseReArmsIt()
        {
            var fake = new FakeGameplayActor();
            NetServerActor actor = CreateActor(fake);

            Assert.IsTrue(actor.ApplyWeaponSwitchIntent(2), "first press should reach the seam");
            Assert.IsFalse(actor.ApplyWeaponSwitchIntent(2), "a held bit must not repeat");
            Assert.IsFalse(actor.ApplyWeaponSwitchIntent(2));

            Assert.IsFalse(actor.ApplyWeaponSwitchIntent(-1), "release selects nothing");
            Assert.IsTrue(actor.ApplyWeaponSwitchIntent(2), "the same slot again after a release");

            CollectionAssert.AreEqual(new[] { 2, 2 }, fake.SwitchedSlots);
        }

        /// <summary>A frame that selects nothing never reaches the seam.</summary>
        [Test]
        public void AFrameThatSelectsNothingNeverReachesTheSeam()
        {
            var fake = new FakeGameplayActor();
            NetServerActor actor = CreateActor(fake);

            Assert.IsFalse(actor.ApplyWeaponSwitchIntent(-1));
            Assert.IsFalse(actor.ApplyWeaponSwitchIntent(-1));

            CollectionAssert.IsEmpty(fake.SwitchedSlots);
        }

        // ------------------------------------------------ X-42: the engine's trigger

        [Test]
        public void FiringTheCarriedWeaponReachesTheGameplaySeamWithTheShotsOwnDirection()
        {
            // Ledger X-42. Offline the path is controller.Fire() -> activeWeapon.Fire(...), and
            // on a server a networked body's controller is the SUSPENDED bot brain -- so nothing
            // ever reached the weapon and a thrown grenade was resolved as a hitscan bullet that
            // never detonated. This seam is the netcode making that call itself.
            //
            // The DIRECTION is asserted, not just the count: ServerCombatAuthority.AimDirection
            // negates pitch, and its own remark says an inverted sign produces shots mirrored
            // vertically that still hit at short range -- which is where a thrown grenade lands.
            var gameplay = new FakeGameplayActor();
            NetServerActor actor = CreateActor(gameplay);

            Assert.IsTrue(actor.FireCarriedWeapon(1.5f, 9f, -2.5f, 0.25f, -0.5f, 0.75f));

            Assert.AreEqual(1, gameplay.FiredDirections.Count);
            Assert.AreEqual(new Vector3(0.25f, -0.5f, 0.75f), gameplay.FiredDirections[0]);

            // The ORIGIN travels with it, and separately. Read off the weapon it used to be a rig
            // the server is not standing in; the authority's own answer is the only one that is
            // the same point on every body prefab, so the two must not be able to swap places.
            Assert.AreEqual(1, gameplay.FiredOrigins.Count);
            Assert.AreEqual(new Vector3(1.5f, 9f, -2.5f), gameplay.FiredOrigins[0]);
        }

        // ------------------------------------------------ The Javelin: aim and a kept trigger

        [Test]
        public void SteeringTheCarriedWeaponReachesTheGameplaySeamWithEyeForwardAndAim()
        {
            // The Javelin's lock-on reads where its user LOOKS between shots, and on a server that
            // is nowhere unless the netcode says so: the transform it samples is destroyed on every
            // body that is not the local player (2026-09-27, every online Javelin pull threw). The
            // three facts travel separately so an eye cannot pose as a direction.
            var gameplay = new FakeGameplayActor();
            NetServerActor actor = CreateActor(gameplay);

            actor.SteerCarriedWeapon(1f, 2f, 3f, 0f, -0.5f, 0.75f, aimHeld: true);

            Assert.AreEqual(1, gameplay.Steers.Count);
            Assert.AreEqual(new Vector3(1f, 2f, 3f), gameplay.Steers[0].Eye);
            Assert.AreEqual(new Vector3(0f, -0.5f, 0.75f), gameplay.Steers[0].Forward);
            Assert.IsTrue(gameplay.Steers[0].Aim);
        }

        [Test]
        public void AWeaponThatKeepsTheTriggerIsReportedAsKeepingIt()
        {
            // True is the answer that keeps the round out of the combat authority. If this seam
            // swallowed it, an unlocked Javelin pull would be spent as a shot again.
            var gameplay = new FakeGameplayActor { WithholdsTrigger = true };
            NetServerActor actor = CreateActor(gameplay);

            Assert.IsTrue(actor.TryWithholdCarriedTrigger(0f, 0f, 1f));
            Assert.AreEqual(new Vector3(0f, 0f, 1f), gameplay.OfferedTriggers[0]);
        }

        [Test]
        public void AnOrdinaryTriggerIsNotKept()
        {
            var gameplay = new FakeGameplayActor { WithholdsTrigger = false };
            NetServerActor actor = CreateActor(gameplay);

            Assert.IsFalse(actor.TryWithholdCarriedTrigger(0f, 0f, 1f));
        }

        [Test]
        public void FiringTheCarriedWeaponOfABodyHoldingNothingReportsFalse()
        {
            // Distinguished from "fired and nothing happened" on purpose: the server has just
            // spent a round on a weapon the body does not have, which means the session and the
            // body disagree about the loadout. A silent zero would present as a grenade count
            // going down and an explosion that never happens -- the row itself.
            var gameplay = new FakeGameplayActor { HoldsAWeapon = false };
            NetServerActor actor = CreateActor(gameplay);

            Assert.IsFalse(actor.FireCarriedWeapon(0f, 0f, 0f, 0f, 0f, 1f));
            Assert.AreEqual(0, gameplay.FiredDirections.Count);
        }

        [Test]
        public void ReleasingAThrowableReachesTheGameplaySeamWithOriginAndDirection()
        {
            var gameplay = new FakeGameplayActor();
            NetServerActor actor = CreateActor(gameplay);

            Assert.IsTrue(actor.ReleaseCarriedThrowable(2f, 3f, 4f, -1f, 0.25f, 0.5f));
            Assert.AreEqual(new Vector3(2f, 3f, 4f), gameplay.FiredOrigins[0]);
            Assert.AreEqual(new Vector3(-1f, 0.25f, 0.5f), gameplay.FiredDirections[0]);
        }

        [Test]
        public void CaptureUsesGameplayVelocityForBotsWithoutANetMovementAgent()
        {
            var gameplay = new FakeGameplayActor { Velocity = new Vector3(4f, 0f, -1.25f) };
            NetServerActor actor = CreateActor(gameplay);

            var entry = actor.Capture();
            var velocity = Ironfront.Net.Replication.SnapshotBuilder.UnpackVelocity(in entry);

            float expectedX = Ironfront.Net.Protocol.Quantize.UnpackVel(
                Ironfront.Net.Protocol.Quantize.PackVel(4f));
            Assert.AreEqual(expectedX, velocity.X, 0.001f);
            Assert.AreEqual(0f, velocity.Y, 0.1f);
            float expectedZ = Ironfront.Net.Protocol.Quantize.UnpackVel(
                Ironfront.Net.Protocol.Quantize.PackVel(-1.25f));
            Assert.AreEqual(expectedZ, velocity.Z, 0.001f);
            Assert.IsTrue((entry.StateFlags & Ironfront.Net.Protocol.ActorStateFlags.IsSprinting) != 0,
                "a moving AI actor must not arrive as an idle default-pose proxy");
        }

        /// <summary>
        /// A crouched bot is sent crouched, and its hitboxes are the crouched ones every client
        /// now draws it in.
        /// </summary>
        /// <remarks>
        /// Leftover from the 2026-09-28 hit-registration audit: the IsCrouching bit came only from
        /// a player's movement agent, so a bot crouching behind cover was drawn standing and boxed
        /// standing -- consistent with each other, and with neither matching the bot.
        /// </remarks>
        [TestCase(true)]
        [TestCase(false)]
        public void ABotsCrouchReachesTheSnapshotAndItsHitboxes(bool crouching)
        {
            var gameplay = new FakeGameplayActor { IsCrouching = crouching };
            NetServerActor actor = CreateActor(gameplay);

            bool sent = (actor.BuildStateFlags() & Ironfront.Net.Protocol.ActorStateFlags.IsCrouching) != 0;
            Assert.AreEqual(crouching, sent, "the IsCrouching bit does not follow the bot's own stance");

            var pose = crouching
                ? Ironfront.Net.Replication.Combat.HumanoidPose.Crouched
                : Ironfront.Net.Replication.Combat.HumanoidPose.Standing;
            var expected = Ironfront.Net.Replication.Combat.HitboxSet.Humanoid(
                Ironfront.Net.Replication.Movement.Vec3.Zero, 0f, pose, 0f, 0f).Head;
            var head = actor.CaptureHitboxes().Head;

            Assert.AreEqual(expected.Min.Y, head.Min.Y, 1e-4f, $"the head box is not the {pose} one");
            Assert.AreEqual(expected.Max.Y, head.Max.Y, 1e-4f, $"the head box is not the {pose} one");
        }

        /// <summary>
        /// A player astride the quad bike has a body posed astride, with its head where every
        /// client draws the rider's.
        /// </summary>
        /// <remarks>
        /// Leftover from the 2026-09-28 audit: <c>PresentAsPlayer</c> wrote <c>seated type</c> 0
        /// for every seat, so the engine colliders a projectile meets sat in the chair pose on a
        /// bike whose rider leans 0.28 m further forward.
        /// </remarks>
        [TestCase(0, 0.95f, 0.155f)]
        [TestCase(1, 0.914f, 0.434f)]
        public void ASeatedPlayersBodyTakesItsSeatsPose(int seatAnimation, float headHeight, float headForward)
        {
            GameObject prefab = UnityEditor.AssetDatabase.LoadAssetAtPath<GameObject>(
                "Assets/Prefab/Ai Character Optimizations.prefab");
            _gameObject = (GameObject)UnityEditor.PrefabUtility.InstantiatePrefab(prefab);
            _gameObject.transform.SetPositionAndRotation(Vector3.zero, Quaternion.identity);

            var actor = _gameObject.GetComponent<NetServerActor>();
            actor.BindGameplaySource(new FakeGameplayActor { SeatAnimation = seatAnimation });
            actor.AttachMovementAgent();

            actor.PresentAsPlayer(seated: true, crouching: false, Ironfront.Net.Replication.Movement.Vec3.Zero);

            Animator animator = _gameObject.GetComponentInChildren<Animator>();
            Assert.AreEqual(seatAnimation, animator.GetInteger("seated type"),
                "the body is not posed in its seat's own seated type");

            for (int i = 0; i < 40; i++) animator.Update(0.05f);
            Physics.SyncTransforms();

            Collider head = null;
            foreach (Collider collider in _gameObject.GetComponentsInChildren<Collider>(true))
                if (collider.gameObject.layer == 8 && collider.name == "Bone_004") head = collider;
            Assert.IsNotNull(head, "no Bone_004 on the Hitbox layer");

            Assert.AreEqual(headHeight, head.bounds.center.y, 0.05f, "head height over the seat");
            Assert.AreEqual(headForward, head.bounds.center.z, 0.05f, "head forward of the seat");
        }

        /// <summary>
        /// A bot knocked over or swimming is sent alive AND ragdolled, at its ragdoll's pelvis, and
        /// boxed where the ragdoll lies.
        /// </summary>
        /// <remarks>
        /// Leftover from the 2026-09-28 audit: only death set IsRagdoll, and the snapshot and the
        /// hitboxes used the actor's transform, which stays where the bot fell while the ragdoll is
        /// thrown or floats away. Every client drew it standing there, and shots hit that drawing.
        /// </remarks>
        [Test]
        public void ALiveRagdollIsSentAliveLyingAtItsPelvisAndBoxedWhereItLies()
        {
            var gameplay = new FakeGameplayActor
            {
                IsRagdolledAlive = true,
                RagdollPelvis = new Vector3(5f, 0.3f, -2f),
                RagdollHead = new Bounds(new Vector3(5.8f, 0.2f, -2f), new Vector3(0.3f, 0.3f, 0.3f)),
                RagdollBody = new Bounds(new Vector3(5f, 0.2f, -2f), new Vector3(1.4f, 0.4f, 0.6f)),
            };
            NetServerActor actor = CreateActor(gameplay);
            _gameObject.transform.position = new Vector3(1f, 0f, 1f); // where it fell

            var flags = actor.BuildStateFlags();
            Assert.IsTrue((flags & Ironfront.Net.Protocol.ActorStateFlags.IsAlive) != 0, "a knocked-over bot is not dead");
            Assert.IsTrue((flags & Ironfront.Net.Protocol.ActorStateFlags.IsRagdoll) != 0,
                "a knocked-over bot is not sent lying down, so every client draws it standing");

            var entry = actor.Capture();
            var position = Ironfront.Net.Replication.SnapshotBuilder.UnpackPosition(in entry);
            Assert.AreEqual(5f, position.X, 0.07f, "the snapshot sends where the bot fell, not where its body is");
            Assert.AreEqual(0.3f, position.Y, 0.07f);
            Assert.AreEqual(-2f, position.Z, 0.07f);

            var boxes = actor.CaptureHitboxes();
            Assert.AreEqual(5.8f, boxes.Head.Center.X, 1e-3f, "the head box is not the ragdoll's head");
            Assert.AreEqual(0.15f, boxes.Head.Extents.X, 1e-3f);
            Assert.AreEqual(5f, boxes.Torso.Center.X, 1e-3f, "the body box is not the ragdoll's body");
            Assert.AreEqual(0.7f, boxes.Torso.Extents.X, 1e-3f);
        }

        [Test]
        public void AStandingBotIsNeitherLyingNorMoved()
        {
            var gameplay = new FakeGameplayActor { RagdollPelvis = new Vector3(5f, 0.3f, -2f) };
            NetServerActor actor = CreateActor(gameplay);
            _gameObject.transform.position = new Vector3(1f, 0f, 1f);

            Assert.IsTrue((actor.BuildStateFlags() & Ironfront.Net.Protocol.ActorStateFlags.IsRagdoll) == 0);
            var entry = actor.Capture();
            Assert.AreEqual(1f, Ironfront.Net.Replication.SnapshotBuilder.UnpackPosition(in entry).X, 0.07f);
        }

        [TestCase(true)]
        [TestCase(false)]
        public void TheWaterBitIsTheGamesOwn(bool inWater)
        {
            NetServerActor actor = CreateActor(new FakeGameplayActor { IsInWater = inWater });

            bool sent = (actor.BuildStateFlags() & Ironfront.Net.Protocol.ActorStateFlags.IsInWater) != 0;
            Assert.AreEqual(inWater, sent, "IsInWater does not follow Actor.inWater");
        }

        /// <summary>
        /// Reporting a drowning is not dying of it. Measured 2026-09-27 on Island: a drowned
        /// player's body stayed alive, kept sinking, and <c>TryRespawn</c> refused every deploy
        /// its player sent until the wire floor killed it about 95 s later.
        /// </summary>
        [Test]
        public void ADrownedActorIsDeadNotMerelyReportedDead()
        {
            NetContext.SetRole(NetRole.Server);
            try
            {
                var gameplay = new FakeGameplayActor { IsDead = false, IsSubmerged = true };
                NetServerActor actor = CreateActor(gameplay);
                Assert.IsTrue(actor.IsAlive, "Setup did not start from a living actor.");

                // Time does not advance in an EditMode test, so the clock cannot be waited out and
                // a back-dated observation reads as "never observed". Charge the drowning clock
                // past its limit directly; written back in case the clock is a struct.
                const System.Reflection.BindingFlags Private =
                    System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic;
                System.Reflection.FieldInfo clockField = typeof(NetServerActor).GetField("_drowning", Private);
                object clock = clockField.GetValue(actor);
                clock.GetType().GetField("_submergedSeconds", Private).SetValue(clock, 60f);
                clockField.SetValue(actor, clock);

                actor.ObserveDrowning();

                Assert.IsFalse(
                    actor.IsAlive,
                    "The actor drowned and was reported dead, but is still alive: its body keeps "
                    + "sinking and every respawn its player requests is refused.");
                Assert.IsTrue(gameplay.IsDead, "The gameplay actor was not marked dead.");
                Assert.AreEqual(0f, gameplay.Health, "A drowned actor kept its health.");
            }
            finally
            {
                NetContext.Clear();
            }
        }
    }
}
