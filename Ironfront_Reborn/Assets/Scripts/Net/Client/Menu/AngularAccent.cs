#nullable enable

using UnityEngine;
using UnityEngine.UI;

namespace Ironfront.Net.Unity.Client.Menu
{
    /// <summary>
    /// The orange bracket on the operations panel's cut corner, as vector geometry.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The shape is <c>panels/operations-panel.svg</c>'s second path,
    /// <c>M0 20 20 0h75L75 5H23L5 23v42H0z</c>: a 5-unit bar that runs 65 units down the left edge,
    /// round the 20-unit cut and 95 units along the top, its far end bevelled. The rect is that
    /// 95×65 box, so the bracket keeps its designed size whatever panel it sits on — it used to be
    /// baked into the panel bitmap and grew and thickened with every stretched panel.
    /// </para>
    /// </remarks>
    [DisallowMultipleComponent]
    [RequireComponent(typeof(CanvasRenderer))]
    public sealed class AngularAccent : MaskableGraphic
    {
        /// <summary>The bracket's box in the SVG's own units.</summary>
        public static readonly Vector2 DesignSize = new Vector2(95f, 65f);

        protected override void OnPopulateMesh(VertexHelper vh)
        {
            vh.Clear();

            Rect rect = GetPixelAdjustedRect();
            if (rect.width <= 0f || rect.height <= 0f) return;

            float sx = rect.width / DesignSize.x;
            float sy = rect.height / DesignSize.y;
            Vector2 Point(float x, float y) => new Vector2(rect.xMin + x * sx, rect.yMax - y * sy);

            // Outer and inner edges of the bar, paired vertex for vertex.
            Vector2[] outer = { Point(0f, 65f), Point(0f, 20f), Point(20f, 0f), Point(95f, 0f) };
            Vector2[] inner = { Point(5f, 65f), Point(5f, 23f), Point(23f, 5f), Point(75f, 5f) };

            Color32 colour = color;
            for (int i = 0; i < outer.Length - 1; i++)
            {
                int start = vh.currentVertCount;
                vh.AddVert(outer[i], colour, Vector2.zero);
                vh.AddVert(outer[i + 1], colour, Vector2.zero);
                vh.AddVert(inner[i + 1], colour, Vector2.zero);
                vh.AddVert(inner[i], colour, Vector2.zero);
                vh.AddTriangle(start, start + 1, start + 2);
                vh.AddTriangle(start, start + 2, start + 3);
            }

            // Smooth the three slanted edges: the outer and inner sides of the cut, and the bevel.
            float pixel = AngularGeometry.PixelSize(this);
            AngularGeometry.AddFringe(vh, outer[1], outer[2], inner[1], pixel, colour, colour);
            AngularGeometry.AddFringe(vh, inner[1], inner[2], outer[1], pixel, colour, colour);
            AngularGeometry.AddFringe(vh, outer[3], inner[3], Point(80f, 2.5f), pixel, colour, colour);
        }
    }
}
