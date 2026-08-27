using System.Globalization;
using Ironfront.Net.Protocol;
using Ironfront.Net.Replication.Movement;

namespace Ironfront.Net.Replication.World
{
    /// <summary>
    /// Counts entity positions the snapshot encoder had to clamp to the wire's range, and
    /// remembers the first one so the report names something rather than a number.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>X-39's third finding, and the one that outlives whichever fix is chosen.</b> The
    /// range problem is a level-design or protocol decision; this is not. An entity past
    /// <see cref="Quantize.POS_MAX"/> decodes to a plausible position exactly on the boundary,
    /// so two vehicles 50 m apart at Dustbowl's Oasis capture point decode to the same place
    /// on every client — and no snapshot, report or log said a word about it. It was found by
    /// noticing that the quantized value 32767 is unreachable any other way.
    /// </para>
    /// <para>
    /// <b>Deliberately not in <see cref="Quantize"/>.</b> That class is a pure codec shared by
    /// projectile hit points, explosion centres and anything else that needs a short — a
    /// counter there would answer a different, broader question and could not name the entity
    /// that caused it. This sits on the two paths that carry REPLICATED ENTITY positions,
    /// which is the population X-39 measured.
    /// </para>
    /// <para>
    /// <b>Static, matching <c>LevelBounds.ClampCount</c>.</b> One server process, one world,
    /// one number, readable from a diagnostics overlay or a log line without threading an
    /// instance through fourteen call sites. <see cref="Reset"/> exists so a test can assert a
    /// delta rather than an absolute, which is the only safe thing to assert about shared state.
    /// </para>
    /// </remarks>
    public static class PositionSaturationLog
    {
        /// <summary>Entity positions clamped by the quantizer since the last <see cref="Reset"/>.</summary>
        /// <remarks>
        /// Counted per AXIS-BEARING POSITION, not per axis: one entity outside on two axes at
        /// one tick is one event, because it is one entity in one wrong place.
        /// </remarks>
        public static long Count { get; private set; }

        /// <summary>Distinct entities seen saturating, by kind and id, since the last reset.</summary>
        public static int DistinctEntities => Seen.Count;

        /// <summary>
        /// The first saturating entity, formatted, or null when nothing has saturated.
        /// </summary>
        public static string? First { get; private set; }

        // PascalCase because it is static: .editorconfig reserves the _camelCase form for
        // instance fields.
        private static readonly System.Collections.Generic.HashSet<(bool IsVehicle, ushort Id)> Seen
            = new System.Collections.Generic.HashSet<(bool, ushort)>();

        /// <summary>
        /// Records <paramref name="position"/> if any axis of it is outside the wire's range.
        /// </summary>
        /// <returns>True when it saturated, so a caller may log or act on the individual event.</returns>
        public static bool Observe(bool isVehicle, ushort id, in Vec3 position)
        {
            if (!Quantize.PositionSaturates(position.X)
                && !Quantize.PositionSaturates(position.Y)
                && !Quantize.PositionSaturates(position.Z))
            {
                return false;
            }

            Count++;
            Seen.Add((isVehicle, id));
            First ??= string.Format(
                CultureInfo.InvariantCulture,
                "{0} {1} at ({2:F2},{3:F2},{4:F2}) — outside +/-{5:F0} m, so it replicates on the "
                + "boundary and every client sees it in the wrong place",
                isVehicle ? "vehicle" : "actor", id,
                position.X, position.Y, position.Z, Quantize.POS_MAX);

            return true;
        }

        public static void Reset()
        {
            Count = 0;
            First = null;
            Seen.Clear();
        }
    }
}
