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
            Encoder        = new DeltaEncoder();
            VehicleEncoder = new VehicleDeltaEncoder();
        }

        public ushort ConnectionId { get; }

        /// <summary>The actor this player drives. One id space shared with bots (spec 4.3.1).</summary>
        public ushort ActorId { get; }

        /// <summary>Per-client delta state. Never shared — baselines are per client by definition.</summary>
        public DeltaEncoder Encoder { get; }

        /// <summary>
        /// Per-client delta state for the vehicle stream. V4 task 7.
        /// </summary>
        /// <remarks>
        /// <para>
        /// <b>A second encoder, not a second use of the first.</b> Actors and vehicles are
        /// separate messages with separate entry layouts and separate id spaces; one encoder
        /// cannot hold both baselines, and the baseline is the thing a delta is measured from.
        /// </para>
        /// <para>
        /// <b>Both are acked by one <c>C_ACK_BASELINE</c>, and that ack does NOT name a state of
        /// both streams.</b> The actor snapshot is in every datagram a client can ack; the vehicle
        /// body is independently rate-limited and is absent from most of them. So when the client
        /// acks tick N and no vehicle body shipped at N, <see cref="VehicleDeltaEncoder"/>'s
        /// history holds no entry for N, <c>TryFindBaseline</c> fails, and the next vehicle body
        /// is written FULL rather than as a delta.
        /// </para>
        /// <para>
        /// <b>That is correct, and it is not free.</b> A full body is written over the per-viewer
        /// VIEW — only the vehicles that were due — so the cost is roughly 30 bytes per entry
        /// instead of ~10, not a whole world. It falls on viewers whose best band is not Near:
        /// Mid sends every 2nd snapshot so about half its bodies go full, Far every 5th so about
        /// four in five do. A viewer with any Near-band vehicle sees a body every tick and never
        /// falls back.
        /// </para>
        /// <para>
        /// <b>Why it is not simply fixed here.</b> Accepting the ack anyway and reaching for an
        /// older recorded baseline would be unsound: the server cannot know the client received
        /// that older datagram, and a delta against a baseline the client lacks is discarded by
        /// its decoder with no way to recover — a deadlock, where the present behaviour is merely
        /// fatter. Sending an empty vehicle body on every snapshot is also unavailable, because a
        /// vehicle absent from a delta is DESPAWNED by the decoder, not held. A real fix needs
        /// either a second ack field or per-stream ack state on the wire, and the wire is frozen
        /// at v3.
        /// </para>
        /// </remarks>
        public VehicleDeltaEncoder VehicleEncoder { get; }

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
        public WeaponRuntimeState Weapon = WeaponRuntimeState.Loaded(WeaponCatalog.Inert);

        /// <summary>
        /// Which weapon this player is holding, as <c>NetServerActor.WeaponId</c> reports it.
        /// </summary>
        /// <remarks>
        /// <para>
        /// The id has existed server-side the whole time — <c>Actor.SpawnWeapon</c> stamps it
        /// onto <c>Weapon.NetworkId</c> and <c>NetServerActor.WeaponId</c> reads it back — and it
        /// is already on the wire in the snapshot, in <c>S_SPAWN</c> and in
        /// <c>S_WEAPON_FIRE</c>. Nobody had plumbed it into the session, so the session kept
        /// answering "rifle" for all seventeen weapons. <b>This is what makes phase-V2 a
        /// no-wire-change phase</b>: a loadout message would be a new opcode and a
        /// <c>PROTOCOL_VERSION</c> bump, and V3 is carrying the only bump this track gets.
        /// </para>
        /// <para>
        /// <b>Assign this BEFORE calling <see cref="ResetWeapon"/>.</b> See that method.
        /// </para>
        /// </remarks>
        public byte WeaponId;

        /// <summary>
        /// The server's copy of this player's weapon numbers. Never accepted from the client.
        /// </summary>
        /// <remarks>
        /// <para>
        /// <b>Derived from <see cref="WeaponId"/>, not stored beside it</b> (phase-V2 D9). Two
        /// fields synchronised by a setter is the derived-field divergence phase-05 D9 already
        /// ruled on for health, and the failure mode is the same: one of them is read by the
        /// snapshot and the other by the damage path, and nothing reports the disagreement.
        /// </para>
        /// <para>
        /// <b>Cost, measured and accepted:</b> a ~48-byte readonly-struct copy per accepted input
        /// frame per player when passed by <c>in</c> — at 16 players x 30 Hz, under 25 KB/s of
        /// stack traffic and zero allocation. If a profiler ever disagrees, the escape hatch is
        /// caching the config in a local for the duration of one tick, never a second stored
        /// field. <see cref="Weapon"/> stays a field for the opposite reason: it is stepped by
        /// <c>ref</c>, and a property there would step a copy.
        /// </para>
        /// </remarks>
        public WeaponConfig WeaponConfig => WeaponCatalog.For(WeaponId);

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

        /// <summary>
        /// The same rotation for the vehicle stream, and deliberately a <b>separate</b> cursor.
        /// </summary>
        /// <remarks>
        /// One shared cursor would rotate the vehicle admission order because the <i>actor</i>
        /// view shed, and vice versa — coupling two orders that have nothing to do with each
        /// other, and re-ordering a vehicle view that fit comfortably for no reason. Each stream
        /// rotates only when it is the one that ran out of room.
        /// </remarks>
        public int VehicleShedCursor;

        // Ledger X-43. One WeaponRuntimeState per weapon id, so a switch parks a clip instead
        // of discarding it. Eighteen entries is the whole id space (WeaponIds.MAX_ASSIGNED), the
        // lookup is an array index rather than a hash, and both arrays are allocated once with
        // the session -- this sits on the 30 Hz switch path.
        private readonly WeaponRuntimeState[] _parkedWeapons =
            new WeaponRuntimeState[WeaponIds.MAX_ASSIGNED + 1];

        private readonly bool[] _hasParkedWeapon = new bool[WeaponIds.MAX_ASSIGNED + 1];

        /// <summary>
        /// Points the session at a different weapon, keeping each one's own clip. Ledger
        /// <b>X-43</b>.
        /// </summary>
        /// <remarks>
        /// <para>
        /// <b>What this replaces.</b> <c>ServerCombatBridge.AdoptTheWeaponTheBodyIsHolding</c>
        /// re-pointed the session and then called <see cref="ResetWeapon"/>, because a full clip
        /// was the only weapon state it could reach: <see cref="Weapon"/> is a single
        /// <see cref="WeaponRuntimeState"/>, and <c>NetServerActor.AmmoInClip</c> could not
        /// supply the missing half either — the bridge WRITES that field from the session every
        /// frame, so it mirrors the session rather than the body. A player who switched away and
        /// back therefore had a full magazine.
        /// </para>
        /// <para>
        /// <b>The whole runtime state is parked, not just the clip (O-D4).</b> Remembering ammo
        /// and forgetting <see cref="WeaponRuntimeState.LastFiredTime"/> would leave a
        /// quick-switch cooldown reset — fire, switch away, switch back, fire again inside the
        /// cooldown the server believes it is still enforcing — which is a rapid-fire exploit
        /// bought back one field at a time, and one <c>FireRateViolations</c> would not move for.
        /// </para>
        /// <para>
        /// <b>A running reload is CANCELLED on the way out, and that is not tidiness.</b> Parked
        /// with <see cref="WeaponRuntimeState.ReloadStartedAt"/> intact, a reload would complete
        /// on its own while the weapon sat in a bag, so a player could switch away, wait, and
        /// switch back to a full clip — the same free magazine arriving by a different door.
        /// </para>
        /// <para>
        /// <b>An unchanged id returns immediately, and that guard is load-bearing for exactly one
        /// reason.</b> The clip would round-trip unchanged without it — park and restore are
        /// inverses — but the reload CANCEL above is not an inverse, so a same-weapon call would
        /// cancel a reload the player never interrupted. The saved work is incidental; the
        /// preserved reload is the point. Mutation-checked: removing the guard leaves every ammo
        /// assertion green and fails only the reload one.
        /// </para>
        /// </remarks>
        public void SwitchWeaponTo(byte weaponId)
        {
            if (weaponId == WeaponId) return;

            if (WeaponId < _parkedWeapons.Length)
            {
                WeaponRuntimeState outgoing = Weapon;

                // Holstered, and not mid-reload. Both are facts about a weapon in a bag.
                outgoing.Unholstered = false;
                outgoing.Reloading = false;
                outgoing.ReloadStartedAt = float.NegativeInfinity;

                _parkedWeapons[WeaponId] = outgoing;
                _hasParkedWeapon[WeaponId] = true;
            }

            WeaponId = weaponId;

            if (weaponId < _parkedWeapons.Length && _hasParkedWeapon[weaponId])
            {
                WeaponRuntimeState incoming = _parkedWeapons[weaponId];
                incoming.Unholstered = true;
                Weapon = incoming;
                return;
            }

            // First time this life. A weapon reached for the first time is loaded, which is
            // what the loadout handed the body.
            ResetWeaponPreservingMemory();
        }

        /// <summary>Re-arms the weapon with a full clip. Called on spawn and respawn.</summary>
        /// <remarks>
        /// <b><see cref="WeaponId"/> must already be assigned when this runs.</b> The clip size
        /// comes from <see cref="WeaponConfig"/>, which is now derived from the id, so calling
        /// this first loads a clip of ZERO and the player cannot fire — and the symptom
        /// (<see cref="FireRejection.NoAmmo"/>, forever) looks exactly like the ammo bug
        /// phase-05 closed. All three call sites — respawn, round reset and join — assign the id
        /// first, and <c>ASpawnAssignsTheWeaponIdBeforeLoadingTheClip</c> is what keeps them
        /// doing so.
        /// </remarks>
        public void ResetWeapon()
        {
            // Ledger X-43: a life's worth of parked clips does not survive a death. Cleared
            // here rather than in a separate call because all three callers -- spawn, respawn
            // and round reset -- want exactly that, and a second method they had to remember to
            // call is a second method one of them would eventually not.
            Array.Clear(_hasParkedWeapon, 0, _hasParkedWeapon.Length);

            ResetWeaponPreservingMemory();
        }

        /// <summary>
        /// Loads a full clip without touching the parked table. Ledger <b>X-43</b>.
        /// </summary>
        /// <remarks>
        /// Split from <see cref="ResetWeapon"/> so <see cref="SwitchWeaponTo"/> can arm a weapon
        /// reached for the first time WITHOUT forgetting the clips it has just parked. Calling
        /// the public one there would have every switch wipe the memory it exists to keep, which
        /// is the defect with an extra step.
        /// </remarks>
        private void ResetWeaponPreservingMemory()
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
