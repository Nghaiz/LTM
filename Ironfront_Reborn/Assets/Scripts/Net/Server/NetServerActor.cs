using Ironfront.Net.Protocol;
using Ironfront.Net.Replication;
using Ironfront.Net.Replication.Combat;
using Ironfront.Net.Replication.Movement;
using System;
using UnityEngine;

namespace Ironfront.Net.Unity.Server
{
    /// <summary>
    /// Marks a GameObject as replicated and turns it into a snapshot entry once per snapshot
    /// tick. Attach to every player slot and every bot that should appear on clients.
    /// </summary>
    /// <remarks>
    /// <para>
    /// It reads gameplay state and never writes it, which is what makes it safe to attach to
    /// an existing prefab: with the server role inactive, nothing on this component runs at
    /// all.
    /// </para>
    /// <para>
    /// <b>Position comes from the simulation, not the transform, when both exist.</b> The
    /// authoritative <see cref="MoveState"/> is what the client is predicting against; the
    /// transform is where Unity's collision resolution left the object. They agree to within
    /// the depenetration Unity applied, and quantized to 6.25 cm they almost always agree
    /// exactly — but when they do not, the simulation is the one the client can reproduce.
    /// </para>
    /// </remarks>
    [DisallowMultipleComponent]
    public sealed class NetServerActor : MonoBehaviour
    {
        [Header("Identity")]
        [Tooltip("Leave at 0 to have the registry assign one on registration.")]
        [SerializeField] private ushort _actorId;

        [Tooltip("Team index, 0..3. Sent only when it changes.")]
        [SerializeField] private byte _team;

        [Tooltip("Player slots are handed to connections as they join. Bots leave this off.")]
        [SerializeField] private bool _availableForPlayers;

        [Header("Replicated gameplay state")]
        [Tooltip("Fallback for an actor with no Actor component. A real actor reports the "
               + "network id of whatever it is currently holding.")]
        [SerializeField] private byte _weaponId;

        // Not serialized: this is per-life runtime state, and an authored value would mean a
        // freshly spawned body arrives already believing it requested a slot.
        private int _lastRequestedWeaponSlot = -1;

        [SerializeField] private byte _ammoInClip;

        /// <summary>Id on the wire. One space shared by players and bots (spec § 4.3.1).</summary>
        public ushort ActorId
        {
            get => _actorId;
            set => _actorId = value;
        }

        public byte Team
        {
            get => _team;
            set => _team = value;
        }

        /// <summary>
        /// The actor's authoritative health. A pass-through to <c>Actor.health</c> whenever
        /// this GameObject has one. Decision D9.
        /// </summary>
        /// <remarks>
        /// <para>
        /// <b>There is one health field, not two kept in sync.</b> This component used to carry
        /// its own <c>[SerializeField] float _health</c>, which the snapshot read and nothing
        /// ever wrote, while <c>Actor.health</c> was the number <c>Die()</c>, the AI and the
        /// ragdoll all read. Two numbers with one writer is a bug waiting for a second writer;
        /// two numbers with two writers is the silent divergence phase-05 exists to remove, and
        /// <c>development-principles.md</c> § "No Derived Fields" already forbids it.
        /// </para>
        /// <para>
        /// <b>The serialized field's removal is safe because nothing had authored a meaningful
        /// value into it.</b> The three prefabs carrying a <c>NetServerActor</c> all stored
        /// <c>_health: 100</c>, which is also <c>Actor.health</c>'s declared default — so no
        /// authored value is lost. This is called out in the task-6 PR so the client track sees it.
        /// </para>
        /// <para>
        /// <b>The fallback is for actors with no <c>Actor</c>,</b> which is every bare test rig
        /// and any replicated prop. It is not a mirror: when <see cref="Source"/> exists the
        /// fallback is dead, and when it does not the fallback is the only copy. Exactly one is
        /// live at any moment, which is the property that matters.
        /// </para>
        /// </remarks>
        public float Health
        {
            get
            {
                IGameplayActorSource source = Source;
                return source != null ? source.Health : _healthWithoutActor;
            }
            set
            {
                IGameplayActorSource source = Source;
                if (source != null) source.Health = value;
                else _healthWithoutActor = value;
            }
        }

