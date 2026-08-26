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
