using Ironfront.Net.Protocol;
using NUnit.Framework;
using UnityEngine;

namespace Ironfront.Net.Unity.Server.Tests
{
    /// <summary>
    /// Pins <see cref="NetServerActor.BuildStateFlags"/>'s ragdoll bit against the real
    /// producer. EXPECTED RED until that method is made to set it — see remarks.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <c>ActorStateFlags.IsRagdoll</c> (<c>Ironfront.Net.Protocol/Enums/GameplayEnums.cs:64</c>)
    /// has zero producers in shipped code. <c>BuildStateFlags</c> sets <c>IsAlive</c>,
    /// <c>IsAiming</c>, <c>IsCrouching</c> and <c>IsSprinting</c>, and never touches
    /// <c>IsRagdoll</c> — the bit is false on every snapshot the server has ever sent.
    /// Downstream, <c>RemoteActorView.ApplyRagdoll</c> is edge-triggered on that always-false
    /// bit, so its <c>RestoreFromRagdoll()</c> teardown is unreachable: a respawned player
    /// stays ragdolled and drags their own limp body.
    /// </para>
    /// <para>
    /// <b>A .NET-level gate cannot reach this defect.</b>
    /// <c>Ironfront.Net.Replication.Tests.RemoteActorViewStateTests</c> and
    /// <c>RemoteLocomotionTests</c> both hand-construct <c>ActorStateFlags.IsRagdoll</c> and
    /// feed it straight to the decoder — their own remarks call this out as a "BLIND TEST".
    /// <c>NetServerActor</c>, the actual producer, lives under <c>Assets/Scripts/Net/Server</c>
    /// in the <c>Ironfront.Net.Unity.Server</c> asmdef, which
    /// <c>Ironfront.Net.Replication.Tests.csproj</c> does not link — it references five files
    /// out of <c>Assets/</c>, all carrying explicit "no UnityEngine" guards, and this type is
    /// not among them. Only a Unity EditMode test can exercise the real producer.
    /// </para>
    /// <para>
    /// <b>Why "dead" is the fixture, not a separate "ragdoll" flag.</b>
    /// <see cref="IGameplayActorSource"/> exposes no ragdoll signal of its own — only
    /// <see cref="IGameplayActorSource.IsDead"/>. The enum's own doc comment
    /// ("Dead; the client enables its own ragdoll") and
    /// <c>NetServerActor.ApplyBalanceDamage</c>'s remark (stagger is deliberately NOT
    /// replicated because <c>ActorStateFlags</c> is 8/8 full) both confirm this bit is meant to
    /// track death, not a separate physics-ragdoll state — so <c>IsDead</c> is the correct, and
    /// only, signal <c>BuildStateFlags</c> has to work from.
    /// </para>
    /// </remarks>
    public sealed class NetServerActorRagdollFlagTests
    {
        private sealed class FakeGameplayActor : IGameplayActorSource
        {
            public bool Exists { get; set; } = true;
            public float Health { get; set; } = 100f;
            public bool IsDead { get; set; }

            public void ApplyBalanceDamage(float balanceDamage) { }

            public bool TryGetActiveWeaponNetworkId(out byte networkId)
            {
                networkId = 0;
                return false;
            }

            public void SwitchWeapon(int slot) { }

            public void EquipLoadout() { }

            public bool FireCarriedWeapon(float directionX, float directionY, float directionZ)
                => false;
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
        /// Builds the actor and performs the binding <c>Awake</c> would have. Unity does not run
        /// <c>Awake</c> on <c>AddComponent</c> outside play mode, so the explicit bind stands in
        /// for the callback EditMode never fires — same pattern as
        /// <c>NetServerActorSeamTests.CreateActor</c>.
        /// </summary>
        private NetServerActor CreateActor(IGameplayActorSource source)
        {
            NetServerBindings.ActorSourceResolver = _ => source;
            _gameObject = new GameObject(nameof(NetServerActorRagdollFlagTests));

            var actor = _gameObject.AddComponent<NetServerActor>();
            actor.BindGameplaySource(NetServerBindings.ResolveActorSource(_gameObject));
            return actor;
        }

        [Test]
        public void ADeadActorsSnapshotSetsTheRagdollBit()
        {
            var gameplay = new FakeGameplayActor { IsDead = true };
            NetServerActor actor = CreateActor(gameplay);

            ActorStateFlags flags = actor.BuildStateFlags();

            Assert.IsTrue((flags & ActorStateFlags.IsRagdoll) != 0,
                "a dead actor's snapshot must carry IsRagdoll, or the client's edge-triggered " +
                "RestoreFromRagdoll() can never fire and a respawned player stays ragdolled, " +
                "dragging their own limp body");
        }

        [Test]
        public void ALiveStandingActorsSnapshotClearsTheRagdollBit()
        {
            var gameplay = new FakeGameplayActor { IsDead = false };
            NetServerActor actor = CreateActor(gameplay);

            ActorStateFlags flags = actor.BuildStateFlags();

            Assert.IsFalse((flags & ActorStateFlags.IsRagdoll) != 0,
                "a live actor must never report ragdoll, or every client renders it limp on " +
                "the ground while it is still standing");
        }
    }
}
