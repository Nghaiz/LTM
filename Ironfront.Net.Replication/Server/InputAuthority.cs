using System;
using Ironfront.Net.Protocol;
using Ironfront.Net.Replication.Movement;

namespace Ironfront.Net.Replication.Server
{
    /// <summary>
    /// Applies client input on the server, under the assumption that the client is hostile.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This is the only place a client's bytes are allowed to influence the authoritative
    /// world, so every check that keeps a modified client honest lives here rather than being
    /// spread through the tick loop.
    /// </para>
    /// <para>
    /// The checks, and what each one stops:
    /// </para>
    /// <list type="number">
    /// <item>
    /// <b>Tick-jump guard.</b> A client that claims to have skipped two seconds forward is
    /// fast-forwarding the simulation. Rejected, and the session's clock is pulled back so the
    /// player continues from where the server thinks they are.
    /// </item>
    /// <item>
    /// <b>Axis normalization.</b> The classic cheat: send <c>moveX = moveZ = 127</c> for a
    /// vector of length 1.41 and move 41% faster. <see cref="MovementCore"/> happens to
    /// normalize the wish direction anyway, so this exploit is already dead there — but the
    /// check stays, because relying on a side effect of the movement port to be the anti-cheat
    /// means the day someone restores the original slope projection, the hole quietly reopens.
    /// </item>
    /// <item>
    /// <b>Post-move speed clamp.</b> Whatever the input said, the distance actually covered in
    /// one tick is bounded. This catches anything the first two checks did not anticipate,
    /// including bugs on our own side.
    /// </item>
    /// </list>
    /// </remarks>
    public static class InputAuthority
    {
        /// <summary>
        /// The largest forward tick jump accepted in one frame: 60 ticks, 2 seconds at 30 Hz.
        /// Generous enough for a real hitch or a lag spike, far short of useful for
        /// fast-forwarding.
        /// </summary>
        public const int MaxTickJump = 2 * ProtocolConstants.SIM_TICK_RATE;

        /// <summary>
        /// Slack on the per-tick distance clamp.
        /// </summary>
        /// <remarks>
        /// 30% rather than 0% because legitimate movement is not capped at the run speed:
        /// explosions throw players, slopes accelerate them, and a jump arc adds vertical
        /// speed. Too tight and ordinary players get rubber-banded by their own game, which is
        /// far more damaging than letting a cheater gain 30%.
        /// </remarks>
        public const float SpeedTolerance = 1.3f;

        /// <summary>
        /// How many ticks of missing input repeat the last frame before the player is stopped.
        /// </summary>
        /// <remarks>
        /// Phase-01 trap 3. Freezing the instant a packet is lost makes every dropped input
        /// packet a visible stutter, when the overwhelmingly likely truth is that the player is
        /// still holding the same keys. Repeating forever is worse — a disconnected player
        /// would run into the horizon — so it is capped at 3 ticks (100 ms).
        /// </remarks>
        public const int MaxMissedInputTicks = 3;

        /// <summary>
        /// The most input frames one session may have applied in a single tick.
        /// </summary>
        /// <remarks>
        /// <para>
        /// <b>Why a bound on frames and not on distance.</b> The post-move clamp bounds ONE
        /// frame, and re-baselines on every call, so it could never bound a tick: a client that
        /// kept the 32-frame ring saturated had all 32 drained in one tick, each moving a full
        /// tick's length, and <c>SpeedViolations</c> stayed at zero because no individual step
        /// ever broke the limit. Clamping the tick's total displacement instead looks like the
        /// obvious fix and is wrong — it also punishes an honest client recovering from packet
        /// loss, whose bunched frames represent ticks it really did intend to move for. The
        /// integration test over an impaired link is what says so.
        /// </para>
        /// <para>
        /// What separates the two is rate, not distance: honest input arrives at one frame per
        /// tick on average, however unevenly it is delivered. So the budget refills at exactly
        /// that rate and only saves up <see cref="MaxInputBurst"/> of it. A recovering client
        /// spends the savings and catches up; a flooding one is metered to the refill rate no
        /// matter how much it sends, and its surplus stays in the ring rather than being
        /// silently dropped.
        /// </para>
        /// <para>
        /// The size matches <see cref="MaxMissedInputTicks"/> plus the tick being served, which
        /// is the largest gap the coast path already tolerates — past that the server has given
        /// up on the connection anyway.
        /// </para>
        /// </remarks>
        public const int MaxInputBurst = MaxMissedInputTicks + 1;

        /// <summary>
        /// Vets one frame and converts it to movement intent.
        /// </summary>
        /// <returns>False when the frame must be discarded.</returns>
        public static bool TryAccept(
            ClientSession session, uint frameTick, in InputFrame frame, out MoveInput input)
        {
            if (session == null) throw new ArgumentNullException(nameof(session));

            input = default;

            // Already applied, or a redundant copy.
            if (session.HasInput && !SequenceMath.IsNewer32(frameTick, session.LastProcessedInputTick))
                return false;

            if (session.HasInput
                && SequenceMath.Distance32(frameTick, session.LastProcessedInputTick) > MaxTickJump)
            {
                session.TickJumpViolations++;

                // Accept the frame but not the jump: treat it as the next tick in sequence, so
                // a client that lied about its clock gains one tick of input rather than sixty.
                session.LastProcessedInputTick = frameTick - 1;
            }

            input = Normalize(MoveInput.FromFrame(in frame));
            return true;
        }

