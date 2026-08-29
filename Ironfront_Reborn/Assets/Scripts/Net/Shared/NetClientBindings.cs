using System;
using Ironfront.Net.Protocol;
using Ironfront.Net.Replication.Projectiles;
using UnityEngine;

namespace Ironfront.Net.Unity
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
        /// Produces the vehicle body for a spawned GameObject, or <see langword="null"/> when it
        /// carries none. Called once per replicated vehicle, at spawn. Phase C4b.
        /// </summary>
        /// <remarks>
        /// A resolver rather than a registered instance, for the reason the server's
        /// <c>VehicleSourceResolver</c> gives: there are many vehicles and they arrive over the
        /// wire. The component form would need an adapter MonoBehaviour on every vehicle prefab,
        /// which is a change to authored assets in a refactor forbidden from changing behaviour.
        /// </remarks>
        public static Func<GameObject, IGameplayVehicleBody> VehicleBodyResolver { get; set; }

        /// <summary>
        /// Produces the cosmetic projectile body for a spawned GameObject, or
        /// <see langword="null"/> when it carries none. Phase C4b.
        /// </summary>
        public static Func<GameObject, IProjectileBody> ProjectileBodyResolver { get; set; }

        /// <summary>The scene's replicated-vehicle prefabs, or null when unavailable.</summary>
        public static IVehiclePrefabDirectory VehiclePrefabs { get; set; }

        /// <summary>Where a blast leaves a scorch mark, or null when this build draws none.</summary>
        public static IDecalSink Decals { get; set; }

        /// <summary>The match scoreboard, or null when this build has none.</summary>
        public static IObjectiveHud Objectives { get; set; }

        /// <summary>The minimap's icon table, or null when this build draws no minimap.</summary>
        public static IMinimapMarkers Minimap { get; set; }

        /// <summary>
        /// Reads a projectile catalogue off the authored prefab array. Phase C4b.
        /// </summary>
        /// <remarks>
        /// <para>
        /// The prefabs are a <c>GameObject[]</c> — an engine type, which crosses freely — and the
        /// result is a <c>ProjectileCatalog</c> from the replication library, which this assembly
        /// references directly. Only the MIDDLE of the operation is off-limits: reading it means
        /// <c>prefab.GetComponent&lt;Projectile&gt;().configuration</c>, and both of those names
        /// belong to <c>Assembly-CSharp</c>. So the seam is a function, not a type.
        /// </para>
        /// <para>
        /// Unset yields an EMPTY catalogue rather than null. A tracker built on null would throw
        /// on the first projectile; one built on an empty catalogue draws nothing and counts the
        /// kinds it could not render, which is the behaviour a build with no projectile prefabs
        /// already had.
        /// </para>
        /// </remarks>
        public static Func<GameObject[], ProjectileCatalog> ProjectileCatalogReader { get; set; }

        /// <summary>
        /// The catalogue for <paramref name="prefabsByKind"/>, or an empty one when nothing is
        /// registered.
        /// </summary>
        public static ProjectileCatalog BuildProjectileCatalog(GameObject[] prefabsByKind)
            => ProjectileCatalogReader != null
                ? ProjectileCatalogReader(prefabsByKind)
                : new ProjectileCatalog();

        /// <summary>
        /// Resolves the vehicle body for <paramref name="gameObject"/>, or
        /// <see langword="null"/> when nothing is registered or the object is not a vehicle.
        /// </summary>
        public static IGameplayVehicleBody ResolveVehicleBody(GameObject gameObject)
            => VehicleBodyResolver?.Invoke(gameObject);

        /// <summary>
        /// Resolves the projectile body for <paramref name="gameObject"/>, or
        /// <see langword="null"/> when nothing is registered or the object is not a projectile.
        /// </summary>
        public static IProjectileBody ResolveProjectileBody(GameObject gameObject)
            => ProjectileBodyResolver?.Invoke(gameObject);

        /// <summary>
        /// Shows a hitmarker, or does nothing when no HUD is registered.
        /// </summary>
        public static void ShowHit(int severity) => Hud?.ShowHit(severity);

        /// <summary>
        /// Reports the local player's team from the client's current snapshot. Registered by the
        /// client assembly; read through <see cref="NetPresenterGate.TryResolveLocalTeam"/>.
        /// </summary>
        /// <remarks>
        /// A named delegate rather than a <c>Func</c> because the answer is a try-pattern with an
        /// <c>out</c> parameter, and <c>Func</c> cannot express one. The alternative — returning a
        /// nullable team — would have changed the shape of a shipped call site to suit a
        /// refactor, which is the trade this phase refuses everywhere else too.
        /// </remarks>
        public delegate bool LocalTeamResolver(out byte team);

        /// <summary>The registered team resolver, or null on a server and offline.</summary>
        public static LocalTeamResolver LocalTeam { get; set; }

        /// <summary>
        /// Draws this client's own explosion immediately rather than a round-trip late. Registered
        /// by the client assembly's <c>ClientCombatEvents</c>; called from <c>ActorManager</c>,
        /// which may no longer name it. Phase C5b.
        /// </summary>
        /// <remarks>
        /// <b>Unregistered is the server and the offline game, and both already did nothing here.</b>
        /// The predictor's own first line is a <c>NetContext.IsClient</c> test, so a null slot and
        /// a registered-but-inert predictor produce the same behaviour — which is what makes this
        /// a relocation of the call rather than a change to when prediction fires.
        /// </remarks>
        public static Action<IGameplayActorPresence, Vector3, float, ExplosionKind> ExplosionPredictor { get; set; }

        /// <summary>
        /// Predicts <paramref name="source"/>'s own explosion, or does nothing when no client is
        /// running to predict for.
        /// </summary>
        public static void PredictExplosion(
            IGameplayActorPresence source, Vector3 centre, float radiusMetres, ExplosionKind kind)
            => ExplosionPredictor?.Invoke(source, centre, radiusMetres, kind);

        /// <summary>
        /// Clears every registration. See the class remark for why this is not optional.
        /// </summary>
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        private static void ResetOnLoad()
        {
            _localPlayer = null;
            Hud = null;
            VehicleBodyResolver = null;
            ProjectileBodyResolver = null;
            VehiclePrefabs = null;
            Decals = null;
            Objectives = null;
            Minimap = null;
            ProjectileCatalogReader = null;
            LocalTeam = null;
            ExplosionPredictor = null;
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
