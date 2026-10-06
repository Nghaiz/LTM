using System;
using Ironfront.Net.Protocol;
using Ironfront.Net.Replication.Combat;
using Ironfront.Net.Replication.Movement;
using Ironfront.Net.Replication.Server;
using Ironfront.Net.Replication.World;
using UnityEngine;

namespace Ironfront.Net.Unity.Server
{
    /// <summary>
    /// One connected player: the authoritative session, the actor it drives, and the bridge
    /// between the engine-free simulation and Unity's collision.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The two <see cref="Func{T, TResult}"/> fields are built once in the constructor rather
    /// than passed as lambdas at the call site. A lambda that closes over <c>this</c> allocates
    /// a delegate on every call, and this one is called once per player per tick — 16 players
    /// at 30 Hz is 480 allocations a second in the loop M1 criterion 9 requires to allocate
    /// nothing.
    /// </para>
    /// </remarks>
    internal sealed class ServerPlayer : IAcceptedFrameObserver
    {
        private readonly Func<Vec3, Vec3> _moveThroughCollision;
        private readonly Func<Vec3, Vec3> _moveDetached;
        private readonly ServerCombatBridge _combat;

        /// <summary>
        /// Watches this body for the X-82 descent. One float comparison per tick when nothing is
        /// wrong; see <see cref="FallDiagnostics"/> for why it is on by default.
        /// </summary>
        private readonly FallDiagnostics _fallDiagnostics = new FallDiagnostics();

        /// <summary>
        /// The wire's own representable cube — <c>Quantize.POS_MIN</c>..<c>POS_MAX</c> on every
        /// axis. Ledger <b>X-75</b>.
        /// </summary>
        /// <remarks>
        /// <para>
        /// <b>Not the authored <c>LevelBounds</c> volume, and that is deliberate.</b>
        /// <c>LevelBounds</c> compiles into <c>Assembly-CSharp</c>, which no asmdef can
        /// reference — the same wall <c>NetServerBindings</c>' resolvers exist to cross for
        /// other seams, and no such seam exists for the play volume today. The wire's own range
        /// needs no seam: it is the same <c>Quantize.POS_MIN</c>/<c>POS_MAX</c>
        /// <c>SnapshotBuilder</c> already clamps every position against, so a body this volume
        /// contains is a body <see cref="PlayVolume.FitsOnTheWire"/> can encode. That is a
        /// narrower promise than "inside the level" — Dustbowl's authored floor sits at
        /// <c>y = -50</c>, nowhere near this cube's <c>y = -1024</c> — but it is the promise
        /// X-75 is actually about: an actor "leaves the wire's position range and is silently
        /// clamped onto the boundary," not an actor that merely fell below the map's own floor
        /// while still on the wire.
        /// </para>
        /// </remarks>
        private static readonly PlayVolume _wireVolume = BuildWireVolume();

        private static PlayVolume BuildWireVolume()
        {
            float centre = (Quantize.POS_MIN + Quantize.POS_MAX) / 2f;
            return new PlayVolume(
                new Vec3(centre, centre, centre),
                new Vec3(Quantize.POS_RANGE, Quantize.POS_RANGE, Quantize.POS_RANGE));
        }

        /// <summary>
        /// How far below the wire's floor a body must fall before it counts as having fallen out
        /// of the world. <b>Zero, and it must stay zero.</b>
        /// </summary>
        /// <remarks>
        /// <para>
        /// <b>Any slack at all re-creates the bug it was meant to soften.</b> This check runs
        /// before the clamp, so a body that is below the floor but inside the slack falls
        /// through to <see cref="PlayVolume.TryClamp"/> and is pushed back UP to the floor. Next
        /// tick gravity moves it down by one tick's fall and the same thing happens again. The
        /// recorded descent rate is ~0.517 m per tick, so a slack of 1 m meant the body could
        /// never get far enough below the floor in a single tick to be judged fallen: it would
        /// have oscillated at the boundary, alive, forever — which IS X-75, at a different y.
        /// </para>
        /// <para>
        /// All four recorded falls landed between -1024.03 and -1025.07, i.e. inside exactly
        /// that window. So the slack would have covered every occurrence on record.
        /// </para>
        /// <para>
        /// Zero is also correct on its own terms rather than merely safe: <c>POS_MIN</c> is
        /// -1024 m and both shipping maps sit near y = 0, so there is no floating-point noise to
        /// absorb. A body below the wire floor has unambiguously left the world.
        /// </para>
        /// </remarks>
        private const float FloorDeathSlackMetres = 0f;

