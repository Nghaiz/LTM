using Ironfront.Net.Protocol;
using Ironfront.Net.Replication.Client;
using Ironfront.Net.Replication.Movement;
using UnityEngine;

namespace Ironfront.Net.Unity.Client
{
    /// <summary>
    /// Turns the three combat events the server has been sending all along — death, weapon fire
    /// and hit confirm — into a corpse, a muzzle flash and a hitmarker. phase-V10 tasks 5 and 6.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>All three had zero production subscribers before this phase.</b> The server side of
    /// combat shipped and the client half was never built: <c>ServerActorDamageSink</c> already
    /// documents that <c>Actor.Die()</c> is deliberately not called because "each client runs its
    /// own ragdoll off <c>S_DEATH</c>" — and no client ran anything off it.
    /// <c>ServerEventWriter.WeaponFireAudibleRadius</c> has been filtering by earshot for an
    /// audience that did not exist.
    /// </para>
    /// <para>
    /// <b>Cosmetics only, and the boundary is mechanical.</b> Nothing here spawns a projectile
    /// or applies recoil: <c>Weapon.SpawnProjectile</c> sets <c>source = user</c> and would do
    /// real damage from a client, and <c>Actor.ApplyRecoil</c> chains to the LOCAL camera rig, so
    /// running it for a remote shooter kicks your own view. Both are outside
    /// <c>Weapon.PlayFireCosmetics</c> by construction (V10 D7) and a CI gate asserts that no
    /// file in this folder names either.
    /// </para>
    /// <para>
    /// <b>No handler throws.</b> <c>ClientMessageRouter.Route</c> counts malformed input rather
    /// than throwing, and an exception raised from a subscriber would propagate straight into
    /// the transport pump (V10 D22).
    /// </para>
    /// </remarks>
    [DefaultExecutionOrder(-50)]
    [DisallowMultipleComponent]
    public sealed class NetClientCombatPresenter : MonoBehaviour
    {
        [Tooltip("Draws the streak. Optional — without it a shot still flashes and reports.")]
        [SerializeField] private CosmeticTracerPool _tracers;

        [Tooltip("Resolves an actor id to the body drawing it. Found on this object if unset.")]
        [SerializeField] private RemoteActorRegistry _registry;

        private NetClientBootstrap _client;

        [Tooltip("Draw the killfeed with IMGUI. A stopgap until a real HUD element reads it.")]
        [SerializeField] private bool _drawKillfeed = true;

        private readonly KillfeedModel _killfeed = new KillfeedModel();
        private readonly HitmarkerModel _hitmarker = new HitmarkerModel();
        private readonly PlayerNameTable _names = new PlayerNameTable();

        /// <summary>The last few kills, newest first. Drawn by the HUD; pruned here.</summary>
        public KillfeedModel Killfeed => _killfeed;

        /// <summary>
        /// Actor id to display name, rebuilt from every S_PLAYER_LIST.
        /// debt-closure phase 2 task 2a.
        /// </summary>
        public PlayerNameTable Names => _names;

        /// <summary>The newest confirmed hit and how long it stays up.</summary>
        public HitmarkerModel Hitmarker => _hitmarker;

        private void Awake()
        {
            if (!NetClientPresenterGuard.IsPresentable)
            {
                enabled = false;
                return;
            }

            if (!NetClientPresenterGuard.TryResolveClient(nameof(NetClientCombatPresenter), out _client))
            {
                enabled = false;
                return;
            }

            if (_registry == null) _registry = GetComponent<RemoteActorRegistry>();
            if (_registry == null) _registry = FindObjectOfType<RemoteActorRegistry>();

            if (_registry == null)
            {
                NetClientPresenterGuard.WarnOnce(
                    "combat-no-registry",
                    "[net] NetClientCombatPresenter found no RemoteActorRegistry, so it cannot "
                    + "resolve who died or who fired. Killfeed and hitmarker still work; corpses "
                    + "and muzzle flashes do not.");
            }
        }