        /// <summary>
        /// The network id of the weapon this actor is holding. A pass-through to
        /// <c>Actor.activeWeapon.NetworkId</c> whenever this GameObject has one, for the same
        /// reason <see cref="Health"/> is a pass-through to <c>Actor.health</c> (D9).
        /// </summary>
        /// <remarks>
        /// <para>
        /// This used to be a plain serialized field that the snapshot read and <b>nothing ever
        /// wrote</b> — so every actor, in every snapshot, in every <c>S_SPAWN</c> and every
        /// <c>S_WEAPON_FIRE</c>, reported weapon 0. Remote clients drew no weapon at all and
        /// nothing anywhere reported an error, because 0 is a legal value meaning "unknown".
        /// </para>
        /// <para>
        /// The id itself has been available since checklist A6: <c>Actor.SpawnWeapon</c> stamps
        /// <c>WeaponManager.NetworkIdOf(entry)</c> onto <c>Weapon.NetworkId</c> at spawn, and
        /// <c>Actor.activeWeapon</c> is whichever one is unholstered. Nobody had connected the
        /// two ends.
        /// </para>
        /// <para>
        /// <b>The serialized field survives as the fallback</b> for a replicated object with no
        /// <c>Actor</c> — a prop, a bare test rig. Exactly one of the two is live at any moment,
        /// which is the property that keeps this from being a second copy of the same fact.
        /// </para>
        /// </remarks>
        public byte WeaponId
        {
            get
            {
                IGameplayActorSource source = Source;
                return source != null && source.TryGetActiveWeaponNetworkId(out byte networkId)
                    ? networkId
                    : _weaponId;
            }
            set => _weaponId = value;
        }

        /// <summary>
        /// Applies one frame's weapon selection, edged. <paramref name="slot"/> is 0..3, or
        /// negative for "this frame selects nothing".
        /// </summary>
        /// <remarks>
        /// <para>
        /// <b>The edge lives here because the intent is a HELD bit and the action is not.</b>
        /// <c>InputButtons.SwitchWeapon0..3</c> ride <c>C_INPUT</c>, which repeats each frame
        /// seven times for redundancy, so a slot holding a <c>ToggleableItem</c> would flip in
        /// and out at tick rate if every arrival called through. Storing the last requested slot
        /// on the actor also means it dies with the actor -- no per-connection table to leak.
        /// </para>
        /// <para>
        /// <b>Releasing resets it</b>, so pressing the same slot twice in a row works: a frame
        /// with no switch bit set writes -1 and re-arms the next press.
        /// </para>
        /// <para>
        /// Returns whether the seam was actually called. A false does NOT mean the switch was
        /// refused -- <c>Actor.SwitchWeapon</c> makes that decision on the far side and says
        /// nothing back.
        /// </para>
        /// </remarks>
        /// <summary>
        /// Arms the body from its loadout. Returns false when there is no gameplay actor behind
        /// this replicated object -- a prop, or a bare test rig.
        /// </summary>
        /// <summary>
        /// Ensures this body has a <see cref="NetMovementAgent"/>, adding one if the prefab does
        /// not carry it. Returns the agent.
        /// </summary>
        /// <remarks>
        /// <para>
        /// <b>Ledger row X-15.</b> <c>Movement</c> is resolved once in <c>Awake</c>, and the AI
        /// character prefab a claimed player body is built from does not carry the component --
        /// it is authored on <c>Prefab/Player Fps Actor.prefab</c> and nowhere else. So
        /// <c>Movement</c> was null for every networked player, <c>ServerPlayer.Tick</c> took its
        /// DETACHED branch, and the session's <c>MoveState</c> integrated gravity with no
        /// collision -- free-falling forever while the transform stood still at the spawn.
        /// Measured: shot origins at <c>y = -353</c> descending to <c>-448</c> with the nearest
        /// target 649 → 704 m away, against a snapshot that reported the same actor at
        /// <c>y = 8.59</c>.
        /// </para>
        /// <para>
        /// <b>Called explicitly rather than resolved lazily.</b> A lazy <c>Movement</c> would
        /// <c>GetComponent</c> on every read for every prop that legitimately has no agent, in
        /// the tick loop. An explicit call from the one place that builds a player body costs
        /// nothing and says who is responsible.
        /// </para>
        /// <para>
        /// <b>Not called for bots.</b> They are driven by <c>AiActorController</c> through the
        /// game's own movement, and giving them a server-authoritative agent would be a second
        /// driver on one body.
        /// </para>
        /// </remarks>
        public NetMovementAgent AttachMovementAgent()
        {
            if (Movement != null) return Movement;

            Movement = GetComponent<NetMovementAgent>();
            if (Movement == null) Movement = gameObject.AddComponent<NetMovementAgent>();

            return Movement;
        }
        public bool EquipLoadout()
        {
            IGameplayActorSource source = Source;
            if (source == null) return false;

            source.EquipLoadout();
            return true;
        }

