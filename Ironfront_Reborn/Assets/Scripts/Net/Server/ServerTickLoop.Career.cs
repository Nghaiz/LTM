using System;
using System.Collections.Generic;
using Ironfront.Net.Protocol;
using Ironfront.Net.Protocol.Achievements;
using Ironfront.Net.Replication.Combat;
using Ironfront.Net.Replication.Match;
using Ironfront.Net.Replication.Server;
using Ironfront.Net.Replication.Vehicles;
using UnityEngine;

namespace Ironfront.Net.Unity.Server
{
    /// <summary>
    /// The achievement counters' hooks (achievements v2, <c>docs/achievements.md</c>): every place
    /// the server learns something the round's career tally needs reports it here, and this
    /// resolves ids, teams and seats once so the tally itself stays engine-free.
    /// </summary>
    public sealed partial class ServerTickLoop : INightVisionHandler
    {
        /// <summary>A signed-in player is leaving: (actor id, master player id). Their round is over.</summary>
        public event Action<ushort, int> PlayerLeavingRound;

        /// <summary>Server time in seconds, the tally's clock.</summary>
        internal float CareerNow => _scheduler.CurrentTick / (float)ProtocolConstants.SIM_TICK_RATE;

        /// <summary>Whether the round is in the phase that counts (the score's own gate).</summary>
        internal bool CareerCounting => _match != null && _match.Match != null && _match.Match.CountsDeaths;

        /// <summary>A Playing phase began on a map with <paramref name="mapFlags"/> capture points.</summary>
        public void BeginCareerRound(int mapFlags) => _careerTally.BeginRound(CareerNow, mapFlags);

        /// <summary>Damage left <paramref name="victimId"/> on <paramref name="healthAfter"/>.</summary>
        public void NoteCareerDamage(ushort victimId, ushort attackerId, float healthAfter, CauseOfDeath cause)
        {
            if (!CareerCounting) return;
            _careerTally.RecordDamage(victimId, AreEnemies(victimId, attackerId), healthAfter, cause);
        }

        /// <summary>A player lived through a fall with <paramref name="healthAfter"/> left.</summary>
        public void NoteCareerLanding(ushort actorId, float healthAfter)
        {
            if (CareerCounting) _careerTally.RecordLanding(actorId, healthAfter);
        }

        private long _careerShotSerial = long.MaxValue / 2;

        /// <summary>A player's hitscan shot and what it hit (accuracy).</summary>
        internal void NoteCareerShot(ushort shooter, byte weaponId, HitResult[] hits, int hitCount)
        {
            if (!CareerCounting || !CareerWeapons.IsFirearm(weaponId)) return;
            _careerTally.RecordShot(shooter, weaponId);
            long serial = ++_careerShotSerial;
            for (int i = 0; i < hitCount && i < hits.Length; i++)
            {
                if (!hits[i].Hit || !AreEnemies(shooter, hits[i].TargetActorId)) continue;
                _careerTally.RecordHit(shooter, weaponId, serial);
                break;
            }
        }

        /// <summary>A bot's (or any engine-fired) projectile from shot <paramref name="serial"/> hurt <paramref name="victim"/>.</summary>
        public void NoteCareerHit(ushort shooter, ushort victim, byte weaponId, long serial)
        {
            if (!CareerCounting || !AreEnemies(shooter, victim)) return;
            _careerTally.RecordHit(shooter, weaponId, serial);
        }

        /// <summary>The horn sounded with <paramref name="driver"/> at the wheel.</summary>
        public void NoteCareerHorn(ushort driver)
        {
            if (CareerCounting) _careerTally.RecordHorn(driver, CareerNow);
        }

        /// <summary><paramref name="giver"/>'s ammo bag or medipack gave <paramref name="target"/> something.</summary>
        public void NoteCareerResupply(ushort giver, ushort target)
        {
            if (!CareerCounting || giver == target || giver == 0) return;
            ServerActorRegistry registry = ServerActorRegistry.Instance;
            if (!registry.TryFind(giver, out NetServerActor a) || a == null) return;
            if (!registry.TryFind(target, out NetServerActor b) || b == null || a.Team != b.Team) return;
            _careerTally.RecordResupply(giver);
        }

        /// <summary>Players whose game reports night vision (it has sent C_NIGHT_VISION at least once).</summary>
        private readonly HashSet<ushort> _nightVisionReporters = new HashSet<ushort>();

        /// <summary>Players with night vision on right now, so a round that starts with it on counts it.</summary>
        private readonly HashSet<ushort> _nightVisionOn = new HashSet<ushort>();

        void INightVisionHandler.OnNightVision(ClientSession session, bool on)
        {
            ushort actor = session.ActorId;
            _nightVisionReporters.Add(actor);
            if (on)
            {
                _nightVisionOn.Add(actor);
                if (CareerCounting) _careerTally.RecordNightVision(actor);
            }
            else
            {
                _nightVisionOn.Remove(actor);
            }
        }

        /// <summary>A player's slot is being forgotten: so is what their game said.</summary>
        private void ForgetCareerActor(ushort actorId)
        {
            _nightVisionReporters.Remove(actorId);
            _nightVisionOn.Remove(actorId);
        }

