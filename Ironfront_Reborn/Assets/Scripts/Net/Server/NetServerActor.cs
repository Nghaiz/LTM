using Ironfront.Net.Protocol;
using Ironfront.Net.Replication;
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
        [SerializeField] private float _health = 100f;

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

        public float Health
        {
            get => _health;
            set => _health = value;
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
        public bool IsAlive { get; set; } = true;

        /// <summary>Aiming down sights, for the state flags byte.</summary>
        public bool IsAiming { get; set; }

        /// <summary>The movement seam, when this actor has one. Bots may not.</summary>
        public NetMovementAgent Movement { get; private set; }

        private void Awake() => Movement = GetComponent<NetMovementAgent>();

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
                _health,
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

        internal void Claim() => IsClaimed = true;

        internal void Release() => IsClaimed = false;
    }
}
