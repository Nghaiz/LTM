using Ironfront.Net.Protocol;
using Ironfront.Net.Replication.Client;
using Ironfront.Net.Replication.Combat;
using UnityEngine;

namespace Ironfront.Net.Unity.Client
{
    /// <summary>
    /// Turns <c>S_EXPLOSION</c> into a blast this client can see, hear and feel — and lets this
    /// client's own grenade or rocket go off the instant it detonates rather than one round-trip
    /// late. phase-V10 task 10.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Supersedes V1 Task 4 (V10 D14).</b> V1 wired the emitter and the earshot filter; its own
    /// client subscriber was struck in favour of this file, which carries the prediction branch
    /// V1's version never had.
    /// </para>
    /// <para>
    /// <b>Your own blast is predicted, the confirmation is suppressed (V10 D13).</b> This
    /// overrides V1 D6 using V1 D6's own recorded fallback clause — no new decision, an earlier
    /// one taken. <see cref="PredictLocalExplosion"/> plays the effect immediately and records the
    /// prediction; the matching <c>S_EXPLOSION</c> then arrives, matches by
    /// <c>SourceActorId</c>, and is dropped by <see cref="ExplosionSuppressor"/>. Accepted cost: an
    /// unconfirmed prediction shows one phantom blast with no damage, bounded by the suppressor's
    /// window — never a swallowed real one.
    /// </para>
    /// <para>
    /// <b>The environment sentinel needs no special case.</b>
    /// <see cref="DeathMessage.EnvironmentKiller"/> (0xFFFF) is not a legal actor id, so it can
    /// never equal a local one and a world-sourced blast is therefore never suppressed — correct
    /// by construction. <see cref="ExplosionSuppressor"/> already encodes this; nothing here
    /// re-checks it, and nothing here should ever grow a branch for it.
    /// </para>
    /// <para>
    /// <b>This applies no health damage.</b> Health arrives in the snapshot, same as every other
    /// remote actor field. Corpse ragdoll impulse from an explosion stays exactly as it is today:
    /// <c>ActorManager.Explode</c>'s own client-role branch already keeps
    /// <c>ApplyRigidbodyForce</c> on corpses locally (AD-4 — corpses are never replicated), and
    /// this presenter does not duplicate it.
    /// </para>
    /// <para>
    /// <b>An unknown <see cref="ExplosionKind"/> draws nothing rather than throwing</b> — carried
    /// from V1 Task 4's rule with the file. The effect array is bounds-checked, never cast and
    /// indexed blind, so a future server sending a kind this build predates costs one missing
    /// flash and nothing else.
    /// </para>
    /// <para>
    /// <b>No handler throws (V10 D22).</b> <c>ClientMessageRouter.Route</c> counts malformed input
    /// rather than throwing; an exception raised from a subscriber would propagate straight into
    /// the transport pump.
    /// </para>
    /// </remarks>
    [DefaultExecutionOrder(-50)]
    [DisallowMultipleComponent]
    public sealed class NetClientExplosionPresenter : MonoBehaviour
    {
        [Tooltip("Indexed by (byte)ExplosionKind. Grenade=0 and Rocket=1 should be filled; "
                 + "Vehicle=2 and Environment=3 may be left empty and must not throw. "
                 + "Client-track item E6.")]
        [SerializeField] private ParticleSystem[] _effectsByKind;

        [Tooltip("Screenshake magnitude per metre of blast radius, fed to "
                 + "PlayerFpParent.ApplyScreenshake.")]
        [SerializeField] private float _shakeMagnitudePerMetre = 1f;

        [Tooltip("Screenshake kick count. Mirrors the fixed iteration counts already used for "
                 + "vehicle heavy-damage and player-damage screenshake.")]
        [SerializeField] private int _shakeIterations = 3;

        [Tooltip("How many blast radii away the screenshake still reaches. Past this the shake "
                 + "is zero, so a distant explosion is seen and not felt.")]
        [SerializeField] private float _shakeRadiusMultiplier = 3f;

        [Tooltip("Scorch decal size per metre of blast radius. There is no scorch DecalType "
                 + "(client-track V7 gap); this reuses Impact the same way a grenade's direct "
                 + "hit does today.")]
        [SerializeField] private float _decalSizePerMetre = 0.5f;

        private NetClientBootstrap _client;

        // Per-connection, fixed-ring, no allocation on the message path. Own instance rather than
        // static: a fresh connection should not inherit a previous match's live predictions.
        private readonly ExplosionSuppressor _suppressor = new ExplosionSuppressor();

        /// <summary>
        /// The presenter this client is running, or null off a client. phase-V1 task 3.
        /// </summary>
        /// <remarks>
        /// Exists so <c>ClientCombatEvents.PredictExplosion</c> can reach it from
        /// <c>ActorManager.Explode</c> without a serialized reference wired in every level.
        /// Same shape as <c>ServerTickLoop.Current</c>, including the reset below, which matters
        /// when Play mode runs with domain reload disabled and statics survive from the previous
        /// session.
        /// </remarks>
        public static NetClientExplosionPresenter Current { get; private set; }

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        private static void ResetCurrentOnLoad() => Current = null;

        private void Awake()
        {
            if (!NetClientPresenterGuard.IsPresentable)
            {
                enabled = false;
                return;
            }

            if (!NetClientPresenterGuard.TryResolveClient(nameof(NetClientExplosionPresenter), out _client))
            {
                enabled = false;
            }
        }

        private void OnEnable()
        {
            if (_client == null) return;

            Current = this;
            _client.Router.OnExplosion += OnExplosion;
        }

