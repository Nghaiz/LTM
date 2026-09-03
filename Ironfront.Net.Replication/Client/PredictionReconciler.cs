using System;
using Ironfront.Net.Protocol;
using Ironfront.Net.Replication.Movement;

namespace Ironfront.Net.Replication.Client
{
    /// <summary>What a call to <see cref="PredictionReconciler.Reconcile"/> did.</summary>
    public enum ReconcileResult
    {
        /// <summary>Prediction matched authority within tolerance. Nothing was touched.</summary>
        Agreed = 0,

        /// <summary>Prediction was corrected and the unacknowledged inputs were replayed.</summary>
        Corrected = 1,

        /// <summary>
        /// The acknowledged tick is not in the buffer — the client stalled longer than the
        /// history, or the server acknowledged something never sent. The authoritative state is
        /// adopted verbatim with no replay.
        /// </summary>
        Resynchronised = 2,

        /// <summary>The acknowledgement is older than one already applied. Ignored.</summary>
        Stale = 3,
    }

    /// <summary>
    /// Keeps the inputs the server has not acknowledged yet, and replays them over an
    /// authoritative state when the two disagree.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The problem this solves.</b> The local player moves the instant a key is pressed,
    /// because waiting a round trip to see yourself move is unplayable. The server simulates the
    /// same input ~RTT/2 later and is the authority. Wherever they disagree — a collision the
    /// client did not know about, a speed the server refused — the client must be corrected
    /// without also throwing away the movement it has predicted since. Snapping to the
    /// authoritative state alone would rewind the player to where they were half a round trip
    /// ago, every single time a correction happens.
    /// </para>
    /// <para>
    /// So the correction is: adopt the authoritative state, then re-run every input the server
    /// had not yet processed when it produced that state, through the same
    /// <see cref="MovementCore"/> the server ran. Both sides step identical code over identical
    /// inputs, so the replayed result is what the server will itself arrive at.
    /// </para>
    /// <para>
    /// <b>Why a tolerance rather than always correcting.</b> Positions travel quantised to
    /// <see cref="Quantize.UnpackPos"/>'s resolution, so client and server essentially never
    /// agree to the last bit. Correcting on any difference would rewrite the player's position
    /// every tick — visible as a permanent shimmer, and a replay's worth of work 30 times a
    /// second for nothing. The tolerance has to sit above the quantisation step; see
    /// <see cref="PositionToleranceMetres"/>.
    /// </para>
    /// <para>
    /// <b>The replay integrates the motion itself, and there is no collision system in here.</b>
    /// <see cref="MovementCore.Step"/> returns a motion delta and deliberately does not write
    /// <see cref="MoveState.Position"/>, because on both the server and an ordinary predicted
    /// tick the caller pushes that delta through the collision system and writes back where the
    /// actor really ended up. A replay cannot: it is re-simulating N ticks in one frame from a
    /// position the body is not standing at, so there is nothing to sweep a capsule through.
    /// It therefore adds the delta directly — which is exactly what the server's own collision
    /// would produce in open space, and an over-estimate anywhere the client has not been told
    /// about geometry yet.
    /// </para>
    /// <para>
    /// <b>The vertical channel is where that shows, and what absorbs it is named.</b> While
    /// grounded, <see cref="MovementCore.Step"/> asks for <see cref="MovementCore.StickToGroundForce"/>
    /// downwards every tick — a force whose whole purpose is to be refused by the floor — so an
    /// N-input replay asks to descend N x 0.333 m. The client does not act on that directly: a
    /// <see cref="ReconcileResult.Corrected"/> result is applied through
    /// <c>NetMovementAgent.ApplyCorrectedState</c>, whose non-resync path MOVES the body with
    /// <c>CharacterMove</c> and writes back the position collision granted, so a grounded body
    /// does not sink. <c>PredictionReplayTests</c> pins the number so a reader who meets it in a
    /// log knows it is expected and knows what cancels it.
    /// </para>
    /// <para>
    /// <b>Zero allocation after construction.</b> The ring is a struct array sized once.
    /// </para>
    /// </remarks>
    public sealed class PredictionReconciler
    {
        /// <summary>
        /// How far apart the two positions may drift before a correction, in metres.
        /// </summary>
        /// <remarks>
        /// Position travels as an i16 over a 4096 m range, so the wire step is 4096/65535 =
        /// <b>6.25 cm</b>, not the 1 cm it is easy to assume. Worst-case rounding error is half a
        /// step on each axis, or sqrt(3) x 3.125 cm = 5.4 cm in 3D — so any tolerance below about
        /// 6 cm fires on quantisation alone and rewrites the player's position every single tick,
        /// forever, for nothing.
        ///
        /// 0.25 m is four wire steps and roughly 4.6x the worst-case rounding error: far enough
        /// above the noise floor to never trigger on it, and still only 38 ms of movement at
        /// <see cref="MovementCore.RunSpeed"/> — so a real disagreement, a wall the client ran
        /// through or a speed the server clamped, is caught within a tick or two, long before it
        /// is visible.
        ///
        /// The first draft said 0.1 m on the strength of a 1 cm step that does not exist. The
        /// test that pins the tolerance against the real step is what caught it.
        /// </remarks>
        public const float PositionToleranceMetres = 0.25f;

