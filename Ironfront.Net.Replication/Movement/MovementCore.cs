using System;

namespace Ironfront.Net.Replication.Movement
{
    /// <summary>Everything the simulation needs to know about one actor between ticks.</summary>
    public struct MoveState
    {
        public Vec3 Position;
        public Vec3 Velocity;

        /// <summary>
        /// Set by the caller from the real ground check before each step. The pure
        /// simulation cannot raycast, so grounding is an input, not an output.
        /// </summary>
        public bool IsGrounded;

        /// <summary>
        /// Set by the caller like <see cref="IsGrounded"/>: whether the body's last move ran into
        /// something at its side -- a wall, a rock, the bank where the water ends.
        /// </summary>
        public bool IsBlockedSideways;

        /// <summary>
        /// Whether a swimmer is hauling itself out up what it is pushing against
        /// (<see cref="MovementCore.ClimbOutLip"/>). In the state because the climb carries the body
        /// past the in-water test, and both sides have to agree it is still climbing.
        /// </summary>
        public bool IsClimbingOut;

        public bool IsCrouching;

        /// <summary>
        /// Whether the jump button was held at the END of the previous step. The jump is an
        /// edge, and this is the half of it the simulation has to remember — see
        /// <see cref="MovementCore.Step"/>.
        /// </summary>
        /// <remarks>
        /// <b>In the state rather than at the input source, deliberately.</b> The edge has to be
        /// computed from one contiguous frame sequence, and the two sides do not share a source:
        /// the client predicts from the frames it generates, the server replays the frames it
        /// received. Recomputing the rise here makes it the same rise on both, which is the same
        /// argument that keeps every other line of this file shared.
        /// </remarks>
        public bool JumpHeld;

        public static MoveState AtRest(Vec3 position, bool grounded = true)
            => new MoveState { Position = position, Velocity = Vec3.Zero, IsGrounded = grounded };
    }

    /// <summary>
    /// The deterministic half of character movement, ported from the game's real movement
    /// code and shared verbatim by the client's prediction and the server's authoritative
    /// simulation.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>There are no <c>if (IsClient)</c> branches here and there must never be any.</b>
    /// The moment the two sides disagree about one line of this file, client prediction
    /// mispredicts every tick and the player rubber-bands. That is the single most expensive
    /// bug class in this milestone, and a shared file with no role branches is what prevents
    /// it structurally rather than by discipline.
    /// </para>
    /// <para>
    /// <b>Where the constants come from.</b> Every value below was read out of the shipped
    /// project, not chosen. Movement is not in <c>Actor.cs</c> at all — it is in Unity
    /// Standard Assets' <c>FirstPersonController.FixedUpdate()</c>, and the speeds are
    /// <c>[SerializeField]</c> values living in <c>Assets/Prefab/Player Fps Actor.prefab</c>.
    /// See <c>docs/movement-analysis.md</c> for the full derivation with line references.
    /// </para>
    /// <para>
    /// <b>Two known, deliberate divergences from the original</b>, both documented in the
    /// analysis and both expected to show up in the shadow-comparison logs:
    /// </para>
    /// <list type="number">
    /// <item>
    /// <b>No slope projection.</b> The original projects the wish direction onto the ground
    /// normal from a <c>SphereCast</c>. On flat ground that normal is straight up and the
    /// projection is a no-op, so this port is exact there; on a slope the original follows
    /// the surface and this does not. Restoring it needs a collision query, which belongs on
    /// the Unity side of the seam, not in a netstandard library.
    /// </item>
    /// <item>
    /// <b>No collision resolution.</b> <see cref="Step"/> returns the motion delta it wants;
    /// applying it against geometry is <c>CharacterController.Move</c>'s job on both sides.
    /// This is why the method returns a delta instead of writing
    /// <see cref="MoveState.Position"/> itself.
    /// </item>
    /// </list>
    /// </remarks>
    public static class MovementCore
    {
        // ===== Ported constants =====
        // Serialized in Assets/Prefab/Player Fps Actor.prefab. Changing them here without
        // changing the prefab desynchronizes the server from what the player feels.

        /// <summary>m_WalkSpeed, m/s. Prefab line 101.</summary>
        public const float WalkSpeed = 3.5f;

        /// <summary>m_RunSpeed, m/s. Prefab line 102. Selected when the sprint button is held.</summary>
        public const float RunSpeed = 6.5f;

        /// <summary>m_JumpSpeed, m/s of instantaneous upward velocity. Prefab line 104.</summary>
        public const float JumpSpeed = 5f;

