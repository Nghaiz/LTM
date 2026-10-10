using System;
using Ironfront.Net.Protocol;
using Ironfront.Net.Protocol.Achievements;

namespace Ironfront.Net.Replication.Match
{
    /// <summary>The non-kill events: damage, health, shots, vehicles, flags, the horn, night vision.</summary>
    public sealed partial class MatchCareerTally
    {
        /// <summary>
        /// Damage was applied to <paramref name="victim"/>, leaving <paramref name="healthAfter"/>.
        /// An enemy's blow that leaves 1 to 5 health starts ON BORROWED TIME.
        /// </summary>
        public void RecordDamage(ushort victim, bool byEnemy, float healthAfter, CauseOfDeath cause)
        {
            if (victim >= Actors || !byEnemy || cause == CauseOfDeath.Fall || cause == CauseOfDeath.Drown) return;
            if (healthAfter <= 0f || healthAfter > CareerRules.LowHealth) return;
            if (_clutch[victim]) return;
            _clutch[victim] = true;
            _clutchKills[victim] = 0;
        }

        /// <summary>
        /// The actor's health this tick. Any drop while alive is damage taken (every source: a
        /// bullet, a blast, a fall, the water); any rise while alive is a heal, which ends ON
        /// BORROWED TIME. Called once per tick for every actor.
        /// </summary>
        public void WatchHealth(ushort actor, bool alive, float health)
        {
            if (actor >= Actors) return;
            if (_watchAlive[actor])
            {
                float before = _watchHealth[actor];
                if (!alive) health = 0f;
                if (health < before) Add(actor, RoundFact.DamageTaken, (long)Math.Ceiling(before - health));
                else if (health > before && alive) _clutch[actor] = false;
            }
            _watchAlive[actor] = alive;
            _watchHealth[actor] = health;
        }

        /// <summary>A fall the player lived through left <paramref name="healthAfter"/>.</summary>
        public void RecordLanding(ushort actor, float healthAfter)
        {
            if (healthAfter > 0f && healthAfter <= CareerRules.LowHealth) Add(actor, RoundFact.FallsSurvivedLow, 1);
        }

        /// <summary>One trigger pull of <paramref name="weaponId"/>. Only carried firearms count.</summary>
        public void RecordShot(ushort shooter, byte weaponId)
        {
            if (CareerWeapons.IsFirearm(weaponId)) Add(shooter, RoundFact.Shots, 1);
        }

        /// <summary>
        /// Shot <paramref name="shotSerial"/> damaged an enemy soldier. A shotgun's pellets share a
        /// serial, so one shot is one hit however many pellets land.
        /// </summary>
        public void RecordHit(ushort shooter, byte weaponId, long shotSerial)
        {
            if (shooter >= Actors || !CareerWeapons.IsFirearm(weaponId)) return;
            if (_lastHitShot[shooter] == shotSerial) return;
            _lastHitShot[shooter] = shotSerial;
            if (_facts[shooter, (int)RoundFact.Hits] < _facts[shooter, (int)RoundFact.Shots])
                Add(shooter, RoundFact.Hits, 1);
        }

        /// <summary>A vehicle's health reached zero.</summary>
        public void RecordVehicleDown(in CareerVehicleDown down)
        {
            for (ushort a = 0; a < Actors; a++)
                if (_stintVehicle[a] == down.VehicleId && down.VehicleId != CareerKill.NoVehicle) EndStint(a);

            ushort destroyer = down.Destroyer;
            if (destroyer >= Actors || !down.EnemyVehicle) return;
            Add(destroyer, RoundFact.VehiclesDestroyed, 1);

            if (down.VehicleType != VehicleIds.HELICOPTER || !down.EnemyPilotAboard) return;
            if (down.HeightMetres < CareerRules.AirborneMetres) return;

            if (down.DestroyerVehicleType == VehicleIds.NONE) Add(destroyer, RoundFact.HelisDownedOnFoot, 1);
            // Its pilot's rockets or its door gun: either seat of a helicopter in the air.
            if (down.DestroyerVehicleType == VehicleIds.HELICOPTER
                && down.DestroyerHeightMetres >= CareerRules.AirborneMetres)
                Add(destroyer, RoundFact.Dogfights, 1);
            if (down.ByTankMainGun && down.HeightMetres >= CareerRules.ImpossibleAngleMetres)
                Add(destroyer, RoundFact.ImpossibleAngles, 1);
        }

