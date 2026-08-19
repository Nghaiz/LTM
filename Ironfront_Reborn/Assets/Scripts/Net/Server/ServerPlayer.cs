using System;
using Ironfront.Net.Protocol;
using Ironfront.Net.Replication.Movement;
using Ironfront.Net.Replication.Server;
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

        /// <param name="combat">
        /// Where accepted frames go for their combat half. Null leaves this player moving but
        /// unarmed, which is what a loop that was never bound to a match looks like.
        /// </param>
        /// <param name="displayName">
        /// What S_PLAYER_LIST calls this player. See <see cref="DisplayName"/> for where it comes
        /// from and what it is not.
        /// </param>
        public ServerPlayer(
            ushort connectionId, ushort actorId, ServerCombatBridge combat = null,
            string displayName = null)
        {
            Session = new ClientSession(connectionId, actorId);
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
        /// <b>This is not the player's master-server username, and the difference is worth
        /// stating rather than hiding.</b> The game server never parses a
        /// <c>JoinTicket</c> — <c>OnClientConnected</c> receives a <c>ConnectionInfo</c> and
        /// nothing else, and the ticket's <c>displayName</c> field is written by the master and
        /// read by nobody on this side. So the only identity the server actually holds is the
        /// transport's <c>PlayerId</c>, and that is what this is built from.
        /// </para>
        /// <para>
        /// <b>Why phase 2 did not plumb the real name through.</b> Doing so needs a new
        /// client-to-server message carrying the ticket, and acceptance criterion 2 forbids
        /// moving <c>PROTOCOL_VERSION</c> — a new opcode is exactly that. Verifying the ticket
        /// here would also need the master's shared secret on every game server, which is a
        /// deployment decision and not a debt repayment. A killfeed that reads "#1042 killed
        /// Player 3" is a worse name than the username and a strictly better one than a bare
        /// actor id, which is what it rendered before; the wire, the writer, the router and the
        /// table are all in place for the real name the moment a join handshake carries it.
        /// </para>
        /// </remarks>
        public string DisplayName { get; }

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
        public void Tick(float dt)
        {
            NetMovementAgent agent = Actor != null ? Actor.Movement : null;

            if (agent == null)
            {
                // No collision seam — a connection that has claimed a slot but whose actor has
                // not spawned yet. Integrating in a straight line keeps its tick accounting and
                // anti-cheat counters honest instead of silently skipping the player.
                InputAuthority.ApplyPendingInput(Session, dt, _moveDetached, this);
                return;
            }

            // Ground contact is Unity's answer, not the simulation's: the CharacterController
            // knows what it is standing on and MovementCore does not.
            Session.State.IsGrounded = agent.IsGrounded;

            InputAuthority.ApplyPendingInput(Session, dt, _moveThroughCollision, this);

            // Mirror the authoritative result back so the agent's stance height tracks the
            // crouch the server actually applied. Tick() would step the simulation twice.
            agent.ApplyAuthoritativeState(in Session.State);
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