        /// <param name="combat">
        /// Where accepted frames go for their combat half. Null leaves this player moving but
        /// unarmed, which is what a loop that was never bound to a match looks like.
        /// </param>
        /// <param name="displayName">
        /// What S_PLAYER_LIST calls this player. See <see cref="DisplayName"/> for where it comes
        /// from and what it is not.
        /// </param>
        /// <param name="playerId">
        /// The master's account id from the signed join ticket, or 0. See <see cref="PlayerId"/>.
        /// </param>
        public ServerPlayer(
            ushort connectionId, ushort actorId, ServerCombatBridge combat = null,
            string displayName = null, uint playerId = 0)
        {
            Session = new ClientSession(connectionId, actorId);
            PlayerId = playerId;
            _combat = combat;
            _moveThroughCollision = MoveThroughCollision;
            _moveDetached = MoveDetached;
            DisplayName = string.IsNullOrEmpty(displayName)
                ? "Player " + actorId
                : displayName;
        }

        /// <summary>
        /// What <c>S_PLAYER_LIST</c> calls this player. debt-closure phase 2 task 2a.
        /// </summary>
        /// <remarks>
        /// <para>
        /// <b>This IS the master-server username now, when the ticket carried one</b>
        /// (verdict-closure R2, ledger X-36). Phase 2 recorded the opposite here and its reason
        /// was wrong in an instructive way: it held that plumbing the real name through needed a
        /// new client-to-server message and therefore a <c>PROTOCOL_VERSION</c> move. It needed
        /// neither. protocol-spec § 12 has carried <c>u8[16] displayNameUtf8</c> inside the
        /// signed ticket since the freeze, <c>UdpTransportServer</c> was already verifying that
        /// ticket and already parsing it to bind <c>PlayerId</c> — and discarding the name
        /// field of the same parse with an <c>out string _</c>. The whole change was to stop
        /// discarding it. Not one byte on the wire moved.
        /// </para>
        /// <para>
        /// <b>The fallbacks are still live and still correct.</b> A transport with no ticket to
        /// read — the loopback, a lane-B harness client, a development stub whose name field is
        /// zeroed — supplies no name, and <c>ServerTickLoop.DisplayNameFor</c> then falls to
        /// <c>"#" + PlayerId</c> and finally to the actor id, exactly as it did before. So does
        /// a name that sanitizes to nothing. That method owns the ordering and states why.
        /// </para>
        /// <para>
        /// <b>Sanitized before it ever reaches this constructor.</b> The string arrives over a
        /// socket and ends in a UI label, so <c>PlayerNameSanitizer</c> runs at the transport's
        /// ingress. Nothing downstream — not this type, not <c>S_PLAYER_LIST</c> — repeats that
        /// work, because two sanitizing sites is two places to drift.
        /// </para>
        /// </remarks>
        public string DisplayName { get; }

        /// <summary>
        /// The master's account id for this connection, or 0 when it carried no signed ticket.
        /// Phase P6, checklist A13.
        /// </summary>
        /// <remarks>
        /// <para>
        /// <b>Captured at the join and kept, because <c>ConnectionInfo</c> is not.</b> The
        /// transport hands the info struct to <c>OnClientConnected</c> and nothing retains it;
        /// the end-of-match report needs this id long afterwards, when the only handle left is
        /// the actor. It travels beside <see cref="DisplayName"/>, which is captured from the
        /// same struct at the same moment for the same reason.
        /// </para>
        /// <para>
        /// <b>0 is honest, not missing.</b> A loopback session, a lane-B harness client and a
        /// development stub all join without a ticket. See
        /// <c>ServerTickLoop.PlayerIdForActor</c> for why the report says 0 rather than
        /// substituting the actor id.
        /// </para>
        /// </remarks>
        public uint PlayerId { get; }

        /// <summary>The authoritative state. This, not the transform, is the truth.</summary>
        public ClientSession Session { get; }

        /// <summary>The actor this connection drives. Null between claim and spawn.</summary>
        public NetServerActor Actor { get; set; }

        /// <summary>
        /// The most recent input frame the server accepted from this player.
        /// </summary>
        /// <remarks>
        /// A delayed throw is released ticks after its trigger, from no input frame of its own, so
        /// its origin reads the thrower's posture from here: <c>ShotOrigin</c> lowers the eye for
        /// a held Prone button, which a default frame never carries.
        /// </remarks>
        public InputFrame LastAcceptedFrame { get; private set; }

        /// <summary>
        /// True from construction until this connection's own first successful deploy.
        /// </summary>
        /// <remarks>
        /// <para>
        /// <b>A join no longer places the body</b> -- the claimed actor is parked Health 0 /
        /// IsAlive false, exactly like a fresh corpse, until this connection's own
        /// C_SPAWN_REQUEST carries a loadout to arm it with. <c>ServerRespawnGate.MayRespawn</c>
        /// requires a prior <c>MarkDeath</c>, which nothing has stamped for a body that has never
        /// lived, so the ordinary respawn gate would refuse this player's very first deploy
        /// forever. This flag is the one-time bypass: true means "grant the next spawn request
        /// unconditionally," and the handler clears it the instant it does.
        /// </para>
        /// <para>
        /// <b>Per-connection, not per-actor-id.</b> Deliberately not read from
        /// <c>ServerRespawnGate</c>'s own per-actor-id state, which can be stale: an actor id
        /// recycled from a player who died mid-match and disconnected without respawning would
        /// still read "dead" there. This field is fresh on every <c>ServerPlayer</c>, which is
        /// itself fresh on every connection, so it cannot inherit a stranger's history. The
        /// worst case on a recycled id is a spurious extra wait behind the ordinary gate once
        /// this flag is spent -- never a wedge.
        /// </para>
        /// </remarks>
        public bool AwaitingFirstDeploy { get; set; } = true;

