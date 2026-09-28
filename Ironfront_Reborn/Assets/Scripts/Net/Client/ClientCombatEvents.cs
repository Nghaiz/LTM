using Ironfront.Net.Protocol;
using UnityEngine;

namespace Ironfront.Net.Unity.Client
{
    /// <summary>
    /// The one call the original game's explosion path makes into the client netcode.
    /// phase-V1 task 3.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The mirror of <c>ServerCombatEvents</c>, and static for the same reason its class doc
    /// gives: <c>ActorManager</c> is a scene singleton with no reference to any presenter, and
    /// acquiring one would mean a serialized field wired in every level.
    /// </para>
    /// <para>
    /// <b>Why this exists at all.</b> V10 D13 overrode V1 D6 — a client draws its own blast the
    /// instant it detonates and swallows the server's confirmation, rather than watching its own
    /// grenade go off a round-trip late. V10 shipped both halves of that mechanism
    /// (<c>NetClientExplosionPresenter.PredictLocalExplosion</c> and
    /// <c>ExplosionSuppressor.PredictLocal</c>) and no producer for either: the only code that
    /// knows a local blast happened is <c>ActorManager.Explode</c>'s client branch, which is
    /// V1's to write. This is that producer, and without it D13's prediction path is the same
    /// shape of dead wire V1 exists to close.
    /// </para>
    /// <para>
    /// <b>Only the local player's own blast is predicted.</b> Suppression keys on
    /// <c>SourceActorId</c> matching this client's actor, so predicting somebody else's
    /// explosion would draw it locally AND fail to suppress the confirmation — one blast, two
    /// flashes. Gating on <see cref="NetClientPresenterGuard.IsLocalActor(IGameplayActorPresence)"/> makes that
    /// unreachable rather than unlikely.
    /// </para>
    /// </remarks>
    public static class ClientCombatEvents
    {
        /// <summary>
        /// Hands <see cref="PredictExplosion"/> to <c>NetClientBindings</c>, so
        /// <c>ActorManager</c> can still reach it. Phase C5b.
        /// </summary>
        /// <remarks>
        /// <c>ActorManager.Damage</c> called this by its full name,
        /// <c>Ironfront.Net.Unity.Client.ClientCombatEvents.PredictExplosion(…)</c>, and C5b sealed
        /// that assembly. The call site now goes through <c>NetClientBindings.PredictExplosion</c>
        /// in <c>Ironfront.Net.Unity.Shared</c> and arrives back here.
        /// <para>
        /// <b>Registered unconditionally, including on a dedicated server</b>, for the reason
        /// <c>IronfrontNetBindings.Install</c> gives about every other seam: the method's own first
        /// line is a <c>NetContext.IsClient</c> test, so registering on a headless process costs
        /// one delegate and changes no behaviour. A role test here would be a second copy of a
        /// decision <c>NetContext</c> already owns.
        /// </para>
        /// <para>
        /// <c>BeforeSceneLoad</c>, after <c>NetClientBindings.ResetOnLoad</c> at
        /// <c>SubsystemRegistration</c> — the other order would clear this registration
        /// immediately after making it.
        /// </para>
        /// </remarks>
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
        private static void Install()
            => NetClientBindings.ExplosionPredictor = PredictExplosion;

        /// <summary>
        /// Draws this client's own explosion now and arms the suppression that will swallow the
        /// server's confirmation of it.
        /// </summary>
        /// <remarks>
        /// A no-op off the client, a no-op when the source is not the local player, and a no-op
        /// when no presenter is in the scene. The last of those is silent on purpose: a build
        /// with no explosion presenter still gets the server's <c>S_EXPLOSION</c> and simply
        /// draws nothing early, which is the pre-V10 behaviour rather than a failure.
        /// </remarks>
        /// <param name="source">
        /// Whoever set it off, for the local-player test. Declared as the seam rather than as
        /// <c>Actor</c>, which this assembly may no longer name — the legacy caller passes its
        /// own component and the conversion is implicit, so <c>ActorManager.Explode</c> is
        /// unchanged (phase C4a).
        /// </param>
        /// <param name="radiusMetres">
        /// The radius the damage selection used — the same value the server puts on the wire,
        /// so the predicted effect and the confirmed one are the same size (V1 D4).
        /// </param>
        public static void PredictExplosion(
            IGameplayActorPresence source, Vector3 centre, float radiusMetres, ExplosionKind kind)
        {
            if (!NetContext.IsClient) return;
            if (!NetClientPresenterGuard.IsLocalActor(source)) return;

            NetClientExplosionPresenter presenter = NetClientExplosionPresenter.Current;
            if (presenter == null) return;

            presenter.PredictLocalExplosion(centre, radiusMetres, kind);
        }
    }
}