        /// <summary>
        /// Pulls the trigger on the weapon this body is holding. Ledger <b>X-42</b>.
        /// </summary>
        /// <remarks>
        /// Called by <c>ServerCombatBridge</c> only for a <c>WeaponDelivery.Projectile</c> weapon
        /// whose trigger pull the server has ALREADY accepted. A hitscan weapon never reaches
        /// here: its shot is resolved in the engine-free authority, which is decision D2 and the
        /// reason every combat rule is graded by <c>dotnet test</c> rather than by two people
        /// playing at once.
        /// </remarks>
        /// <returns>False when nothing is bound, or the body is holding nothing.</returns>
        public bool FireCarriedWeapon(float directionX, float directionY, float directionZ)
        {
            IGameplayActorSource source = Source;
            if (source == null) return false;

            return source.FireCarriedWeapon(directionX, directionY, directionZ);
        }
        public bool ApplyWeaponSwitchIntent(int slot)
        {
            if (slot == _lastRequestedWeaponSlot) return false;

            _lastRequestedWeaponSlot = slot;

            if (slot < 0) return false;

            IGameplayActorSource source = Source;
            if (source == null)
            {
                LogSwitchIntent(slot, "no gameplay source");
                return false;
            }

            source.SwitchWeapon(slot);
            LogSwitchIntent(slot, "forwarded");
            return true;
        }

        // Ledger X-31. The grenade never gets held, and every link of the chain reads correct in
        // source: the programme asks for slot 2, InputButtonPacker sets SwitchWeapon2,
        // InputFrame.WeaponSlot decodes 2, the slots are armed (`slot2[FRAG toggleable=False]`),
        // and Actor.SwitchWeapon's guards all pass for a live body holding a non-toggleable
        // weapon. Reading further is out of road -- what is missing is whether this edge is
        // reached at all, and with what.
        //
        // Off unless asked for, matching IRONFRONT_LOG_SHOTS; observation only, per the
        // phase-3d section 6 decision that permits gated diagnostic logging.
        private static bool? _switchLogging;

        private void LogSwitchIntent(int slot, string outcome)
        {
            _switchLogging ??= Environment.GetEnvironmentVariable("IRONFRONT_LOG_LOADOUT") == "1";
            if (_switchLogging != true) return;

            Debug.Log($"[switch] actor={ActorId} slot={slot} outcome={outcome} weaponId={WeaponId}");
        }

        public byte AmmoInClip
        {
            get => _ammoInClip;
            set => _ammoInClip = value;
        }

