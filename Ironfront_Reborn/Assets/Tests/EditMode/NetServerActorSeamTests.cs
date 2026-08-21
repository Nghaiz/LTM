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

            public bool Exists { get; set; } = true;
            public float Health { get; set; } = 100f;
            public bool IsDead { get; set; }

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
    }
}
