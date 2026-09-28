using System;
using UnityEngine;

namespace Ironfront.Net.Unity.Server
{
    /// <summary>
    /// Where <c>Assembly-CSharp</c> hands this assembly its implementations of
    /// <see cref="IGameplayActorSource"/> and <see cref="ISpawnPointDirectory"/>.
    /// Scene objects both sides write — capture points — live on
    /// <see cref="NetSceneBindings"/> instead.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Registration rather than <c>GetComponent&lt;IGameplayActorSource&gt;()</c>: the component
    /// form would need an adapter <c>MonoBehaviour</c> added to every actor prefab, which is a
    /// change to authored assets in a refactor that is supposed to change no behaviour at all.
    /// A resolver keeps the lookup exactly the <c>GetComponent&lt;Actor&gt;()</c> it replaced,
    /// performed on the far side of the seam.
    /// </para>
    /// <para>
    /// <b>Unset is a supported state, not an error.</b> Nothing registered means every seam
    /// reports absent, which is the branch this code already had for an actor with no
    /// <c>Actor</c> component and for a scene with no spawn points. That is what lets a test
    /// assembly drive these types with no game and no scene.
    /// </para>
    /// </remarks>
    public static class NetServerBindings
    {
        /// <summary>
        /// Produces the gameplay source for a replicated GameObject, or <see langword="null"/>
        /// when that object has none. Called once per actor, from <c>Awake</c>.
        /// </summary>
        public static Func<GameObject, IGameplayActorSource> ActorSourceResolver { get; set; }

        /// <summary>
        /// Produces the gameplay source for a vehicle GameObject, or <see langword="null"/> when
        /// it has none. Called once per vehicle, at spawn. V4 task 2.
        /// </summary>
        public static Func<GameObject, IGameplayVehicleSource> VehicleSourceResolver { get; set; }

        /// <summary>
        /// Installs a network input source on a driver's controller and returns the handle, or
        /// <see langword="null"/> when that GameObject has no controller. V5 task 5.
        /// </summary>
        /// <remarks>
        /// Installing rather than merely resolving: the call it has to make
        /// (<c>FpsActorController.SetInputSource</c>) and the type it has to pass
        /// (<c>NetInputSource</c>) both live in <c>Assembly-CSharp</c>, which no asmdef can
        /// reference. See <see cref="IDriverInputSink"/>.
        /// </remarks>
        public static Func<GameObject, IDriverInputSink> DriverInputSinkResolver { get; set; }

        /// <summary>
        /// Produces the bot brain steering a replicated body, or <see langword="null"/> when
        /// that body has none. Called once per actor, from <c>Awake</c>. Phase-3A.
        /// </summary>
        public static Func<GameObject, IAiDriver> AiDriverResolver { get; set; }

        /// <summary>
        /// Builds one player-slot body on the given team, or <see langword="null"/> when this
        /// process cannot make one. Phase-3A.
        /// </summary>
        /// <remarks>
        /// <para>
        /// A factory rather than a prefab reference on <see cref="NetServerBootstrap"/>. The
        /// body is one of the game's own AI characters and creating one correctly means calling
        /// <c>Actor.SetTeam</c> — a type this assembly cannot name, and a step that colours the
        /// renderer and is what every other spawn path in the game does. Handing over a prefab
        /// and instantiating it here would produce a body on team 0 with the wrong material,
        /// which is a difference nothing would report.
        /// </para>
        /// <para>
        /// It also keeps the slot count out of the scene asset. A serialized prefab field is one
        /// more thing that can be left unwired in a scene that still starts cleanly and then
        /// admits nobody — the exact shape of failure the fail-closed remark on
        /// <c>NetServerBootstrap.RegisterTicketValidator</c> exists to prevent.
        /// </para>
        /// </remarks>
        public static Func<byte, GameObject> PlayerBodyFactory { get; set; }

        /// <summary>The scene's spawn points, or <see langword="null"/> when unavailable.</summary>
        public static ISpawnPointDirectory SpawnPoints { get; set; }

        /// <summary>
        /// Forces named weapons into loadout slots instead of the random draw. Null — the
        /// default, and the shipped configuration — means every slot keeps its draw, so this
        /// changes nothing until something installs a directory. Ledger <b>X-27</b>.
        /// </summary>
        public static ILoadoutDirectory Loadouts { get; set; }