        /// <summary>
        /// Staggers the underlying gameplay actor. A no-op for a replicated object that has none
        /// -- a prop or a bare test rig has no balance to lose.
        /// </summary>
        /// <remarks>
        /// phase-V2 D6/D7. Applied server-side and NOT replicated: there is no wire field for
        /// stagger and <c>ActorStateFlags</c> is 8/8 full, so the authoritative view and the bots
        /// stagger while a remote client sees nothing until V3 buys a bit for it.
        /// </remarks>
        public void ApplyBalanceDamage(float balanceDamage)
        {
            IGameplayActorSource source = Source;
            if (source != null) source.ApplyBalanceDamage(balanceDamage);
        }

        /// <summary>Whether a joining connection may be given this actor.</summary>
        public bool AvailableForPlayers => _availableForPlayers;

        /// <summary>True once a connection has been given this actor.</summary>
        public bool IsClaimed { get; private set; }

        /// <summary>Aim pitch in degrees, driven by whoever controls this actor.</summary>
        public float PitchDegrees { get; set; }

        /// <summary>
        /// Aim yaw in degrees, written from the accepted input frame. <see cref="float.NaN"/>
        /// means nothing drives it, and the transform's own rotation is used instead.
        /// </summary>
        /// <remarks>
        /// <para>
        /// <b>A headless server's player transform does not turn.</b> Player rotation in the
        /// original game comes from <c>FpsActorController</c> reading the mouse, and there is no
        /// mouse in batch mode — so <see cref="Capture"/> reading <c>transform.eulerAngles.y</c>
        /// reported the spawn heading for the whole match. Every remote client drew the player
        /// facing one fixed direction while they shot in another, and the &gt;500 m view-cone
        /// rescue in <c>InterestManager</c> tested that same fixed direction. Phase-05 already
        /// says aim comes from the frame, not the transform; only pitch had been wired.
        /// </para>
        /// <para>
        /// <b>The NaN default is what keeps bots correct.</b> An AI actor is rotated by its own
        /// controller and never receives an input frame, so for it the transform is the truth.
        /// Defaulting to 0 would have snapped every bot to face north.
        /// </para>
        /// </remarks>
        public float YawDegrees { get; set; } = float.NaN;

        /// <summary>
        /// Health an actor is given when it takes a fresh life — a claimed player slot or a
        /// respawn.
        /// </summary>
        /// <remarks>
        /// One constant rather than the literal 100 written at each site. It is still a guess at
        /// what <c>Actor</c> considers full health; reading a real maximum off the actor is a
        /// separate change.
        /// </remarks>
        public const float DefaultSpawnHealth = 100f;

        /// <summary>Alive flag. A corpse is never replicated (AD-4) but the flag still ships.</summary>
        /// <remarks>
        /// <para>
        /// A pass-through to <c>Actor.dead</c> for the same reason <see cref="Health"/> is a
        /// pass-through to <c>Actor.health</c> (D9), and it is the same defect: with an
        /// auto-property here, an actor killed through <c>Actor.Damage</c> would set
        /// <c>dead = true</c> while the snapshot kept reporting <c>IsAlive</c>, so every client
        /// would render a corpse that was still standing and still a valid hitscan target.
        /// D9 names health because health is where it was noticed; the flag beside it had
        /// exactly the same shape.
        /// </para>
        /// <para>
        /// <b>Setting this true does not resurrect a Unity actor.</b> It clears the gameplay
        /// flag and the replicated bit; the animator, ragdoll and collider work a real respawn
        /// needs is Editor-phase work and deliberately not attempted here.
        /// </para>
        /// </remarks>
        public bool IsAlive
        {
            get
            {
                IGameplayActorSource source = Source;
                return source != null ? !source.IsDead : _isAliveWithoutActor;
            }
            set
            {
                IGameplayActorSource source = Source;
                if (source != null) source.IsDead = !value;
                else _isAliveWithoutActor = value;
            }
        }

