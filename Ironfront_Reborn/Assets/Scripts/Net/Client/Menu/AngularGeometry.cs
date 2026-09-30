#nullable enable

using UnityEngine;
using UnityEngine.UI;

namespace Ironfront.Net.Unity.Client.Menu
{
    /// <summary>
    /// The polygon arithmetic shared by the menu's vector surfaces.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Why the anti-aliasing is a one-pixel fringe on the cut edges only.</b> A UI mesh is
    /// rasterised without multisampling, so a diagonal edge comes out as a staircase. Fading the
    /// colour to transparent across one device pixel beyond the edge is the usual cure, but it is
    /// applied to the 45-degree cuts alone: the horizontal and vertical edges already lie on pixel
    /// boundaries on a pixel-perfect canvas, and a fringe there would turn a crisp edge into a
    /// two-pixel blur.
    /// </para>
    /// </remarks>
    internal static class AngularGeometry
    {
        /// <summary>
        /// The rectangle with its top-left and bottom-right corners cut, clockwise from the top-left
        /// cut's upper end (y up). A cut of zero is the plain rectangle, without the doubled
        /// vertices a zero-length edge would leave.
        /// </summary>
        internal static Vector2[] CutRectangle(Rect rect, float cut)
        {
            if (cut <= 0.01f)
            {
                return new[]
                {
                    new Vector2(rect.xMin, rect.yMax),
                    new Vector2(rect.xMax, rect.yMax),
                    new Vector2(rect.xMax, rect.yMin),
                    new Vector2(rect.xMin, rect.yMin),
                };
            }

            return new[]
            {
                new Vector2(rect.xMin + cut, rect.yMax),
                new Vector2(rect.xMax, rect.yMax),
                new Vector2(rect.xMax, rect.yMin + cut),
                new Vector2(rect.xMax - cut, rect.yMin),
                new Vector2(rect.xMin, rect.yMin),
                new Vector2(rect.xMin, rect.yMax - cut),
            };
        }

        /// <summary>
        /// A clockwise convex polygon moved inward by <paramref name="distance"/> on every edge,
        /// with mitred corners, so a stroke of that width keeps its width round the cuts.
        /// </summary>
        internal static Vector2[] Inset(Vector2[] polygon, float distance)
        {
            int count = polygon.Length;
            var result = new Vector2[count];
            for (int i = 0; i < count; i++)
            {
                Vector2 previous = polygon[(i + count - 1) % count];
                Vector2 corner = polygon[i];
                Vector2 next = polygon[(i + 1) % count];
                Vector2 n1 = InwardNormal(previous, corner);
                Vector2 n2 = InwardNormal(corner, next);
                // The mitre: the corner moves along the bisector far enough that both edges
                // move by exactly `distance`.
                result[i] = corner + (n1 + n2) * (distance / (1f + Vector2.Dot(n1, n2)));
            }
            return result;
        }

        /// <summary>How long one device pixel is in <paramref name="graphic"/>'s own units.</summary>
        internal static float PixelSize(Graphic graphic)
        {
            Canvas? canvas = graphic.canvas;
            if (canvas == null) return 1f;

            Canvas root = canvas.rootCanvas;
            float rootScale = root.transform.lossyScale.x;
            if (rootScale <= 0f) return 1f;

            // Screen pixels per local unit: the canvas scale, times whatever this graphic's own
            // hierarchy scales on top of the root.
            float pixelsPerUnit = root.scaleFactor * (graphic.transform.lossyScale.x / rootScale);
            return pixelsPerUnit > 0f ? 1f / pixelsPerUnit : 1f;
        }

        /// <summary>A convex polygon as a triangle fan, one colour.</summary>
        internal static void AddConvex(VertexHelper vh, Vector2[] polygon, Color32 colour)
        {
            var centre = Vector2.zero;
            for (int i = 0; i < polygon.Length; i++) centre += polygon[i];
            centre /= polygon.Length;

            int start = vh.currentVertCount;
            vh.AddVert(centre, colour, Vector2.zero);
            for (int i = 0; i < polygon.Length; i++) vh.AddVert(polygon[i], colour, Vector2.zero);
            for (int i = 0; i < polygon.Length; i++)
                vh.AddTriangle(start, start + 1 + i, start + 1 + (i + 1) % polygon.Length);
        }

        /// <summary>The band between two polygons with matching vertices, one colour.</summary>
        internal static void AddRing(VertexHelper vh, Vector2[] outer, Vector2[] inner, Color32 colour)
        {
            int count = outer.Length;
            for (int i = 0; i < count; i++)
            {
                int j = (i + 1) % count;
                AddQuad(vh, outer[i], outer[j], inner[j], inner[i], colour, colour, colour, colour);
            }
        }

        /// <summary>
        /// Fades <paramref name="colourA"/>/<paramref name="colourB"/> to transparent across
        /// <paramref name="width"/> beyond the edge a→b, on the side away from
        /// <paramref name="awayFrom"/> — if the edge is diagonal; axis-aligned edges are left crisp.
        /// </summary>
        internal static void AddFringe(VertexHelper vh, Vector2 a, Vector2 b, Vector2 awayFrom,
            float width, Color32 colourA, Color32 colourB)
        {
            Vector2 edge = b - a;
            if (Mathf.Abs(edge.x) < 0.01f || Mathf.Abs(edge.y) < 0.01f) return;

            Vector2 normal = new Vector2(-edge.y, edge.x).normalized;
            if (Vector2.Dot(awayFrom - a, normal) > 0f) normal = -normal;
            Vector2 offset = normal * width;

            Color32 clearA = colourA;
            clearA.a = 0;
            Color32 clearB = colourB;
            clearB.a = 0;
            AddQuad(vh, a, b, b + offset, a + offset, colourA, colourB, clearB, clearA);
        }

        /// <summary><see cref="AddFringe"/> for every edge of a closed polygon.</summary>
        internal static void AddFringes(VertexHelper vh, Vector2[] polygon, Vector2 awayFrom,
            float width, Color32 colour)
        {
            for (int i = 0; i < polygon.Length; i++)
                AddFringe(vh, polygon[i], polygon[(i + 1) % polygon.Length], awayFrom, width,
                    colour, colour);
        }

        internal static Vector2 Centre(Vector2[] polygon)
        {
            var centre = Vector2.zero;
            for (int i = 0; i < polygon.Length; i++) centre += polygon[i];
            return centre / polygon.Length;
        }

        private static void AddQuad(VertexHelper vh, Vector2 p0, Vector2 p1, Vector2 p2, Vector2 p3,
            Color32 c0, Color32 c1, Color32 c2, Color32 c3)
        {
            int start = vh.currentVertCount;
            vh.AddVert(p0, c0, Vector2.zero);
            vh.AddVert(p1, c1, Vector2.zero);
            vh.AddVert(p2, c2, Vector2.zero);
            vh.AddVert(p3, c3, Vector2.zero);
            vh.AddTriangle(start, start + 1, start + 2);
            vh.AddTriangle(start, start + 2, start + 3);
        }

        /// <summary>The unit normal pointing into a clockwise (y-up) polygon from edge a→b.</summary>
        private static Vector2 InwardNormal(Vector2 a, Vector2 b)
        {
            Vector2 d = (b - a).normalized;
            return new Vector2(d.y, -d.x);
        }
    }
}
