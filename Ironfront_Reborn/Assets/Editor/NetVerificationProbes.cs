using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Reflection;
using System.Text;
using Ironfront.Net.Protocol;
using Ironfront.Net.Replication.Combat;
using Ironfront.Net.Replication.Movement;
using Ironfront.Net.Replication.Server;
using Ironfront.Net.Unity.Server;
using UnityEditor;
using UnityEngine;

namespace Ironfront.Editor.Verification
{
    /// <summary>
    /// The probes checklist rows E3, E6 and E8 turned out to need, kept apart from
    /// <see cref="NetVerificationHarness"/> because they measure the server in isolation rather
    /// than driving a client over the wire.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Why these exist at all.</b> Both E3 and E6 ask for something that cannot be produced
    /// from a keyboard in this scene. E3 wants a shot aimed through a wall at a live target, and
    /// a yaw sweep cannot deliver one — a 0.5 m torso subtends a quarter of a degree at 110 m
    /// while a 150 deg/s sweep lands shots 15 degrees apart, so 507 real shots over UDP produced
    /// zero intersections and told us nothing about occlusion either way. E6 wants a respawn, and
    /// no client under <c>Assets/</c> sends <c>C_SPAWN_REQUEST</c>. So the trigger is synthesised
    /// while every line of the decision path — <c>ServerCombatBridge</c>,
    /// <see cref="ServerCombatAuthority"/>, <see cref="ServerFireResolver"/>,
    /// <see cref="LagCompensator"/> and the real Unity colliders — stays the shipping one.
    /// </para>
    /// <para>
    /// <b>Two workarounds live here, and both compensate for server defects rather than for the
    /// probe's convenience.</b> <see cref="FixRegistrySplit"/> repairs a split
    /// <see cref="ServerActorRegistry"/> singleton and <see cref="PinAuthoritativeGround"/>
    /// supplies the ground the authoritative simulation has no source for. Without them every
    /// shot resolves against an empty candidate set from an origin kilometres below the map, so
    /// E3 is unobservable — not failing, unobservable. They are named after what they compensate
    /// for so a later reader does not mistake them for test rigging. Both must be deleted once
    /// the underlying defects are fixed; see the round-9 report.
    /// </para>
    /// </remarks>
    public static class NetVerificationProbes
    {
        /// <summary>Everything except layer 11, matching <c>ServerTickLoop.BulletBlockingLayers</c>.</summary>
        private const int BulletBlockingLayers = -2049;

        /// <summary>Below this the actor has already left the world and has no ground to pin to.</summary>
        private const float LostToFreefallY = -100f;

        private static EditorApplication.CallbackFunction _groundPin;

        // =====================================================================================
        // Workaround 1 — the split registry.
        // =====================================================================================

        /// <summary>
        /// Repoints the combat bridge and the damage sink at the
        /// <see cref="ServerActorRegistry"/> the scene's actors actually registered into.
        /// </summary>
        /// <remarks>
        /// <para>
        /// <b>This is a defect workaround.</b> Both objects capture
        /// <c>ServerActorRegistry.Instance</c> from <see cref="ServerTickLoop"/>'s constructor,
        /// and that runs while Unity deserialises the play-mode scene — before
        /// <c>[RuntimeInitializeOnLoadMethod(SubsystemRegistration)] ResetOnLoad</c> nulls the
        /// static. Actors then register from <c>OnEnable</c>, which is after the reset, so they
        /// populate a second instance. Measured live: the bridge's registry held 0 actors while
        /// the one actors registered into held 41, and the two were not reference-equal.
        /// </para>
        /// <para>
        /// The consequence is total. <c>BuildTargets</c> scans an empty list, so the hitscan
        /// candidate span is empty on every shot and no bullet in the game can hit anyone. The
        /// counters read as healthy while it happens — 507 shots resolved, 0 hits, 0 occlusions,
        /// 0 kills — because "there was nobody to hit" and "everybody missed" produce the same
        /// numbers.
        /// </para>
        /// </remarks>
        public static string FixRegistrySplit()
        {
            ServerTickLoop loop = ServerTickLoop.Current;
            if (loop == null) return "no ServerTickLoop";

            ServerActorRegistry live = ServerActorRegistry.Instance;
            var report = new StringBuilder();
            report.Append("live registry ").Append(live.Count).Append(" actors");

            foreach (string holderName in new[] { "_combat", "_damageSink" })
            {
                object holder = Field(typeof(ServerTickLoop), holderName)?.GetValue(loop);
                if (holder == null) { report.Append(" | ").Append(holderName).Append(" missing"); continue; }

                FieldInfo registryField = Field(holder.GetType(), "_registry");
                if (registryField == null)
                {
                    report.Append(" | ").Append(holderName).Append(" has no _registry");
                    continue;
                }

                var before = registryField.GetValue(holder) as ServerActorRegistry;
                bool split = !ReferenceEquals(before, live);
                if (split) registryField.SetValue(holder, live);

                report.Append(" | ").Append(holderName).Append(": ")
                      .Append(before == null ? "null" : before.Count.ToString())
                      .Append(split ? " -> repointed" : " already live");
            }

            return report.ToString();
        }

