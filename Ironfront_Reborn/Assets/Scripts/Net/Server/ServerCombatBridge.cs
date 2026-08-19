using System;
using System.Collections.Generic;
using Ironfront.Net.Protocol;
using Ironfront.Net.Replication.Combat;
using Ironfront.Net.Replication.Movement;
using Ironfront.Net.Replication.Server;
using Ironfront.Net.Transport;
using UnityEngine;

namespace Ironfront.Net.Unity.Server
{
    /// <summary>
    /// Carries one accepted input frame from the engine-free authority out to the transport:
    /// hits into the damage sink, S_WEAPON_FIRE / S_HIT_CONFIRM / S_DEATH onto the wire.
    /// phase-05 task 3.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>This class decides nothing.</b> Whether the shot was legal, what it hit, how much it
    /// did and whether that killed anyone are all <see cref="ServerCombatAuthority"/>'s answers,
    /// arrived at in a library CI can run. What is left here is the three things a library
    /// cannot do: read the connection's RTT off the transport, collect the live actor set, and
    /// put bytes on a socket. That is the same division of labour <c>ServerTickLoop</c> already
    /// follows, and for the same reason — a MonoBehaviour cannot be unit-tested, so nothing
    /// that could be wrong is allowed to live in one.
    /// </para>
    /// <para>
    /// <b>Allocation-free per tick.</b> The target array, the hit span backing and both payload
    /// buffers are fields built once. This runs once per accepted frame at 30 Hz across up to
    /// 16 players, which is the loop M1 criterion 9 requires to allocate nothing.
    /// </para>
    /// </remarks>
    internal sealed class ServerCombatBridge
    {
        /// <summary>
        /// Room for the widest shotgun in scope. A weapon with more pellets than this still
        /// fires and still resolves every projectile — <c>ServerFireResolver.Resolve</c>
        /// discards the overflow rather than overrunning — it just reports fewer hits.
        /// </summary>
        private const int MaxProjectilesPerShot = 16;

        private readonly ServerTickLoop _loop;
        private readonly ServerActorRegistry _registry;
        private readonly ServerCombatAuthority _authority;
        private readonly ServerRespawnGate _respawnGate;

        // V6 task 3. Null on a loop with no mounted-weapon subsystem, which is what every
        // pre-V6 construction of this class looks like.
        private readonly MountedWeaponRegistry _mountedWeapons;
        private readonly MountedWeaponAuthority _mountedWeaponAuthority;

        private readonly HitscanTarget[] _targets = new HitscanTarget[ProtocolConstants.MAX_ACTORS];
        private int _targetCount;
        private uint _targetsBuiltForTick = uint.MaxValue;

        private readonly HitResult[] _hits = new HitResult[MaxProjectilesPerShot];
        private readonly byte[] _eventPayload = new byte[ProtocolConstants.MAX_PAYLOAD];

        public ServerCombatBridge(
            ServerTickLoop loop,
            ServerActorRegistry registry,
            ServerCombatAuthority authority,
            ServerRespawnGate respawnGate,
            MountedWeaponRegistry mountedWeapons = null,
            MountedWeaponAuthority mountedWeaponAuthority = null)
        {
            _loop = loop ?? throw new ArgumentNullException(nameof(loop));
            _registry = registry ?? throw new ArgumentNullException(nameof(registry));
            _authority = authority ?? throw new ArgumentNullException(nameof(authority));
            _respawnGate = respawnGate ?? throw new ArgumentNullException(nameof(respawnGate));

            _mountedWeapons = mountedWeapons;
            _mountedWeaponAuthority = mountedWeaponAuthority;
        }

        /// <summary>Mounted shots this bridge resolved and announced. V6 task 3.</summary>
        public long MountedShotsFired { get; private set; }

        /// <summary>Deaths this bridge reported to the match. The killfeed's denominator.</summary>
        public long DeathsReported { get; private set; }