        /// <summary>A vehicle's health reached zero; <paramref name="destroyer"/> is credited, 0 for nobody.</summary>
        private void OnCareerVehicleDowned(ushort vehicleId, ushort destroyer)
        {
            if (!CareerCounting) return;
            ServerVehicleRegistry vehicles = ServerVehicleRegistry.Instance;
            VehicleRegistry seats = vehicles.Registry;
            vehicles.TryFind(vehicleId, out IGameplayVehicleSource source);

            var down = new CareerVehicleDown
            {
                VehicleId = vehicleId,
                VehicleType = source != null ? source.NetworkTypeId : VehicleIds.NONE,
                Destroyer = destroyer != 0 ? destroyer : ushort.MaxValue,
                HeightMetres = source != null ? source.HeightAboveGround : -1f,
            };

            if (destroyer != 0 && ServerActorRegistry.Instance.TryFind(destroyer, out NetServerActor attacker) && attacker != null)
            {
                bool anyone = false;
                int seatCount = source != null ? source.SeatCount : 0;
                for (int s = 0; s < seatCount; s++)
                {
                    ushort occupant = seats.OccupantOf(vehicleId, (byte)s);
                    if (occupant == 0) continue;
                    anyone = true;
                    bool enemy = AreEnemies(destroyer, occupant);
                    if (enemy) down.EnemyVehicle = true;
                    if (s == 0 && enemy) down.EnemyPilotAboard = true;
                }
                if (!anyone && source != null && source.OwnerTeam >= 0 && source.OwnerTeam != attacker.Team)
                    down.EnemyVehicle = true;

                if (seats.TryFindSeatOf(destroyer, out ushort seatedIn, out byte seat))
                {
                    down.DestroyerVehicleType = VehicleTypeOf(seatedIn);
                    down.DestroyerIsPilot = seat == 0;
                    down.DestroyerHeightMetres = vehicles.TryFind(seatedIn, out IGameplayVehicleSource own) && own != null
                        ? own.HeightAboveGround
                        : -1f;
                    down.ByTankMainGun = down.DestroyerVehicleType == VehicleIds.TANK && NetVehicleAuthority.DamageIsExplosive;
                }
            }

            _careerTally.RecordVehicleDown(in down);
        }

        /// <summary>Once per step: every actor's health (damage taken, heals), and stint holders' seats.</summary>
        private void WatchCareer()
        {
            if (!CareerCounting) return;
            foreach (ushort on in _nightVisionOn) _careerTally.RecordNightVision(on);

            IReadOnlyList<NetServerActor> actors = ServerActorRegistry.Instance.Actors;
            VehicleRegistry seats = ServerVehicleRegistry.Instance.Registry;
            for (int i = 0; i < actors.Count; i++)
            {
                NetServerActor actor = actors[i];
                if (actor == null || actor.ActorId == 0) continue;
                _careerTally.WatchHealth(actor.ActorId, actor.IsAlive, actor.Health);
                if (!_careerTally.HasStint(actor.ActorId)) continue;
                seats.TryFindSeatOf(actor.ActorId, out ushort vehicleId, out byte seat);
                _careerTally.NoteSeat(actor.ActorId, vehicleId, seat);
            }
        }

        /// <summary>When <paramref name="actorId"/> pilots a helicopter: its height; -1 otherwise.</summary>
        private float PilotHeightOf(ushort actorId)
        {
            ServerVehicleRegistry vehicles = ServerVehicleRegistry.Instance;
            if (!vehicles.Registry.TryFindSeatOf(actorId, out ushort vehicleId, out byte seat) || seat != 0) return -1f;
            if (!vehicles.TryFind(vehicleId, out IGameplayVehicleSource source) || source == null) return -1f;
            return source.NetworkTypeId == VehicleIds.HELICOPTER ? source.HeightAboveGround : -1f;
        }

        private static byte VehicleTypeOf(ushort vehicleId)
            => vehicleId != 0 && ServerVehicleRegistry.Instance.TryFind(vehicleId, out IGameplayVehicleSource source) && source != null
                ? source.NetworkTypeId
                : VehicleIds.NONE;

        /// <summary>Two actors on different sides, both known. The world (0, MaxValue) is nobody's enemy.</summary>
        private static bool AreEnemies(ushort a, ushort b)
        {
            if (a == 0 || b == 0 || a == b || a == DeathMessage.EnvironmentKiller || b == DeathMessage.EnvironmentKiller) return false;
            ServerActorRegistry registry = ServerActorRegistry.Instance;
            return registry.TryFind(a, out NetServerActor x) && x != null
                   && registry.TryFind(b, out NetServerActor y) && y != null
                   && x.Team != y.Team;
        }

        /// <summary>
        /// One player's round as facts, ranks among everyone still in the round when
        /// <paramref name="finished"/>. Null when the player has no body.
        /// </summary>
        internal RoundSheet BuildRoundSheet(ushort actorId, byte winningTeam, bool finished)
        {
            ServerActorRegistry registry = ServerActorRegistry.Instance;
            if (!registry.TryFind(actorId, out NetServerActor body) || body == null) return null;

            int present = 0;
            if (finished)
            {
                IReadOnlyList<NetServerActor> actors = registry.Actors;
                for (int i = 0; i < actors.Count && present < _careerPopulation.Length; i++)
                {
                    NetServerActor actor = actors[i];
                    if (actor == null || actor.ActorId == 0) continue;
                    bool human = IsPlayerActor(actor.ActorId);
                    // A parked, unclaimed player body is nobody (#416): only bots and connected players count.
                    if (!human && actor.AvailableForPlayers) continue;
                    _careerPopulation[present++] = new RoundActor(actor.ActorId, actor.Team, human);
                }
            }

            var sheet = new RoundSheet();
            _careerTally.Fill(actorId, body.Team, winningTeam, CareerNow, finished, _scoreTally,
                new ReadOnlySpan<RoundActor>(_careerPopulation, 0, present), sheet);
            sheet.Set(RoundFact.NightVisionKnown, _nightVisionReporters.Contains(actorId) ? 1 : 0);
            return sheet;
        }

        private readonly RoundActor[] _careerPopulation = new RoundActor[ProtocolConstants.MAX_ACTORS];
    }
}