        /// <summary>
        /// Inputs retained. One second at 30 Hz, which covers an RTT far worse than the 100 ms
        /// criterion 7 grades on; past that the client resynchronises instead, which is the
        /// honest outcome when the server is a second behind.
        /// </summary>
        public const int Capacity = ProtocolConstants.SIM_TICK_RATE;

        /// <summary>
        /// Above this error, authority is adopted with NO replay and the caller is told to
        /// <see cref="ReconcileResult.Resynchronised"/> — which the Unity side turns into a
        /// teleport rather than a swept move.
        /// </summary>
        /// <remarks>
        /// <para>
        /// <b>Without a bound, a large error can never converge.</b> A
        /// <see cref="ReconcileResult.Corrected"/> result is applied by
        /// <c>NetMovementAgent.ApplyCorrectedState(hardSnap: false)</c>, which is a
        /// <c>CharacterController.Move</c> — a SWEPT motion that collision stops at the first
        /// thing in the way. Sweeping a several-hundred-metre delta therefore does not move the
        /// body to authority; it wedges it against geometry, the same error is computed again on
        /// the very next snapshot, and the body is shoved every tick for as long as the
        /// disagreement lasts. That is the juddering an unplaced body shows.
        /// </para>
        /// <para>
        /// <b>This is not hypothetical, and the distance is not small.</b> The player prefab is
        /// parked near <c>(0, 1000, 0)</c> until the server places it (the same authored spot
        /// <c>ServerTickLoop</c>'s announce guard names for X-17), so a client whose body has not
        /// been placed yet sits ~975 m from authority — three orders of magnitude past anything
        /// a replay can fix.
        /// </para>
        /// <para>
        /// <b>Why this value.</b> The replay can only cover <see cref="Capacity"/> ticks, one
        /// second at <see cref="ProtocolConstants.SIM_TICK_RATE"/>. The worst divergence that
        /// second can legitimately hold is both simulations running flat out in OPPOSITE
        /// directions — <c>2 x <see cref="MovementCore.RunSpeed"/></c>, 13 m — and the factor of
        /// two on top covers the vertical axis, where a jump and a fall diverge faster than a
        /// run. Anything beyond that is not a mispredict the ring can replay away, so replaying
        /// it is the wrong operation regardless of what caused it.
        /// </para>
        /// </remarks>
        public const float ResyncDistanceMetres = 4f * MovementCore.RunSpeed;

        private readonly MoveInput[] _inputs = new MoveInput[Capacity];
        private readonly uint[] _ticks = new uint[Capacity];

        // Ledger X-41. The position this client predicted itself to be at AFTER each recorded
        // tick's input was applied. Without it, Reconcile could only compare the client's
        // CURRENT position against an authoritative state for a tick `lag` in the past -- so a
        // client predicting perfectly was compared against a position it had legitimately left,
        // and every snapshot past 0.25 m of lag reported Corrected.
        private readonly Vec3[] _positions = new Vec3[Capacity];

        private long _count;
        private uint _lastAckedTick;
        private bool _hasAcked;

        /// <summary>Corrections applied. The number to quote for "how often prediction missed".</summary>
        /// <remarks>
        /// <b>True since X-41 closed, and NOT true of any artifact recorded before it.</b> This
        /// counter used to move on lag rather than on error: the comparison was against the
        /// client's current position, so once <c>lag x speed</c> passed
        /// <see cref="PositionToleranceMetres"/> — 2.1 ticks at a walk, 1.2 at a sprint — every
        /// snapshot reported <see cref="ReconcileResult.Corrected"/> while the replay moved the
        /// client by nothing at all. A <c>corrections: N</c> from an older run is a lag
        /// measurement and is not comparable with one from a newer one.
        /// </remarks>
        public long CorrectionCount { get; private set; }

        /// <summary>Times the acknowledged tick fell outside the buffer.</summary>
        public long ResyncCount { get; private set; }