        /// <summary>
        /// m_StickToGroundForce, prefab line 105. Applied as a constant downward velocity
        /// while grounded so the controller stays pinned to the surface instead of skipping
        /// down slopes and losing its ground contact every other tick.
        /// </summary>
        public const float StickToGroundForce = 10f;

        /// <summary>m_GravityMultiplier, prefab line 106.</summary>
        public const float GravityMultiplier = 1.2f;

        /// <summary>Physics.gravity.y from ProjectSettings/DynamicsManager.asset.</summary>
        public const float BaseGravity = -9.81f;

        /// <summary>The gravity actually applied while airborne: -11.772 m/s².</summary>
        public const float Gravity = BaseGravity * GravityMultiplier;

        /// <summary>CharacterController height while standing. Prefab line 82.</summary>
        public const float StandHeight = 1.8f;

        /// <summary>
        /// CharacterController height while crouched, set by
        /// <c>FpsActorController.StartCrouch()</c>.
        /// </summary>
        public const float CrouchHeight = 0.5f;

        /// <summary>
        /// The fastest a legitimate player can move horizontally under their own power.
        /// The server's speed check is built on this, not on a re-derived number.
        /// </summary>
        public const float MaxHorizontalSpeed = RunSpeed;

        /// <summary>
        /// Chooses the speed for this tick.
        /// </summary>
        /// <remarks>
        /// <para>
        /// <b>There is no crouch speed, and that is not an oversight.</b> The phase-00 sketch
        /// assumed a <c>CROUCH_SPEED</c> of 2.0 m/s. The shipped game has no such value:
        /// <c>FpsActorController.StartCrouch()</c> only changes the CharacterController's
        /// height. Inventing a crouch speed here would make the server authoritatively slower
        /// than the client every time a player crouches, which presents as rubber-banding while
        /// crouch-walking and would have been extremely annoying to trace back to a constant
        /// nobody wrote down.
        /// </para>
        /// <para>
        /// <b>Sprint is the controller's <c>sprinting</c> FIELD, not the Sprint button.</b> This
        /// read the raw button, on the reasoning that <c>FirstPersonController.GetInput()</c>
        /// "picks between exactly two speeds on the sprint flag alone". It does — but the flag it
        /// reads is written every render frame by <c>FpsActorController.Update()</c> as
        /// <c>IsSprinting()</c>, which is <c>!Crouch() &amp;&amp; !Aiming() &amp;&amp; !IsReloading()
        /// &amp;&amp; InputSource.Sprint() &amp;&amp; !IsSeated()</c>. The shipped game therefore gives
        /// WALK speed to a player holding Sprint while aiming, crouching or reloading, and this
        /// method gave them run speed — 6.5 m/s against 3.5, on both sides at once, so it never
        /// rubber-banded and nothing caught it.
        /// </para>
        /// <para>
        /// <b>The seated term of that composite is absent here on purpose.</b> A seated body is
        /// not simulated by this file at all: <c>FpsActorController</c>'s <c>SimulationEnabled</c>
        /// is false while <c>IsSeated()</c>, so no tick reaches this line to be gated.
        /// </para>
        /// </remarks>
        public static float SpeedFor(in MoveInput input)
            => (input.Sprint && !input.Crouch && !input.Aim && !input.Reload)
                ? RunSpeed
                : WalkSpeed;