        /// <summary>
        /// Whether this connection's first vehicle catch-up has run, so the empty-table warning
        /// is only ever about a join.
        /// </summary>
        /// <remarks>
        /// <c>ServerTickLoop.AnnounceNewVehicles</c> runs for every client on every snapshot, and
        /// a round reset empties the vehicle table for a moment. The warning, whose own remark
        /// says it is logged per join, fired every tick for every client in that window: hundreds
        /// of lines in one second at the 2026-09-30 reset (B4), none of them about a join.
        /// </remarks>
        public bool VehicleTableChecked { get; set; }

        /// <summary>Seeds the session from wherever the claimed actor currently stands.</summary>
        public void SyncFromActor()
        {
            if (Actor == null) return;

            Vec3 position = Actor.Movement != null
                ? Actor.Movement.State.Position
                : MovementSimulation.ToCore(Actor.transform.position);

            Session.State = MoveState.AtRest(position);
            Session.PreviousPosition = position;
            _fall.Forget();
        }

        /// <summary>
        /// The body was just put at <paramref name="y"/> by something other than a fall (a spawn
        /// point), so a fall from here is measured from here. See <see cref="FallTracker"/>.
        /// </summary>
        public void RebaseFall(float y) => _fall.Rebase(y);

        /// <summary>
        /// Applies every input frame buffered for this tick, plus the coast that covers a
        /// dropped packet.
        /// </summary>
        /// <summary>One simulation second at 30 Hz. Long enough that a genuine mid-spawn gap
        /// stays quiet, short enough that a permanent one is reported before the body has fallen
        /// far.</summary>
        private const int DetachedTicksBeforeWarning = 30;

        private int _detachedTicks;

        /// <summary>Whether the last tick found this player in a vehicle seat.</summary>
        private bool _seated;

        /// <summary>The vehicle the last seated tick found this player in.</summary>
        private ushort _seatVehicleId;

        /// <summary>
        /// The ticks a capsule stays out of the vehicle it just left before its first check.
        /// </summary>
        public const int ExitGraceMinTicks = 15;

        /// <summary>The longest a capsule may stay out of that vehicle's collision.</summary>
        public const int ExitGraceMaxTicks = 90;

        /// <summary>Gap left between a climbing-out capsule and the vehicle's bounds, metres.</summary>
        public const float ExitClearanceMetres = 0.5f;

        private IGameplayVehicleSource _exitVehicle;
        private CharacterController _exitCapsule;
        private int _exitGraceTicks;

        /// <summary>Whether this player has ticked its body at least once.</summary>
        private bool _adoptedBody;