        /// <summary>Aiming down sights, for the state flags byte.</summary>
        public bool IsAiming { get; set; }

        /// <summary>The movement seam, when this actor has one. Bots may not.</summary>
        public NetMovementAgent Movement { get; private set; }

        /// <summary>
        /// The gameplay actor whose health is the authoritative one, behind the seam that keeps
        /// this assembly free of <c>Assembly-CSharp</c>. May be absent. See
        /// <see cref="IGameplayActorSource"/>.
        /// </summary>
        private IGameplayActorSource _actorSource;

        /// <summary>
        /// <see cref="_actorSource"/> while it still refers to a live component, otherwise
        /// <see langword="null"/> — the exact meaning the <c>_actor != null</c> this replaced
        /// carried, since a destroyed <c>UnityEngine.Object</c> compares equal to null and a
        /// plain interface reference does not.
        /// </summary>
        private IGameplayActorSource Source
            => _actorSource != null && _actorSource.Exists ? _actorSource : null;

        /// <summary>Health for a replicated object that is not an <c>Actor</c>. See <see cref="Health"/>.</summary>
        private float _healthWithoutActor = 100f;

        /// <summary>Alive flag for a replicated object that is not an <c>Actor</c>.</summary>
        private bool _isAliveWithoutActor = true;

        private void Awake()
        {
            Movement = GetComponent<NetMovementAgent>();
            // One allocation per actor, at Awake, replacing a GetComponent<Actor>() that the
            // adapter now performs on the other side of the seam. Nothing here runs per tick.
            BindGameplaySource(NetServerBindings.ResolveActorSource(gameObject));
            BindAiDriver(NetServerBindings.ResolveAiDriver(gameObject));
        }

        /// <summary>
        /// Attaches the gameplay actor this component reads its replicated state from.
        /// </summary>
        /// <remarks>
        /// This is <c>Awake</c>'s own step, factored out because an EditMode test cannot reach
        /// it otherwise: Unity does not run <c>Awake</c> on <c>AddComponent</c> outside play
        /// mode (verified against this Editor, 6000.3.21f1), so a test that only added the
        /// component would silently exercise the no-actor fallback and pass while asserting
        /// nothing. Binding explicitly is what lets the suite pin the pass-throughs — which is
        /// where the weapon-id-always-0 defect lived.
        /// </remarks>
        internal void BindGameplaySource(IGameplayActorSource source) => _actorSource = source;

        /// <summary>
        /// Attaches the bot brain this body is steered by while nobody has claimed it.
        /// </summary>
        /// <remarks>
        /// Factored out of <c>Awake</c> for the reason <see cref="BindGameplaySource"/> is:
        /// Unity does not run <c>Awake</c> on <c>AddComponent</c> outside play mode, so an
        /// EditMode test that only added the component would exercise the no-driver branch and
        /// pass while asserting nothing.
        /// </remarks>
        internal void BindAiDriver(IAiDriver driver) => _aiDriver = driver;

        private IAiDriver _aiDriver;

        private void OnEnable() => ServerActorRegistry.Instance.Register(this);

        private void OnDisable() => ServerActorRegistry.Instance.Unregister(this);

