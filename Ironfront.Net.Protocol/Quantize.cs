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
        //
        // A 4096 m window, placed where playable content actually lives. An i16 has 65536
        // levels, so the WIDTH is what buys resolution and the PLACEMENT is free:
        //   Resolution = 4096 / 65536 = 0.0625 m = 6.25 cm. Good enough for an FPS.
        //
        // X-53. This used to read "the current map fits inside a +/-2048 m box", and that was
        // false the whole time it was written down: Dustbowl's authored play volume runs
        // (650, -50, 620) .. (2350, 650, 2220), so 302 m of x and 172 m of z sat outside a
        // symmetric window -- including the Oasis capture point at x = 2085.6, which is team 0's
        // opening base. Every body out there encoded to exactly 2048.00 on every client, ~37 m
        // from where the server had it, over terrain that is not there. Measured on a real run:
        // both team-0 clients spawned at x = 2084-2086 and fell.
        //
        // WIDENING was the obvious fix and is the wrong one. +/-4096 would double the window and
        // halve the resolution to 12.5 cm for every actor on every map, forever, to buy negative
        // space no map uses. SHIFTING costs nothing: same width, same 6.25 cm, same 6 bytes per
        // position on the wire.
        //
        // The split is a rule rather than a fit to one map: 1024 m of negative headroom for a map
        // built around the origin, 3072 m of positive for a map built in positive space (which is
        // how Ravenfield's are authored). Dustbowl's far corner sits 722 m inside the ceiling.
        // A map that needs more negative space than this wants a per-map origin in the protocol,
        // not a wider window -- and it will say so out loud rather than silently, because
        // LevelBounds.SetupBounds checks the authored volume against these constants on load.
        public const float POS_MIN   = -1024f;
        public const float POS_MAX   =  3072f;
        public const float POS_RANGE = POS_MAX - POS_MIN;        // 4096, unchanged

        // ===== ANGLES =====
        public const float YAW_SCALE   = 65536f / 360f;    // u16
        public const float PITCH_SCALE = 16384f / 90f;     // i16, using +/-16384
        // Yaw resolution   = 360/65536 = 0.0055 degrees
        // Pitch resolution =  90/16384 = 0.0055 degrees

        // ===== VELOCITY =====
        public const float VEL_MAX   = 64f;                // m/s, enough for everything but aircraft
        public const float VEL_SCALE = 127f / VEL_MAX;     // i8

        /// <summary>Angular-velocity saturation, rad/s. ~1.3 rev/s. Added v3 for vehicles.</summary>
        public const float ANGVEL_MAX   = 8f;
        /// <summary>i8 scale for angular velocity. Resolution = 8/127 = 0.063 rad/s.</summary>
        public const float ANGVEL_SCALE = 127f / ANGVEL_MAX;
        // Resolution = 64/127 = 0.5 m/s — only used for extrapolation, which is fine

        // ===== ROTATION (full, smallest-three) =====
        // A unit quaternion's largest component is at least 0.5, so the other three are each
        // inside +/-1/sqrt(2). Sending only those three at 10 bits apiece, plus a 2-bit index
        // of the one that was dropped, is a full rotation in 32 bits.
        public const float QUAT_MIN    = -0.70710678f;          // -1/sqrt(2)
        public const float QUAT_RANGE  =  1.41421356f;          // 2/sqrt(2)
        /// <summary>Steps in the 10-bit component field. 1023, so both endpoints are exact.</summary>
        public const int   QUAT_LEVELS = 1023;
        // Step = 1.41421356 / 1023 = 1.38e-3, so each transmitted component is off by at most
        // half a step. That is NOT the whole error, and reading it as though it were is how the
        // 0.16 degrees this comment used to claim was arrived at.
        //
        // The dropped component is reconstructed as m = sqrt(1 - a^2 - b^2 - c^2), so its error
        // is dm = -(a*da + b*db + c*dc) / m and it grows as m shrinks. m is smallest at the
        // four-way tie (0.5, 0.5, 0.5, 0.5), where it is exactly 0.5 and the three transmitted
        // components are at their largest simultaneously:
        //
        //     |dm|   <= 3 * 0.5 * 6.912e-4 / 0.5 = 2.074e-3
        //     |dq|   ~  sqrt(3 * (6.912e-4)^2 + (2.074e-3)^2) = 2.394e-3
        //     angle  ~  2 * |dq| = 4.79e-3 rad = 0.274 degrees
        //
        // Measured worst case is 0.271 degrees, against a 0.3 degree budget: a 2-million-sample
        // uniform sweep finds 0.243, a dense grid over the three transmitted components finds
        // 0.241, and only a deliberate search of the tie corner finds 0.271. A random sweep of
        // 10^4 rotations reports about 0.19 and looks like a pass — which is exactly why the
        // conformance test searches the corner instead of sampling and hoping.
        //
        // 0.27 degrees on a vehicle at 20 Hz is well below anything visible, and finer than the
        // 0.5 m/s the same stream already accepts for velocity.

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

        /// <summary>
        /// True when <paramref name="v"/> is outside the representable range, so
        /// <see cref="PackPos"/> will return a boundary code that does not describe it.
        /// </summary>
        /// <remarks>
        /// <para>
        /// <b>The clamp in <see cref="PackPos"/> is silent, and that is the defect underneath
        /// X-39.</b> An entity east of <see cref="POS_MAX"/> does not decode to something
        /// obviously wrong — it decodes to a perfectly plausible position exactly on the
        /// boundary, and two entities 50 m apart out there decode to the same one. Nothing in
        /// the snapshot, the report or the log said so; the only way it was ever found was
        /// noticing that the quantized value 32767 is unreachable by any other means.
        /// </para>
        /// <para>
        /// Exclusive at the boundary: <see cref="POS_MAX"/> itself round-trips, so a body
        /// resting exactly on the edge is representable and is not reported.
        /// </para>
        /// </remarks>
        public static bool PositionSaturates(float v)
            => !(v >= POS_MIN && v <= POS_MAX);   // written this way so NaN reports as saturating

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

        // ---------------------------------------------------------- angular velocity

        /// <summary>
        /// Packs a body angular velocity component, in radians per second, into the i8 slot the
        /// vehicle snapshot entry gives it (protocol-spec.md § 4.10, mask bit 3).
        /// </summary>
        /// <remarks>
        /// <para>
        /// <b>A separate scale from <see cref="VEL_SCALE"/>, because the units are different.</b>
        /// Sharing the linear scale would put the saturation point at 64 rad/s — ten revolutions
        /// a second — so every real rotation a vehicle performs would live in the bottom two or
        /// three codes and quantize to nothing. <see cref="ANGVEL_MAX"/> = 8 rad/s is about
        /// 1.3 rev/s, past anything a car spinning out or a helicopter yawing hard produces, and
        /// leaves 0.063 rad/s of resolution.
        /// </para>
        /// <para>
        /// <b>Saturating rather than wrapping.</b> A cast of an out-of-range float to
        /// <c>sbyte</c> is implementation-defined and in practice wraps, which turns a violent
        /// spin into a slow counter-rotation on every client — the one failure mode that looks
        /// like a physics bug rather than a codec bug. The clamp is the same shape
        /// <see cref="PackVel"/> uses.
        /// </para>
        /// </remarks>
        public static sbyte PackAngVel(float radiansPerSecond)
        {
            float clamped = Clamp(radiansPerSecond, -ANGVEL_MAX, ANGVEL_MAX);
            return (sbyte)(clamped * ANGVEL_SCALE);
        }

        /// <summary>Inverse of <see cref="PackAngVel"/>.</summary>
        public static float UnpackAngVel(sbyte q) => q / ANGVEL_SCALE;

        // -------------------------------------------------------------- input axis

        /// <summary>Packs a -1..1 movement axis into an i8 (-127..127).</summary>
        public static sbyte PackMoveAxis(float v)
        {
            float clamped = Clamp(v, -1f, 1f);
            return (sbyte)(clamped * MOVE_AXIS_SCALE);
        }

        public static float UnpackMoveAxis(sbyte q) => q / MOVE_AXIS_SCALE;

        // ------------------------------------------------------------------ rotation

        /// <summary>
        /// Packs a unit quaternion into 32 bits: the 2-bit index of its largest-magnitude
        /// component, then the other three in source order at 10 bits each.
        /// </summary>
        /// <remarks>
        /// <para>
        /// Layout, high bits first: <c>[31:30]</c> largest index (0=x, 1=y, 2=z, 3=w),
        /// <c>[29:20]</c>, <c>[19:10]</c>, <c>[9:0]</c> the remaining three, each an unsigned
        /// quantization of <see cref="QUAT_MIN"/>..<c>-QUAT_MIN</c>. The dropped component is
        /// rebuilt on unpack as <c>sqrt(1 - a^2 - b^2 - c^2)</c>.
        /// </para>
        /// <para>
        /// <b>The sign is canonicalized before packing.</b> <c>q</c> and <c>-q</c> are the same
        /// rotation, so the largest component is forced positive and the reconstructed sign is
        /// therefore always <c>+</c>. Without this, half of all rotations would decode
        /// mirrored — and they would decode as perfectly valid quaternions while doing it,
        /// which is why it is done here rather than checked for later.
        /// </para>
        /// </remarks>
        public static uint PackQuat(float x, float y, float z, float w)
        {
            float ax = x < 0f ? -x : x;
            float ay = y < 0f ? -y : y;
            float az = z < 0f ? -z : z;
            float aw = w < 0f ? -w : w;

            int largest = 0;
            float largestMagnitude = ax;
            if (ay > largestMagnitude) { largest = 1; largestMagnitude = ay; }
            if (az > largestMagnitude) { largest = 2; largestMagnitude = az; }
            if (aw > largestMagnitude) { largest = 3; largestMagnitude = aw; }

            float largestValue = largest == 0 ? x : largest == 1 ? y : largest == 2 ? z : w;
            if (largestValue < 0f) { x = -x; y = -y; z = -z; w = -w; }

            float a, b, c;
            switch (largest)
            {
                case 0:  a = y; b = z; c = w; break;
                case 1:  a = x; b = z; c = w; break;
                case 2:  a = x; b = y; c = w; break;
                default: a = x; b = y; c = z; break;
            }

            return ((uint)largest << 30)
                 | (PackQuatComponent(a) << 20)
                 | (PackQuatComponent(b) << 10)
                 |  PackQuatComponent(c);
        }

        /// <summary>
        /// Reverses <see cref="PackQuat"/>. Defined for every 32-bit input, including ones no
        /// encoder would ever produce.
        /// </summary>
        /// <remarks>
        /// <para>
        /// <b>The radical is clamped at zero.</b> 10-bit round-off — and any hostile input,
        /// <c>0xFFFFFFFF</c> being the obvious one — can push <c>1 - a^2 - b^2 - c^2</c>
        /// fractionally or wildly negative. <c>sqrt</c> of that is <c>NaN</c>, and a
        /// <c>NaN</c> quaternion assigned to a Unity transform is a vehicle that silently
        /// vanishes rather than an exception anybody can trace.
        /// </para>
        /// <para>
        /// <b>The result is renormalized.</b> Quantization leaves the length off unit by up to
        /// ~0.1%. Unity tolerates that; three frames of interpolated blending against it drift.
        /// </para>
        /// </remarks>
        public static void UnpackQuat(
            uint packed, out float x, out float y, out float z, out float w)
        {
            int largest = (int)(packed >> 30);

            float a = UnpackQuatComponent((packed >> 20) & 0x3FFu);
            float b = UnpackQuatComponent((packed >> 10) & 0x3FFu);
            float c = UnpackQuatComponent(packed & 0x3FFu);

            float remainder = 1f - (a * a + b * b + c * c);
            if (remainder < 0f) remainder = 0f;
            float largestValue = (float)Math.Sqrt(remainder);

            switch (largest)
            {
                case 0:  x = largestValue; y = a; z = b; w = c; break;
                case 1:  x = a; y = largestValue; z = b; w = c; break;
                case 2:  x = a; y = b; z = largestValue; w = c; break;
                default: x = a; y = b; z = c; w = largestValue; break;
            }

            float length = (float)Math.Sqrt(x * x + y * y + z * z + w * w);
            if (length > 1e-6f)
            {
                float inverse = 1f / length;
                x *= inverse; y *= inverse; z *= inverse; w *= inverse;
            }
            else
            {
                // Only reachable from bytes no encoder produces. Identity is the one answer
                // that is always a legal rotation.
                x = 0f; y = 0f; z = 0f; w = 1f;
            }
        }

        private static uint PackQuatComponent(float v)
        {
            float t = Clamp01((v - QUAT_MIN) / QUAT_RANGE);
            return (uint)(t * QUAT_LEVELS + 0.5f);
        }

        private static float UnpackQuatComponent(uint q)
            => (q / (float)QUAT_LEVELS) * QUAT_RANGE + QUAT_MIN;

        // ------------------------------------------------------------------ helpers

        private static float Clamp01(float v) => v < 0f ? 0f : (v > 1f ? 1f : v);

        private static float Clamp(float v, float min, float max)
            => v < min ? min : (v > max ? max : v);
    }
}