        public void Tick(float dt)
        {
            NetMovementAgent agent = Actor != null ? Actor.Movement : null;

            if (agent == null)
            {
                // No collision seam — a connection that has claimed a slot but whose actor has
                // not spawned yet. Integrating in a straight line keeps its tick accounting and
                // anti-cheat counters honest instead of silently skipping the player.
                //
                // TEMPORARY is the whole premise, and until 2026-08-22 nothing checked it. X-15:
                // the claimed body is the AI character prefab, which carries no NetMovementAgent,
                // so this branch was PERMANENT for every networked player -- and it integrates
                // gravity with no collision, so the session MoveState free-fell out of the world
                // while the transform stood still at the spawn. Every shot then originated from a
                // ghost hundreds of metres below the map, and no artifact said so.
                //
                // The fix is NetServerActor.AttachMovementAgent, called where a player body is
                // built. This warning is the leash: whatever else changes, a player that is still
                // detached after a second of ticks says so once, by name.
                _detachedTicks++;
                if (_detachedTicks == DetachedTicksBeforeWarning)
                {
                    Debug.LogWarning(
                        $"[net] player {Session.ActorId} has ticked {_detachedTicks} times with no "
                        + "NetMovementAgent, so its authoritative position is integrating with NO "
                        + "COLLISION and will fall out of the world. Shots will originate from "
                        + "wherever it has fallen to. See ledger X-15.");
                }

                InputAuthority.ApplyPendingInput(Session, dt, _moveDetached, this);
                return;
            }

            _detachedTicks = 0;

            if (!_adoptedBody)
            {
                // A pooled body outlives the connections that occupy it, so the one X-19 line a
                // bypassed move owes is owed to each occupant, not once per server process.
                _adoptedBody = true;
                agent.RearmCollisionBypassWarning();
            }

            // Before the seat is ridden: a seat that has gone under water throws its occupant out.
            EjectFromSunkenSeat();

            // Seated: input is consumed and acknowledged, the capsule is out of the world, and
            // the session rides the seat. See InputAuthority.ConsumePendingInputSeated.
            if (ServerVehicleRegistry.Instance.Registry.TryFindSeatOf(Session.ActorId, out ushort seatedIn, out _))
            {
                _seatVehicleId = seatedIn;
                if (!_seated)
                {
                    _seated = true;
                    agent.SetSeated(true);
                }
                Vec3 seat = MovementSimulation.ToCore(Actor.transform.position);
                InputAuthority.ConsumePendingInputSeated(Session, seat, this);

                // The agent rides the seat as well, as the capsule centre a standing body on the
                // seat would have -- the convention every player position is sent in, so a client
                // that lowers it by half a capsule draws the body on the seat. Playtest
                // 2026-09-28, bug 5 audit: nothing wrote the agent while seated, so the snapshot
                // and the hitbox history kept the spot the player BOARDED from -- measured 141 m
                // from the body in a carried vehicle -- and a player in a moving jeep was drawn
                // and hit back where they climbed in.
                agent.State = MoveState.AtRest(
                    new Vec3(seat.X, seat.Y + MovementCore.HeightFor(crouching: false) * 0.5f, seat.Z),
                    grounded: true);

                Actor.PresentAsPlayer(seated: true, crouching: false, Vec3.Zero);
                return;
            }

            if (_seated)
            {
                // Out of the seat: capsule back, and the session and the agent rebased onto
                // wherever LeaveSeat put the body, so the first on-foot tick starts there rather
                // than inside the vehicle.
                _seated = false;
                BeginExitGrace(agent);
                agent.SetSeated(false);
                Vec3 exit = MovementSimulation.ToCore(Actor.transform.position);
                Session.State = MoveState.AtRest(exit);
                Session.PreviousPosition = exit;
                agent.ApplyAuthoritativeState(in Session.State);
                // Out of a helicopter in the air, this is where the fall starts.
                _fall.Rebase(exit.Y);
            }
            else if (!agent.CollisionEnabled && Actor.IsAlive)
            {
                RestoreLeakedCapsule(agent);
            }

            TickExitGrace();

            // Ground contact is Unity's answer, not the simulation's: the CharacterController
            // knows what it is standing on and MovementCore does not.
            Session.State.IsGrounded = agent.IsGrounded;
            Session.State.IsBlockedSideways = agent.IsBlockedSideways;

            // A dead body's falls are nobody's: the next life measures from where it is placed.
            if (!Actor.IsAlive)
            {
                _fall.Forget();
            }
            else
            {
                float landedAt = _fall.Observe(
                    Session.State.IsGrounded, Session.State.Position.Y, Session.State.Velocity.Y);
                if (landedAt > 0f)
                {
                    ApplyLanding(landedAt);
                }
            }

            InputAuthority.ApplyPendingInput(Session, dt, _moveThroughCollision, this);

            // Mirror the authoritative result back so the agent's stance height tracks the
            // crouch the server actually applied. Tick() would step the simulation twice.
            agent.ApplyAuthoritativeState(in Session.State);

            // AFTER the move and BEFORE the containment, deliberately. After, because the
            // question is what collision just did; before, because EnforceWireVolume can kill or
            // teleport the body, and a sample taken past that would describe the correction
            // rather than the fall it was correcting. Ledger X-82.
            _fallDiagnostics.Sample(Session.ActorId, agent, in Session.State);

            EnforceWireVolume(agent);

            // After the move and the containment: the body is posed where it ended the tick.
            Actor.PresentAsPlayer(
                seated: false, crouching: Session.State.IsCrouching, Session.State.Velocity);
        }

        /// <summary>
        /// The fall damage of a landing at <paramref name="impactSpeed"/> metres a second
        /// (<see cref="FallDamage"/>), and the death when it is enough.
        /// </summary>
        /// <remarks>
        /// <para>
        /// <b>Owner request 2026-10-06</b>: a player who leaves a helicopter high up takes fall
        /// damage on landing, and a high enough fall kills. The fall is this server's own
        /// simulation (a client sends move axes and a jump bit, never a velocity), so the speed
        /// cannot be forged, and it is the speed the client predicted as well.
        /// </para>
        /// <para>
        /// <b>The speed of the height fallen</b> (<see cref="FallTracker"/>), from the last ground
        /// the body stood on, not its velocity, which carries the stick-to-ground pull. Every move
        /// of the body that is not a fall rebases the tracker: a seat exit (from there a bail-out
        /// falls), the wire-volume clamp, and a death, which a respawn always follows. Water never
        /// lands: a body that falls into water swims and is never grounded on the way down.
        /// </para>
        /// <para>
        /// <b>Killed the way a drowning kills</b> (<c>NetServerActor.ApplyBreath</c>): through
        /// <c>IsAlive</c>, reported with no attacker and <see cref="CauseOfDeath.Fall"/>, so the
        /// killfeed says the player fell to their death and nobody is credited.
        /// </para>
        /// </remarks>
        private readonly FallTracker _fall = new FallTracker();

