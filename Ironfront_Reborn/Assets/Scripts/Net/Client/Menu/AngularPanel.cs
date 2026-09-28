#nullable enable

using UnityEngine;
using UnityEngine.UI;

namespace Ironfront.Net.Unity.Client.Menu
{
    /// <summary>
    /// Which edges <see cref="AngularPanel"/> strokes.
    /// </summary>
    /// <remarks>
    /// The prototype uses a full border on cards and actions, but a single heavy edge on three
    /// recurring surfaces: <c>box-shadow: inset 3px 0</c> on <c>.field</c>, <c>border-left: 3px</c>
    /// on <c>.player-card</c>, and <c>border-bottom: 2px</c> on <c>.team header</c>. A full-border
    /// panel with a transparent fill cannot express those, so the edge is a mode rather than a
    /// width.
    /// </remarks>
    public enum AngularEdge
    {
        /// <summary>All four sides, following the cut corners.</summary>
        All,

        /// <summary>A bar down the left edge, inset below the top-left cut.</summary>
        Left,

        /// <summary>A bar along the bottom edge, inset left of the bottom-right cut.</summary>
        Bottom,

        /// <summary>No stroke.</summary>
        None,
    }

    /// <summary>
    /// A rectangle with the top-left and bottom-right corners cut away, in the prototype's own
    /// geometry.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>This exists because the CSS is <c>clip-path</c>, not a texture.</b> Every angular surface
    /// in the pack — <c>.menu-button</c> (13px), <c>.action</c> (10px), <c>.glass-card</c> (18px),
    /// <c>.operations-panel</c> (20px), <c>.player-avatar</c> (8px) — is a
    /// <c>polygon(13px 0, 100% 0, 100% calc(100% - 13px), calc(100% - 13px) 100%, 0 100%, 0 13px)</c>
    /// over a flat background. Drawing that polygon is both <em>faster</em> than a 9-sliced sprite
    /// and <em>correct at every size</em>, whereas a sliced sprite smears its corners when a panel
    /// is stretched.
    /// </para>
    /// <para>
    /// <b>It is also the only option that works with this pack.</b> The supplied panels and buttons
    /// are SVGs imported by Unity's scripted SVG importer, which writes
    /// <c>SpriteBorder: {x: 0, y: 0, z: 0, w: 0}</c> — there is no nine-slice region to slice, so
    /// <see cref="Image.Type.Sliced"/> on one of them renders identically to
    /// <see cref="Image.Type.Simple"/> and stretches the artwork instead. The spec anticipates
    /// this: <i>"Angular surfaces and state changes described by CSS may be reproduced with Unity
    /// UI geometry and colors rather than new art."</i>
    /// </para>
    /// <para>
    /// <b>The cut is in reference pixels and scales with the panel.</b> <c>clip-path</c> uses
    /// <c>px</c> against a viewport the browser does not scale, but this canvas uses a
    /// <see cref="CanvasScaler"/> at 1920×1080, so a fixed pixel cut would shrink relative to the
    /// artwork on a larger display. <see cref="ScaleWithCanvas"/> reproduces the CSS behaviour by
    /// scaling the cut with the canvas, and the authoring tool leaves it off so the numbers stay
    /// the ones written in the stylesheet.
    /// </para>
    /// </remarks>
    [DisallowMultipleComponent]
    public sealed class AngularPanel : MaskableGraphic
    {
        [SerializeField] private float _cut = 14f;
        [SerializeField] private AngularEdge _edge = AngularEdge.All;
        [SerializeField] private float _edgeWidth = 1f;
        [SerializeField] private Color _edgeColour = new Color(0.44f, 0.72f, 0.89f, 0.45f);
        [SerializeField] private bool _gradient;
        [SerializeField] private Color _gradientTo = Color.white;
        [SerializeField] private float _gradientAngle = 135f;

        /// <summary>How much of each cut corner to remove, in reference pixels.</summary>
        public float Cut
        {
            get => _cut;
            set { _cut = value; SetVerticesDirty(); }
        }

        /// <summary>Which edges to stroke.</summary>
        public AngularEdge Edge
        {
            get => _edge;
            set { _edge = value; SetVerticesDirty(); }
        }

        /// <summary>How thick the stroke is, in reference pixels.</summary>
        public float EdgeWidth
        {
            get => _edgeWidth;
            set { _edgeWidth = value; SetVerticesDirty(); }
        }

        /// <summary>The stroke's colour. The fill is <see cref="Graphic.color"/>.</summary>
        public Color EdgeColour
        {
            get => _edgeColour;
            set { _edgeColour = value; SetVerticesDirty(); }
        }

        /// <summary>
        /// Configures every field at once, which is what the authoring tool needs.
        /// </summary>
        public void Configure(float cut, AngularEdge edge, float edgeWidth, Color edgeColour)
        {
            _cut = cut;
            _edge = edge;
            _edgeWidth = edgeWidth;
            _edgeColour = edgeColour;
            SetVerticesDirty();
        }

        /// <summary>
        /// Turns the flat fill into a linear gradient, in CSS's own terms.
        /// </summary>
        /// <param name="to">The far stop. The near stop is <see cref="Graphic.color"/>.</param>
        /// <param name="angleDegrees">
        /// CSS <c>linear-gradient</c> angle: 0 points to the top, 90 to the right, 135 to the
        /// bottom-right.
        /// </param>
        /// <remarks>
        /// <b>Accurate rather than approximate, despite being a vertex-colour gradient.</b> A
        /// linear gradient is an affine function of position, and barycentric interpolation across
        /// a triangle reproduces an affine function exactly — so evaluating the gradient at each of
        /// the hexagon's seven vertices and letting the rasteriser interpolate gives the same
        /// result as a shader would, with no texture and no extra draw call.
        /// </remarks>
        public void SetGradient(Color to, float angleDegrees)
        {
            _gradient = true;
            _gradientTo = to;
            _gradientAngle = angleDegrees;
            SetVerticesDirty();
        }