        /// <summary>Total inputs re-simulated. Divided by <see cref="CorrectionCount"/>, the
        /// average replay depth — which is RTT expressed in ticks.</summary>
        public long ReplayedInputCount { get; private set; }

        /// <summary>Inputs currently held, up to <see cref="Capacity"/>.</summary>
        public int Pending => (int)Math.Min(_count, Capacity);

        /// <summary>Clears the history. Call on disconnect or respawn.</summary>
        public void Reset()
        {
            _count = 0;
            _hasAcked = false;
            _lastAckedTick = 0;
            CorrectionCount = 0;
            ResyncCount = 0;
            ReplayedInputCount = 0;
        }

        /// <summary>
        /// Records an input the client has just predicted, so it can be replayed if needed, and
        /// where that input left it.
        /// </summary>
        /// <remarks>
        /// <para>
        /// Call this every tick you step prediction, with the tick that input belongs to —
        /// recording after stepping, or with the wrong tick, silently shifts every replay by one
        /// frame and shows up as a correction that never converges.
        /// </para>
        /// <para>
        /// <b><paramref name="predictedPosition"/> is REQUIRED rather than defaulted, and that is
        /// ledger X-41 rather than API taste.</b> A default would let a caller that forgot it
        /// keep the old comparison — current position against a past authoritative state — with
        /// nothing anywhere reporting the difference. That is precisely the failure this row is
        /// about, reintroduced by the fix for it.
        /// </para>
        /// <para>
        /// <b>The position AFTER this tick's input, not before.</b>
        /// <c>NetPredictionClock</c> raises <c>OnTickSimulated</c> after <c>_agent.Tick(...)</c>,
        /// so the caller has exactly that value in hand and no new ordering is introduced.
        /// Recording the pre-step position would offset every comparison by one tick's motion —
        /// which at a sprint is most of the tolerance.
        /// </para>
        /// </remarks>
        public void Record(uint tick, in MoveInput input, in Vec3 predictedPosition)
        {
            int slot = (int)(_count % Capacity);
            _ticks[slot] = tick;
            _inputs[slot] = input;
            _positions[slot] = predictedPosition;
            _count++;
        }

        /// <summary>
        /// Compares the predicted state against the server's and corrects it if they disagree.
        /// </summary>
        /// <param name="predicted">The local state. Rewritten in place when corrected.</param>
        /// <param name="authoritative">The server's state for the local actor.</param>
        /// <param name="lastProcessedInputTick">
        /// From the snapshot header: the newest input tick the server had consumed when it
        /// produced <paramref name="authoritative"/>. Everything after it is unacknowledged and
        /// gets replayed.
        /// </param>
        /// <param name="dt">Simulation step, seconds. Must be the server's step, not a frame
        /// time — a replay stepped at the render rate lands somewhere the server never will.</param>
        public ReconcileResult Reconcile(
            ref MoveState predicted, in MoveState authoritative, uint lastProcessedInputTick, float dt)
        {
            // IsNewer32, not <=: the tick wraps, and a plain comparison would treat every
            // acknowledgement as stale for a while after the wrap -- the player would stop being
            // corrected at all, which is worse than being corrected wrongly because nothing
            // reports it.
            if (_hasAcked && !SequenceMath.IsNewer32(lastProcessedInputTick, _lastAckedTick))
                return ReconcileResult.Stale;

            _lastAckedTick = lastProcessedInputTick;
            _hasAcked = true;

            // Ledger X-41. Compared at the ACKNOWLEDGED tick, not against where the client is
            // standing now. `authoritative` is the server's answer for the tick it names, and
            // the client left that position `lag` ticks ago on purpose -- so comparing the two
            // measured how far behind the server was, and reported it as a mispredict. Measured:
            // 16 of 16 snapshots Corrected on a client that mispredicted nothing, with the
            // replay then moving it by nothing at all.
            //
            // A tick that has fallen out of the ring falls back to the current position, which
            // is the pre-X-41 comparison and the only one still available. That is the
            // resynchronise neighbourhood, and the fallback is an approximation rather than a
            // second opinion.
            Vec3 predictedThen = TryGetRecordedPosition(lastProcessedInputTick, out Vec3 recorded)
                ? recorded
                : predicted.Position;

            if (WithinTolerance(predictedThen, authoritative.Position))
            {
                return ReconcileResult.Agreed;
            }

            // BEFORE the replay path, not after it: past ResyncDistanceMetres the replay is not
            // merely unnecessary, it is the wrong operation -- see that constant's remark. The
            // caller applies this result as a teleport, which is the only thing that moves a body
            // the width of the map.
            if (BeyondResyncDistance(predictedThen, authoritative.Position))
            {
                predicted = authoritative;
                ResyncCount++;
                return ReconcileResult.Resynchronised;
            }

            if (!TryFindSlotAfter(lastProcessedInputTick, out long firstUnacked))
            {
                predicted = authoritative;
                ResyncCount++;
                return ReconcileResult.Resynchronised;
            }

            // Adopt authority FIRST, then replay. Replaying onto the predicted state instead
            // would compound the very error being corrected.
            predicted = authoritative;

            for (long i = firstUnacked; i < _count; i++)
            {
                int slot = (int)(i % Capacity);

                // The RETURN VALUE, written back. Ledger row X-21: this line used to call Step
                // and discard it, and `MovementCore.Step` deliberately does not write
                // MoveState.Position -- "only the collision system knows where the actor really
                // ended up, so the caller writes it back after moving". So the replay advanced
                // velocity and stance and never the position, and every correction landed the
                // client on the server's STALE position with the unacknowledged motion thrown
                // away. Measured: `corrections: 2208` in a 136 s run that never converged, with
                // pendingInputs pinned at Capacity.
                predicted.Position += MovementCore.Step(ref predicted, in _inputs[slot], dt);
                ReplayedInputCount++;
            }

            CorrectionCount++;
            return ReconcileResult.Corrected;
        }