        /// <summary>
        /// Clamps the movement axes to a unit disc.
        /// </summary>
        /// <remarks>
        /// Only shrinks. An honest client at half deflection stays at half deflection; the
        /// simulation's own normalize is what turns that into full speed, matching the shipped
        /// game.
        /// </remarks>
        public static MoveInput Normalize(in MoveInput input)
        {
            float magnitudeSquared = input.MoveX * input.MoveX + input.MoveZ * input.MoveZ;
            if (magnitudeSquared <= 1f) return input;

            float inverse = 1f / (float)Math.Sqrt(magnitudeSquared);
            return input.WithAxes(input.MoveX * inverse, input.MoveZ * inverse);
        }

        /// <summary>
        /// The furthest an actor may legitimately travel in one tick.
        /// </summary>
        /// <remarks>
        /// Built from the run speed and the jump speed — the vertical term matters, because a
        /// clamp derived from horizontal speed alone would fire on every jump and drag players
        /// back down through their own arc.
        /// </remarks>
        public static float MaxMovePerTick(float dt)
        {
            float horizontal = MovementCore.MaxHorizontalSpeed;
            float vertical   = Math.Max(MovementCore.JumpSpeed, MovementCore.StickToGroundForce);
            float combined   = (float)Math.Sqrt(horizontal * horizontal + vertical * vertical);
            return combined * SpeedTolerance * dt;
        }

        /// <summary>
        /// Enforces the distance clamp after the move has been applied, pulling the actor back
        /// along its own movement vector when it went too far.
        /// </summary>
        /// <returns>True when the clamp fired.</returns>
        public static bool ClampMovement(ClientSession session, float dt)
        {
            if (session == null) throw new ArgumentNullException(nameof(session));

            Vec3 delta = session.State.Position - session.PreviousPosition;
            float moved = delta.Magnitude;
            float limit = MaxMovePerTick(dt);

            if (moved <= limit)
            {
                session.PreviousPosition = session.State.Position;
                return false;
            }

            session.State.Position = session.PreviousPosition + delta.Normalized * limit;
            session.PreviousPosition = session.State.Position;
            session.SpeedViolations++;
            return true;
        }

        /// <summary>
        /// Applies every buffered frame for this tick, repeating the last one when input is
        /// missing, and returns how many simulation steps ran.
        /// </summary>
        /// <param name="applyMove">
        /// Moves the actor by the returned delta and writes back where it actually ended up —
        /// the collision system's job, which is why it is a callback rather than something
        /// this class does itself. In a unit test it is straight-line integration; in Unity it
        /// is <c>CharacterController.Move</c>.
        /// </param>
        /// <param name="observer">
        /// Optional. Invoked once per <b>accepted</b> frame, with the frame intact. Phase-05
        /// task 2 — the seam through which combat reaches the server at all.
        /// </param>
        public static int ApplyPendingInput(
            ClientSession session, float dt, Func<Vec3, Vec3> applyMove,
            IAcceptedFrameObserver? observer = null)
        {
            if (session == null) throw new ArgumentNullException(nameof(session));
            if (applyMove == null) throw new ArgumentNullException(nameof(applyMove));

            int steps = 0;

            // One tick's worth of budget arrives per tick, and unspent budget accumulates only
            // up to MaxInputBurst. See MaxInputBurst for why a bound on FRAMES, rather than on
            // the distance they cover, is the correct shape of this check.
            session.InputBudget = Math.Min(session.InputBudget + 1, MaxInputBurst);

            while (session.InputBudget > 0
                && session.TryDequeueInput(out uint tick, out InputFrame frame))
            {
                if (!TryAccept(session, tick, in frame, out MoveInput input)) continue;

                session.InputBudget--;

                StepOnce(session, in input, dt, applyMove);
                session.LastProcessedInputTick = tick;
                session.LastInput = input;
                session.HasInput  = true;
                session.MissedInputTicks = 0;
                steps++;

                // AFTER the move, so the shot originates from where the server actually put the
                // player this tick rather than from where they were at the top of it. At a
                // sprint that gap is about 20 cm, which is enough to change whether a shot
                // taken while rounding a corner had line of sight.
                observer?.OnAcceptedFrame(session, tick, in frame, in input);
            }

            // Ran out of budget with frames still waiting: the client is sending faster than the
            // server ticks. They stay in the ring for the next tick rather than being dropped,
            // so an honest burst is delayed and a flood is metered.
            if (session.InputBudget == 0 && session.PendingInputCount > 0) session.InputThrottleEvents++;

            if (steps > 0) return steps;

            // Nothing arrived this tick. Coast on the last known intent, briefly.
            //
            // The observer is deliberately NOT called here. Coasting repeats a movement intent
            // to cover one dropped packet; repeating a combat intent would have a player who
            // was holding the trigger when their connection hiccuped fire three free rounds
            // they never asked for — and, worse, do it from a frame the anti-cheat has already
            // graded. Movement can be replayed because it is idempotent in aggregate. A shot
            // cannot.
            if (!session.HasInput || session.MissedInputTicks >= MaxMissedInputTicks) return 0;

            StepOnce(session, in session.LastInput, dt, applyMove);
            session.MissedInputTicks++;
            return 1;
        }

        private static void StepOnce(
            ClientSession session, in MoveInput input, float dt, Func<Vec3, Vec3> applyMove)
        {
            Vec3 motion = MovementCore.Step(ref session.State, in input, dt);
            session.State.Position = applyMove(motion);
            ClampMovement(session, dt);
        }
    }
}
