using System;
using Ironfront.Net.Protocol;
using Ironfront.Net.Replication.Combat;
using Ironfront.Net.Replication.Movement;

namespace Ironfront.Net.Replication.Server
{
    /// <summary>
    /// Everything the server tracks for one connected player: their pending input, their
    /// authoritative movement state, and their delta baseline.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Allocation-free after construction. The input buffer is a fixed ring rather than a
    /// <c>Queue</c> so a 30 Hz stream from 16 clients produces no garbage — and, more
    /// importantly, so a client that floods input cannot make the server allocate. When the
    /// ring is full the oldest frame is discarded, because in a fixed-size buffer of
    /// timestamped input the stale end is always the right thing to lose.
    /// </para>
    /// </remarks>
    public sealed class ClientSession
    {
        /// <summary>
        /// Input frames buffered per client. A client sends
        /// <see cref="ProtocolConstants.INPUT_REDUNDANCY"/> frames per packet at 30 Hz, so 32
        /// is roughly a second of slack — enough to ride out a stall, far too little to be
        /// worth flooding.
        /// </summary>
        public const int InputBufferCapacity = 32;

        private readonly InputFrame[] _inputRing = new InputFrame[InputBufferCapacity];
        private readonly uint[] _inputTicks = new uint[InputBufferCapacity];
        private int _head;
        private int _count;

        public ClientSession(ushort connectionId, ushort actorId)
        {
            ConnectionId = connectionId;
            ActorId      = actorId;
            Encoder      = new DeltaEncoder();
        }

        public ushort ConnectionId { get; }

        /// <summary>The actor this player drives. One id space shared with bots (spec 4.3.1).</summary>
        public ushort ActorId { get; }

        /// <summary>Per-client delta state. Never shared — baselines are per client by definition.</summary>
        public DeltaEncoder Encoder { get; }

        /// <summary>Authoritative movement state. The server's copy is the truth.</summary>
        public MoveState State;

        /// <summary>Position at the end of the previous tick, for the speed check.</summary>
        public Vec3 PreviousPosition;

        /// <summary>Newest input tick applied. Older or equal frames are redundant copies.</summary>
        public uint LastProcessedInputTick;

        /// <summary>The last frame applied, repeated when input goes missing.</summary>
        public MoveInput LastInput;

        /// <summary>True once at least one real frame has arrived.</summary>
        public bool HasInput;

        /// <summary>Consecutive ticks with no fresh input. Reset on arrival.</summary>
        public int MissedInputTicks;

        /// <summary>Times the post-move speed clamp fired. High values suggest a speed hack.</summary>
        public int SpeedViolations;

        /// <summary>
        /// Input frames this session may still have applied. Refilled one per tick, capped at
        /// <c>InputAuthority.MaxInputBurst</c>. See that constant for what it defends against.
        /// </summary>
        /// <remarks>
        /// Starts full rather than empty. A session that has just connected has been idle for
        /// longer than any gap the budget is meant to absorb, so metering its very first
        /// delivery would throttle the one client that has provably sent nothing yet.
        /// </remarks>
        public int InputBudget = InputAuthority.MaxInputBurst;

        /// <summary>
        /// Frames left in the ring because the budget ran out. A sustained non-zero value is a
        /// client sending faster than the server ticks, which is the speed hack the budget
        /// meters — the frames are held, not dropped, so an honest burst is only delayed.
        /// </summary>
        public int InputThrottleEvents;

        /// <summary>Times an abnormal forward tick jump was rejected.</summary>
        public int TickJumpViolations;

        /// <summary>
        /// This player's authoritative weapon state: ammo, cooldown stamp, reload clock.
        /// </summary>
        /// <remarks>
        /// A field rather than a property so <c>ServerCombatAuthority.Step</c> can take it by
        /// <c>ref</c>. Passing a property's value would step a copy and throw the result away,
        /// which compiles, runs, and leaves the ammo count frozen forever.
        /// </remarks>
        public WeaponRuntimeState Weapon = WeaponRuntimeState.Loaded(WeaponConfig.Rifle);

        /// <summary>
        /// The server's copy of this player's weapon numbers. Never accepted from the client.
        /// </summary>
        /// <remarks>
        /// <see cref="WeaponConfig.Rifle"/> until a loadout message or the client track's weapon assets
        /// say otherwise — the placeholder the phase-05 risk table names. Because the seam
        /// takes a config rather than hardcoding one, swapping in the real numbers is data.
        /// </remarks>
        public WeaponConfig WeaponConfig = WeaponConfig.Rifle;

        /// <summary>
        /// Where this client's snapshot last stopped shedding actors, so the next one resumes
        /// past it. Phase-05 task 4, decision D6.
        /// </summary>
        /// <remarks>
        /// Lives on the session rather than inside <c>InterestManager</c>'s tables because it
        /// is per-connection state with a per-connection lifetime: it dies with the session
        /// instead of needing its own entry in the trap-2 forget path.
        /// </remarks>
        public int ShedCursor;

        /// <summary>Re-arms the weapon with a full clip. Called on spawn and respawn.</summary>
        public void ResetWeapon()
        {
            Weapon = WeaponRuntimeState.Loaded(WeaponConfig);
        }

        /// <summary>Input frames buffered right now.</summary>
        public int PendingInputCount => _count;

        public bool InputBufferIsEmpty => _count == 0;

        /// <summary>
        /// Buffers one frame. Frames at or below <see cref="LastProcessedInputTick"/> are
        /// dropped here — those are the redundant copies the client deliberately repeats
        /// (protocol-spec.md section 4.2), and re-applying them would move the player twice
        /// for one input.
        /// </summary>
        /// <returns>False when the frame was a duplicate or the ring was full.</returns>
        public bool EnqueueInput(uint tick, in InputFrame frame)
        {
            if (HasInput && !SequenceMath.IsNewer32(tick, LastProcessedInputTick)) return false;

            if (_count == InputBufferCapacity)
            {
                // Full: drop the oldest. Keeping it and rejecting the newest would let a burst
                // pin the buffer to stale input and freeze the player where they were.
                _head = (_head + 1) % InputBufferCapacity;
                _count--;
            }

            int tail = (_head + _count) % InputBufferCapacity;
            _inputRing[tail]  = frame;
            _inputTicks[tail] = tick;
            _count++;
            return true;
        }

        /// <summary>Takes the oldest buffered frame.</summary>
        public bool TryDequeueInput(out uint tick, out InputFrame frame)
        {
            if (_count == 0)
            {
                tick  = 0;
                frame = default;
                return false;
            }

            tick  = _inputTicks[_head];
            frame = _inputRing[_head];
            _head = (_head + 1) % InputBufferCapacity;
            _count--;
            return true;
        }

        public void ClearInput()
        {
            _head  = 0;
            _count = 0;
        }
    }
}