        // =====================================================================================
        // Workaround 2 — the ground the authoritative simulation does not have.
        // =====================================================================================

        /// <summary>
        /// Holds every session's authoritative <see cref="MoveState"/> on the ground its actor's
        /// transform is standing on.
        /// </summary>
        /// <remarks>
        /// <para>
        /// <b>Also a defect workaround.</b> <see cref="MovementCore"/> takes <c>IsGrounded</c> as
        /// an input because a pure simulation cannot raycast, and the only thing that could supply
        /// it — <c>NetMovementAgent</c> — is on no prefab and in no scene. Every session therefore
        /// runs <c>ServerPlayer.MoveDetached</c>, which is unopposed gravity: measured live, one
        /// session's authoritative Y was -4225 m and another's -11218 m while both actors stood
        /// visibly on the terrain at Y≈25 m, and the scene's own player actor reached -120941 m.
        /// Shot origins leave weapon range within a second of joining, which is the second reason
        /// E3 could not be observed.
        /// </para>
        /// <para>
        /// Deliberately minimal: position, velocity and the grounded flag only. It does not top
        /// the clip back up, because a session that cannot reload is a separate reported defect
        /// (no client sends <c>Reload</c>) and hiding it here would cost us the evidence.
        /// </para>
        /// <para>
        /// Idempotent under the MCP plugin's request retries — the delegate is stored, and a
        /// second call replaces rather than stacks it. That matters because a retried
        /// <c>script-execute</c> can run the same snippet ten times.
        /// </para>
        /// </remarks>
        public static string PinAuthoritativeGround(bool on)
        {
            if (_groundPin != null)
            {
                EditorApplication.update -= _groundPin;
                _groundPin = null;
            }

            if (!on) return "ground pin removed";

            _groundPin = PinOnce;
            EditorApplication.update += _groundPin;
            return "ground pin installed";
        }

        private static void PinOnce()
        {
            ServerTickLoop loop = ServerTickLoop.Current;
            if (loop == null) return;

            foreach (object player in Players(loop))
            {
                ClientSession session = SessionOf(player);
                NetServerActor actor = ActorOf(player);
                if (session == null || actor == null) continue;

                Vector3 feet = actor.transform.position;
                if (feet.y < LostToFreefallY) continue;

                var position = new Vec3(feet.x, feet.y, feet.z);
                session.State.Position = position;
                session.State.Velocity = Vec3.Zero;
                session.State.IsGrounded = true;
                session.PreviousPosition = position;
            }
        }

        // =====================================================================================
        // E3 — does the occlusion delegate block a shot that would otherwise land?
        // =====================================================================================