        private void ApplyLanding(float impactSpeed)
        {
            if (Actor == null || !Actor.IsAlive) return;

            float damage = FallDamage.ForImpact(impactSpeed);
            if (damage <= 0f) return;

            float remaining = Actor.Health - damage;
            Debug.Log($"[net] actor {Actor.ActorId} (team {Actor.Team}) landed at {impactSpeed:F1} m/s: "
                      + $"{damage:F0} fall damage" + (remaining > 0f ? $", {remaining:F0} health left." : ", killed."));
            if (remaining > 0f)
            {
                Actor.Health = remaining;
                return;
            }

            Actor.Health = 0f;
            Actor.IsAlive = false;
            ServerCombatEvents.ReportDeath(Actor, Vector3.zero, cause: CauseOfDeath.Fall);
        }

        /// <summary>
        /// Lets the capsule of a player who has just climbed out pass through the vehicle they
        /// left, until the two have separated.
        /// </summary>
        /// <remarks>
        /// <para>
        /// <b>What it stops: a tank thrown into the air as its driver gets out</b> (owner report
        /// 2026-09-27). The capsule is re-enabled at the seat's exit offset, and on a tank that
        /// point is inside the hull or the turret's swing. The body carries the prefab's
        /// kinematic Rigidbody, so PhysX resolves the overlap by pushing the 7-tonne hull out of
        /// an immovable capsule: lane-B run <c>tankdrive-02</c> logged impulses of 375000 N s and
        /// the hull leaving at 36.8 m/s, spinning. Bots never hit this because an AI body has no
        /// CharacterController; a networked player's body gets one from NetMovementAgent.
        /// </para>
        /// <para>
        /// <b>Scoped to the one vehicle and to the one capsule</b>, so the player still collides
        /// with the world and with every other vehicle, and the grace ends once the capsule's
        /// bounds no longer touch the hull's (<see cref="ExitGraceMaxTicks"/> at the latest).
        /// </para>
        /// </remarks>
        private void BeginExitGrace(NetMovementAgent agent)
        {
            EndExitGrace();

            ushort vehicleId = _seatVehicleId;
            _seatVehicleId = 0;
            if (vehicleId == 0) return;
            if (!ServerVehicleRegistry.Instance.TryFind(vehicleId, out IGameplayVehicleSource vehicle)
                || vehicle == null || !vehicle.Exists) return;

            CharacterController capsule = agent.GetComponent<CharacterController>();
            if (capsule == null) return;

            vehicle.SetCollisionIgnored(capsule, true);

            // Called before the session reads the body's position, so it rebases onto the spot.
            if (vehicle.TryGetBounds(out Bounds hull)
                && TryFindExitSpot(Actor.transform.position, hull, capsule, out Vector3 spot))
            {
                Actor.transform.position = spot;
            }

            _exitVehicle = vehicle;
            _exitCapsule = capsule;
            _exitGraceTicks = 0;
        }

        /// <summary>
        /// Layers a climbing-out capsule may stand on: the world, not bodies, vehicles or shots.
        /// </summary>
        private const int ExitGroundMask = ~((1 << 2) | (1 << 8) | (1 << 9) | (1 << 10) | (1 << 11)
                                             | (1 << 12) | (1 << 13) | (1 << 14) | (1 << 16) | (1 << 17));

        /// <summary>Layers that must not overlap the capsule where it lands: the ground mask plus vehicles.</summary>
        private const int ExitBlockingMask = ExitGroundMask | (1 << 12);