        /// <summary>Quantizes this actor's current state into a snapshot entry.</summary>
        public ActorSnapshotEntry Capture()
        {
            Vec3 position = Movement != null
                ? Movement.State.Position
                : MovementSimulation.ToCore(transform.position);

            Vec3 velocity = Movement != null ? Movement.State.Velocity : Vec3.Zero;

            // Through the properties, not the backing fields: both are pass-throughs to the
            // gameplay actor now, and reading _weaponId here is how the weapon id stayed 0 in
            // the snapshot and in S_SPAWN even after the property was correct.
            float yaw = float.IsNaN(YawDegrees) ? transform.eulerAngles.y : YawDegrees;

            // Seat state, from the server's OWN occupancy record rather than the scene's.
            //
            // This is the field that makes "who is in what seat" answerable at all. Design D2
            // names the actor entry as the single source of truth for occupancy and V3 finished
            // its codec — but nothing ever populated it, so every actor reported as on foot and
            // S_SEAT_CHANGE was the only carrier. That message is the TRANSITION and only fires
            // on a client request, so a client that joined mid-match, or missed one datagram, or
            // watched a vehicle die and eject its occupants, had no way to learn who was aboard.
            //
            // Read from SeatArbiter's record via the registry, not from Actor.seat: the arbiter's
            // record is what the server decided, and the two are only equal because the bridge
            // keeps them so. Taking the scene's copy here would make the snapshot agree with
            // whichever of the two happened to be wrong.
            ServerVehicleRegistry.Instance.Registry.TryFindSeatOf(
                _actorId, out ushort vehicleId, out byte seatIndex);

            return SnapshotBuilder.Capture(
                _actorId,
                position,
                yaw,
                PitchDegrees,
                velocity,
                BuildStateFlags(),
                Health,
                WeaponId,
                _ammoInClip,
                _team,
                vehicleId,
                seatIndex);
        }

        /// <summary>Packs the gameplay booleans the snapshot carries as one byte.</summary>
        public ActorStateFlags BuildStateFlags()
        {
            ActorStateFlags flags = ActorStateFlags.None;

            if (IsAlive) flags |= ActorStateFlags.IsAlive;
            if (IsAiming) flags |= ActorStateFlags.IsAiming;

            if (Movement != null)
            {
                if (Movement.State.IsCrouching) flags |= ActorStateFlags.IsCrouching;

                // Sprinting is derived rather than stored: the simulation has no sprint flag on
                // its state, only a speed, and reporting "moving faster than a walk" is what the
                // client actually animates from.
                float horizontal = Movement.State.Velocity.X * Movement.State.Velocity.X
                                 + Movement.State.Velocity.Z * Movement.State.Velocity.Z;

                float walk = MovementSimulation.WalkSpeed;
                if (horizontal > walk * walk * 1.05f) flags |= ActorStateFlags.IsSprinting;
            }

            return flags;
        }

        /// <summary>
        /// This actor's hitboxes in world space, for the rewind history.
        /// </summary>
        /// <remarks>
        /// <para>
        /// World space is what makes lag compensation free of mutation: the stored pose is a
        /// value the raycast reads, so nothing is ever moved into the past and nothing has to be
        /// put back. See <c>LagCompensator</c>.
        /// </para>
        /// <para>
        /// <b>The boxes are a placeholder built from the actor's position.</b> the client track's rig has
        /// the real ones, and swapping them in is a change to this method and nothing else —
        /// the resolution path does not care where the numbers came from. Until then, hit
        /// geometry is a plausible humanoid rather than this character.
        /// </para>
        /// </remarks>
        public HitboxSet CaptureHitboxes()
        {
            Vec3 feet = Movement != null
                ? Movement.State.Position
                : MovementSimulation.ToCore(transform.position);

            return HitboxSet.Humanoid(in feet);
        }