        /// <summary>
        /// The pending deploy selection set by <c>ServerCombatBridge.PlaceAtSpawn</c>, or null.
        /// See <see cref="DeployLoadoutSelection"/>'s own remarks for why this exists and the
        /// guards <see cref="TryConsumeDeploySelection"/> applies around it. Never read directly
        /// by a caller outside this class — go through that method, which owns the guard.
        /// </summary>
        private static DeployLoadoutSelection? _pendingDeploySelection;

        /// <summary>
        /// Stamps the deploy selection <paramref name="actorId"/>'s next <c>EquipLoadout</c> call
        /// should arm from. Valid for exactly one consume — see
        /// <see cref="TryConsumeDeploySelection"/>.
        /// </summary>
        public static void SetPendingDeploySelection(DeployLoadoutSelection selection)
            => _pendingDeploySelection = selection;

        /// <summary>
        /// Consumes the pending deploy selection for <paramref name="actorId"/>, or answers
        /// false when there is none. One-shot regardless of outcome: the pending value is
        /// cleared here whether or not the id matched, so a stale value can never be read twice.
        /// </summary>
        /// <remarks>
        /// A value stamped for a DIFFERENT actor id is refused and logged rather than armed onto
        /// the wrong body — see <see cref="DeployLoadoutSelection"/>'s own remarks for why that
        /// window exists and why it must be audible rather than silently absorbed.
        /// </remarks>
        public static bool TryConsumeDeploySelection(ushort actorId, out DeployLoadoutSelection selection)
        {
            selection = default;
            if (!_pendingDeploySelection.HasValue) return false;

            DeployLoadoutSelection pending = _pendingDeploySelection.Value;
            _pendingDeploySelection = null;

            if (pending.ActorId != actorId)
            {
                Debug.LogError(
                    $"[net] a deploy selection stamped for actor {pending.ActorId} was still "
                    + $"pending when actor {actorId} called EquipLoadout -- two deploys landed "
                    + "inside each other's window. Refusing to arm the wrong body; falling back "
                    + "to the server's own draw for this one.");
                return false;
            }

            selection = pending;
            return true;
        }

        /// <summary>
        /// Resolves the gameplay source for <paramref name="gameObject"/>, or
        /// <see langword="null"/> when nothing is registered or the object has no actor.
        /// </summary>
        public static IGameplayActorSource ResolveActorSource(GameObject gameObject)
            => ActorSourceResolver?.Invoke(gameObject);

        /// <summary>
        /// Resolves the gameplay source for a vehicle, or <see langword="null"/> when nothing is
        /// registered or the object is not a vehicle.
        /// </summary>
        public static IGameplayVehicleSource ResolveVehicleSource(GameObject gameObject)
            => VehicleSourceResolver?.Invoke(gameObject);

        /// <summary>
        /// Installs a network input source on a driver, or returns <see langword="null"/> when
        /// nothing is registered or the actor has no controller.
        /// </summary>
        public static IDriverInputSink AttachDriverInput(GameObject gameObject)
            => DriverInputSinkResolver?.Invoke(gameObject);

        /// <summary>
        /// Resolves the bot brain for <paramref name="gameObject"/>, or <see langword="null"/>
        /// when nothing is registered or the body drives itself no other way.
        /// </summary>
        public static IAiDriver ResolveAiDriver(GameObject gameObject)
            => AiDriverResolver?.Invoke(gameObject);

        /// <summary>
        /// Builds one player-slot body, or <see langword="null"/> when nothing is registered.
        /// </summary>
        public static GameObject CreatePlayerBody(byte team)
            => PlayerBodyFactory?.Invoke(team);

        /// <summary>Clears every seam. For tests, and for a clean re-install.</summary>
        public static void Clear()
        {
            ActorSourceResolver = null;
            VehicleSourceResolver = null;
            DriverInputSinkResolver = null;
            AiDriverResolver = null;
            PlayerBodyFactory = null;
            SpawnPoints = null;
            Loadouts = null;

            // The scene registry is cleared here too, so a test that resets the server
            // seams does not leave a capture-point directory installed behind it. Moving
            // CapturePoints to NetSceneBindings must not quietly change what Clear clears.
            NetSceneBindings.Clear();
        }
    }
}