        /// <summary>
        /// A spot on the ground beside <paramref name="hull"/>, clear of it and of everything else,
        /// for a capsule leaving a seat.
        /// </summary>
        /// <remarks>
        /// <para>
        /// <b>The seat's own exit offset is not trusted,</b> because on the tank it is not outside
        /// the tank: lane-B run <c>tankdrive-03</c> left the driver 3 m above the hull's origin and
        /// 0.5 m from its centre, standing inside its bounds. The search starts on the side of the
        /// hull that offset points to, so it lands where the vehicle's author meant, and walks
        /// round the other seven sides when that one is blocked.
        /// </para>
        /// <para>
        /// Ground comes from a ray cast down past the hull, which skips vehicles, bodies and
        /// hitboxes; the capsule is then checked against the world and every vehicle.
        /// </para>
        /// <para>
        /// <b>Only ground the vehicle stands on.</b> The ray reaches
        /// <see cref="FallDamage.SafeDropMetres"/> below the hull's lowest point and no further, so
        /// a player leaving a helicopter in the air is left beside it, in the air, and falls
        /// (owner request 2026-10-06: a bail-out from high up must hurt). It used to reach 40 m
        /// below the hull, which set a player leaving a helicopter at up to 40 m straight down
        /// on the ground beside it, unhurt. A hover lower than the safe drop still steps the player
        /// down, which is all a fall from there would have done.
        /// </para>
        /// </remarks>
        internal static bool TryFindExitSpot(
            Vector3 preferred, Bounds hull, CharacterController capsule, out Vector3 root)
        {
            root = preferred;

            float radius = capsule.radius;
            float half = Mathf.Max(capsule.height * 0.5f, radius);

            Vector3 away = preferred - hull.center;
            away.y = 0f;
            if (away.sqrMagnitude < 0.01f) away = Vector3.right;
            away.Normalize();

            float reach = Mathf.Max(hull.extents.x, hull.extents.z) + radius + ExitClearanceMetres;
            float rayTop = hull.max.y + 3f;
            float rayLength = rayTop - hull.min.y + FallDamage.SafeDropMetres;

            for (int i = 0; i < 8; i++)
            {
                // 0, +45, -45, +90, -90, ... so the preferred side is tried first.
                float degrees = 45f * ((i + 1) / 2) * (i % 2 == 0 ? 1f : -1f);
                Vector3 direction = Quaternion.Euler(0f, degrees, 0f) * away;
                Vector3 probe = hull.center + direction * reach;

                if (!Physics.Raycast(new Vector3(probe.x, rayTop, probe.z), Vector3.down, out RaycastHit ground,
                        rayLength, ExitGroundMask, QueryTriggerInteraction.Ignore))
                    continue;

                Vector3 centre = ground.point + Vector3.up * (half + 0.05f);
                Vector3 spine = Vector3.up * (half - radius);
                if (Physics.CheckCapsule(centre - spine, centre + spine, radius * 0.95f,
                        ExitBlockingMask, QueryTriggerInteraction.Ignore))
                    continue;

                root = centre - capsule.center;
                return true;
            }

            return false;
        }

        private void TickExitGrace()
        {
            if (_exitVehicle == null) return;

            _exitGraceTicks++;
            if (!_exitVehicle.Exists || _exitCapsule == null)
            {
                _exitVehicle = null;
                _exitCapsule = null;
                return;
            }

            if (_exitGraceTicks < ExitGraceMinTicks) return;

            bool touching = _exitVehicle.TryGetBounds(out Bounds hull)
                            && hull.Intersects(_exitCapsule.bounds);
            if (touching && _exitGraceTicks < ExitGraceMaxTicks) return;

            EndExitGrace();
        }

        private void EndExitGrace()
        {
            if (_exitVehicle != null && _exitVehicle.Exists && _exitCapsule != null)
                _exitVehicle.SetCollisionIgnored(_exitCapsule, false);

            _exitVehicle = null;
            _exitCapsule = null;
        }

        /// <summary>
        /// Throws the player out of a seat that has gone under water, to swim.
        /// </summary>
        /// <remarks>
        /// <para>
        /// <b>Owner report 2026-09-29</b>: a vehicle driven into deep water sank with its player
        /// still sitting in it, and the view stayed at the steering wheel on the bottom until the
        /// eight-second drowning clock killed him. The original throws every body out of a seat the
        /// moment it is in water (<c>Actor.Update</c>: in water, <c>LeaveSeat</c>, then swim), and
        /// bots still get that from the game itself; a player's body is parked there, so it is done
        /// here, by the same test the player then swims by.
        /// </para>
        /// <para>
        /// <b>Through the vehicle's own <c>TryLeaveSeat</c></b>, the path every seat exit takes:
        /// <c>Vehicle.OccupantLeft</c> publishes the seat table, the snapshot then carries the
        /// player on foot, and its client leaves the seat when it sees that
        /// (<c>ClientVehicleStage.OnSnapshotApplied</c>). A boat's seats ride above its waterline,
        /// so a floating boat never throws anybody out.
        /// </para>
        /// </remarks>
        private void EjectFromSunkenSeat()
        {
            if (!ServerVehicleRegistry.Instance.Registry.TryFindSeatOf(Session.ActorId, out ushort vehicleId, out _))
                return;

            Vector3 seat = Actor.transform.position;
            if (!MovementCore.IsInWater(seat.x, seat.y + MovementCore.HeightFor(crouching: false) * 0.5f, seat.z)) return;
            if (!ServerVehicleRegistry.Instance.TryFind(vehicleId, out IGameplayVehicleSource vehicle)) return;

            if (vehicle.TryLeaveSeat(Actor.gameObject))
            {
                Debug.Log($"[net] actor {Session.ActorId} thrown out of vehicle {vehicleId}: its seat is "
                          + $"under water at y {seat.y:F1} (surface {MovementCore.SurfaceAt(seat.x, seat.z):F1}).");
            }
        }

