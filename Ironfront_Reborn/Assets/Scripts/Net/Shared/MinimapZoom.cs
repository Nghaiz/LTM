using UnityEngine;

namespace Ironfront.Net.Unity
{
    /// <summary>
    /// The in-match map's zoom: which part of the map picture is shown, and where an icon lands
    /// on it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Owner ruling 2026-09-29</b>: houses, rocks and rails drawn on the map were too small to
    /// read, and the fix chosen was a mouse-wheel zoom while M is held, centred on the player,
    /// the whole map still being the default. The picture is not re-rendered; the map shows a
    /// window of it (<see cref="ViewRect"/>) and every icon is moved into that window
    /// (<see cref="ToMap"/>).
    /// </para>
    /// <para>
    /// <b>Viewport</b> below means the minimap camera's viewport, 0-1 across the whole picture;
    /// <b>map</b> means 0-1 across what is drawn right now. At zoom 1 they are the same.
    /// </para>
    /// </remarks>
    public static class MinimapZoom
    {
        /// <summary>The whole map.</summary>
        public const float MinZoom = 1f;

        /// <summary>The closest view: a third of the map across.</summary>
        public const float MaxZoom = 3f;

        /// <summary>How much one notch of the wheel zooms in (or out, inverted).</summary>
        public const float StepPerNotch = 1.25f;

        /// <summary>The zoom after <paramref name="notches"/> wheel notches, positive in.</summary>
        public static float Next(float zoom, float notches)
        {
            if (float.IsNaN(zoom) || zoom < MinZoom)
            {
                zoom = MinZoom;
            }
            if (float.IsNaN(notches) || notches == 0f)
            {
                return Mathf.Clamp(zoom, MinZoom, MaxZoom);
            }
            return Mathf.Clamp(zoom * Mathf.Pow(StepPerNotch, notches), MinZoom, MaxZoom);
        }

        /// <summary>
        /// The part of the picture shown at <paramref name="zoom"/> around
        /// <paramref name="centre"/>, as a UV rect; slid back inside the picture near its edges,
        /// so a zoomed map is never half empty.
        /// </summary>
        public static Rect ViewRect(Vector2 centre, float zoom)
        {
            zoom = Mathf.Clamp(float.IsNaN(zoom) ? MinZoom : zoom, MinZoom, MaxZoom);
            float size = 1f / zoom;
            float half = size * 0.5f;
            float x = Mathf.Clamp(float.IsNaN(centre.x) ? 0.5f : centre.x, half, 1f - half);
            float y = Mathf.Clamp(float.IsNaN(centre.y) ? 0.5f : centre.y, half, 1f - half);
            return new Rect(x - half, y - half, size, size);
        }

        /// <summary>Where a viewport point lands on the map currently showing <paramref name="view"/>.</summary>
        public static Vector2 ToMap(Vector2 viewport, Rect view)
        {
            if (view.width <= 0f || view.height <= 0f)
            {
                return viewport;
            }
            return new Vector2((viewport.x - view.x) / view.width, (viewport.y - view.y) / view.height);
        }

        /// <summary>Whether a map point is inside what is drawn.</summary>
        public static bool IsOnMap(Vector2 map)
        {
            return map.x >= 0f && map.x <= 1f && map.y >= 0f && map.y <= 1f;
        }
    }
}
