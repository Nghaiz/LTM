using System.Globalization;
using Ironfront.Net.Protocol;
using Ironfront.Net.Replication.Movement;

namespace Ironfront.Net.Replication.Combat
{
    /// <summary>
    /// What a shot that hit nothing came closest to, and by how much.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Ledger row X-24, and the measurement it asked for before any fix.</b> The hitbox
    /// stack had a 3 cm vertical seam — torso and arms both stopped at 1.550 m while the head
    /// started at 1.580 m — and <see cref="LagCompensator.ResolveHitscan"/> returned
    /// <see cref="HitResult.Miss"/> without recording which box the ray had passed and on which
    /// side. Every artifact could therefore say only <c>hits=0</c>, which reads identically for
    /// a shot aimed at the sky, a shot 3 cm high, and a shot the boxes never saw.
    /// </para>
    /// <para>
    /// The row is explicit that the measurement comes first: <i>"widening a box is a balance
    /// change and guessing at 0.03 m is how a hitbox stops matching the mesh"</i>. This struct
    /// is that instrument. It states facts and no verdict — the box, the gap, and the SIGN of
    /// the vertical offset, which is what distinguishes "the ray went over the torso" from "it
    /// went under the head" when the two edges are 3 cm apart.
    /// </para>
    /// <para>
    /// <b>Not a wire type.</b> Nothing here is serialized; it is a server-side diagnostic read
    /// by the shot log. Keeping it out of the protocol is deliberate — a client that could ask
    /// "how far did I miss by" is an aimbot oracle.
    /// </para>
    /// </remarks>
    public readonly struct HitboxMiss
    {
        /// <summary>
        /// False when no measurement was taken — no live candidate, or the ray was rejected
        /// before any box was considered.
        /// </summary>
        /// <remarks>
        /// Distinct from a gap of 0, which means the ray grazed the box's surface. An unmeasured
        /// miss must never render as a measured zero: that is the shape
        /// <c>green-that-proves-nothing.md</c> calls a rate over an empty denominator.
        /// </remarks>
        public readonly bool Measured;

        /// <summary>The actor the nearest box belonged to.</summary>
        public readonly ushort ActorId;

        /// <summary>Index into <see cref="HitboxSet"/>: 0 head, 1 torso, 2 arms, 3 legs.</summary>
        public readonly int BoxIndex;

        /// <summary>The damage class of <see cref="BoxIndex"/>.</summary>
        public readonly HitboxType Type;

        /// <summary>
        /// Metres from the ray to the box's surface at the ray's closest approach. Never
        /// negative; 0 means the ray touched the surface without the slab test accepting it.
        /// </summary>
        public readonly float GapMetres;

        /// <summary>
        /// Signed vertical offset at the same point: <b>positive</b> when the ray passed ABOVE
        /// the box's top edge, <b>negative</b> when it passed BELOW its bottom edge, 0 when it
        /// was level with the box and missed to one side.
        /// </summary>
        /// <remarks>
        /// This is the number the X-24 fix is sized from. A shot through the old seam reports
        /// roughly <c>+0.015</c> against the torso or <c>-0.015</c> against the head, and the
        /// two together say the gap is 3 cm without anyone deriving it from the constants that
        /// produced it.
        /// </remarks>
        public readonly float VerticalOffsetMetres;

        /// <summary>Where on the ray the closest approach happened.</summary>
        public readonly Vec3 PointOnRay;

        public HitboxMiss(
            ushort actorId, int boxIndex, HitboxType type,
            float gapMetres, float verticalOffsetMetres, in Vec3 pointOnRay)
        {
            Measured = true;
            ActorId = actorId;
            BoxIndex = boxIndex;
            Type = type;
            GapMetres = gapMetres;
            VerticalOffsetMetres = verticalOffsetMetres;
            PointOnRay = pointOnRay;
        }

        /// <summary>The no-measurement value. <see cref="Measured"/> is false.</summary>
        public static HitboxMiss None => default;

        /// <summary>The box's name, for a log line a human reads.</summary>
        public string BoxName => BoxIndex switch
        {
            0 => "head",
            1 => "torso",
            2 => "arms",
            3 => "legs",
            _ => "box" + BoxIndex.ToString(CultureInfo.InvariantCulture),
        };

        /// <summary>
        /// One line of facts. Pure, so the netstandard suite pins the format without an engine.
        /// </summary>
        public string Describe()
        {
            if (!Measured) return "unmeasured";

            return string.Format(
                CultureInfo.InvariantCulture,
                "actor={0} box={1} type={2} gap={3:F3}m vertical={4:+0.000;-0.000;0.000}m at={5:F2},{6:F2},{7:F2}",
                ActorId, BoxName, Type, GapMetres, VerticalOffsetMetres,
                PointOnRay.X, PointOnRay.Y, PointOnRay.Z);
        }
    }
}