        /// <summary>
        /// Advances one tick and returns the motion the caller should feed to
        /// <c>CharacterController.Move</c>.
        /// </summary>
        /// <param name="state">
        /// Updated in place: <see cref="MoveState.Velocity"/> and
        /// <see cref="MoveState.IsCrouching"/> are written.
        /// <see cref="MoveState.Position"/> is <b>not</b> — only the collision system knows
        /// where the actor really ended up, so the caller writes it back after moving.
        /// </param>
        /// <param name="input">This tick's intent. Already dequantized.</param>
        /// <param name="dt">
        /// Seconds. Must be the same on client and server — the fixed tick interval
        /// (1/<see cref="Protocol.ProtocolConstants.SIM_TICK_RATE"/>), never a variable
        /// frame delta.
        /// </param>
        public static Vec3 Step(ref MoveState state, in MoveInput input, float dt)
        {
            bool inWater = IsInWater(state.Position.Y);
            state.IsClimbingOut = (inWater || state.IsClimbingOut) && CanClimbOut(in state, in input);
            if (inWater || state.IsClimbingOut) return Swim(ref state, in input, dt);

            float speed = SpeedFor(in input);

            Vec3 forward = ForwardFromYaw(input.YawDegrees);
            Vec3 right   = new Vec3(forward.Z, 0f, -forward.X);

            // Port note: the original builds this vector, projects it onto the ground normal
            // and then normalizes. The normalize is the part that matters and it is easy to
            // read past: it means ANY non-zero input produces FULL speed. A half-deflected
            // analog stick walks at 3.5 m/s, not 1.75. That is the shipped game's behaviour,
            // so the simulation reproduces it — and as a side effect the classic
            // moveX=moveZ=127 diagonal exploit cannot work here, because a longer input
            // vector normalizes back to the same unit length. The server still normalizes the
            // raw axes separately (InputAuthority) rather than relying on this.
            Vec3 wish = (forward * input.MoveZ + right * input.MoveX).Normalized;

            Vec3 velocity = state.Velocity;
            velocity = new Vec3(wish.X * speed, velocity.Y, wish.Z * speed);

            if (state.IsGrounded)
            {
                velocity = new Vec3(velocity.X, -StickToGroundForce, velocity.Z);

                // The jump is an EDGE, not a level. Holding the key in the shipped game gives one
                // jump: FirstPersonController latches it on the button's down-transition and
                // FixedUpdate consumes it -- `if (!m_Jump) m_Jump = GetButtonDown("Jump")`.
                // Re-applying JumpSpeed on every grounded tick instead turns a held key into a
                // hop, which is what reading the button as a level here did.
                //
                // The rise is computed from the previous frame's own bit rather than from a
                // "jump pressed" flag the caller sets, because the caller differs on the two
                // sides and this file must not: the client predicts from frames it generated and
                // the server replays frames it received.
                if (input.Jump && !state.JumpHeld)
                    velocity = new Vec3(velocity.X, JumpSpeed, velocity.Z);
            }
            else
            {
                velocity = new Vec3(velocity.X, velocity.Y + Gravity * dt, velocity.Z);
            }

            state.Velocity    = velocity;
            state.IsCrouching = input.Crouch;
            state.JumpHeld    = input.Jump;

            return velocity * dt;
        }

        // ===== Water =====

        /// <summary>
        /// The surface of the loaded map's water, or negative infinity on a map with none.
        /// </summary>
        /// <remarks>
        /// <para>
        /// <b>Set by the map, on both sides, before anybody moves.</b> <c>WaterLevel.Awake</c>
        /// publishes its own height here, on the client and on the game server alike, so the
        /// prediction and the authority swim against the same surface -- a water line the two
        /// disagreed about would mispredict every tick of every swim.
        /// </para>
        /// <para>
        /// A process runs one map at a time (a game server hosts one match; a client plays one),
        /// which is what lets this be a single value rather than an input every call site has to
        /// thread through.
        /// </para>
        /// </remarks>
        public static float WaterHeight { get; set; } = float.NegativeInfinity;

        /// <summary>
        /// How far above the capsule's centre the swim test samples: the original's
        /// <c>Actor.Update</c> swims once <c>CenterPosition() + 0.5</c> is under the surface,
        /// which on a standing body is about 1.4 m of water.
        /// </summary>
        public const float SwimSampleAbove = 0.5f;

        /// <summary>
        /// Where a swimmer's capsule centre floats: this far under the surface. Deeper than
        /// <see cref="SwimSampleAbove"/>, so a body that has floated up is still swimming.
        /// </summary>
        public const float SwimFloatDepth = 0.7f;

        /// <summary>
        /// Swimming speed, m/s: the original's, where <c>SwimInput() * 30 * 0.8</c> pulls the head
        /// through water with a drag of 10 and so settles at 2.4 m/s.
        /// </summary>
        public const float SwimSpeed = 2.4f;

        /// <summary>How fast a swimmer closes on its floating depth, per second of the gap.</summary>
        public const float SwimLiftRate = 3f;

        /// <summary>The fastest a swimmer rises or sinks toward the floating depth, m/s.</summary>
        public const float MaxSwimVerticalSpeed = 2f;

        /// <summary>
        /// How far over the surface a swimmer can lift its feet climbing out up what it is pushing
        /// against; the capsule's own step then carries it onto a lip about twice this high.
        /// </summary>
        public const float ClimbOutLip = 0.3f;

        /// <summary>How fast a swimmer climbs out, m/s.</summary>
        public const float ClimbOutSpeed = 2f;

