using System;
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
    /// OWNER: Dev C.
    /// </para>
    /// <para>
    /// The two <see cref="Func{T, TResult}"/> fields are built once in the constructor rather
    /// than passed as lambdas at the call site. A lambda that closes over <c>this</c> allocates
    /// a delegate on every call, and this one is called once per player per tick — 16 players
    /// at 30 Hz is 480 allocations a second in the loop M1 criterion 9 requires to allocate
    /// nothing.
    /// </para>
    /// </remarks>
    internal sealed class ServerPlayer
    {
        private readonly Func<Vec3, Vec3> _moveThroughCollision;
        private readonly Func<Vec3, Vec3> _moveDetached;

        public ServerPlayer(ushort connectionId, ushort actorId)
        {
            Session = new ClientSession(connectionId, actorId);
            _moveThroughCollision = MoveThroughCollision;
            _moveDetached = MoveDetached;
        }

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
                InputAuthority.ApplyPendingInput(Session, dt, _moveDetached);
                return;
            }

            // Ground contact is Unity's answer, not the simulation's: the CharacterController
            // knows what it is standing on and MovementCore does not.
            Session.State.IsGrounded = agent.IsGrounded;

            InputAuthority.ApplyPendingInput(Session, dt, _moveThroughCollision);

            // Mirror the authoritative result back so the agent's stance height tracks the
            // crouch the server actually applied. Tick() would step the simulation twice.
            agent.ApplyAuthoritativeState(in Session.State);
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