        /// <summary>
        /// Steps combat for one accepted frame and emits whatever it produced.
        /// </summary>
        public void StepCombat(ServerPlayer player, in InputFrame frame)
        {
            NetServerActor actor = player.Actor;
            if (actor == null) return;

            ClientSession session = player.Session;
            uint tick = _loop.CurrentTick;

            // The server's own clock, derived from the tick rather than from the wall clock.
            // Time.realtimeSinceStartup would make the reload duration depend on how the frame
            // rate happened to line up with the tick, which is the sort of difference that
            // shows up as a reload that occasionally takes an extra tick on a loaded server.
            float now = tick / (float)ProtocolConstants.SIM_TICK_RATE;

            // V6 task 3, and it RETURNS: a gunner operating a mounted weapon is not also firing
            // the rifle on their back. That is exactly what Seat.CanUseCarriedWeapon() has always
            // meant (V6-D7) -- a Gunner fires through the HasMountedWeapon() clause and never
            // through the carried one -- and letting both run would have one trigger pull spend a
            // turret round AND hitscan from the gunner's chest.
            if (StepMountedWeapon(session, actor, in frame, now)) return;

            BuildTargets(tick);

            // Hoisted to a local because WeaponConfig is a property since phase-V2 (D9) and an
            // explicit `in` argument needs an lvalue. This IS the struct copy that decision
            // priced: ~48 bytes once per accepted frame per player, no allocation. The escape
            // hatch it named -- caching the config for the duration of a tick -- is exactly this
            // local, and deliberately not a second stored field.
            WeaponConfig weapon = session.WeaponConfig;

            CombatTickResult result = _authority.Step(
                ref session.Weapon,
                in weapon,
                session.ActorId,
                in frame,
                in session.State,
                new ReadOnlySpan<HitscanTarget>(_targets, 0, _targetCount),
                actor.IsAlive,
                now,
                SmoothedRttMs(session.ConnectionId),
                tick,
                _hits);

            // The line that closes the reported bug: the server's clip is now the actor's clip,
            // so a reload or a shot changes SnapshotField.Weapon and the client's _reloadPending
            // finally clears.
            actor.AmmoInClip = session.Weapon.AmmoInClip;

            if (!result.Fired) return;

            EmitWeaponFire(session, actor, in result);
            EmitHitConfirms(session, in result);

            if (result.VictimDied) EmitDeath(session, in result);
        }

        /// <summary>
        /// Resolves one accepted frame against the mounted weapon this actor is sitting behind.
        /// V6 task 3.
        /// </summary>
        /// <remarks>
        /// <para>
        /// <b>The muzzle it fires from was settled earlier in this same tick</b> by
        /// <c>ServerTurretAuthority.Step</c>, which runs in the input stage before any player
        /// steps. Getting that order backwards is silent: every shot leaves from where the turret
        /// pointed one tick ago, which is invisible against a static target and systematically
        /// wrong against a traversing one.
        /// </para>
        /// <para>
        /// <b>No hitscan and no damage here.</b> A mounted weapon launches a projectile, and
        /// projectile flight is V7. This spends the server's ammo, honours the server's cooldown,
        /// and announces the shot so remote clients can draw it.
        /// </para>
        /// </remarks>
        /// <returns>True when this actor's fire intent belonged to a mounted weapon.</returns>
        private bool StepMountedWeapon(
            ClientSession session, NetServerActor actor, in InputFrame frame, float now)
        {
            if (_mountedWeaponAuthority == null || _mountedWeapons == null) return false;

            if (!ServerVehicleRegistry.Instance.Registry.TryFindSeatOf(
                    session.ActorId, out ushort vehicleId, out byte seatIndex))
                return false;

            // Tracked, not merely seated. A passenger in a seat with no mounted weapon keeps
            // their own rifle and takes the infantry path, which is the shipped behaviour.
            if (!_mountedWeapons.IsTracked(vehicleId, seatIndex)) return false;

            MountedFireResult result = _mountedWeaponAuthority.Step(
                vehicleId, seatIndex, in frame, actor.IsAlive, now);

            if (!result.Fired) return true;

            MountedShotsFired++;
            EmitMountedFire(session, vehicleId, seatIndex);
            return true;
        }

        /// <summary>
        /// Announces a mounted shot on the cosmetic channel, filtered by earshot.
        /// </summary>
        /// <remarks>
        /// The aim direction is zero and honestly so: <c>S_WEAPON_FIRE</c>'s direction field
        /// drives a hitscan TRACER, and a mounted weapon fires a projectile whose flight V7
        /// replicates in its own message with a server-computed origin. Writing the turret's
        /// heading here would draw a tracer that the shell does not follow.
        /// </remarks>
        private void EmitMountedFire(ClientSession shooter, ushort vehicleId, byte seatIndex)
        {
            var message = new WeaponFireMessage(
                shooter.ActorId,
                _mountedWeapons.WeaponIdOf(vehicleId, seatIndex),
                0, 0, 0);

            int written = ServerEventWriter.WriteWeaponFire(_eventPayload, in message);
            if (written < 0) return;

            _loop.SendToListenersInEarshot(
                shooter.State.Position,
                ServerEventWriter.WeaponFireAudibleRadius,
                new ReadOnlySpan<byte>(_eventPayload, 0, written),
                (byte)ServerEventWriter.CosmeticChannel,
                reliable: false);
        }

