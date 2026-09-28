using System.Globalization;
using Ironfront.Net.Protocol;
using Ironfront.Net.Replication.Movement;

namespace Ironfront.Net.Replication.Vehicles
{
    /// <summary>
    /// The one line each vehicle emits about the state it was born in, and the budget that
    /// keeps a server replacing a vehicle every few seconds for days from drowning in it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>This log exists to settle an argument, not to diagnose one.</b> Players report smoke
    /// on freshly spawned vehicles. Protocol 10 § 8.3 and § 16 say the same thing from both
    /// ends: if the server's first state is full health with no flags, the smoke is a client
    /// particle bug and the evidence goes to the client side — and lowering a vehicle's health
    /// to make the particles stop is falsifying the instrument to match the complaint. So the
    /// line is emitted BEFORE anything can have damaged the vehicle, and
    /// <see cref="IsFreshlySpawned"/> states the invariant the line is supposed to show.
    /// </para>
    /// <para>
    /// <b>Engine-free, like every other decision in this library.</b> There is no logger here;
    /// the caller writes the string. <c>VehicleSpawner</c> is the one place that knows which
    /// pad produced the vehicle, which is the field — <c>spawner=</c> — that makes the line
    /// worth reading when a single map has fourteen of them.
    /// </para>
    /// <para>
    /// <b>The budget counts what it dropped.</b> A rate limiter that silently discards turns a
    /// quiet log into evidence of a quiet server, which is the same instrument failure the line
    /// is here to avoid one layer up. <see cref="SuppressedSinceLastLine"/> is carried into the
    /// next line that does get through.
    /// </para>
    /// </remarks>
    public sealed class VehicleSpawnStateLog
    {
        /// <summary>Lines allowed per <see cref="WindowSeconds"/>.</summary>
        /// <remarks>
        /// Eight rather than one: a world reset re-arms every pad at once, and the opening wave
        /// of a round is exactly the moment somebody reading this file wants to see all of it.
        /// </remarks>
        public const int MaxLinesPerWindow = 8;

        /// <summary>How long the budget above covers.</summary>
        public const float WindowSeconds = 10f;

        private float _windowEndsAt;
        private int _linesThisWindow;

        /// <summary>Lines dropped by the budget since the last one that got through.</summary>
        public int SuppressedSinceLastLine { get; private set; }

        /// <summary>
        /// The invariant protocol 10 § 8.3 requires immediately before <c>S_VEHICLE_SPAWN</c>
        /// and the first full vehicle snapshot.
        /// </summary>
        /// <remarks>
        /// Health is checked BOTH as the server's float and as the wire's <c>u8</c>. They can
        /// disagree: a hull one part in five hundred below full still normalizes to 255, so the
        /// byte alone would call a vehicle that has already taken a scrape "fresh", and the
        /// float alone would say nothing about what the client is actually going to receive.
        /// </remarks>
        public static bool IsFreshlySpawned(
            float health, float maxHealth, VehicleStateFlags flags)
            => maxHealth > 0f
               && health >= maxHealth
               && Normalize(health, maxHealth) == Quantize.HEALTH_MAX
               && flags == VehicleStateFlags.None;

        /// <summary>Health as the wire's <c>u8</c>, on the same ladder as <c>VehicleState</c>.</summary>
        public static byte Normalize(float health, float maxHealth)
        {
            if (maxHealth <= 0f) return 0;

            float t = health / maxHealth;
            if (t <= 0f) return 0;
            if (t >= 1f) return Quantize.HEALTH_MAX;

            return (byte)(t * Quantize.HEALTH_MAX + 0.5f);
        }

        /// <summary>
        /// Builds the line for one vehicle's first state, or refuses when the budget is spent.
        /// </summary>
        /// <param name="now">The engine clock, in seconds.</param>
        /// <returns>False when this line was dropped; the drop is counted, not lost.</returns>
        public bool TryFormat(
            ushort vehicleId, ushort spawnerId, VehicleKind kind,
            float health, float maxHealth, VehicleStateFlags flags,
            ushort driverActorId, in Vec3 position, float now, out string line)
        {
            if (now >= _windowEndsAt)
            {
                _windowEndsAt    = now + WindowSeconds;
                _linesThisWindow = 0;
            }

            if (_linesThisWindow >= MaxLinesPerWindow)
            {
                SuppressedSinceLastLine++;
                line = string.Empty;
                return false;
            }

            _linesThisWindow++;
            line = Format(
                vehicleId, spawnerId, kind, health, maxHealth, flags, driverActorId,
                in position, SuppressedSinceLastLine);
            SuppressedSinceLastLine = 0;
            return true;
        }

        /// <summary>The line itself, with no budget attached. Exposed so a test can read it.</summary>
        public static string Format(
            ushort vehicleId, ushort spawnerId, VehicleKind kind,
            float health, float maxHealth, VehicleStateFlags flags,
            ushort driverActorId, in Vec3 position, int suppressed = 0)
        {
            CultureInfo c = CultureInfo.InvariantCulture;

            string tail = suppressed > 0
                ? $" suppressed={suppressed.ToString(c)}"
                : string.Empty;

            // driver=none rather than driver=0, because 0 is ALSO the seatInfo sentinel for "not
            // seated" and a reader scanning for an id would have to know that to read the line.
            string driver = driverActorId == 0 ? "none" : driverActorId.ToString(c);

            return "[vehicle-spawn-state]"
                 + $" id={vehicleId.ToString(c)}"
                 + $" spawner={spawnerId.ToString(c)}"
                 + $" kind={kind}"
                 + $" health={health.ToString("0.##", c)}/{maxHealth.ToString("0.##", c)}"
                 + $" ({Normalize(health, maxHealth).ToString(c)}/{((byte)Quantize.HEALTH_MAX).ToString(c)})"
                 + $" flags={flags}"
                 + $" driver={driver}"
                 + $" position=({position.X.ToString("0.##", c)},"
                 + $" {position.Y.ToString("0.##", c)},"
                 + $" {position.Z.ToString("0.##", c)})"
                 + tail;
        }
    }
}