        /// <summary>
        /// Puts back a capsule that is switched off on a living body with no seat to explain it.
        /// </summary>
        /// <remarks>
        /// <para>
        /// <b>The capsule's state is the body's, not this connection's.</b> The only undo for
        /// <see cref="NetMovementAgent.SetSeated"/> used to be the <see cref="_seated"/> edge
        /// above, and <see cref="_seated"/> lives on this object, which is discarded on
        /// disconnect while the pooled body goes back to the slot pool. Measured 2026-09-27 on
        /// Island: player 25 timed out while seated in room 22, rejoined in room 23 as a fresh
        /// <see cref="ServerPlayer"/> on the same body (actor 2), and that body's capsule stayed
        /// off for good. Every spawn after that sank straight down through the map at the
        /// grounded stick speed, drowned, and came back from the wire floor to do it again.
        /// </para>
        /// <para>
        /// <b>Alive only.</b> <c>NetServerActor.DisableCorpseColliders</c> switches a dead body's
        /// colliders off on purpose, so vehicle pads do not refuse to spawn into a corpse, and it
        /// restores them itself on the revive.
        /// </para>
        /// </remarks>
        private void RestoreLeakedCapsule(NetMovementAgent agent)
        {
            agent.SetSeated(false);
            Debug.LogWarning(
                $"[net] actor {Session.ActorId}: its capsule was switched off with no seat to "
                + "explain it -- a previous occupant of this body left it that way -- and has been "
                + "switched back on. Without this the body moves with no collision and sinks "
                + "through the map (X-19).");
        }

        /// <summary>
        /// Leaves this body usable by whoever occupies it next. Called by
        /// <see cref="ServerTickLoop"/> as the connection goes away, before its slot is released.
        /// </summary>
        /// <remarks>
        /// Two things this class switches on a body are remembered only on this object: the
        /// capsule it takes out of the world while seated, and the collision it ignores between
        /// a climbing-out capsule and the vehicle it left. The next occupant gets a fresh
        /// <see cref="ServerPlayer"/> that knows neither. The capsule is also repaired by
        /// <see cref="RestoreLeakedCapsule"/>, but an ignored collision pair cannot be seen from
        /// the body at all — it has to be ended here, by the only object that knows it exists.
        /// A seated body keeps its capsule off here: switched on inside the hull it would shove
        /// the vehicle, and <c>NetServerActor.ReturnToPool</c> takes it out of the seat first and
        /// switches it back on after.
        /// </remarks>
        public void ReleaseBody()
        {
            EndExitGrace();

            NetMovementAgent agent = Actor != null ? Actor.Movement : null;
            if (agent == null || agent.CollisionEnabled || !Actor.IsAlive) return;
            if (ServerVehicleRegistry.Instance.Registry.TryFindSeatOf(Session.ActorId, out _, out _)) return;

            agent.SetSeated(false);
        }

        /// <summary>
        /// Keeps this player's authoritative position inside <see cref="_wireVolume"/> after a
        /// tick's movement has run. Ledger <b>X-75</b>.
        /// </summary>
        /// <remarks>
        /// <para>
        /// <b>Two different faults, two different responses.</b> A body that has fallen through
        /// collision (ledger X-15's free-fall, or a hole in the map's geometry) is not somewhere
        /// <see cref="PlayVolume.TryClamp"/> can usefully pull it back to — there is nothing
        /// under it to stand on, and clamping would leave it hanging in empty air on the wire's
        /// floor forever. It is killed instead, as an environment death, so the normal respawn
        /// path puts it back on solid ground. A body that has merely crossed the wire's
        /// horizontal or vertical CEILING — a helicopter flown far enough, an actor pushed
        /// through a wall — has somewhere sane to go back to, so it is clamped and stopped
        /// exactly as <c>Vehicle.KeepInsideLevelBounds</c> already does for vehicles (E-6).
        /// </para>
        /// <para>
        /// <b>The clamp teleports through the movement API, never a raw transform write.</b>
        /// <see cref="NetMovementAgent.Teleport"/> disables the <c>CharacterController</c>
        /// around the position assignment and resets velocity, which is exactly what a
        /// direct <c>transform.position = ...</c> would skip — the controller would fight the
        /// write and the body would land somewhere else. <see cref="ClientSession.State"/> is
        /// updated to match so next tick's <see cref="InputAuthority.ApplyPendingInput"/> — which
        /// reads the session, not the agent — starts from the corrected position rather than
        /// re-deriving the crossing on its very next step.
        /// </para>
        /// </remarks>
        private void EnforceWireVolume(NetMovementAgent agent)
        {
            Vec3 position = Session.State.Position;

            if (_wireVolume.IsBelowFloor(in position, FloorDeathSlackMetres))
            {
                KillForFallingOutOfTheWorld();
                return;
            }

            if (!_wireVolume.TryClamp(in position, out Vec3 contained)) return;

            Session.State.Position = contained;
            Session.State.Velocity = Vec3.Zero;
            agent.Teleport(MovementSimulation.ToUnity(contained), resetVelocity: true);
            _fall.Rebase(contained.Y);
        }