        /// <summary>
        /// Grants a respawn if the gate allows it, and silently drops it otherwise.
        /// </summary>
        /// <remarks>
        /// A request that arrives early is dropped rather than answered with an error. A client
        /// whose clock runs a few milliseconds fast asking half a tick early is the most common
        /// thing this message will ever do, and treating it as a protocol violation would
        /// disconnect honest players over clock skew.
        /// </remarks>
        public bool TryRespawn(ServerPlayer player)
        {
            NetServerActor actor = player.Actor;
            if (actor == null) return false;

            ClientSession session = player.Session;
            float now = _loop.CurrentTick / (float)ProtocolConstants.SIM_TICK_RATE;

            if (!_respawnGate.MayRespawn(session.ActorId, now)) return false;

            _respawnGate.MarkRespawned(session.ActorId);

            actor.Health = NetServerActor.DefaultSpawnHealth;
            actor.IsAlive = true;

            MoveToSpawnPoint(player);

            // BEFORE ResetWeapon, always. The clip size comes from the config, the config is
            // derived from this id (phase-V2 D9), and re-arming an unassigned id loads a clip of
            // zero — which presents as FireRejection.NoAmmo forever and reads exactly like the
            // ammo bug phase-05 closed.
            session.WeaponId = actor.WeaponId;

            session.ResetWeapon();
            actor.AmmoInClip = session.Weapon.AmmoInClip;

            return true;
        }

        /// <summary>
        /// Rebuilds the hitscan candidate set, at most once per tick.
        /// </summary>
        /// <remarks>
        /// Memoized on the tick because up to 16 players fire on the same tick and the live
        /// actor set does not change between them. Rebuilding per shot would be 16 scans of 64
        /// actors per tick for one answer.
        /// </remarks>
        private void BuildTargets(uint tick)
        {
            if (_targetsBuiltForTick == tick) return;

            _targetCount = 0;
            IReadOnlyList<NetServerActor> actors = _registry.Actors;

            for (int i = 0; i < actors.Count; i++)
            {
                NetServerActor candidate = actors[i];
                if (candidate == null || !candidate.isActiveAndEnabled) continue;
                if (_targetCount >= _targets.Length) break;

                _targets[_targetCount++] = new HitscanTarget(
                    candidate.ActorId, candidate.IsAlive, candidate.CaptureHitboxes());
            }

            _targetsBuiltForTick = tick;
        }

        /// <summary>
        /// This connection's smoothed RTT, or 0 when the transport has no reading yet.
        /// </summary>
        /// <remarks>
        /// <b>The phase-02 debt, settled.</b> Everything downstream of here was already written
        /// to compensate for latency — <c>LagCompensator.RewindTicks</c> has been correct since
        /// phase-02 — and it was being handed a hardcoded zero by the only caller that existed,
        /// which rewinds nothing. The symptom is not an error: high-ping players simply miss,
        /// consistently, and nothing anywhere says why.
        /// </remarks>
        private float SmoothedRttMs(ushort connectionId)
        {
            ITransportServer transport = _loop.Transport;
            if (transport == null) return 0f;

            float rtt = transport.GetInfo(connectionId).SmoothedRttMs;
            return float.IsNaN(rtt) || rtt < 0f ? 0f : rtt;
        }

        /// <summary>
        /// Puts a respawning player back at a spawn point instead of where they died.
        /// </summary>
        /// <remarks>
        /// <para>
        /// Respawn used to restore health and the alive flag and nothing else, so a player
        /// killed at a chokepoint reappeared at full health in the same square metre, usually
        /// still inside the killer's line of fire. Spawn-camping was free and needed no skill.
        /// </para>
        /// <para>
        /// <b>The authoritative position has to move with the transform.</b> The client is
        /// predicting against <c>MoveState</c>, and the speed clamp measures against
        /// <c>PreviousPosition</c> — teleporting the GameObject alone would leave the simulation
        /// standing at the corpse, and moving the simulation without re-baselining the clamp
        /// would score the teleport itself as a speed violation on the very next tick.
        /// </para>
        /// <para>
        /// Spawn points come from the game's own <c>ActorManager</c>, filtered by team the way
        /// <c>SpawnPoint.owner</c> already defines it — reached through
        /// <see cref="ISpawnPointDirectory"/>, because neither type is visible from an asmdef.
        /// No spawn points at all (a bare test scene, or nothing registered) leaves the player
        /// where they were, which is the previous behaviour.
        /// </para>
        /// </remarks>
        private static void MoveToSpawnPoint(ServerPlayer player)
        {
            NetServerActor actor = player.Actor;
            ISpawnPointDirectory spawnPoints = NetServerBindings.SpawnPoints;
            if (spawnPoints == null) return;

            int chosen = ChooseSpawnIndex(spawnPoints, actor.Team);
            if (chosen < 0) return;

            Vector3 position = spawnPoints.GetSpawnPosition(chosen);

            // Teleport, not a transform write: it disables the CharacterController around the
            // assignment, which otherwise fights it and lands the actor somewhere else.
            if (actor.Movement != null) actor.Movement.Teleport(position);
            else actor.transform.position = position;

            Vec3 core = MovementSimulation.ToCore(position);
            player.Session.State.Position = core;
            player.Session.State.Velocity = Vec3.Zero;
            player.Session.PreviousPosition = core;
        }

