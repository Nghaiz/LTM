using Ironfront.Net.Protocol;

namespace Ironfront.Net.Replication.Vehicles
{
    /// <summary>
    /// Packs and unpacks the fixed 2-byte subtype tail of a vehicle snapshot entry
    /// (protocol-spec.md § 4.10).
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Fixed width is the whole point, and it is why this is a class rather than four ad-hoc
    /// casts at the call sites.</b> A variable-width tail would make the stream unparseable by
    /// any decoder that missed the spawn: one lost type mapping and every <i>subsequent</i>
    /// entry in the same datagram misaligns. Every method here writes exactly two bytes and
    /// every reader consumes exactly two, whatever the kind — so an unknown
    /// <see cref="VehicleKind"/> costs two skipped bytes and nothing else.
    /// </para>
    /// <para>
    /// <b>Engine-free even though only Unity calls it.</b> The choice of <i>which number</i>
    /// goes in the tail belongs to the concrete vehicle; the choice of <i>how it is encoded</i>
    /// is protocol business and is exactly the kind of thing that goes silently wrong — a
    /// helicopter's rotor speed is a normalized <c>u16</c> split across two bytes, and getting
    /// the byte order backwards produces a rotor that reads as spinning at some other plausible
    /// speed. Here it is one function with a test.
    /// </para>
    /// </remarks>
    public static class VehicleSubtypeTail
    {
        /// <summary>Steering saturation in degrees. An i8 covers ±127, so no scale is needed.</summary>
        public const float SteerAngleMaxDegrees = 127f;

        /// <summary>
        /// Car and Boat: <c>steerAngle</c> (i8 degrees) and <c>surfaceFriction</c>
        /// (u8, 0..255 -> 0..1).
        /// </summary>
        public static void PackSteered(
            float steerAngleDegrees, float surfaceFriction01, out byte a, out byte b)
        {
            a = unchecked((byte)PackSteerAngle(steerAngleDegrees));
            b = PackUnitByte(surfaceFriction01);
        }

        /// <summary>Tank: <c>steerAngle</c> (i8 degrees) and <c>currentMuzzle</c> (u8 index).</summary>
        /// <remarks>
        /// <c>currentMuzzle</c> is captured from V4 onward but nothing writes it authoritatively
        /// until V6 owns mounted-weapon aim and fire. Sending a zero is honest — the client's
        /// muzzle index is simply the one it was told, which today is the default.
        /// </remarks>
        public static void PackTank(
            float steerAngleDegrees, byte currentMuzzle, out byte a, out byte b)
        {
            a = unchecked((byte)PackSteerAngle(steerAngleDegrees));
            b = currentMuzzle;
        }

        /// <summary>
        /// Helicopter: <c>rotorSpeed</c> as a normalized <c>u16</c>, low byte then high byte.
        /// </summary>
        /// <remarks>
        /// Little-endian across the two tail bytes, matching <see cref="Endian"/> and every
        /// other multi-byte field in the protocol. The tail is the one place a reader cannot
        /// lean on <c>SpanReader</c> to get this right, because the two bytes are declared
        /// separately — hence one function rather than two shifts at the call site.
        /// </remarks>
        public static void PackHelicopter(float rotorSpeed01, out byte a, out byte b)
        {
            ushort packed = PackUnitU16(rotorSpeed01);
            a = (byte)(packed & 0xFF);
            b = (byte)(packed >> 8);
        }

        /// <summary>
        /// Folds a received muzzle index into the range this peer's prefab actually has. V6 task 4.
        /// </summary>
        /// <remarks>
        /// <para>
        /// <b>The receiver folds; the sender never does.</b> <c>muzzles.Length</c> is a per-prefab
        /// authored value, so a client running a revision with fewer barrels than the server's
        /// would index past the end of the array and throw <i>inside the render path</i> — a
        /// content mismatch presenting as a crash. Folding costs one modulo and turns it into a
        /// wrong-but-harmless muzzle choice, which is a thing somebody can notice and report.
        /// </para>
        /// <para>
        /// Here rather than in the Unity component because it is protocol business — the same
        /// argument this class makes for owning the helicopter's byte order — and because a fold
        /// living in a <c>MonoBehaviour</c> is a fold no test in CI can reach.
        /// </para>
        /// </remarks>
        /// <param name="index">The index as received.</param>
        /// <param name="muzzleCount">Barrels this peer's prefab has. Zero or negative yields 0.</param>
        public static byte FoldMuzzleIndex(byte index, int muzzleCount)
            => muzzleCount > 0 ? (byte)(index % muzzleCount) : (byte)0;

        /// <summary>Reverses <see cref="PackSteered"/> / <see cref="PackTank"/>'s first byte.</summary>
        public static float UnpackSteerAngle(byte a) => unchecked((sbyte)a);

        /// <summary>Reverses a <c>u8</c> 0..1 field.</summary>
        public static float UnpackUnitByte(byte b) => b / 255f;

        /// <summary>Reverses <see cref="PackHelicopter"/>.</summary>
        public static float UnpackHelicopter(byte a, byte b)
            => (ushort)(a | (b << 8)) / 65535f;

        private static sbyte PackSteerAngle(float degrees)
        {
            if (degrees >= SteerAngleMaxDegrees) return sbyte.MaxValue;
            if (degrees <= -SteerAngleMaxDegrees) return sbyte.MinValue;
            return (sbyte)degrees;
        }

        private static byte PackUnitByte(float v)
        {
            if (v <= 0f) return 0;
            if (v >= 1f) return 255;
            return (byte)(v * 255f + 0.5f);
        }

        private static ushort PackUnitU16(float v)
        {
            if (v <= 0f) return 0;
            if (v >= 1f) return 65535;
            return (ushort)(v * 65535f + 0.5f);
        }
    }
}