        protected override void OnPopulateMesh(VertexHelper vh)
        {
            vh.Clear();

            Rect rect = GetPixelAdjustedRect();
            if (rect.width <= 0f || rect.height <= 0f) return;

            // Half the shorter side is the point at which two opposite cuts meet and the shape
            // stops being a hexagon, so a cut past it is a request the geometry cannot honour.
            float cut = Mathf.Clamp(_cut, 0f, Mathf.Min(rect.width, rect.height) * 0.5f);

            // The stroke is drawn as a filled hexagon UNDER the fill rather than as a ring of
            // quads. It is one shape fewer to compute and it makes the stroke follow the cut
            // corners for free -- a ring would need its own six mitred corners.
            if (_edge == AngularEdge.All && _edgeWidth > 0f && _edgeColour.a > 0f)
                AddPolygon(vh, rect, Hexagon(rect, cut), _edgeColour, default, 0f, false);

            if (color.a > 0f)
                AddPolygon(vh, rect, Hexagon(rect, cut), color, _gradientTo, _gradientAngle, _gradient);

            // A single edge is a plain rectangle clipped to the part of the hexagon it can occupy,
            // because a bar that ran the full height would stick out past the cut.
            if (_edgeWidth <= 0f || _edgeColour.a <= 0f) return;

            if (_edge == AngularEdge.Left)
            {
                AddQuad(vh,
                    new Rect(rect.xMin, rect.yMin + cut, _edgeWidth, rect.height - cut),
                    _edgeColour);
            }
            else if (_edge == AngularEdge.Bottom)
            {
                AddQuad(vh,
                    new Rect(rect.xMin, rect.yMin, rect.width - cut, _edgeWidth),
                    _edgeColour);
            }
        }

        /// <summary>
        /// The six points of the cut rectangle, in the stylesheet's own order.
        /// </summary>
        /// <remarks>
        /// Clockwise from the first point of the CSS <c>polygon()</c>, so the shape here can be
        /// read against the stylesheet line for line.
        /// </remarks>
        private static Vector2[] Hexagon(Rect rect, float cut) => new[]
        {
            new Vector2(rect.xMin + cut, rect.yMax),
            new Vector2(rect.xMax, rect.yMax),
            new Vector2(rect.xMax, rect.yMin + cut),
            new Vector2(rect.xMax - cut, rect.yMin),
            new Vector2(rect.xMin, rect.yMin),
            new Vector2(rect.xMin, rect.yMax - cut),
        };

        /// <summary>
        /// Adds a convex polygon as a triangle fan around its centroid.
        /// </summary>
        /// <remarks>
        /// Valid because the cut rectangle is convex whatever the cut: the two cuts are on opposite
        /// corners, so no interior angle exceeds 180 degrees until the cut passes half the short
        /// side, which <see cref="OnPopulateMesh"/> clamps against.
        /// </remarks>
        private static void AddPolygon(VertexHelper vh, Rect rect, Vector2[] points, Color32 near,
            Color32 far, float angleDegrees, bool gradient)
        {
            var centre = Vector2.zero;
            for (int i = 0; i < points.Length; i++) centre += points[i];
            centre /= points.Length;

            int start = vh.currentVertCount;
            vh.AddVert(centre, Stop(rect, centre, near, far, angleDegrees, gradient), Vector2.zero);
            for (int i = 0; i < points.Length; i++)
                vh.AddVert(points[i], Stop(rect, points[i], near, far, angleDegrees, gradient),
                    Vector2.zero);

            for (int i = 0; i < points.Length; i++)
                vh.AddTriangle(start, start + 1 + i, start + 1 + (i + 1) % points.Length);
        }

        /// <summary>
        /// The gradient's colour at <paramref name="point"/>, using CSS's own construction.
        /// </summary>
        /// <remarks>
        /// The gradient line runs through the rect's centre along the angle's direction, and its
        /// length is the rect's extent projected onto that direction — the same definition the
        /// CSS specification gives, which is why a 135-degree gradient here matches the browser's
        /// rather than merely resembling it. The angle is flipped into Unity's y-up space: CSS
        /// measures from "to top" and increases clockwise.
        /// </remarks>
        private static Color32 Stop(Rect rect, Vector2 point, Color32 near, Color32 far,
            float angleDegrees, bool gradient)
        {
            if (!gradient) return near;

            float radians = angleDegrees * Mathf.Deg2Rad;
            var direction = new Vector2(Mathf.Sin(radians), Mathf.Cos(radians));

            float length = Mathf.Abs(rect.width * direction.x) + Mathf.Abs(rect.height * direction.y);
            if (length <= 0f) return near;

            float t = Mathf.Clamp01(Vector2.Dot(point - rect.center, direction) / length + 0.5f);
            return Color32.Lerp(near, far, t);
        }

        private static void AddQuad(VertexHelper vh, Rect rect, Color32 colour)
        {
            if (rect.width <= 0f || rect.height <= 0f) return;

            int start = vh.currentVertCount;
            vh.AddVert(new Vector2(rect.xMin, rect.yMin), colour, Vector2.zero);
            vh.AddVert(new Vector2(rect.xMin, rect.yMax), colour, Vector2.zero);
            vh.AddVert(new Vector2(rect.xMax, rect.yMax), colour, Vector2.zero);
            vh.AddVert(new Vector2(rect.xMax, rect.yMin), colour, Vector2.zero);
            vh.AddTriangle(start, start + 1, start + 2);
            vh.AddTriangle(start, start + 2, start + 3);
        }
    }
}
