namespace Ironfront.Net.Replication.Vehicles
{
    /// <summary>
    /// The window in which a freshly spawned vehicle, or one a driver has just entered, takes no
    /// COLLISION damage. Weapon damage is never suppressed.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>This existed as three copies of <c>5f</c> and one boolean expression written twice,
    /// all inside <c>Vehicle.cs</c>.</b> <c>Assembly-CSharp</c> is not referenceable from any
    /// test assembly (E-11b), so the only thing a reader could do with it was believe the
    /// comment beside it. Protocol 10 § 8.3 asks for the guard to be TESTED, and a guard that
    /// cannot be executed from CI is a claim, not a guard — so the decision moved here and
    /// <c>Vehicle</c> now applies what this decides.
    /// </para>
    /// <para>
    /// <b>Collision only, and never open-ended.</b> This used to answer "suppressed" for any
    /// driverless vehicle with no deadline at all, and <c>Vehicle.Damage</c> asked it too, so on a
    /// server an EMPTY vehicle ignored bullets, rockets, grenades and ramming for the whole match
    /// (2026-09-23 owner report: "vehicles are literally invulnerable"). The original has no such
    /// rule: <c>ActorManager.Explode</c> damages every vehicle in range, driven or not. What the
    /// grace exists for is PhysX settling a vehicle onto its pad, and a driver's first seconds
    /// pulling off it; both are collisions and both are bounded. Crossfire at an empty pad
    /// vehicle is gameplay, exactly as offline.
    /// </para>
    /// <para>
    /// <b>Seconds, not ticks.</b> The two writers are Unity's <c>Awake</c> and
    /// <c>FixedUpdate</c>, which have <c>Time.time</c> and no tick counter — the opposite of
    /// <see cref="Server.VehicleIdPool"/>'s quarantine, which the protocol states in ticks and
    /// the server already counts. Converting either to the other's unit would add a rounding
    /// question to the side that has no reason to ask one.
    /// </para>
    /// </remarks>
    public static class VehicleSpawnSettle
    {
        /// <summary>
        /// Seconds of settle grace, from protocol 10 § 8.3 ("trong ít nhất 5 giây đầu").
        /// </summary>
        public const float SettleSeconds = 5f;

        /// <summary>
        /// The absolute time crash damage becomes authoritative again, given the current time.
        /// </summary>
        public static float DeadlineFrom(float now) => now + SettleSeconds;

        /// <summary>
        /// Whether a COLLISION must be discarded rather than applied. Only
        /// <c>Vehicle.OnCollisionEnter</c> asks; weapon damage never does.
        /// </summary>
        /// <param name="isServer">
        /// False offline and on a client, where this whole guard is absent by design: offline
        /// Ravenfield's crash damage is unchanged, and a client never decides damage at all.
        /// </param>
        /// <param name="now">The engine clock.</param>
        /// <param name="notBefore">
        /// The deadline last written by <see cref="DeadlineFrom"/>: at spawn, and when a driver
        /// enters.
        /// </param>
        public static bool CollisionDamageIsSuppressed(bool isServer, float now, float notBefore)
            => isServer && now < notBefore;
    }
}