        /// <summary>
        /// Opens this body to joining connections. Phase-3A; used by
        /// <see cref="ServerPlayerSlotPool"/> on the bodies it creates.
        /// </summary>
        /// <remarks>
        /// <para>
        /// A method rather than a setter on <see cref="AvailableForPlayers"/>, and internal
        /// rather than public, because there is exactly one legitimate caller. The flag is
        /// otherwise authored on the prefab, and a public setter is an invitation for gameplay
        /// code to open a slot mid-match on a body that is already being driven by something
        /// else — which is the state <c>NetVerificationHarness.OpenSecondSlot</c> produced by
        /// reflecting the private field, and the reason that method is gone.
        /// </para>
        /// <para>
        /// There is deliberately no matching "close". A body whose claim was released goes back
        /// to the pool as a claimable slot; a body that should never be claimable says so on its
        /// prefab.
        /// </para>
        /// </remarks>
        internal void MarkAvailableForPlayers()
        {
            _availableForPlayers = true;

            // P12 D-4. A player slot's bot brain is parked FROM CREATION, not from the claim.
            //
            // ServerPlayerSlotPool.Fill builds Config.MaxConnections of these at Start and every
            // one was a live AI character until somebody claimed it. At MaxConnections 16 with
            // two humans that is fourteen extra AI-driven, shootable, SCORING bodies standing on
            // top of the map's authored 20/20 -- split 7/7 by the pool's own `i % 2` -- so a 1v1
            // was really a 21v21. Worse than merely surplus: X-18 holds an unclaimed slot out of
            // both the announce and the snapshot, so those fourteen were invisible to every
            // client while still shooting at it.
            //
            // This is the cheaper half of D-4. Sizing the pool to the room's MaxPlayers instead
            // of the fixed MaxConnections needs a real room and lands in P14; parking the brain
            // needs neither and is available now.
            //
            // IAiDriver.Suspend is the existing seam and the existing mechanism -- the same one
            // Claim uses -- deliberately rather than a second suspension concept.
            SetAiDriverSuspended(true);
        }

        /// <summary>
        /// Parks or unparks the bot brain, doing nothing when it is already in that state.
        /// </summary>
        /// <remarks>
        /// <para>
        /// <b>Transition-tracked, because the callers now overlap.</b> A pool body is marked
        /// available and then claimed, and both want it suspended;
        /// <c>AiActorController.enabled</c> is not a free write to repeat, and — more to the
        /// point — collapsing the repeat is what keeps <see cref="Claim"/> and
        /// <see cref="Release"/> emitting exactly one driver call each, which is the count
        /// <c>ServerPlayerSlotPoolTests</c> already pins.
        /// </para>
        /// <para>
        /// <b><see cref="Release"/> still resumes, and that is deliberate.</b> The invariant is
        /// "a body that has been handed to a connection is not bot-driven", not "a player slot is
        /// never bot-driven" — <c>IAiDriver.Resume</c>'s own remark gives the reason: a slot is
        /// reused across a match, and without the resume every disconnect would leave one more
        /// inert mannequin standing in the map. What P12 changes is only the state a slot starts
        /// in, which nothing set before.
        /// </para>
        /// </remarks>
        private void SetAiDriverSuspended(bool suspended)
        {
            if (_aiDriver == null || !_aiDriver.Exists) return;
            if (suspended == _aiDriverSuspended) return;

            _aiDriverSuspended = suspended;

            if (suspended) _aiDriver.Suspend();
            else _aiDriver.Resume();
        }

        /// <summary>Whether <see cref="SetAiDriverSuspended"/> has the brain parked.</summary>
        private bool _aiDriverSuspended;

        /// <summary>
        /// Hands this body to a connection, and stops the bot brain steering it.
        /// </summary>
        /// <remarks>
        /// The suspend is here rather than at the call site because there is more than one call
        /// site and only one of them is obvious. Server movement for a claimed body runs through
        /// <c>ServerPlayer</c> and <c>NetMovementAgent</c>; an <c>AiActorController</c> still
        /// running is a second writer to the same <c>CharacterController</c>, and the client is
        /// predicting against only one of the two.
        /// </remarks>
        internal void Claim()
        {
            IsClaimed = true;

            // Already parked when this body came from the slot pool (P12 D-4), so on that path
            // this is a no-op. Still called, because Claim's contract is "the bot brain is not
            // steering after this returns" and that must not depend on who built the body.
            SetAiDriverSuspended(true);
        }

        /// <summary>Takes the body back and returns it to the bot brain.</summary>
        internal void Release()
        {
            IsClaimed = false;

            SetAiDriverSuspended(false);
        }
    }
}
