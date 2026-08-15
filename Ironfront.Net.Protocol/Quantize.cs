using System;

namespace Ironfront.Net.Protocol
{
    /// <summary>
    /// Mandatory shared quantization constants and pack/unpack routines.
    /// Mirrors protocol-spec.md section 4.4.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This is the easiest place to get wrong and the worst place to get it wrong. If the
    /// client uses POS_RANGE = 2048 while the server uses 4096, characters end up at
    /// double the wrong position — with no runtime error to point at it.
    /// </para>
    /// <para>
    /// The spec's formulas use UnityEngine.Mathf.Clamp; this library must never reference
    /// UnityEngine (architecture.md section 5.1), so the clamps below are the plain
    /// System.Math equivalents. The arithmetic is otherwise byte-for-byte the spec's.
    /// </para>
    /// </remarks>
    public static class Quantize
    {
        // ===== POSITION =====
        // The current map fits inside a +/-2048 m box. An i16 has 65536 levels.
        public const float POS_MIN   = -2048f;
        public const float POS_MAX   =  2048f;
        public const float POS_RANGE = POS_MAX - POS_MIN;        // 4096
        // Resolution = 4096 / 65536 = 0.0625 m = 6.25 cm. Good enough for an FPS.

        // ===== ANGLES =====
        public const float YAW_SCALE   = 65536f / 360f;    // u16
        public const float PITCH_SCALE = 16384f / 90f;     // i16, using +/-16384
        // Yaw resolution   = 360/65536 = 0.0055 degrees
        // Pitch resolution =  90/16384 = 0.0055 degrees

        // ===== VELOCITY =====
        public const float VEL_MAX   = 64f;                // m/s, enough for everything but aircraft
        public const float VEL_SCALE = 127f / VEL_MAX;     // i8
        // Resolution = 64/127 = 0.5 m/s — only used for extrapolation, which is fine

        // ===== HEALTH =====
        // health is a u8 directly in 0..100, no scaling needed
        public const byte HEALTH_MAX = 100;

        // ===== INPUT AXES =====
        // moveX / moveZ travel as i8 in -127..127 (protocol-spec.md section 4.2)
        public const float MOVE_AXIS_SCALE = 127f;

        // ---------------------------------------------------------------- position

        public static short PackPos(float v)
        {
            float t = Clamp01((v - POS_MIN) / POS_RANGE);
            return (short)(t * 65535f - 32768f);
        }

        public static float UnpackPos(short q)
            => ((q + 32768f) / 65535f) * POS_RANGE + POS_MIN;

        // ------------------------------------------------------------------- angles

        /// <summary>Packs a yaw in degrees into a u16. Input is wrapped into [0, 360).</summary>
        public static ushort PackYaw(float degrees)
        {
            float wrapped = degrees % 360f;
            if (wrapped < 0f) wrapped += 360f;
            // 359.999.. * YAW_SCALE can round up to 65536, which wraps to 0 — that is the
            // correct answer for a full turn, but cast through a long first so the
            // intermediate never overflows.
            long q = (long)(wrapped * YAW_SCALE + 0.5f);
            return (ushort)(q & 0xFFFF);
        }

        public static float UnpackYaw(ushort q) => q / YAW_SCALE;

        /// <summary>Packs a pitch in degrees into an i16. Input is clamped to [-90, 90].</summary>
        public static short PackPitch(float degrees)
        {
            float clamped = Clamp(degrees, -90f, 90f);
            return (short)(clamped * PITCH_SCALE);
        }

        public static float UnpackPitch(short q) => q / PITCH_SCALE;

        /// <summary>
        /// Packs a pitch into the i8 slot used by the snapshot rotation field
        /// (protocol-spec.md section 4.3: "rotation u16 yaw + i8 pitch").
        /// </summary>
        public static sbyte PackPitchByte(float degrees)
        {
            float clamped = Clamp(degrees, -90f, 90f);
            return (sbyte)(clamped * (127f / 90f));
        }

        public static float UnpackPitchByte(sbyte q) => q / (127f / 90f);

        // ----------------------------------------------------------------- velocity

        public static sbyte PackVel(float v)
        {
            float clamped = Clamp(v, -VEL_MAX, VEL_MAX);
            return (sbyte)(clamped * VEL_SCALE);
        }

        public static float UnpackVel(sbyte q) => q / VEL_SCALE;

        /// <summary>
        /// Packs a velocity into the i16 slot S_DEATH and S_WEAPON_FIRE use for their force and
        /// direction vectors (protocol-spec.md §§ 4.6 and 4.7).
        /// </summary>
        /// <remarks>
        /// <b>A wider slot at the same scale, not a second scale.</b> The snapshot's velocity is
        /// an i8, which saturates at <see cref="VEL_MAX"/>; a ragdoll impulse is routinely
        /// several times that, and reusing <see cref="PackVel"/> here would clamp every kill's
        /// force to 64 m/s and make heavy weapons feel identical to light ones. Sharing
        /// <see cref="VEL_SCALE"/> keeps one conversion factor for both widths, so a reader that
        /// knows the scale can decode either.
        /// </remarks>
        public static short PackVel16(float v)
        {
            float scaled = v * VEL_SCALE;

            if (scaled >= short.MaxValue) return short.MaxValue;
            if (scaled <= short.MinValue) return short.MinValue;

            return (short)scaled;
        }

        /// <summary>Inverse of <see cref="PackVel16"/>.</summary>
        public static float UnpackVel16(short q) => q / VEL_SCALE;

        // -------------------------------------------------------------- input axis

        /// <summary>Packs a -1..1 movement axis into an i8 (-127..127).</summary>
        public static sbyte PackMoveAxis(float v)
        {
            float clamped = Clamp(v, -1f, 1f);
            return (sbyte)(clamped * MOVE_AXIS_SCALE);
        }

        public static float UnpackMoveAxis(sbyte q) => q / MOVE_AXIS_SCALE;

        // ------------------------------------------------------------------ helpers

        private static float Clamp01(float v) => v < 0f ? 0f : (v > 1f ? 1f : v);

        private static float Clamp(float v, float min, float max)
            => v < min ? min : (v > max ? max : v);
    }
}