        /// <summary>
        /// Picks one spawn slot this team may use, or -1 when the scene offers none.
        /// </summary>
        /// <remarks>
        /// Reservoir sampling over the matching points: one pass, no allocation, and an even
        /// spread rather than always the first one in the array. Extracted from
        /// <see cref="MoveToSpawnPoint"/> so the EditMode suite can drive it with a fake
        /// directory — the team filter and the "no eligible point leaves the player where they
        /// were" branch are both behaviours a snapshot bug would silently break.
        /// </remarks>
        internal static int ChooseSpawnIndex(ISpawnPointDirectory spawnPoints, int team)
        {
            int chosen = -1;
            int candidates = 0;
            int count = spawnPoints.Count;

            for (int i = 0; i < count; i++)
            {
                if (!spawnPoints.IsEligible(i, team)) continue;

                candidates++;
                if (UnityEngine.Random.Range(0, candidates) == 0) chosen = i;
            }

            return chosen;
        }

        private void EmitWeaponFire(
            ClientSession shooter, NetServerActor actor, in CombatTickResult result)
        {
            var message = new WeaponFireMessage(
                shooter.ActorId,
                actor.WeaponId,
                Quantize.PackVel16(result.AimDirection.X),
                Quantize.PackVel16(result.AimDirection.Y),
                Quantize.PackVel16(result.AimDirection.Z));

            int written = ServerEventWriter.WriteWeaponFire(_eventPayload, in message);
            if (written < 0) return;

            // Cosmetic channel, and only to clients close enough to hear it — a gunshot is a
            // muzzle flash and a sound, so a client 300 m away gains nothing from it and a
            // client that can hear every shot on the map has been handed an audio wallhack.
            _loop.SendToListenersInEarshot(
                shooter.State.Position,
                ServerEventWriter.WeaponFireAudibleRadius,
                new ReadOnlySpan<byte>(_eventPayload, 0, written),
                (byte)ServerEventWriter.CosmeticChannel,
                reliable: false);
        }

        private void EmitHitConfirms(ClientSession shooter, in CombatTickResult result)
        {
            for (int i = 0; i < result.HitCount; i++)
            {
                ref readonly HitResult hit = ref _hits[i];

                WeaponConfig config = shooter.WeaponConfig;
                float damage = ServerFireResolver.DamageFor(in config, hit.HitboxType, hit.Distance);

                HitFlags flags = HitFlags.None;
                if (hit.IsHeadshot) flags |= HitFlags.Headshot;
                if (result.VictimDied && hit.TargetActorId == result.DeadActorId)
                    flags |= HitFlags.Killed;

                var message = new HitConfirmMessage(
                    hit.TargetActorId, HitConfirmMessage.PackDamage(damage), hit.HitboxType, flags);

                int written = ServerEventWriter.WriteHitConfirm(_eventPayload, in message);
                if (written < 0) continue;

                // To the shooter alone. Broadcasting a hit confirmation would tell every client
                // exactly when and how hard everyone else was being hit, which is a wallhack
                // served by the server.
                _loop.SendTo(
                    shooter.ConnectionId,
                    (byte)ServerEventWriter.ReliableChannel,
                    new ReadOnlySpan<byte>(_eventPayload, 0, written),
                    reliable: true);
            }
        }

        private void EmitDeath(ClientSession killer, in CombatTickResult result)
        {
            Vec3 force = result.AimDirection * killer.WeaponConfig.Force;

            byte hitbox = (byte)HitboxType.Body;
            for (int i = 0; i < result.HitCount; i++)
            {
                if (_hits[i].TargetActorId != result.DeadActorId) continue;
                hitbox = (byte)_hits[i].HitboxType;
                break;
            }

            // Through the loop's single death path, not framed here: a bot bullet and a player
            // bullet must produce the same S_DEATH, the same respawn stamp and the same ticket.
            _loop.EmitDeath(
                result.DeadActorId, killer.ActorId, in force, hitbox, CauseOfDeath.Bullet);

            DeathsReported++;
        }
    }
}