        private void OnDisable()
        {
            // Cleared before the unsubscribe so a disabled presenter cannot be handed a
            // prediction it will never draw.
            if (ReferenceEquals(Current, this)) Current = null;

            if (_client == null) return;
            _client.Router.OnExplosion -= OnExplosion;
        }

        /// <summary>
        /// Plays this client's own explosive detonating, before the confirming
        /// <c>S_EXPLOSION</c> can possibly arrive. phase-V10 task 10, decision D13.
        /// </summary>
        /// <remarks>
        /// <para>
        /// Renders immediately, then records the prediction so the confirmation is swallowed
        /// instead of replaying the same blast a second time.
        /// </para>
        /// <para>
        /// <b>If the local actor id has not resolved yet</b> (the welcome message has not
        /// arrived), the effect still plays but no prediction is recorded — there is nothing to
        /// key a suppression on. The confirming message will simply not be suppressed and the
        /// blast will play twice; that is strictly better than not playing it at all.
        /// </para>
        /// </remarks>
        public void PredictLocalExplosion(Vector3 position, float radiusMetres, ExplosionKind kind)
        {
            if (!enabled) return;

            RenderExplosion(position, radiusMetres, kind);

            if (NetClientPresenterGuard.TryResolveLocalActorId(out ushort localId))
                _suppressor.PredictLocal(localId, Time.time);
        }

        private void OnExplosion(ExplosionMessage message)
        {
            // true: this is the confirmation of a blast already drawn by PredictLocalExplosion.
            // Drop it -- rendering it again would double the flash, the shake and the decal.
            if (_suppressor.ShouldSuppress(in message, Time.time)) return;

            Vector3 position = new Vector3(
                Quantize.UnpackPos(message.PosX),
                Quantize.UnpackPos(message.PosY),
                Quantize.UnpackPos(message.PosZ));

            // Through ExplosionEncoding rather than an implicit byte->float widening, for the
            // same reason PosX/Y/Z go through Quantize.UnpackPos: the packing and its inverse
            // are one decision, and V1 task 1 gave it one home. The emitter rounds UP, so this
            // radius is never smaller than the blast that did the damage.
            RenderExplosion(
                position, ExplosionEncoding.UnpackRadiusMetres(message.RadiusMetres), message.Kind);
        }

        private void RenderExplosion(Vector3 position, float radiusMetres, ExplosionKind kind)
        {
            PlayEffect(position, radiusMetres, kind);
            ApplyScreenshake(position, radiusMetres);

            // debt-closure phase 2 task 2d (ledger C-7): a blast now draws a scorch mark rather
            // than the bullet chip it reused for want of an enum member. DecalManager falls back
            // to Impact when Scorch has no authored drawer, so this is safe on a build that
            // predates that authoring. There is still no surface normal on the wire, so this
            // projects straight up rather than raycasting for one; a slightly wrong decal
            // orientation is a cosmetic detail, not a correctness one.
            NetClientBindings.Decals?.AddScorch(
                position, Vector3.up, radiusMetres * _decalSizePerMetre);
        }

        private void PlayEffect(Vector3 position, float radiusMetres, ExplosionKind kind)
        {
            int index = (int)kind;

            // Bounds-checked, never cast-and-indexed blind: an ExplosionKind this build does not
            // know must draw nothing, not throw (carried from V1 Task 4's rule).
            if (_effectsByKind == null || index < 0 || index >= _effectsByKind.Length)
            {
                NetClientPresenterGuard.WarnOnce(
                    "explosion-unknown-kind:" + index,
                    "[net] NetClientExplosionPresenter received an ExplosionKind with no "
                    + "configured effect slot. Drawing nothing for it rather than throwing.");
                return;
            }

            ParticleSystem effect = _effectsByKind[index];
            if (effect == null)
            {
                // Grenade (0) and Rocket (1) are expected to be filled; Vehicle (2) and
                // Environment (3) may legitimately be empty (client-track item E6) -- either way
                // this must not throw, and the warning names which row to fill.
                NetClientPresenterGuard.WarnOnce(
                    "explosion-missing-effect:" + index,
                    "[net] NetClientExplosionPresenter has no ParticleSystem configured for "
                    + $"ExplosionKind {kind}. Client-track item E6.");
                return;
            }

            effect.transform.position = position;
            effect.transform.localScale = Vector3.one * Mathf.Max(radiusMetres, 0.01f);
            effect.Play();
        }

        /// <summary>
        /// Shakes the local camera in proportion to the blast and how close it was.
        /// </summary>
        /// <remarks>
        /// <b>The distance term is not decoration.</b> The server broadcasts an explosion to
        /// every client that could plausibly witness it, which is a far wider set than the ones
        /// standing in it. Scaling by radius alone would kick the camera of a player half the
        /// map away every time a grenade went off — a bug that reads as "the netcode is
        /// shaking my screen at random" rather than as a missing falloff.
        /// </remarks>
        private void ApplyScreenshake(Vector3 position, float radiusMetres)
        {
            ILocalPlayerRig local = NetClientBindings.LocalPlayer;
            if (!local.CanApplyScreenshake) return;

            float audible = radiusMetres * _shakeRadiusMultiplier;
            if (audible <= 0f) return;

            float distance = Vector3.Distance(local.Position, position);
            if (distance >= audible) return;

            float falloff = 1f - distance / audible;
            float magnitude = radiusMetres * _shakeMagnitudePerMetre * falloff;
            if (magnitude <= 0f) return;

            local.ApplyScreenshake(magnitude, _shakeIterations);
        }
    }
}
