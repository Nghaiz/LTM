using Ironfront.Net.Protocol;

namespace Ironfront.Net.Replication.Server
{
    /// <summary>
    /// Who a death is credited to, and what <c>S_DEATH</c>'s detail tail says about it: the rules
    /// the server applies to the facts the engine gathered. Playtest 2026-09-28, feature 2.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Why these rules exist.</b> Three kinds of death reached the killfeed as "The world"
    /// although somebody had caused them: the crew of a destroyed vehicle (<c>Vehicle.Die</c>
    /// damages them with no attacker), a player run over by a vehicle (the ram check named no
    /// driver), and the gunner of a vehicle's own weapon (the kill named the gunner but nothing
    /// said it was the tank's cannon rather than the rifle they carry). The engine's call sites
    /// gather what they know; this decides what it means.
    /// </para>
    /// <para>
    /// <b>Engine-free so the rules are tested once</b>, here, rather than inferred from a
    /// killfeed screenshot. The Unity side resolves vehicles and seats to their wire types and
    /// passes plain numbers in.
    /// </para>
    /// <para>
    /// <b>A credited kill scores.</b> The killer this returns is the one <c>S_DEATH</c> carries
    /// and the one the score tally counts, so the destroyer of a full jeep scores its crew. That
    /// is the point: a feed naming the destroyer while the scoreboard credited nobody would be
    /// two answers to one question.
    /// </para>
    /// </remarks>
    public readonly struct DeathAttribution
    {
        /// <summary>
        /// How long after the hit that emptied a vehicle its attacker is still credited with the
        /// crew that dies with it.
        /// </summary>
        /// <remarks>
        /// Every shipped vehicle burns for 4 s between that hit and the <c>Die</c> that kills its
        /// crew (<c>burnTime: 4</c> on all five prefabs), so the crediting hit is always at least
        /// that old when it is read. Ten seconds covers the burn and a crash moments after being
        /// shot, and stops there: a vehicle winged a minute ago and flown into a hill was crashed,
        /// not shot down.
        /// </remarks>
        public const float DestroyerCreditSeconds = 10f;

        /// <summary><see cref="DestroyerCreditSeconds"/> in server ticks.</summary>
        public const uint DestroyerCreditTicks =
            (uint)(DestroyerCreditSeconds * ProtocolConstants.SIM_TICK_RATE);

        private DeathAttribution(ushort killerActorId, byte weaponId, byte vehicleType, DeathDetail detail)
        {
            KillerActorId = killerActorId;
            WeaponId = weaponId;
            VehicleType = vehicleType;
            Detail = detail;
        }

        /// <summary>The actor credited, or <see cref="DeathMessage.EnvironmentKiller"/>.</summary>
        public ushort KillerActorId { get; }

        /// <summary>The killing weapon, <see cref="WeaponIds.NONE"/> when no hand weapon killed.</summary>
        public byte WeaponId { get; }

        /// <summary>The vehicle the death involved (<see cref="VehicleIds"/>), <c>NONE</c> when none.</summary>
        public byte VehicleType { get; }

        /// <summary>How the vehicle was involved, and whether the blow was melee.</summary>
        public DeathDetail Detail { get; }

        /// <summary>Applies the rules to one death.</summary>
        /// <param name="killerActorId">
        /// Whom the damage named, or <see cref="DeathMessage.EnvironmentKiller"/>.
        /// </param>
        /// <param name="weaponId">
        /// The hand weapon the damage named. <see cref="WeaponIds.NONE"/> for a vehicle's own gun,
        /// which carries no id, for a ram, and for a fall or a drowning.
        /// </param>
        /// <param name="detail">
        /// What the damage's call site knew: <see cref="DeathDetail.WentDownWithVehicle"/> and
        /// <see cref="DeathDetail.Melee"/>. <see cref="DeathDetail.KillerInVehicle"/> is decided
        /// here and is ignored on the way in.
        /// </param>
        /// <param name="vehicleType">
        /// The vehicle the call site named — the one that went down, or the one that ran the
        /// victim over — or <see cref="VehicleIds.NONE"/>.
        /// </param>
        /// <param name="vehicleDestroyerActorId">
        /// Who emptied that vehicle within <see cref="DestroyerCreditSeconds"/>, or
        /// <see cref="DeathMessage.EnvironmentKiller"/>.
        /// </param>
        /// <param name="killerSeatVehicleType">
        /// The vehicle the killer is seated in, or <see cref="VehicleIds.NONE"/>.
        /// </param>
        public static DeathAttribution Resolve(
            ushort killerActorId, byte weaponId, DeathDetail detail, byte vehicleType,
            ushort vehicleDestroyerActorId, byte killerSeatVehicleType)
        {
            bool hasKiller = killerActorId != DeathMessage.EnvironmentKiller;
            detail &= DeathDetail.WentDownWithVehicle | DeathDetail.Melee;

            // The crew of a destroyed vehicle: Vehicle.Die damages them naming nobody, so the
            // kill belongs to whoever destroyed the vehicle -- and to the world when nobody did.
            if ((detail & DeathDetail.WentDownWithVehicle) != 0)
            {
                ushort killer = hasKiller ? killerActorId : vehicleDestroyerActorId;
                return new DeathAttribution(killer, weaponId, vehicleType, detail);
            }

            // No hand weapon named the kill and the killer is seated: the vehicle did it, with
            // its own gun or by driving into the victim. A passenger shooting their rifle names
            // the rifle and never reaches this branch.
            if (hasKiller && weaponId == WeaponIds.NONE && killerSeatVehicleType != VehicleIds.NONE)
            {
                return new DeathAttribution(
                    killerActorId, weaponId, killerSeatVehicleType,
                    detail | DeathDetail.KillerInVehicle);
            }

            // Anything else keeps what the call site named: a runaway vehicle with nobody at the
            // wheel still names the vehicle, and the killfeed says what hit the victim.
            return new DeathAttribution(killerActorId, weaponId, vehicleType, detail);
        }
    }
}