        /// <summary>
        /// Whether a swimmer pushing against something is still low enough to haul itself up it:
        /// moving, blocked at its side, and its feet no more than <see cref="ClimbOutLip"/> over
        /// the surface.
        /// </summary>
        /// <remarks>
        /// <b>Live test 2026-09-30, Island's west shore.</b> The capsule floats with its feet 1.6 m
        /// under the surface, and there the bottom rises from that depth to the beach inside two
        /// metres at 43 to 51 degrees -- steeper than the 45 degrees the capsule can walk. The
        /// swimmer pressed against the bank until its breath ran out. The original's swimmer is a
        /// ragdoll shoved up any bank by its swim force; this is that shove for a capsule.
        /// </remarks>
        private static bool CanClimbOut(in MoveState state, in MoveInput input)
            => state.IsBlockedSideways
               && (input.MoveX != 0f || input.MoveZ != 0f)
               && state.Position.Y - StandHeight * 0.5f <= WaterHeight + ClimbOutLip;

        /// <summary>
        /// Whether a body whose capsule centre is at <paramref name="centreY"/> is in water: the
        /// same half metre over the centre the original tests, against <see cref="WaterHeight"/>.
        /// </summary>
        public static bool IsInWater(float centreY) => centreY + SwimSampleAbove <= WaterHeight;

        /// <summary>
        /// A tick in water: no gravity and no jump; the body floats up to
        /// <see cref="SwimFloatDepth"/> and swims at <see cref="SwimSpeed"/> where it looks.
        /// </summary>
        /// <remarks>
        /// <para>
        /// <b>Players could not swim before 2026-09-29.</b> The original swims by ragdoll --
        /// <c>Actor.Update</c> fells a body the moment it is in water and buoys its hips and head --
        /// and a networked player is never ragdolled, so this file, which is all a player's body
        /// moves by, walked it along the bottom instead. This is that swim as a capsule: the same
        /// test for being in water, the same speed, and the body held at the surface.
        /// </para>
        /// <para>
        /// <b>Leaving the water is the same test failing.</b> Swimming into shallows, the bottom
        /// lifts the capsule until its centre is more than <see cref="SwimSampleAbove"/> under the
        /// surface no longer, and the next tick walks. A crouch has no meaning in water and is
        /// dropped, so the capsule does not shrink under a swimmer.
        /// </para>
        /// <para>
        /// <b>Or climbing out.</b> Where the bottom rises too steeply to walk up, a swimmer pushing
        /// against it rises along it instead, and keeps rising past the in-water test until its
        /// feet are <see cref="ClimbOutLip"/> over the surface (<see cref="CanClimbOut"/>).
        /// </para>
        /// </remarks>
        private static Vec3 Swim(ref MoveState state, in MoveInput input, float dt)
        {
            Vec3 forward = ForwardFromYaw(input.YawDegrees);
            Vec3 right   = new Vec3(forward.Z, 0f, -forward.X);
            Vec3 wish    = (forward * input.MoveZ + right * input.MoveX).Normalized;

            float gap = WaterHeight - SwimFloatDepth - state.Position.Y;
            float rise = gap * SwimLiftRate;
            if (rise > MaxSwimVerticalSpeed) rise = MaxSwimVerticalSpeed;
            else if (rise < -MaxSwimVerticalSpeed) rise = -MaxSwimVerticalSpeed;
            if (state.IsClimbingOut) rise = ClimbOutSpeed;

            Vec3 velocity = new Vec3(wish.X * SwimSpeed, rise, wish.Z * SwimSpeed);

            state.Velocity    = velocity;
            state.IsCrouching = false;
            state.JumpHeld    = input.Jump;

            return velocity * dt;
        }

        /// <summary>
        /// The flattened forward vector for a yaw in degrees, matching Unity's left-handed
        /// Y-up convention: yaw 0 faces +Z, yaw 90 faces +X.
        /// </summary>
        public static Vec3 ForwardFromYaw(float yawDegrees)
        {
            float radians = yawDegrees * (float)(Math.PI / 180.0);
            return new Vec3((float)Math.Sin(radians), 0f, (float)Math.Cos(radians));
        }

        /// <summary>
        /// The CharacterController height for a stance. Exposed so the server's hitbox
        /// history and the client agree on how tall a crouched actor is.
        /// </summary>
        public static float HeightFor(bool crouching) => crouching ? CrouchHeight : StandHeight;
    }
}
