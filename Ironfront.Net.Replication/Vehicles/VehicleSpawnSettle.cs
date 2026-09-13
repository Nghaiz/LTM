namespace Ironfront.Net.Replication.Vehicles
{
    /// <summary>
    /// The window in which a freshly spawned, driverless vehicle takes no crash damage.
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
    /// <b>Why a driverless vehicle is suppressed with no deadline at all.</b> A dedicated
    /// server starts its bot match while rendered clients are still loading, and an unattended
    /// vehicle standing on a capture-point pad is a physics object being leaned on by whatever
    /// walks into it. Charging that to the hull would destroy pad vehicles before a human saw
    /// the first frame — which is X-70's sibling symptom, "the wreck that read as on fire".
    /// The deadline is what bounds the grace once somebody IS driving: from then on it is a
    /// short exit-from-pad window and crash damage is gameplay again.
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
        /// Whether a crash must be discarded rather than applied.
        /// </summary>
        /// <param name="isServer">
        /// False offline and on a client, where this whole guard is absent by design: offline
        /// Ravenfield's crash damage is unchanged, and a client never decides damage at all.
        /// </param>
        /// <param name="hasDriver">Somebody is in the driver's seat right now.</param>
        /// <param name="now">The engine clock.</param>
        /// <param name="notBefore">The deadline last written by <see cref="DeadlineFrom"/>.</param>
        public static bool CrashDamageIsSuppressed(
            bool isServer, bool hasDriver, float now, float notBefore)
            => isServer && (!hasDriver || now < notBefore);
    }
}
