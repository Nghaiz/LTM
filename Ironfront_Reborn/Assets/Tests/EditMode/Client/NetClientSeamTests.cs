using NUnit.Framework;
using UnityEngine;

namespace Ironfront.Net.Unity.Client.Tests
{
    /// <summary>
    /// The EditMode suite phase C4 exists to make possible. Every test here drives a
    /// <c>Net/Client</c> type with no scene, no game object graph and no <c>Assembly-CSharp</c>,
    /// which was impossible before the folder became an assembly: a test assembly cannot
    /// reference a predefined one, so nothing under <c>Net/Client</c> was reachable from a test
    /// at all.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>These are not "the asmdef landed" tests.</b> Phase C4's acceptance criterion 3 asks for
    /// a test that <em>could not have been written before</em>, and the distinction that matters
    /// is between the two halves of the phase:
    /// </para>
    /// <list type="bullet">
    /// <item><description>
    /// <see cref="IsLocalActor_IsDecidedByRoleAndRig"/> could not have been written before
    /// <b>C4a</b>, and the asmdef is beside the point. Its subject used to take an <c>Actor</c> —
    /// a <c>MonoBehaviour</c> whose <c>aiControlled</c> flag needed a real component, and whose
    /// "is this the local rig" half reached <c>FpsActorController.instance</c>, a scene singleton
    /// no test can install. The seam is what made the question answerable in isolation.
    /// </description></item>
    /// <item><description>
    /// <see cref="AbsentRig_IsSafeToCallAndReportsAbsent"/> pins the null object every presenter
    /// now depends on. Before C4a the equivalent branch was <c>instance == null</c> repeated at
    /// nine call sites, and nothing could assert that all nine agreed.
    /// </description></item>
    /// </list>
    /// <para>
    /// <b>Role is global state and is restored per test.</b> <c>NetContext.Role</c> is a static
    /// that survives between tests in one domain, and a test that left it at <c>Client</c> would
    /// change the answer of every test that ran after it — silently, and differently depending on
    /// run order.
    /// </para>
    /// </remarks>
    public sealed class NetClientSeamTests
    {
        private NetRole _role;
        private ILocalPlayerRig _rig;

        [SetUp]
        public void CaptureGlobals()
        {
            _role = NetContext.Role;
            _rig  = NetClientBindings.LocalPlayer;
        }

        [TearDown]
        public void RestoreGlobals()
        {
            NetContext.SetRole(_role);
            NetClientBindings.LocalPlayer = _rig;
        }

        /// <summary>
        /// The predicate that replaced <c>!aiControlled</c>, across the three inputs that decide
        /// it. phase-V10 finding A16, made testable by phase C4a.
        /// </summary>
        /// <remarks>
        /// The offline row is the one worth reading twice: at <c>Offline</c> the answer is
        /// <c>!IsAiControlled</c> regardless of the rig, which is literally the shipped
        /// single-player test. That is how V10 preserved offline behaviour rather than believing
        /// it had — and until now, nothing asserted it.
        /// </remarks>
        [Test]
        public void IsLocalActor_IsDecidedByRoleAndRig()
        {
            var human = new FakeActorPresence { IsAiControlled = false, IsLocalPlayerBody = false };
            var bot   = new FakeActorPresence { IsAiControlled = true,  IsLocalPlayerBody = false };
            var mine  = new FakeActorPresence { IsAiControlled = false, IsLocalPlayerBody = true };

            NetContext.SetRole(NetRole.Offline);
            Assert.IsTrue(NetClientPresenterGuard.IsLocalActor(human),
                "offline, the policy is !aiControlled — the shipped single-player test.");
            Assert.IsFalse(NetClientPresenterGuard.IsLocalActor(bot),
                "a bot is never the local player, in any role.");

            NetContext.SetRole(NetRole.Client);
            Assert.IsTrue(NetClientPresenterGuard.IsLocalActor(mine),
                "at the client role the rig decides, and this body is the one it drives.");
            Assert.IsFalse(NetClientPresenterGuard.IsLocalActor(human),
                "A REMOTE HUMAN IS NOT THE LOCAL PLAYER. This is finding A16 exactly: "
                + "!aiControlled coincided with 'is me' only while I was the only human in "
                + "the process, and a remote player taking damage wrote my health bar.");
        }

        /// <summary>A destroyed body answers false rather than throwing.</summary>
        /// <remarks>
        /// <c>IGameplayActorPresence.Exists</c> exists for this: an interface reference to a
        /// destroyed <c>UnityEngine.Object</c> stays non-null, so the plain <c>actor == null</c>
        /// the guard used to perform would have said "still here" over a corpse.
        /// </remarks>
        [Test]
        public void IsLocalActor_RejectsADestroyedBody()
        {
            NetContext.SetRole(NetRole.Client);

            var destroyed = new FakeActorPresence
            {
                Exists = false, IsAiControlled = false, IsLocalPlayerBody = true,
            };

            Assert.IsFalse(NetClientPresenterGuard.IsLocalActor(destroyed));
            Assert.IsFalse(NetClientPresenterGuard.IsLocalActor(null),
                "a null presence is not the local player either.");
        }

