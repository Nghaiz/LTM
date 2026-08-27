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
            // Weapon selection, BEFORE the mounted-weapon return and before anything reads the
            // active weapon. A gunner may still re-select what they will be holding when they
            // leave the seat, and Actor.SwitchWeapon refuses on its own if the seat forbids it.
            //
            // Bits 11-14 have been on the wire since the freeze with zero producers and zero
            // consumers. This is the consumer; the producer is InputButtonPacker's weaponSlot
            // overload. Both landed together deliberately -- a bit only one half understands is
            // how a protocol field becomes permanently zero, which is the packer's own remark.
            //
            // This is also what makes check 4 (two-client grenade parity) runnable at all: a
            // grenade is thrown by selecting the gear slot and pressing Fire, the path V6 already
            // made server-authoritative. Bit 7 was ThrowGrenade and V7-D10 retired it rather than
            // implementing it, because a dedicated throw bit is a second route to firing that
            // does not pass Weapon.CanFire().
            actor.ApplyWeaponSwitchIntent(frame.WeaponSlot);
            AdoptTheWeaponTheBodyIsHolding(session, actor);
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

            LogShot(session, in frame, in result, tick);

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

            PlaceAtSpawn(player);
            return true;
        }

        /// <summary>
        /// Re-points the combat session at whatever weapon the body is now holding.
        /// </summary>
        /// <remarks>
        /// <para>
        /// <b>Without this, a weapon switch changes the body and not the gun that fires.</b>
        /// <see cref="ClientSession.WeaponId"/> was assigned in exactly three places — join,
        /// respawn and round reset — all of them spawn-shaped, and
        /// <c>ApplyWeaponSwitchIntent</c> is none of them. So the actor's
        /// <c>activeWeapon.NetworkId</c> moved to the grenade while
        /// <see cref="ClientSession.WeaponConfig"/> — derived from the session's id — stayed the
        /// rifle's, and <c>ServerCombatAuthority</c> went on resolving hitscan with rifle
        /// ballistics.
        /// </para>
        /// <para>
        /// <b>Measured, not reasoned:</b> <c>artifacts/lane-b/r1-grenade-02</c> logs
        /// <c>[switch] actor=41 slot=2 outcome=forwarded weaponId=7</c> — the switch arrived and
        /// took — beside 60 of 60 <c>[shot] actor=41 weapon=1</c>. The body held the FRAG and
        /// every trigger pull spent a rifle round. That is the whole distance between X-31's fix
        /// and a detonation, and it is why R1.1's acceptance asks for both.
        /// </para>
        /// <para>
        /// <b>On change only, and the three statements are <see cref="PlaceAtSpawn"/>'s own, in
        /// its order</b> — id, then <c>ResetWeapon</c>, then the actor's clip. That order is not
        /// stylistic: <see cref="ClientSession.ResetWeapon"/> takes its clip size from the config
        /// the id derives, so re-arming before assigning loads a clip of zero and presents as
        /// <see cref="FireRejection.NoAmmo"/> forever.
        /// </para>
        /// <para>
        /// <b>A switch reloads, and that is a known consequence rather than an oversight.</b>
        /// The netcode session models ONE weapon — <c>Weapon</c> is a single
        /// <c>WeaponRuntimeState</c> — so there is nowhere to park the outgoing weapon's clip and
        /// nothing to restore the incoming one's. <c>NetServerActor.AmmoInClip</c> cannot supply
        /// it either: the bridge WRITES that field from the session every frame, so it mirrors the
        /// session rather than the body. Re-arming is therefore the only state this method can
        /// reach, and it means a player who switches away and back has a full magazine. Filed as
        /// its own row rather than fixed here, because per-slot ammo is a state the wire, the
        /// session and the snapshot would all have to grow.
        /// </para>
        /// </remarks>
        private static void AdoptTheWeaponTheBodyIsHolding(ClientSession session, NetServerActor actor)
        {
            if (actor.WeaponId == session.WeaponId) return;

            session.WeaponId = actor.WeaponId;
            session.ResetWeapon();
            actor.AmmoInClip = session.Weapon.AmmoInClip;
        }

        /// <summary>
        /// Puts a claimed body into the world: full health, alive, standing on a spawn point of
        /// its own team, holding a reloaded weapon.
        /// </summary>
        /// <remarks>
        /// <para>
        /// <b>This exists because the JOIN path did every step of it except the one that
        /// matters.</b> <c>OnClientConnected</c> set <c>Health</c> and <c>IsAlive</c>, then
        /// <c>WeaponId</c> / <c>ResetWeapon</c> / <c>AmmoInClip</c> — the same five statements in
        /// the same order as the respawn below — and never moved the body. The claimed actor
        /// therefore stayed wherever <c>Instantiate</c> left it: the world origin, falling.
        /// Measured on <c>artifacts/lane-b/combat-roster01</c>, where the local actor reports
        /// <c>x=0, z=0</c> with <c>y</c> descending 996.73 → 967.44 across all seven checkpoints
        /// while the snapshot cheerfully reported it alive on 100 health.
        /// </para>
        /// <para>
        /// <b>And <c>IsAlive = true</c> was not merely insufficient, it was actively
        /// disqualifying.</b> <c>ActorManager.SpawnWave</c> — the only code in the project that
        /// ever calls <c>Actor.SpawnAt</c> — selects on <c>actor.dead</c>, and
        /// <c>NetServerActor.IsAlive</c> is a pass-through to that flag. So the join cleared the
        /// one bit that would have let a wave place the body, and nothing else ever would. The
        /// comment on those two lines explains why they clear the previous occupant's corpse,
        /// which is correct and remains correct; what neither line did was finish the spawn.
        /// </para>
        /// <para>
        /// <b>Still short of a gameplay spawn, deliberately and visibly.</b>
        /// <see cref="MoveToSpawnPoint"/> teleports; it does not run <c>Actor.SpawnAt</c>, so
        /// <c>SpawnLoadoutWeapons</c> never runs and <c>actor.WeaponId</c> — which reads
        /// <c>activeWeapon.NetworkId</c> — stays 0 for a claimed body. That was already true of
        /// every respawn and is ledger row <b>X-11</b>; it needs a seam that does not exist yet
        /// (<c>IGameplayActorSource</c> has no spawn hook) and a decision about whether driving
        /// <c>controller.EnableInput()</c> server-side is right for a remotely-driven body.
        /// Fixing the position without claiming to have fixed the loadout.
        /// </para>
        /// </remarks>
        public void PlaceAtSpawn(ServerPlayer player)
        {
            NetServerActor actor = player.Actor;
            if (actor == null) return;

            ClientSession session = player.Session;

            actor.Health = NetServerActor.DefaultSpawnHealth;
            actor.IsAlive = true;

            MoveToSpawnPoint(player);

            // Arms the body. MoveToSpawnPoint teleports and does not call Actor.SpawnAt, so
            // until 2026-08-21 SpawnLoadoutWeapons never ran for a claimed body and every
            // networked player spawned holding nothing: weaponId 0, ammo 0/0, and eight seconds
            // of point-blank fire doing zero damage. That was X-11's predicted "next one".
            //
            // BEFORE the WeaponId read below, necessarily: that read goes through
            // Actor.activeWeapon.NetworkId, which does not exist until the loadout is spawned
            // and the first weapon unholstered.
            actor.EquipLoadout();

            // BEFORE ResetWeapon, always. The clip size comes from the config, the config is
            // derived from this id (phase-V2 D9), and re-arming an unassigned id loads a clip of
            // zero — which presents as FireRejection.NoAmmo forever and reads exactly like the
            // ammo bug phase-05 closed.
            session.WeaponId = actor.WeaponId;

            session.ResetWeapon();
            actor.AmmoInClip = session.Weapon.AmmoInClip;
        }

        /// <summary>
        /// Rebuilds the hitscan candidate set, at most once per tick.
        /// </summary>
        /// <remarks>
        /// Memoized on the tick because up to 16 players fire on the same tick and the live
        /// actor set does not change between them. Rebuilding per shot would be 16 scans of 64
        /// actors per tick for one answer.
        /// </remarks>
        /// <summary>
        /// One line per trigger frame, when <c>IRONFRONT_LOG_SHOTS=1</c>. Silent otherwise.
        /// </summary>
        /// <remarks>
        /// <para>
        /// <b>Ledger row X-15 exists because this did not.</b> On 2026-08-21 a client held
        /// Fire|Aim at six metres, the server drained a thirty-round clip on its own accounting,
        /// and the victim finished on 100 health -- and there was no way to tell from any
        /// artifact whether the shots were refused, fired and missed, or fired at a target list
        /// that was empty. Three very different bugs, one indistinguishable symptom.
        /// </para>
        /// <para>
        /// <b>Off by default, and by an env var rather than a define</b>, so a running dedicated
        /// server can be asked the question without a rebuild. Bounded by the fire rate rather
        /// than the tick rate: only a frame that pulled the trigger or was refused for pulling it
        /// prints, so an idle player costs one branch.
        /// </para>
        /// <para>
        /// <c>hitboxes</c> is the total across every candidate target, because a target with no
        /// hitboxes cannot be hit and would otherwise be indistinguishable from a miss. That is
        /// the first hypothesis this line was written to kill or confirm.
        /// </para>
        /// </remarks>
        /// <summary>
        /// <c>ShotsOccluded</c> as of the previous logged shot, so the occlusion description can
        /// be dated rather than reprinted. See <c>ServerTickLoop.OcclusionFor</c>.
        /// </summary>
        private long _occludedAtLastShotLog;

        /// <summary>
        /// <c>NearestMissesMeasured</c> as of the previous logged shot, so the nearest-miss
        /// description can be dated rather than reprinted. See
        /// <c>LagCompensator.NearestMissFor</c>.
        /// </summary>
        private long _nearestMissesAtLastShotLog;

        private void LogShot(
            ClientSession session, in InputFrame frame, in CombatTickResult result, uint tick)
        {
            if (!ShotLoggingEnabled) return;
            if (!frame.IsPressed(InputButtons.Fire)) return;

            // A HitboxSet is always four AABBs, so counting them proves nothing. What can be
            // wrong is that they are DEGENERATE -- Aabb.IsEmpty -- which is indistinguishable
            // from a miss in every artifact that exists today. So count the targets whose torso
            // is a real box, and print the nearest other target's torso outright.
            int alive = 0;
            int solid = 0;
            int nearestIndex = -1;
            float nearestDistance = float.MaxValue;

            for (int i = 0; i < _targetCount; i++)
            {
                HitscanTarget target = _targets[i];
                if (target.IsAlive) alive++;
                if (!target.Present.Torso.IsEmpty) solid++;
                if (target.ActorId == session.ActorId) continue;

                Vec3 delta = target.Present.Torso.Center - result.Origin;
                float distance = MathF.Sqrt(delta.X * delta.X + delta.Y * delta.Y + delta.Z * delta.Z);
                if (distance >= nearestDistance) continue;

                nearestDistance = distance;
                nearestIndex = i;
            }

            string nearest = "none";
            if (nearestIndex >= 0)
            {
                HitscanTarget target = _targets[nearestIndex];
                nearest = $"actor={target.ActorId} alive={target.IsAlive} d={nearestDistance:F1}m "
                          + $"torso={target.Present.Torso.Center.X:F1},{target.Present.Torso.Center.Y:F1},"
                          + $"{target.Present.Torso.Center.Z:F1} "
                          + $"extents={target.Present.Torso.Extents.X:F2},{target.Present.Torso.Extents.Y:F2},"
                          + $"{target.Present.Torso.Extents.Z:F2}";
            }

            // X-20 freshness: LastOcclusion is last-write-wins and only written on a HIT, so a
            // shot nothing blocked would otherwise reprint the previous shot's collider. The
            // counter rises exactly when a description is written, so it dates it.
            long occludedNow = _authority.FireResolver.LagCompensator.ShotsOccluded;
            string occlusion = ServerTickLoop.OcclusionFor(
                occludedNow, _occludedAtLastShotLog, ServerTickLoop.LastOcclusion);
            _occludedAtLastShotLog = occludedNow;

            // X-24: `hits=0` reads identically for a shot aimed at the sky, a shot three
            // centimetres high, and a shot the boxes never saw. This says WHICH box the ray came
            // closest to and on WHICH SIDE of it -- the measurement the row required before any
            // fix, so a widening is sized from a run rather than from the constants that produced
            // the gap. Dated the same way the occlusion line is, and for the same reason.
            long nearestMissesNow = _authority.FireResolver.LagCompensator.NearestMissesMeasured;
            string nearestMiss = LagCompensator.NearestMissFor(
                nearestMissesNow, _nearestMissesAtLastShotLog,
                _authority.FireResolver.LagCompensator.LastNearestMiss);
            _nearestMissesAtLastShotLog = nearestMissesNow;

            Debug.Log(
                $"[shot] actor={session.ActorId} weapon={session.WeaponId} "
                // Ledger X-31. The grenade is never held, and the loss is somewhere between the
                // programme's switchWeaponSlot and frame.WeaponSlot: the slots ARE armed
                // (slot2[FRAG toggleable=False]), the wire carries the full ushort, this very
                // method runs 60 times in the run that reports it, and `fire` from the SAME step
                // object arrives while the slot bit does not. These two fields are the only place
                // left to look -- what the server actually received, before anything interprets it.
                + $"buttons=0x{(ushort)frame.Buttons:X4} slot={frame.WeaponSlot} "
                + $"ammo={session.Weapon.AmmoInClip} rejection={result.Rejection} "
                + $"fired={result.Fired} hits={result.HitCount} "
                + $"targets={_targetCount} alive={alive} solidTorsos={solid} "
                + $"origin={result.Origin.X:F1},{result.Origin.Y:F1},{result.Origin.Z:F1} "
                + $"aim={result.AimDirection.X:F2},{result.AimDirection.Y:F2},{result.AimDirection.Z:F2} "
                + $"nearest[{nearest}] "
                // A ray can be PROVEN to enter a hitbox and still resolve as a miss. These are
                // the two ways, and without them a rejected hit is indistinguishable from a
                // bad aim -- which cost a slab test done by hand on 2026-08-22 to rule out. X-19.
                + $"occluded={_authority.FireResolver.LagCompensator.ShotsOccluded} "
                // X-20: occluded counts rejections; this says WHAT rejected them. The two
                // readings the 2026-08-23 run could not separate are "there is a wall between
                // them" and "the victim's own capsule blocked the shot that hit it", and the
                // collider name tells them apart on sight.
                + $"occlusionHit[{occlusion}] "
                + $"nearestMiss[{nearestMiss}] "
                + $"presentFallbacks={_authority.FireResolver.LagCompensator.PresentFallbacks} "
                + $"resolved={_authority.FireResolver.LagCompensator.ShotsResolved} "
                + DescribeX19(session, nearestIndex, tick));
        }

        /// <summary>
        /// The five fields phase-3F section 3 names, on the same line as the shot they belong to.
        /// </summary>
        /// <remarks>
        /// <para>
        /// <b>Three runs could not tell two different files apart, because the line printed one
        /// pose and the resolver used another.</b> <c>nearest[... torso=...]</c> above comes from
        /// <c>HitscanTarget.Present</c> -- the pose the server holds NOW -- while
        /// <see cref="LagCompensator.ResolveHitscan"/> raycasts against
        /// <c>HitboxHistory.Frame.Boxes</c> at the REWOUND tick. Both are printed here, so the
        /// artifact separates "the pose was recorded low" from "the pose is fine and the body is
        /// drawn low". Those are different defects in different files, and X-19 has been stuck on
        /// exactly that fork since 2026-08-22.
        /// </para>
        /// <para>
        /// <b>The rewind tick is recomputed, not read back.</b> <c>CombatTickResult</c> carries
        /// the tick only on a <see cref="HitResult"/>, and the shots this line exists to explain
        /// are the ones that produced no hit at all. Recomputing through the same
        /// <see cref="LagCompensator.ResolveTargetTick"/> the resolver used, from the same tick
        /// and the same RTT, is the only way to name the frame a MISS was judged against -- and
        /// it is the same static function, so it cannot drift from the resolver's answer.
        /// </para>
        /// <para>
        /// <b><c>shooter.movement</c> and <c>shooter.transform</c> are printed separately on
        /// purpose.</b> The first is <c>NetMovementAgent.State.Position</c>, which is what
        /// <see cref="ServerCombatAuthority.ShotOrigin"/> adds the eye height to; the second is
        /// where the body actually stands in the scene. On the server those are written together
        /// by <c>NetMovementAgent.CharacterMove</c> and must agree; a gap between them is the
        /// server-side half of the same disagreement the clients report at every checkpoint.
        /// </para>
        /// </remarks>
        private string DescribeX19(ClientSession session, int nearestIndex, uint tick)
        {
            uint rewindTick = LagCompensator.ResolveTargetTick(
                tick, SmoothedRttMs(session.ConnectionId));

            string present = "none";
            string recorded = "none";
            string recordedTick = "absent";

            if (nearestIndex >= 0)
            {
                HitscanTarget target = _targets[nearestIndex];
                present = Describe(target.Present.Torso.Center);

                if (_loop.HitboxHistory.TryGetFrame(
                        target.ActorId, rewindTick, out HitboxHistory.Frame recordedFrame))
                {
                    recorded = Describe(recordedFrame.Boxes.Torso.Center);
                    recordedTick = recordedFrame.Tick.ToString();
                }
            }

            string movement = "absent";
            string drawn = "absent";

            if (_registry.TryFind(session.ActorId, out NetServerActor shooter) && shooter != null)
            {
                if (shooter.Movement != null)
                    movement = Describe(shooter.Movement.State.Position);

                Vector3 p = shooter.transform.position;
                drawn = $"{p.x:F3},{p.y:F3},{p.z:F3}";
            }

            return $"present.torso={present} frame.torso={recorded} "
                   + $"frame.tick={recordedTick} wanted.tick={rewindTick} tick={tick} "
                   + $"shooter.movement={movement} shooter.transform={drawn}";
        }

        /// <summary>Three decimals: the offset under investigation is a third of a metre.</summary>
        private static string Describe(in Vec3 v) => $"{v.X:F3},{v.Y:F3},{v.Z:F3}";

        /// <summary>Read once: this is consulted on every trigger frame.</summary>
        private static bool ShotLoggingEnabled =>
            _shotLogging ??= Environment.GetEnvironmentVariable("IRONFRONT_LOG_SHOTS") == "1";

        private static bool? _shotLogging;
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
        // Once-only, because a player who cannot spawn will keep asking: the respawn path calls
        // MoveToSpawnPoint on every request, and a warning that repeats sixty times a second is
        // filtered out as noise, which is the same as not warning at all.
        private static readonly System.Collections.Generic.HashSet<string> _warned =
            new System.Collections.Generic.HashSet<string>();

        private static void WarnOnce(string key, string message)
        {
            if (!_warned.Add(key)) return;
            Debug.LogWarning(message);
        }

        private static void MoveToSpawnPoint(ServerPlayer player)
        {
            NetServerActor actor = player.Actor;
            ISpawnPointDirectory spawnPoints = NetServerBindings.SpawnPoints;

            // BOTH of these used to return in silence, and a spawn that silently does not happen
            // is the single most expensive shape of bug in this subsystem: the actor stays where
            // Instantiate left it, the snapshot reports it alive on full health, every client
            // renders a healthy player at the world origin, and no log anywhere says why. That
            // cost a whole investigation on 2026-08-21 (X-12) — and then cost a second one,
            // because after the join was taught to call this, the body STILL did not move and
            // there was no way to tell which of these two branches had fired.
            if (spawnPoints == null)
            {
                WarnOnce(
                    "spawn-no-directory",
                    "[net] no ISpawnPointDirectory, so no player can ever be placed. "
                    + "NetServerBindings.SpawnPoints is installed by IronfrontNetBindings; a "
                    + "scene with no ActorManager has nothing to install.");
                return;
            }

            int chosen = ChooseSpawnIndex(spawnPoints, actor.Team);
            if (chosen < 0)
            {
                WarnOnce(
                    "spawn-none-eligible-team" + actor.Team,
                    $"[net] actor {actor.ActorId} (team {actor.Team}) has no eligible spawn point "
                    + $"among {spawnPoints.Count}, so it stays where it is. SpawnPoint.owner must "
                    + "be -1 (any team) or match the team.");
                return;
            }

            Vector3 position = spawnPoints.GetSpawnPosition(chosen);
            Debug.Log($"[net] actor {actor.ActorId} (team {actor.Team}) placed at spawn point "
                      + $"{chosen} of {spawnPoints.Count} {position}");

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
