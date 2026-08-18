using Ironfront.Net.Protocol;

namespace Ironfront.Net.Replication.Vehicles
{
    /// <summary>
    /// What the server holds as authoritative about one vehicle, apart from its pose.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Scalars only — no arrays, and that is deliberate.</b> The phase plan put
    /// <c>SeatOccupants</c> and <c>SeatClaims</c> on this struct. An array field on a struct is
    /// a shared reference that survives every copy, so two "copies" of a vehicle's state would
    /// silently share one seat table and a test that took a snapshot to compare against would
    /// be comparing the value to itself. Occupancy lives in <see cref="VehicleRegistry"/>'s
    /// record, which is a reference type and means it; claims live in
    /// <see cref="BotSeatClaims"/>, which owns them outright (V4-D10).
    /// </para>
    /// <para>
    /// <b><see cref="Health"/> is the real number, not the wire's <c>u8</c>.</b> The entry
    /// normalizes against <see cref="MaxHealth"/> at capture. Storing the normalized byte here
    /// instead would make the server's own damage arithmetic run at 1/255 resolution, so a
    /// rifle round against a 1000 HP tank would round to zero and the tank would be
    /// invulnerable to small arms — on the server only, which is the worst place for it.
    /// </para>
    /// <para>
    /// <b><see cref="BurnEndsAtTick"/> is a tick, and <c>burnTime</c> is not replicated at
    /// all</b> (V4-D11). <c>Vehicle.burnTime</c> is simultaneously the serialized designer
    /// default and the live countdown, so a client receiving it could not tell which of the two
    /// it held. The server owns when the burn ends and announces the end as an event.
    /// </para>
    /// </remarks>
    public struct VehicleState
    {
        /// <summary>
        /// Seats the server will track per vehicle.
        /// </summary>
        /// <remarks>
        /// Not a protocol constant: <c>S_VEHICLE_SPAWN.seatCount</c> is a <c>u8</c> and the wire
        /// imposes no ceiling. This is a server-side sizing choice, generous against the shipped
        /// prefabs (the largest is a transport helicopter), and it is a named constant rather
        /// than an <c>8</c> in three files because <see cref="VehicleRegistry"/> and
        /// <see cref="BotSeatClaims"/> must agree on it or a claim lands in a seat that
        /// occupancy does not know about.
        /// </remarks>
        public const int MaxSeats = 8;

        /// <summary>Network id. Never 0 for a live vehicle — 0 is the protocol's "no vehicle".</summary>
        public ushort VehicleId;

        /// <summary>
        /// Which spawner produced it. Diagnostics only; <c>S_VEHICLE_SPAWN</c> carries no
        /// spawner field, so this exists to name the pad a level designer has to go and look at.
        /// </summary>
        public ushort SpawnerId;

        /// <summary>The physics family, which decides how the subtype tail is read.</summary>
        public VehicleKind Kind;

        /// <summary>Seats this vehicle actually has. Clamped to <see cref="MaxSeats"/>.</summary>
        public byte SeatCount;

        /// <summary>Current health, in the vehicle's own units.</summary>
        public float Health;

        /// <summary>Health at full. Never 0 — the capture normalizes against it.</summary>
        public float MaxHealth;

        /// <summary>True once health reached zero and the burn started.</summary>
        public bool Burning;

        /// <summary>True once the burn ended, or a crash skipped it.</summary>
        public bool Dead;

        /// <summary>
        /// The tick <see cref="Burning"/> turns into <see cref="Dead"/>. Meaningless while
        /// <see cref="Burning"/> is false.
        /// </summary>
        public uint BurnEndsAtTick;

        /// <summary>
        /// Whichever team's player last sat in it, for AI bookkeeping. <b>Never used as an
        /// interest-management team</b> — see <see cref="Interest.InterestSubject"/> and V4-D5.
        /// </summary>
        public byte OwnerTeam;

        /// <summary>Health as the wire's <c>u8</c>, 0..<see cref="Quantize.HEALTH_MAX"/>.</summary>
        /// <remarks>
        /// Computed, never stored — <c>code-conventions.md</c> § "No Derived Fields". A stored
        /// copy is one more thing that can disagree with <see cref="Health"/>, and the whole
        /// reason <c>Vehicle.ApplyHealth</c> exists is that two writers of one number diverge.
        /// </remarks>
        public byte NormalizedHealth
        {
            get
            {
                if (MaxHealth <= 0f) return 0;

                float t = Health / MaxHealth;
                if (t <= 0f) return 0;
                if (t >= 1f) return Quantize.HEALTH_MAX;

                return (byte)(t * Quantize.HEALTH_MAX + 0.5f);
            }
        }

        /// <summary>Creates a vehicle at full health.</summary>
        public static VehicleState Spawned(
            ushort vehicleId, ushort spawnerId, VehicleKind kind, byte seatCount,
            float maxHealth, byte ownerTeam)
        {
            return new VehicleState
            {
                VehicleId = vehicleId,
                SpawnerId = spawnerId,
                Kind      = kind,
                SeatCount = seatCount > MaxSeats ? (byte)MaxSeats : seatCount,
                Health    = maxHealth,
                MaxHealth = maxHealth,
                OwnerTeam = ownerTeam,
            };
        }
    }
}