        /// <summary>
        /// With nothing registered, the rig reports absent and every member is safe to call.
        /// </summary>
        /// <remarks>
        /// This is the contract nine presenter call sites rely on: they check <c>Exists</c> and
        /// otherwise take the branch they already had for <c>instance == null</c>. A null
        /// reference here would be an NRE on a headless client at the first explosion.
        /// </remarks>
        [Test]
        public void AbsentRig_IsSafeToCallAndReportsAbsent()
        {
            NetClientBindings.LocalPlayer = null;

            ILocalPlayerRig rig = NetClientBindings.LocalPlayer;

            Assert.IsNotNull(rig, "the property never yields null; absence is a real object.");
            Assert.IsFalse(rig.Exists);
            Assert.IsNull(rig.InputSource);
            Assert.IsFalse(rig.CanApplyScreenshake);
            Assert.IsNull(rig.GameObject);
            Assert.IsFalse(rig.IsInputEnabled);
            Assert.IsFalse(rig.HasFellableBody);
            Assert.IsFalse(rig.IsDriving(new FakeActorPresence()));
            Assert.AreEqual(Vector3.zero, rig.Position);
            Assert.AreEqual(0f, rig.YawDegrees);

            Assert.DoesNotThrow(() =>
            {
                rig.EnableInput();
                rig.DisableInput();
                rig.EnterDeployedView();
                rig.ApplyScreenshake(1f, 2);
                rig.SetInputSource(null);
                rig.FellBody(Vector3.up, HumanBodyBones.Hips);
            });
        }

        /// <summary>A registered rig is handed back, not swallowed by the null object.</summary>
        /// <remarks>
        /// The green twin of the test above. Without it, <c>LocalPlayer</c> could be returning
        /// the null object unconditionally and every assertion above would still pass — which is
        /// a check that cannot fail, and therefore not a check.
        /// </remarks>
        [Test]
        public void RegisteredRig_IsTheOneHandedBack()
        {
            var rig = new FakeLocalPlayerRig();
            NetClientBindings.LocalPlayer = rig;

            Assert.AreSame(rig, NetClientBindings.LocalPlayer);
            Assert.IsTrue(NetClientBindings.LocalPlayer.Exists);
        }

        /// <summary>An unregistered hitmarker is silent rather than fatal.</summary>
        [Test]
        public void ShowHit_IsSilentWithNoHud()
        {
            IHitmarkerHud hud = NetClientBindings.Hud;
            try
            {
                NetClientBindings.Hud = null;
                Assert.DoesNotThrow(() => NetClientBindings.ShowHit(2));
            }
            finally
            {
                NetClientBindings.Hud = hud;
            }
        }

        /// <summary>
        /// With no catalogue reader registered, the projectile catalogue is empty rather than
        /// null.
        /// </summary>
        /// <remarks>
        /// A tracker built on null throws on the first projectile; one built on an empty
        /// catalogue draws nothing and counts the kinds it could not render, which is what a
        /// build with no projectile prefabs already did.
        /// </remarks>
        [Test]
        public void ProjectileCatalog_IsEmptyRatherThanNullWithNoReader()
        {
            var reader = NetClientBindings.ProjectileCatalogReader;
            try
            {
                NetClientBindings.ProjectileCatalogReader = null;
                Assert.IsNotNull(NetClientBindings.BuildProjectileCatalog(null));
            }
            finally
            {
                NetClientBindings.ProjectileCatalogReader = reader;
            }
        }

        private sealed class FakeActorPresence : IGameplayActorPresence
        {
            public bool Exists { get; set; } = true;
            public bool IsAiControlled { get; set; }
            public bool IsLocalPlayerBody { get; set; }

            public bool HasRagdollRig => false;
            public bool IsRagdollActive => false;
            public Rigidbody MainRagdollBody => null;
            public void KnockOver(Vector3 force) { }
            public void KnockOver(Vector3 force, HumanBodyBones bone) { }
            public void RestoreFromRagdoll() { }
            public IGameplayWeapon ActiveWeapon => null;

            public bool TryGetWeaponByNetworkId(byte networkId, out IGameplayWeapon weapon)
            {
                weapon = null;
                return false;
            }
        }

        private sealed class FakeLocalPlayerRig : ILocalPlayerRig
        {
            public bool Exists => true;
            public IInputSource InputSource => null;
            public GameObject GameObject => null;
            public bool IsInputEnabled => false;
            public void SetInputSource(IInputSource source) { }
            public void EnableInput() { }
            public void DisableInput() { }
            public void EnterDeployedView() { }
            public bool IsDriving(IGameplayActorPresence actor) => false;
            public Vector3 Position => Vector3.zero;
            public float YawDegrees => 0f;
            public bool CanApplyScreenshake => false;
            public void ApplyScreenshake(float magnitude, int iterations) { }
            public bool HasFellableBody => false;
            public void FellBody(Vector3 force, HumanBodyBones bone) { }
        }
    }
}
