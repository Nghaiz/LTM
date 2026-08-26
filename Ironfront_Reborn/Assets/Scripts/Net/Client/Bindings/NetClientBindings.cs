using UnityEngine;

namespace Ironfront.Net.Unity.Client
{
    /// <summary>
    /// Where <c>Assembly-CSharp</c> hands this assembly the client-side singletons it may no
    /// longer name: the local player's rig and the HUD. The client mirror of
    /// <c>NetServerBindings</c>. Phase C4a.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Why registration and not <c>GetComponent</c>.</b> Both members below replace a
    /// <em>static singleton read</em> — <c>FpsActorController.instance</c> and
    /// <c>IngameUi.Hit</c> — not a component lookup. A resolver keeps the call shape identical to
    /// the one it replaces; an adapter component would have to be added to authored prefabs,
    /// which is a change to authored assets in a refactor whose acceptance criteria forbid
    /// changing behaviour at all.
    /// </para>
    /// <para>
    /// <b>Unset is a supported state, not an error.</b> Nothing registered means
    /// <see cref="LocalPlayer"/> reports absent and <see cref="ShowHit"/> is silent — which is
    /// precisely the branch every one of these call sites already had for
    /// <c>instance == null</c>. It is also what lets a test assembly drive these presenters with
    /// no game, no HUD and no scene, which is the deliverable phase C4 exists for.
    /// </para>
    /// <para>
    /// <b>Statics survive a Play session</b> with domain reload disabled, so
    /// <see cref="ResetOnLoad"/> clears them at subsystem registration for the same reason
    /// <c>NetContext.ResetOnLoad</c> and <c>NetClientPresenterGuard.ResetOnLoad</c> exist: a rig
    /// registered by the previous run would otherwise be handed to the next one as a destroyed
    /// object that still answers <c>Exists</c> through a stale adapter.
    /// </para>
    /// </remarks>
    public static class NetClientBindings
    {
        private static ILocalPlayerRig _localPlayer;

        /// <summary>
        /// The local player's rig, or a never-present stand-in when nothing is registered.
        /// </summary>
        /// <remarks>
        /// Never null, so no call site needs a null check on top of the
        /// <see cref="ILocalPlayerRig.Exists"/> check it already had. The absent case is a real
        /// object answering "no" rather than a reference that throws.
        /// </remarks>
        public static ILocalPlayerRig LocalPlayer
        {
            get => _localPlayer ?? AbsentLocalPlayerRig.Instance;
            set => _localPlayer = value;
        }

        /// <summary>The HUD, or null when this build has none.</summary>
        public static IHitmarkerHud Hud { get; set; }

        /// <summary>
        /// Shows a hitmarker, or does nothing when no HUD is registered.
        /// </summary>
        public static void ShowHit(int severity) => Hud?.ShowHit(severity);

        /// <summary>
        /// Clears every registration. See the class remark for why this is not optional.
        /// </summary>
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        private static void ResetOnLoad()
        {
            _localPlayer = null;
            Hud = null;
        }

        /// <summary>
        /// The null object behind <see cref="LocalPlayer"/> when nothing is registered.
        /// </summary>
        /// <remarks>
        /// A NoOp rather than a throw, per <c>rules/library-third-party-decoupling.md</c>: this
        /// sits on per-frame presenter paths, and the absence it represents — a headless process,
        /// a test — is normal rather than exceptional.
        /// </remarks>
        private sealed class AbsentLocalPlayerRig : ILocalPlayerRig
        {
            internal static readonly AbsentLocalPlayerRig Instance = new AbsentLocalPlayerRig();

            public bool Exists => false;
            public IInputSource InputSource => null;
            public void EnableInput() { }
            public void DisableInput() { }
            public bool IsDriving(IGameplayActorPresence actor) => false;
            public Vector3 Position => Vector3.zero;
            public bool CanApplyScreenshake => false;
            public void ApplyScreenshake(float magnitude, int iterations) { }
            public bool HasFellableBody => false;
            public void FellBody(Vector3 force, HumanBodyBones bone) { }
        }
    }
}