        /// <summary>The driver sounded the horn: VICTORY LAP when it follows a roadkill closely enough.</summary>
        public void RecordHorn(ushort driver, float now)
        {
            if (driver >= Actors) return;
            if (now - _lastRoadkillTime[driver] > CareerRules.HornAfterRoadkillSeconds) return;
            _lastRoadkillTime[driver] = float.NegativeInfinity;
            Add(driver, RoundFact.HornAfterRoadkill, 1);
        }

        /// <summary>Flag <paramref name="pointId"/> turned to <paramref name="actor"/>'s side while they stood in it.</summary>
        public void CreditCapture(ushort actor, byte pointId)
        {
            if (actor >= Actors) return;
            Add(actor, RoundFact.FlagsCaptured, 1);
            if (pointId < 64) _flagMask[actor] |= 1UL << pointId;
        }

        /// <summary><paramref name="giver"/>'s ammo bag or medipack resupplied a teammate.</summary>
        public void RecordResupply(ushort giver) => Add(giver, RoundFact.Resupplies, 1);

        /// <summary>
        /// The player had night vision on during this round. Whether their game reports it at all
        /// (<see cref="RoundFact.NightVisionKnown"/>) is the session's, not the round's, and the
        /// server writes it into the sheet.
        /// </summary>
        public void RecordNightVision(ushort actor)
        {
            if (actor < Actors) _facts[actor, (int)RoundFact.NightVisionUsed] = 1;
        }

        /// <summary>Whether <paramref name="actor"/> has a vehicle stint running (TANK ACE, SKY KING).</summary>
        public bool HasStint(ushort actor) => actor < Actors && _stintVehicle[actor] != CareerKill.NoVehicle;

        /// <summary>
        /// Where a stint holder sits now. Leaving the tank, or the helicopter seat, ends the stint;
        /// the caller checks only the actors <see cref="HasStint"/> names, every tick.
        /// </summary>
        public void NoteSeat(ushort actor, ushort vehicleId, byte seat)
        {
            if (!HasStint(actor)) return;
            bool same = _stintVehicle[actor] == vehicleId && (!_stintIsHeli[actor] || _stintSeat[actor] == seat);
            if (!same) EndStint(actor);
        }

        private void EndStint(ushort actor)
        {
            _stintVehicle[actor] = CareerKill.NoVehicle;
            _stintSeat[actor] = -1;
            _stintKills[actor] = 0;
        }

        /// <summary>Forgets one actor (a player left; the slot may be reused).</summary>
        public void Forget(ushort actor)
        {
            if (actor >= Actors) return;
            for (int i = 0; i < RoundFacts.Count; i++) _facts[actor, i] = 0;
            _deathTime[actor] = float.NegativeInfinity;
            _joinTime[actor] = float.NegativeInfinity;
            _lastKillTime[actor] = float.NegativeInfinity;
            _multi[actor] = 0;
            _headshotRun[actor] = 0;
            _blastTime[actor] = float.NegativeInfinity;
            _blastKills[actor] = 0;
            _graveKills[actor] = 0;
            _lifeWeapons[actor] = 0;
            _enemyBlastKillTime[actor] = float.NegativeInfinity;
            _selfBlastDeathTime[actor] = float.MinValue;
            _mutualCountedAt[actor] = float.NegativeInfinity;
            _lastRoadkillTime[actor] = float.NegativeInfinity;
            _clutch[actor] = false;
            _clutchKills[actor] = 0;
            EndStint(actor);
            _watchAlive[actor] = false;
            _watchHealth[actor] = 0f;
            _lastHitShot[actor] = -1;
            _flagMask[actor] = 0;
            for (int other = 0; other < Actors; other++)
            {
                _pairKills[actor * Actors + other] = 0;
                _pairKills[other * Actors + actor] = 0;
                _killedBy[actor * Actors + other] = false;
                _killedBy[other * Actors + actor] = false;
            }
        }

        /// <summary>Starts over: a new round.</summary>
        public void Clear()
        {
            for (ushort a = 0; a < Actors; a++) Forget(a);
            _roundStart = 0f;
            _mapFlags = 0;
            _nearLoss[0] = false;
            _nearLoss[1] = false;
        }
    }
}
