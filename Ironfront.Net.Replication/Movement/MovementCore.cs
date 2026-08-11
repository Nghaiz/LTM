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

        public bool IsCrouching;

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
        /// <b>There is no crouch speed, and that is not an oversight.</b> The phase-00 sketch
        /// assumed a <c>CROUCH_SPEED</c> of 2.0 m/s. The shipped game has no such value:
        /// <c>FpsActorController.StartCrouch()</c> only changes the CharacterController's
        /// height, and <c>FirstPersonController.GetInput()</c> picks between exactly two
        /// speeds on the sprint flag alone. Inventing a crouch speed here would make the
        /// server authoritatively slower than the client every time a player crouches, which
        /// presents as rubber-banding while crouch-walking and would have been extremely
        /// annoying to trace back to a constant nobody wrote down.
        /// </remarks>
        public static float SpeedFor(in MoveInput input) => input.Sprint ? RunSpeed : WalkSpeed;

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

                if (input.Jump)
                    velocity = new Vec3(velocity.X, JumpSpeed, velocity.Z);
            }
            else
            {
                velocity = new Vec3(velocity.X, velocity.Y + Gravity * dt, velocity.Z);
            }

            state.Velocity    = velocity;
            state.IsCrouching = input.Crouch;

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