        /// <summary>
        /// The position this client predicted for <paramref name="tick"/>, if it is still held.
        /// Ledger <b>X-41</b>.
        /// </summary>
        /// <remarks>
        /// A linear scan of at most <see cref="Capacity"/> entries, once per snapshot rather than
        /// once per tick — 30 reads a second against a ring the same walk already searches in
        /// <see cref="TryFindSlotAfter"/>. Indexing arithmetic off the tick would be O(1) and
        /// would also have to reproduce the wrap rules <c>SequenceMath</c> owns, in a second
        /// place, for a saving nothing has asked for.
        /// </remarks>
        private bool TryGetRecordedPosition(uint tick, out Vec3 position)
        {
            position = default;
            if (_count == 0) return false;

            long oldest = Math.Max(0, _count - Capacity);

            for (long i = oldest; i < _count; i++)
            {
                int slot = (int)(i % Capacity);
                if (_ticks[slot] != tick) continue;

                position = _positions[slot];
                return true;
            }

            return false;
        }

        /// <summary>
        /// Whether the two positions are further apart than a replay could ever close. See
        /// <see cref="ResyncDistanceMetres"/>.
        /// </summary>
        private static bool BeyondResyncDistance(in Vec3 a, in Vec3 b)
        {
            float dx = a.X - b.X;
            float dy = a.Y - b.Y;
            float dz = a.Z - b.Z;

            // Squared, for the same reason WithinTolerance is: this runs once per snapshot.
            return dx * dx + dy * dy + dz * dz
                   > ResyncDistanceMetres * ResyncDistanceMetres;
        }

        private static bool WithinTolerance(in Vec3 a, in Vec3 b)
        {
            float dx = a.X - b.X;
            float dy = a.Y - b.Y;
            float dz = a.Z - b.Z;

            // Squared, so the per-tick check costs no square root.
            return dx * dx + dy * dy + dz * dz
                   <= PositionToleranceMetres * PositionToleranceMetres;
        }

        /// <summary>
        /// Finds the first buffered input newer than <paramref name="ackedTick"/>.
        /// </summary>
        /// <returns>
        /// False when the acknowledged tick has already fallen out of the ring, which is the
        /// resynchronise case. Also false when nothing is buffered at all.
        /// </returns>
        private bool TryFindSlotAfter(uint ackedTick, out long index)
        {
            index = 0;
            if (_count == 0) return false;

            long oldest = Math.Max(0, _count - Capacity);

            // The acknowledged tick must still be inside the buffer, or one tick before its
            // oldest entry. Older than that and the inputs between it and the buffer have been
            // evicted -- scanning on regardless would find the oldest RETAINED input and replay
            // from there, silently re-applying movement the server has already consumed and
            // leaving the client permanently ahead of authority. Resynchronising is the honest
            // answer: the history needed to do better is gone.
            if (SequenceMath.IsNewer32(_ticks[(int)(oldest % Capacity)], unchecked(ackedTick + 1)))
                return false;

            for (long i = oldest; i < _count; i++)
            {
                if (SequenceMath.IsNewer32(_ticks[(int)(i % Capacity)], ackedTick))
                {
                    index = i;
                    return true;
                }
            }

            // Every held input is acknowledged. Nothing to replay, and that is not a resync --
            // but the caller only reaches here when the positions disagreed, and with no
            // unacknowledged input to explain the gap the only correct answer is authority.
            index = _count;
            return true;
        }

    }
}