        private void OnEnable()
        {
            if (_client == null) return;
            _client.Router.OnDeath += OnDeath;
            _client.Router.OnWeaponFire += OnWeaponFire;
            _client.Router.OnHitConfirm += OnHitConfirm;

            // debt-closure phase 2 task 2a. This subscription is what retires OnPlayerList from
            // ClientWiringGate's KnownUnwiredEvents: the exemption retires on SUBSCRIPTION, and
            // this presenter is the killfeed's owner, so the name table belongs beside it rather
            // than on a component of its own that the scene would then have to carry.
            _client.Router.OnPlayerList += _names.Apply;
        }

        private void OnDisable()
        {
            if (_client == null) return;
            _client.Router.OnDeath -= OnDeath;
            _client.Router.OnWeaponFire -= OnWeaponFire;
            _client.Router.OnHitConfirm -= OnHitConfirm;
            _client.Router.OnPlayerList -= _names.Apply;
            _names.Reset();
        }

        private void Update()
        {
            // KillfeedModel deliberately has no clock of its own, so expiry is the caller's to
            // run. Once a frame, before anything reads it.
            _killfeed.Prune(Time.time);
        }

        /// <summary>
        /// Draws the killfeed with names. debt-closure phase 2 task 2a, acceptance criterion 3.
        /// </summary>
        /// <remarks>
        /// <para>
        /// <b>IMGUI, and deliberately a stopgap.</b> <c>KillfeedModel</c> shipped in phase-02 and
        /// has had no consumer in the entire repository since — the model is exercised by tests
        /// and drawn by nothing, so "the killfeed" has never actually appeared on screen. A real
        /// HUD element belongs on <c>Ingame UI Container.prefab</c>, and phase 2 owns no prefabs
        /// or scenes (they are Phase 1's), so this draws from the presenter that already holds
        /// the model and already sits on the <c>NetClient</c> object. Delete it when a HUD
        /// element reads <see cref="Killfeed"/> and <see cref="Names"/> instead.
        /// </para>
        /// <para>
        /// <b>Nothing here is allocated per frame except the strings IMGUI needs.</b> That is the
        /// honest cost of the stopgap and the second reason it is one; five lines at 60 Hz is
        /// not a per-tick path, and the replacement does not have this problem.
        /// </para>
        /// </remarks>
        private void OnGUI()
        {
            if (!_drawKillfeed) return;
            if (_killfeed.Count == 0) return;

            const float width = 420f;
            const float lineHeight = 20f;

            for (int i = 0; i < _killfeed.Count; i++)
            {
                KillfeedEntry entry = _killfeed[i];

                string killer = entry.KilledByEnvironment
                    ? "The world"
                    : NameFor(entry.KillerActorId);

                string line = killer + (entry.Headshot ? " ▸ " : " → ") + NameFor(entry.VictimActorId);

                GUI.Label(
                    new Rect(Screen.width - width - 12f, 12f + i * lineHeight, width, lineHeight),
                    line);
            }
        }

        /// <summary>
        /// The name S_PLAYER_LIST gave this actor, or the id when no broadcast named it.
        /// </summary>
        /// <remarks>
        /// The fallback is HERE and not in <c>PlayerNameTable</c>, which returns null: only the
        /// caller knows what an unnamed actor should read as, and manufacturing it in the table
        /// would make a genuinely missing name indistinguishable from a real one. "actor 7" is
        /// exactly what this feed rendered for every line before this phase.
        /// </remarks>
        private string NameFor(ushort actorId) => _names.NameOr(actorId, "actor " + actorId);

