using System;
using Ironfront.Net.Protocol;
using Ironfront.Net.Replication.Movement;

namespace Ironfront.Net.Replication.World
{
    /// <summary>
    /// The axis-aligned box a match is played inside, and what the server does with a body that
    /// leaves it. Ledger <b>E-6</b>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The failure this exists to stop is silent, and it is not a wall.</b>
    /// <c>Quantize.PackPos</c> clamps to <c>POS_MIN</c>/<c>POS_MAX</c> before it packs
    /// (<c>Clamp01((v - POS_MIN) / POS_RANGE)</c>), so a body outside the window is still simulated at
    /// its true position by the server while every snapshot pins it to the boundary. The server
    /// and its clients then disagree permanently, with no exception, no counter and nothing in a
    /// log — and the symptom a player reports is "the helicopter broke", which reads as lag.
    /// Dustbowl has fourteen vehicle spawners, two of them respawning helicopters, and from the
    /// worst playable coordinate one needs about another 1,100 m of level flight to get there.
    /// </para>
    /// <para>
    /// <b>Why this is a struct in the library and not three lines in the MonoBehaviour.</b>
    /// <c>LevelBounds</c> compiles into <c>Assembly-CSharp</c>, which no test assembly can
    /// reference (<b>E-11b</b>). Arithmetic that lives there can only be graded by eye. The
    /// containment and clamp rules live here where <c>dotnet test</c> reaches them, and
    /// <c>LevelBounds</c> holds the authored box and delegates — one containment rule, not two
    /// that agree until somebody edits one.
    /// </para>
    /// <para>
    /// <b><see cref="FitsOnTheWire"/> is the check that matters most and costs least.</b> The
    /// clamp below only keeps a body on the wire if the authored box is itself inside the wire's
    /// range. Dustbowl's is — 1700 × 700 × 1600 m centred near the origin, so roughly ±920 m at
    /// its widest — but nothing said so, and an artist widening it past 2048 would reintroduce
    /// the exact silent divergence this closes while every check here still passed.
    /// </para>
    /// </remarks>
    public readonly struct PlayVolume
    {
        /// <summary>Builds a volume from a centre and a full size, matching Unity's Bounds.</summary>
        /// <remarks>
        /// Size rather than extents because that is what <c>Bounds(center, size)</c> takes and
        /// what <c>LevelBounds.SetupBounds</c> passes it — a constructor here that meant extents
        /// would be off by exactly a factor of two, in the direction that makes the box look
        /// correct in the Editor and lets bodies out at half the authored distance.
        /// </remarks>
        public PlayVolume(Vec3 center, Vec3 size)
        {
            if (size.X < 0f || size.Y < 0f || size.Z < 0f)
                throw new ArgumentOutOfRangeException(
                    nameof(size), size, "A play volume cannot have a negative dimension.");

            var half = new Vec3(size.X * 0.5f, size.Y * 0.5f, size.Z * 0.5f);
            Min = new Vec3(center.X - half.X, center.Y - half.Y, center.Z - half.Z);
            Max = new Vec3(center.X + half.X, center.Y + half.Y, center.Z + half.Z);
        }

        /// <summary>The low corner.</summary>
        public Vec3 Min { get; }

        /// <summary>The high corner.</summary>
        public Vec3 Max { get; }

        /// <summary>
        /// True when every point this volume permits can be carried by a snapshot.
        /// </summary>
        /// <remarks>
        /// Inclusive at the boundary: <c>Quantize.PackPos</c> maps exactly <c>POS_MAX</c> to the
        /// top code, so a body resting on the edge round-trips. A volume that fails this is not
        /// an authoring preference — it is a promise the wire cannot keep.
        /// </remarks>
        public bool FitsOnTheWire =>
            Min.X >= Quantize.POS_MIN && Max.X <= Quantize.POS_MAX &&
            Min.Y >= Quantize.POS_MIN && Max.Y <= Quantize.POS_MAX &&
            Min.Z >= Quantize.POS_MIN && Max.Z <= Quantize.POS_MAX;

        /// <summary>True when <paramref name="point"/> is inside, boundary included.</summary>
        public bool Contains(in Vec3 point) =>
            point.X >= Min.X && point.X <= Max.X &&
            point.Y >= Min.Y && point.Y <= Max.Y &&
            point.Z >= Min.Z && point.Z <= Max.Z;

        /// <summary>
        /// Pulls <paramref name="point"/> back to the nearest point inside, and reports whether
        /// it had to.
        /// </summary>
        /// <returns>
        /// True when the point was outside and <paramref name="clamped"/> differs from it; false
        /// when it was already inside, in which case <paramref name="clamped"/> is the input.
        /// </returns>
        /// <remarks>
        /// The bool is the whole reason this is not a plain <c>Clamp</c>: the caller has to know
        /// a crossing HAPPENED so it can count it. A clamp that returns only a position converts
        /// the event into a value and loses it, which is how this became invisible in the first
        /// place.
        /// </remarks>
        public bool TryClamp(in Vec3 point, out Vec3 clamped)
        {
            if (Contains(in point))
            {
                clamped = point;
                return false;
            }

            clamped = new Vec3(
                Math.Clamp(point.X, Min.X, Max.X),
                Math.Clamp(point.Y, Min.Y, Max.Y),
                Math.Clamp(point.Z, Min.Z, Max.Z));

            return true;
        }
    }
}