        /// <summary>
        /// Reports an environment death for a body that fell through collision and is now below
        /// the wire's own floor, then asks the combat bridge to put it back on its feet. Ledger
        /// <b>X-75</b>.
        /// </summary>
        /// <remarks>
        /// <para>
        /// <b>The wire death, not <c>Actor.Damage</c>.</b> There is no attacker and no impact
        /// point — this is exactly the shape <see cref="DeathMessage.EnvironmentKiller"/> and
        /// <see cref="CauseOfDeath.Fall"/> exist for (see the killfeed and score-tally tests
        /// that already exercise both). Setting <c>Health</c>/<c>IsAlive</c> directly rather than
        /// routing through the gameplay actor's own damage method matches the flag-only death
        /// <see cref="ServerCombatBridge.TryRespawn"/>'s own revival already performs on the far
        /// side of the same seam — that method sets <c>Health</c> and <c>IsAlive</c> the same
        /// way, in the same direction, for the same reason: this assembly cannot name
        /// <c>Actor.Damage</c>.
        /// </para>
        /// <para>
        /// <b><see cref="ServerCombatBridge.TryRespawn"/> is gated by the same respawn cooldown
        /// every other death uses</b> (<c>_respawnGate.MayRespawn</c>), so a call here can be
        /// declined exactly as a client's own respawn request can be — this death does not get a
        /// faster respawn than a bullet does, nor should it.
        /// </para>
        /// <para>
        /// <b>It says so in the log, because a player sees this as "killed by the world" and
        /// nothing else.</b> Two very different things reach here — a body that walked off the
        /// authored edge of the map, which is the game working, and a body that left its ground
        /// standing still (X-82, still open) — and the only way to tell them apart afterwards is
        /// the position it started falling from. That is what the line records. It is one line per
        /// death, not per tick: the caller only reaches it while <c>IsAlive</c>, which this method
        /// clears on its way through.
        /// </para>
        /// </remarks>
        private void KillForFallingOutOfTheWorld()
        {
            if (Actor == null || !Actor.IsAlive) return;

            Vec3 below = Session.State.Position;
            Debug.Log(
                $"[net] actor {Actor.ActorId} (team {Actor.Team}) killed for leaving the world at "
                + $"({below.X:F2}, {below.Y:F2}, {below.Z:F2}) — below the wire floor. Walking off "
                + "the authored edge of the map reaches here legitimately; a body that left its "
                + "ground while standing on it is X-82, and the [fall] lines above say which.");

            Actor.Health = 0f;
            Actor.IsAlive = false;

            ServerCombatEvents.ReportDeath(Actor, Vector3.zero, cause: CauseOfDeath.Fall);

            _combat?.TryRespawn(this);
        }

        /// <summary>
        /// One accepted frame's combat half. Phase-05 task 2's seam, landing here.
        /// </summary>
        /// <remarks>
        /// Implemented on this class rather than handed over as a lambda so the reference
        /// <c>ApplyPendingInput</c> receives is <c>this</c> — a capturing lambda would allocate
        /// a delegate per player per tick, which at 16 players and 30 Hz is 480 allocations a
        /// second in the loop that is graded on producing none.
        /// </remarks>
        void IAcceptedFrameObserver.OnAcceptedFrame(
            ClientSession session, uint frameTick, in InputFrame frame, in MoveInput input)
        {
            if (_combat == null) return;

            // Aim is replicated from the frame the shot was graded on, so the pose other clients
            // see and the pose the server resolved against are the same one. Yaw as well as
            // pitch: a headless server's player transform never turns, so leaving yaw to
            // NetServerActor's transform read had every remote player facing their spawn
            // heading while shooting somewhere else entirely.
            if (Actor != null)
            {
                Actor.YawDegrees   = frame.YawDegrees;
                Actor.PitchDegrees = frame.PitchDegrees;
            }

            LastAcceptedFrame = frame;
            _combat.StepCombat(this, frameTick, in frame);
        }

        private Vec3 MoveThroughCollision(Vec3 motion)
        {
            NetMovementAgent agent = Actor.Movement;
            Vector3 landed = agent.CharacterMove(MovementSimulation.ToUnity(motion));
            return MovementSimulation.ToCore(landed);
        }

        private Vec3 MoveDetached(Vec3 motion) => Session.State.Position + motion;
    }
}
