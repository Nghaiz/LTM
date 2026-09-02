using System;
using Ironfront.Net.Protocol;
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

        /// <summary>Seeds the session from wherever the claimed actor currently stands.</summary>
        public void SyncFromActor()
        {
            if (Actor == null) return;

            Vec3 position = Actor.Movement != null
                ? Actor.Movement.State.Position
                : MovementSimulation.ToCore(Actor.transform.position);

            Session.State = MoveState.AtRest(position);
            Session.PreviousPosition = position;
        }

        /// <summary>
        /// Applies every input frame buffered for this tick, plus the coast that covers a
        /// dropped packet.
        /// </summary>
        /// <summary>One simulation second at 30 Hz. Long enough that a genuine mid-spawn gap
        /// stays quiet, short enough that a permanent one is reported before the body has fallen
        /// far.</summary>
        private const int DetachedTicksBeforeWarning = 30;

        private int _detachedTicks;
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

            // Ground contact is Unity's answer, not the simulation's: the CharacterController
            // knows what it is standing on and MovementCore does not.
            Session.State.IsGrounded = agent.IsGrounded;

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
        /// </remarks>
        private void KillForFallingOutOfTheWorld()
        {
            if (Actor == null || !Actor.IsAlive) return;

            Actor.Health = 0f;
            Actor.IsAlive = false;

            ServerCombatEvents.ReportDeath(Actor, Vector3.zero, CauseOfDeath.Fall);

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

            _combat.StepCombat(this, in frame);
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
