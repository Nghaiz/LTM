using Ironfront.Net.Protocol;
using Ironfront.Net.Replication;
using Ironfront.Net.Replication.Combat;
using Ironfront.Net.Replication.Movement;
using UnityEngine;

namespace Ironfront.Net.Unity.Server
{
    /// <summary>
    /// Marks a GameObject as replicated and turns it into a snapshot entry once per snapshot
    /// tick. Attach to every player slot and every bot that should appear on clients.
    /// </summary>
    /// <remarks>
    /// <para>
    /// OWNER: Dev C.
    /// </para>
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
        [Tooltip("Ships as 0 until Dev A lands the weapon id registry (checklist A6).")]
        [SerializeField] private byte _weaponId;

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
        /// authored value is lost. This is called out in the task-6 PR so Dev A sees it.
        /// </para>
        /// <para>
        /// <b>The fallback is for actors with no <c>Actor</c>,</b> which is every bare test rig
        /// and any replicated prop. It is not a mirror: when <see cref="_actor"/> exists the
        /// fallback is dead, and when it does not the fallback is the only copy. Exactly one is
        /// live at any moment, which is the property that matters.
        /// </para>
        /// </remarks>
        public float Health
        {
            get => _actor != null ? _actor.health : _healthWithoutActor;
            set
            {
                if (_actor != null) _actor.health = value;
                else _healthWithoutActor = value;
            }
        }

        public byte WeaponId
        {
            get => _weaponId;
            set => _weaponId = value;
        }

        public byte AmmoInClip
        {
            get => _ammoInClip;
            set => _ammoInClip = value;
        }

        /// <summary>Whether a joining connection may be given this actor.</summary>
        public bool AvailableForPlayers => _availableForPlayers;

        /// <summary>True once a connection has been given this actor.</summary>
        public bool IsClaimed { get; private set; }

        /// <summary>Aim pitch in degrees, driven by whoever controls this actor.</summary>
        public float PitchDegrees { get; set; }

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
            get => _actor != null ? !_actor.dead : _isAliveWithoutActor;
            set
            {
                if (_actor != null) _actor.dead = !value;
                else _isAliveWithoutActor = value;
            }
        }

        /// <summary>Aiming down sights, for the state flags byte.</summary>
        public bool IsAiming { get; set; }

        /// <summary>The movement seam, when this actor has one. Bots may not.</summary>
        public NetMovementAgent Movement { get; private set; }

        /// <summary>The gameplay actor whose health is the authoritative one. May be absent.</summary>
        private Actor _actor;

        /// <summary>Health for a replicated object that is not an <c>Actor</c>. See <see cref="Health"/>.</summary>
        private float _healthWithoutActor = 100f;

        /// <summary>Alive flag for a replicated object that is not an <c>Actor</c>.</summary>
        private bool _isAliveWithoutActor = true;

        private void Awake()
        {
            Movement = GetComponent<NetMovementAgent>();
            _actor = GetComponent<Actor>();
        }

        private void OnEnable() => ServerActorRegistry.Instance.Register(this);

        private void OnDisable() => ServerActorRegistry.Instance.Unregister(this);

        /// <summary>Quantizes this actor's current state into a snapshot entry.</summary>
        public ActorSnapshotEntry Capture()
        {
            Vec3 position = Movement != null
                ? Movement.State.Position
                : MovementSimulation.ToCore(transform.position);

            Vec3 velocity = Movement != null ? Movement.State.Velocity : Vec3.Zero;

            return SnapshotBuilder.Capture(
                _actorId,
                position,
                transform.eulerAngles.y,
                PitchDegrees,
                velocity,
                BuildStateFlags(),
                Health,
                _weaponId,
                _ammoInClip,
                _team);
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
        /// <b>The boxes are a placeholder built from the actor's position.</b> Dev A's rig has
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

        internal void Claim() => IsClaimed = true;

        internal void Release() => IsClaimed = false;
    }
}