        /// <summary>
        /// Fires one shot at every reachable actor and reports each against Unity's own
        /// line-of-sight verdict.
        /// </summary>
        /// <remarks>
        /// <para>
        /// The aim point is the pose <see cref="LagCompensator"/> will rewind to, not the live
        /// transform. Aiming at the present pose of a target that walked half a metre since the
        /// rewind tick misses by more than its own box is wide at these ranges, and the resulting
        /// table of zeroes looks exactly like "occlusion does nothing".
        /// </para>
        /// <para>
        /// Spread is forced to zero for the duration so a miss is a miss rather than an unlucky
        /// cone roll, and the shooter's original <see cref="WeaponConfig"/> is restored before
        /// returning. Cooldown is zeroed for the same reason: at 0.1 s every shot after the first
        /// would be swallowed by the rate limiter and score as a silent miss.
        /// </para>
        /// </remarks>
        /// <param name="reviveTargets">
        /// Raise dead actors to full health first. The scene's AI kill each other continuously and
        /// <c>ResolveHitscan</c> skips a target that is not alive, so without this most rows
        /// report nothing at all.
        /// </param>
        public static string OcclusionSweep(bool reviveTargets)
        {
            ServerTickLoop loop = ServerTickLoop.Current;
            if (loop == null) return "no ServerTickLoop";

            object shooter = null;
            foreach (object player in Players(loop)) { shooter = player; break; }
            if (shooter == null) return "no connected session to shoot with";

            ClientSession session = SessionOf(shooter);
            NetServerActor shooterActor = ActorOf(shooter);
            if (session == null || shooterActor == null) return "session has no actor";

            object bridge = Field(typeof(ServerTickLoop), "_combat").GetValue(loop);
            MethodInfo stepCombat = bridge.GetType().GetMethod(
                "StepCombat", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            MethodInfo rttOf = bridge.GetType().GetMethod(
                "SmoothedRttMs", BindingFlags.Instance | BindingFlags.NonPublic);
            FieldInfo builtForTick = Field(bridge.GetType(), "_targetsBuiltForTick");

            Vector3 feet = shooterActor.transform.position;
            session.State.Position = new Vec3(feet.x, feet.y, feet.z);
            session.State.Velocity = Vec3.Zero;
            session.State.IsGrounded = true;
            session.State.IsCrouching = false;
            session.PreviousPosition = session.State.Position;
            shooterActor.IsAlive = true;
            shooterActor.Health = NetServerActor.DefaultSpawnHealth;

            var origin = new Vector3(feet.x, feet.y + ProtocolConstants.EYE_HEIGHT, feet.z);
            float rtt = (float)rttOf.Invoke(bridge, new object[] { session.ConnectionId });
            uint tick = loop.CurrentTick;
            uint rewindTick = LagCompensator.ResolveTargetTick(tick, rtt);

            WeaponConfig restore = session.WeaponConfig;
            session.WeaponConfig = new WeaponConfig(
                cooldown: 0f, spread: 0f, projectilesPerShot: 1, range: restore.Range,
                damage: restore.Damage, force: restore.Force, clipSize: restore.ClipSize);

            LagCompensator lag = loop.LagCompensator;
            int wallShots = 0, wallHits = 0, wallOccluded = 0;
            int clearShots = 0, clearHits = 0, damaged = 0;
            var rows = new StringBuilder();

            var actors = new List<NetServerActor>(ServerActorRegistry.Instance.Actors);
            foreach (NetServerActor target in actors)
            {
                if (target == null || !target.isActiveAndEnabled) continue;
                if (target.ActorId == session.ActorId) continue;

                if (reviveTargets)
                {
                    target.IsAlive = true;
                    target.Health = NetServerActor.DefaultSpawnHealth;

                    // BuildTargets memoises per tick, and the tick cannot advance while this
                    // method holds the main thread, so without invalidating it the revive would
                    // not be visible to the candidate set it was done for.
                    builtForTick.SetValue(bridge, uint.MaxValue);
                }

                HitboxSet boxes = loop.HitboxHistory.TryGetFrame(
                    target.ActorId, rewindTick, out HitboxHistory.Frame frame)
                    ? frame.Boxes
                    : target.CaptureHitboxes();

                Vec3 centre = boxes.Torso.Center;
                var aimPoint = new Vector3(centre.X, centre.Y, centre.Z);
                Vector3 toTarget = aimPoint - origin;
                float distance = toTarget.magnitude;
                if (distance < 4f || distance > restore.Range) continue;

                bool blocked = Physics.Linecast(origin, aimPoint, BulletBlockingLayers);

                float yaw = Mathf.Repeat(Mathf.Atan2(toTarget.x, toTarget.z) * Mathf.Rad2Deg, 360f);

                // Negated because AimDirection is rebuilt through Unity's euler X, where looking
                // down is positive.
                float pitch = -Mathf.Asin(Mathf.Clamp(toTarget.y / distance, -1f, 1f)) * Mathf.Rad2Deg;

                session.Weapon.LastFiredTime = -1000f;
                session.Weapon.AmmoInClip = restore.ClipSize;
                session.Weapon.Reloading = false;

                long resolvedBefore = lag.ShotsResolved;
                long hitBefore = lag.ShotsHit;
                long occludedBefore = lag.ShotsOccluded;
                float healthBefore = target.Health;

                stepCombat.Invoke(bridge, new object[]
                {
                    shooter,
                    InputFrame.FromFloats(0f, 0f, yaw, pitch, InputButtons.Fire),
                });

                // No resolve at all means the shot never reached the compensator — an empty
                // candidate set or a rate limit, neither of which says anything about occlusion.
                if (lag.ShotsResolved == resolvedBefore) continue;

                long hitDelta = lag.ShotsHit - hitBefore;
                long occludedDelta = lag.ShotsOccluded - occludedBefore;
                if (target.Health < healthBefore) damaged++;

                if (blocked) { wallShots++; wallHits += (int)hitDelta; wallOccluded += (int)occludedDelta; }
                else { clearShots++; clearHits += (int)hitDelta; }

                rows.Append("\n  id").Append(target.ActorId)
                    .Append(" d=").Append(distance.ToString("0", CultureInfo.InvariantCulture)).Append('m')
                    .Append(blocked ? " BLOCKED" : " clear  ")
                    .Append(" hit+").Append(hitDelta)
                    .Append(" occl+").Append(occludedDelta)
                    .Append(" hp ").Append(healthBefore.ToString("0", CultureInfo.InvariantCulture))
                    .Append("->").Append(target.Health.ToString("0", CultureInfo.InvariantCulture));
            }

            session.WeaponConfig = restore;

            return "shooter actor " + session.ActorId
                   + " origin " + origin.ToString("0.0")
                   + " tick " + tick + " rewind " + rewindTick + " rtt " + rtt.ToString("0")
                   + rows
                   + "\n  WALL  shots=" + wallShots + " hits=" + wallHits + " occluded+=" + wallOccluded
                   + "\n  CLEAR shots=" + clearShots + " hits=" + clearHits + " damaged=" + damaged
                   + "\n  cumulative resolved=" + lag.ShotsResolved + " hit=" + lag.ShotsHit
                   + " occluded=" + lag.ShotsOccluded + " fallbacks=" + lag.PresentFallbacks
                   + " kills=" + loop.CombatAuthority.KillsResolved;
        }

        // =====================================================================================
        // E6 — did the respawn land on an ActorManager spawn point?
        // =====================================================================================

        /// <summary>
        /// The spawn points the server's respawn can choose from, with each connected session's
        /// distance to the nearest one it is eligible for.
        /// </summary>
        /// <remarks>
        /// Reads <c>ActorManager.instance.spawnPoints</c> rather than scanning the scene, because
        /// that array is what <c>MoveToSpawnPoint</c> samples — a scene scan could report a point
        /// the respawn would never pick. The team filter is mirrored for the same reason: a point
        /// with <c>owner &gt;= 0</c> is only a candidate for its own team.
        /// </remarks>
        public static string SpawnPointReport()
        {
            ServerTickLoop loop = ServerTickLoop.Current;
            var report = new StringBuilder();

            ActorManager manager = ActorManager.instance;
            SpawnPoint[] points = manager != null ? manager.spawnPoints : null;
            if (points == null) return "ActorManager.instance.spawnPoints is null";

            report.Append("spawnPoints=").Append(points.Length);
            for (int i = 0; i < points.Length; i++)
            {
                if (points[i] == null) { report.Append("\n  [").Append(i).Append("] null"); continue; }

                report.Append("\n  [").Append(i).Append("] owner=").Append(points[i].owner)
                      .Append(' ').Append(points[i].transform.position.ToString("0"));
            }

            if (loop == null) return report.Append("\n  no ServerTickLoop").ToString();

            foreach (object player in Players(loop))
            {
                ClientSession session = SessionOf(player);
                NetServerActor actor = ActorOf(player);
                if (session == null || actor == null) continue;

                float nearest = float.MaxValue;
                int nearestIndex = -1;
                for (int i = 0; i < points.Length; i++)
                {
                    if (points[i] == null) continue;
                    if (points[i].owner >= 0 && points[i].owner != actor.Team) continue;

                    float d = Vector3.Distance(actor.transform.position, points[i].transform.position);
                    if (d >= nearest) continue;

                    nearest = d;
                    nearestIndex = i;
                }

                report.Append("\n  session ").Append(session.ConnectionId)
                      .Append(" actor ").Append(session.ActorId)
                      .Append(" team ").Append(actor.Team)
                      .Append(" alive ").Append(actor.IsAlive)
                      .Append(" hp ").Append(actor.Health.ToString("0", CultureInfo.InvariantCulture))
                      .Append(" at ").Append(actor.transform.position.ToString("0"))
                      .Append(" | eligible nearest [").Append(nearestIndex).Append("] ")
                      .Append(nearestIndex < 0
                          ? "none"
                          : nearest.ToString("0.0", CultureInfo.InvariantCulture) + "m")
                      .Append(" | speedViolations ").Append(session.SpeedViolations)
                      .Append(" statePos ").Append(Fmt(session.State.Position));
            }

            return report.ToString();
        }

        /// <summary>
        /// Runs the server's own respawn for one session and reports where it put the actor.
        /// </summary>
        /// <remarks>
        /// Goes through <c>ServerCombatBridge.TryRespawn</c>, the same method
        /// <c>ServerTickLoop.OnSpawnRequested</c> calls, so the respawn gate, the spawn-point
        /// selection and the position re-baseline under test are all the shipping ones. Only the
        /// trigger is synthetic, because no client under <c>Assets/</c> sends
        /// <c>C_SPAWN_REQUEST</c>. A <c>granted=false</c> result is therefore a real verdict from
        /// <c>ServerRespawnGate</c>, most likely its cooldown, not a probe failure.
        /// </remarks>
        public static string Respawn(int connectionId)
        {
            ServerTickLoop loop = ServerTickLoop.Current;
            if (loop == null) return "no ServerTickLoop";

            object bridge = Field(typeof(ServerTickLoop), "_combat").GetValue(loop);
            MethodInfo tryRespawn = bridge.GetType().GetMethod(
                "TryRespawn", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);

            foreach (object player in Players(loop))
            {
                ClientSession session = SessionOf(player);
                NetServerActor actor = ActorOf(player);
                if (session == null || actor == null || session.ConnectionId != connectionId) continue;

                Vector3 before = actor.transform.position;
                int violationsBefore = session.SpeedViolations;
                bool aliveBefore = actor.IsAlive;
                float healthBefore = actor.Health;

                bool granted = (bool)tryRespawn.Invoke(bridge, new object[] { player });

                return "respawn granted=" + granted
                       + " | alive " + aliveBefore + "->" + actor.IsAlive
                       + " hp " + healthBefore.ToString("0", CultureInfo.InvariantCulture)
                       + "->" + actor.Health.ToString("0", CultureInfo.InvariantCulture)
                       + " | pos " + before.ToString("0") + "->" + actor.transform.position.ToString("0")
                       + " moved " + Vector3.Distance(before, actor.transform.position)
                            .ToString("0.0", CultureInfo.InvariantCulture) + "m"
                       + " | statePos " + Fmt(session.State.Position)
                       + " | speedViolations " + violationsBefore + "->" + session.SpeedViolations;
            }

            return "no session on connection " + connectionId;
        }

        /// <summary>Puts an actor into the dead state so a respawn has something to undo.</summary>
        /// <remarks>
        /// Writes health and the alive flag directly rather than routing damage through
        /// <see cref="ServerCombatAuthority"/>, because the authority's path needs a shooter,
        /// a weapon and a resolved hit — all of which E3 already covers. What E6 needs from this
        /// is only that the actor be dead where it stood.
        /// </remarks>
        public static string Kill(int victimActorId)
        {
            ServerTickLoop loop = ServerTickLoop.Current;
            if (loop == null) return "no ServerTickLoop";

            if (!ServerActorRegistry.Instance.TryFind((ushort)victimActorId, out NetServerActor victim))
                return "no actor " + victimActorId;

            float healthBefore = victim.Health;
            bool aliveBefore = victim.IsAlive;

            victim.Health = 0f;
            victim.IsAlive = false;

            return "actor " + victimActorId
                   + " | hp " + healthBefore.ToString("0", CultureInfo.InvariantCulture) + "->0"
                   + " alive " + aliveBefore + "->" + victim.IsAlive
                   + " | died at " + victim.transform.position.ToString("0");
        }

        // =====================================================================================
        // E8 — does a match reset leave state behind?
        // =====================================================================================

        /// <summary>Audits, resets the match, and audits again.</summary>
        /// <remarks>
        /// The field to read is <c>cleanOfActorState</c>, not <c>IsClean</c>: a reset deliberately
        /// keeps its sessions, so <c>IsClean</c> reports a leak on every round transition with
        /// anyone connected.
        /// </remarks>
        public static string ResetMatch()
        {
            ServerTickLoop loop = ServerTickLoop.Current;
            if (loop == null) return "no ServerTickLoop";

            ServerStateSnapshot before = loop.AuditState();
            loop.ResetForNewMatch();
            ServerStateSnapshot after = loop.AuditState();

            return "before: " + before
                   + " cleanOfActorState=" + before.IsCleanOfActorState
                   + "\n  after:  " + after
                   + " cleanOfActorState=" + after.IsCleanOfActorState
                   + "\n  registry now " + ServerActorRegistry.Instance.Count + " actors";
        }

        /// <summary>The audit on its own, for a before/after around some other action.</summary>
        public static string Audit()
        {
            ServerTickLoop loop = ServerTickLoop.Current;
            if (loop == null) return "no ServerTickLoop";

            ServerStateSnapshot snapshot = loop.AuditState();
            return snapshot
                   + " cleanOfActorState=" + snapshot.IsCleanOfActorState
                   + " | registry " + ServerActorRegistry.Instance.Count
                   + " | players " + loop.PlayerCount;
        }

        // =====================================================================================
        // Reflection plumbing. ServerPlayer, ServerCombatBridge and ServerActorDamageSink are
        // internal to Assembly-CSharp, and a predefined Editor assembly sees that assembly's
        // publics but not its internals.
        // =====================================================================================

        private static IEnumerable Players(ServerTickLoop loop)
            => (IEnumerable)Field(typeof(ServerTickLoop), "_players").GetValue(loop);

        private static ClientSession SessionOf(object player)
            => player.GetType().GetProperty("Session")?.GetValue(player) as ClientSession;

        private static NetServerActor ActorOf(object player)
            => player.GetType().GetProperty("Actor")?.GetValue(player) as NetServerActor;

        private static FieldInfo Field(Type type, string name)
        {
            for (Type walk = type; walk != null; walk = walk.BaseType)
            {
                FieldInfo found = walk.GetField(
                    name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                if (found != null) return found;
            }

            return null;
        }

        private static string Fmt(Vec3 v)
            => v.X.ToString("0", CultureInfo.InvariantCulture) + ","
               + v.Y.ToString("0", CultureInfo.InvariantCulture) + ","
               + v.Z.ToString("0", CultureInfo.InvariantCulture);
    }
}
