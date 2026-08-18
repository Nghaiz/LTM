namespace Ironfront.Net.Replication.Vehicles
{
    /// <summary>What one application of vehicle damage did.</summary>
    /// <remarks>
    /// Deliberately the same shape as <c>Combat.DamageOutcome</c>, so the actor and vehicle
    /// damage paths read alike — a reader who has understood one has understood the other, and
    /// the difference that matters (a vehicle burns before it dies) is then visibly the one
    /// extra field rather than a second vocabulary.
    /// </remarks>
    public readonly struct VehicleDamageOutcome
    {
        /// <summary>Health after the hit, in the vehicle's own units.</summary>
        public readonly float RemainingHealth;

        /// <summary>
        /// True only on the hit that took health to zero and lit the fire.
        /// </summary>
        /// <remarks>
        /// Edge-triggered, for <c>ServerActorDamageSink.DamageOutcome.Died</c>'s reason: a
        /// shotgun blast whose second pellet lands on an already-burning tank must report one
        /// transition, not two. Getting this wrong double-counts and looks like a scoring bug
        /// rather than a damage bug.
        /// </remarks>
        public readonly bool StartedBurning;

        /// <summary>
        /// True only on the hit that killed outright — a crash on a vehicle whose
        /// <c>crashSkipsBurn</c> is set.
        /// </summary>
        /// <remarks>
        /// <b>Ordinary damage never sets this</b> (V4-D11). <c>health &lt;= 0</c> starts the
        /// burn (<c>Vehicle.cs</c> ApplyHealth) and death arrives later from the
        /// <c>burnTime</c> countdown, which <see cref="VehicleBurnClock"/> owns. A damage sink
        /// that killed at zero health would produce a game where nothing ever burns.
        /// </remarks>
        public readonly bool Died;

        public VehicleDamageOutcome(float remainingHealth, bool startedBurning, bool died)
        {
            RemainingHealth = remainingHealth;
            StartedBurning  = startedBurning;
            Died            = died;
        }

        /// <summary>Nothing happened — an unknown or already-dead vehicle.</summary>
        public static readonly VehicleDamageOutcome NoOp =
            new VehicleDamageOutcome(0f, startedBurning: false, died: false);
    }

    /// <summary>
    /// The one place vehicle health is written on the server.
    /// </summary>
    /// <remarks>
    /// Phase-05 D9's rule, one entity type over: one number, not a mirror that can drift. Every
    /// vehicle damage source in the shipped game already funnels through <c>Vehicle.Damage</c>,
    /// so the role guard there is the choke point and this is what it routes to.
    /// </remarks>
    public interface IVehicleDamageSink
    {
        /// <summary>
        /// Applies damage to a vehicle.
        /// </summary>
        /// <param name="attackerId">
        /// The actor that caused it, or 0. Carried so a death event can attribute the kill —
        /// <c>Vehicle.Damage(float)</c> had no attacker parameter at all before V0 opened one.
        /// </param>
        VehicleDamageOutcome ApplyDamage(ushort vehicleId, float amount, ushort attackerId);
    }
}
