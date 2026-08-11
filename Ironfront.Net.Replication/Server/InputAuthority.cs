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
        public static int ApplyPendingInput(
            ClientSession session, float dt, Func<Vec3, Vec3> applyMove)
        {
            if (session == null) throw new ArgumentNullException(nameof(session));
            if (applyMove == null) throw new ArgumentNullException(nameof(applyMove));

            int steps = 0;

            while (session.TryDequeueInput(out uint tick, out InputFrame frame))
            {
                if (!TryAccept(session, tick, in frame, out MoveInput input)) continue;

                StepOnce(session, in input, dt, applyMove);
                session.LastProcessedInputTick = tick;
                session.LastInput = input;
                session.HasInput  = true;
                session.MissedInputTicks = 0;
                steps++;
            }

            if (steps > 0) return steps;

            // Nothing arrived this tick. Coast on the last known intent, briefly.
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