        /// <summary>
        /// One death message, two consumers: the feed takes the line, the ragdoll takes the
        /// impulse (V10 D19).
        /// </summary>
        /// <remarks>
        /// <c>KillfeedEntry.From</c> drops <c>ForceX/Y/Z</c> — correctly, a text line has no use
        /// for a vector — so the corpse cannot be driven from the feed. <see cref="DeathImpulse"/>
        /// carries it instead. Neither shipped type changes.
        /// </remarks>
        private void OnDeath(DeathMessage message)
        {
            _killfeed.Push(in message, Time.time);

            DeathImpulse impulse = DeathImpulse.From(in message);
            Vector3 force = new Vector3(impulse.Force.X, impulse.Force.Y, impulse.Force.Z);

            // The local player is deliberately absent from the remote registry — it is
            // predicted, not interpolated — so its own death must be resolved first or it
            // looks like an actor this client never heard of.
            var hitbox = (HitboxType)message.HitboxHit;

            if (NetClientPresenterGuard.IsLocalActor(message.VictimActorId))
            {
                KnockOverLocalActor(force, hitbox);
                return;
            }

            if (_registry == null) return;

            // A miss is a NORMAL outcome: the victim died outside this client's interest
            // radius and was never spawned here. The killfeed line is still correct, there is
            // simply no body to fell. This must not log.
            if (!_registry.TryFindView(message.VictimActorId, out RemoteActorView view)) return;

            FellBody(view, force, hitbox);
        }

        /// <summary>
        /// Drops a remote body using the ready-made public pair on <c>Actor</c>.
        /// </summary>
        /// <remarks>
        /// <para>
        /// <c>Actor.KnockOver</c> enables the ragdoll via <c>FallOver()</c> and applies the
        /// impulse in one call, behind its own <c>if (!ragdoll.IsRagdoll())</c> re-entrancy
        /// guard — so a snapshot confirming the same death cannot throw the corpse twice.
        /// </para>
        /// <para>
        /// <b><c>Actor.Die</c> is not called and not widened.</b> It is private with one caller,
        /// and it calls <c>ScoreUi.AddScore</c> — which on a client would double-count against
        /// the server's authoritative <c>S_MATCH_STATE</c>. <c>ServerActorDamageSink</c> already
        /// documents that the netcode deliberately does not call it.
        /// </para>
        /// <para>
        /// <b>The impulse goes to the bone that was hit.</b> debt-closure phase 2 task 2d closed
        /// ledger C-8: <c>ActiveRaggy.RigidbodyForBone</c> resolves through the animator's
        /// humanoid bone map, so it does not depend on rig NAMING the way V10 assumed when it
        /// left this open — a humanoid avatar has already normalised that. A bone the rig does
        /// not simulate falls back to the main body, which is what every corpse used to get.
        /// </para>
        /// </remarks>
        private static void FellBody(RemoteActorView view, Vector3 force, HitboxType hitbox)
        {
            if (view == null) return;

            if (view.Actor != null && view.HasRagdollRig)
            {
                view.Actor.KnockOver(force, BoneFor(hitbox));
                return;
            }

            // Degraded, and loudly. A silent no-op here is indistinguishable from the bug this
            // whole phase exists to close.
            NetClientPresenterGuard.WarnOnce(
                "death-no-rig",
                "[net] a remote actor died but its prefab carries no Actor with a ragdoll rig, "
                + "so the corpse cannot be felled. Hiding the body instead. Client-track item E1.");
            view.gameObject.SetActive(false);
        }

        /// <summary>
        /// Which bone an impulse lands on, from the hitbox S_DEATH carries.
        /// </summary>
        /// <remarks>
        /// <b>Three hitboxes, not a skeleton.</b> The wire carries Body/Head/Limb and nothing
        /// finer, so this maps to three bones and stops. <c>Limb</c> resolves to the right upper
        /// leg rather than to the limb that was actually hit — the byte does not say which — and
        /// that is a visible improvement over the pelvis without pretending to information the
        /// protocol does not carry. Widening it means widening <c>HitboxType</c>, which is a
        /// PROTOCOL_VERSION decision.
        /// </remarks>
        private static HumanBodyBones BoneFor(HitboxType hitbox)
        {
            switch (hitbox)
            {
                case HitboxType.Head: return HumanBodyBones.Head;
                case HitboxType.Limb: return HumanBodyBones.RightUpperLeg;
                default: return HumanBodyBones.Hips;
            }
        }

        private static void KnockOverLocalActor(Vector3 force, HitboxType hitbox)
        {
            // ClientCombatState owns the local player's death STATE — respawn timer, ammo,
            // health — and V10 does not duplicate it. It is a pure model and no Unity component
            // holds one yet, which is a recorded gap, not this presenter's to close. What is
            // this presenter's is that the body falls over: at the client role Actor.Damage
            // never reaches Die() (ownsHealth is false), so without this the local player takes
            // hits, staggers, and stands there dead.
            FpsActorController local = FpsActorController.instance;
            if (local == null || local.actor == null) return;
            if (local.actor.ragdoll == null) return;

            local.actor.KnockOver(force, BoneFor(hitbox));
        }

        /// <summary>
        /// Somebody else's shot: a flash at the right height, one report, and a streak.
        /// </summary>
        /// <remarks>
        /// <para>
        /// <b>Stateless, by decision (V10 D9).</b> Weapon fire rides the cosmetic channel —
        /// unreliable-sequenced, documented safe to drop — so nothing here may read or advance
        /// <c>currentMuzzle</c>. Driving that counter from received events would desynchronise
        /// permanently on the first dropped packet, and would not reproduce on a clean network.
        /// </para>
        /// <para>
        /// <b>No distance test.</b> Earshot filtering is already done server-side by
        /// <c>ServerEventWriter.WeaponFireAudibleRadius</c>. A second filter here would be a
        /// second thing to keep in agreement with the first.
        /// </para>
        /// </remarks>
        private void OnWeaponFire(WeaponFireMessage message)
        {
            // The local player's own shot was already drawn by Weapon.Shoot when the trigger was
            // pulled. Playing the echo would double the flash and the report.
            if (NetClientPresenterGuard.IsLocalActor(message.ShooterActorId)) return;
            if (_registry == null) return;
            if (!_registry.TryFindView(message.ShooterActorId, out RemoteActorView view)) return;

            // A corpse fires nothing. A fire event that crossed a death in flight would
            // otherwise flash a muzzle on a ragdoll.
            if (!view.CanPlayCosmetics) return;

            ShotEvent shot = ShotEvent.From(in message);
            Vector3 direction = new Vector3(shot.Direction.X, shot.Direction.Y, shot.Direction.Z);

            Weapon weapon = view.ActiveWeapon;
            if (weapon != null)
            {
                // One report per message, never the Fire() loop: each S_WEAPON_FIRE is one shot,
                // and Shoot alone is SILENT on an automatic weapon (V10 D8).
                weapon.PlayFireCosmetics();
            }

            if (_tracers != null) _tracers.Fire(view.MuzzlePosition, direction);
        }

        /// <summary>
        /// A hit this client landed. Shooter-only, and that is a security property.
        /// </summary>
        /// <remarks>
        /// The server sends <c>S_HIT_CONFIRM</c> to the shooter alone. Rendering it for anybody
        /// else would tell a player that someone, somewhere, hit something — a server-served
        /// wallhack. Recorded because "why does only one client get this event" is exactly the
        /// question a future reader answers by broadcasting it (V10 D18).
        /// </remarks>
        private void OnHitConfirm(HitConfirmMessage message)
        {
            uint tick = _client != null ? _client.Router.Decoder.Current.ServerTick : 0u;

            _hitmarker.Push(in message, tick, Time.time);

            // The newest hit wins, including a quieter one — that is HitmarkerModel's documented
            // semantics, and the severity travels as an int so Assembly-CSharp takes no
            // dependency on the replication library for a cosmetic.
            IngameUi.Hit((int)_hitmarker.Current.Severity);
        }
    }
}
