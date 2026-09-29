using UnityEngine;
using UnityEngine.UI;

/// <summary>
/// The player's field of view on the minimap: a fan from the arrow, as wide as the camera really
/// sees, bright at the arrow and fading out with distance, with a crisp line down either edge.
/// </summary>
/// <remarks>
/// <para>
/// <b>Drawn as a mesh, not a texture.</b> The original cone was a fixed 64 px sprite at 38%
/// opacity through the additive HUD material: on sand it vanished, and its angle had nothing to
/// do with the camera (owner report 2026-09-29: the view cone must be redone to be clearer). A fan
/// built in <see cref="OnPopulateMesh"/> takes its angle from the real horizontal field of view
/// and stays sharp at any size.
/// </para>
/// <para>
/// The fan points up the graphic's local Y axis and its apex is the rect's centre; the owner
/// places the rect on the arrow and turns it to the camera's yaw.
/// </para>
/// </remarks>
[RequireComponent(typeof(CanvasRenderer))]
public sealed class MinimapViewCone : MaskableGraphic
{
	private const int ARC_SEGMENTS = 32;

	/// <summary>Opacity of the fan at the apex, as a share of the colour's alpha.</summary>
	private const float APEX_OPACITY = 0.55f;

	/// <summary>Opacity of the two edge lines at the apex.</summary>
	private const float EDGE_OPACITY = 0.95f;

	/// <summary>Width of each edge line, in canvas pixels.</summary>
	private const float EDGE_PIXELS = 2.2f;

	/// <summary>
	/// A change smaller than this is not redrawn: invisible at minimap scale, and a camera's field
	/// of view wobbles by less while it zooms, so rebuilding for it would dirty the canvas every frame.
	/// </summary>
	private const float REDRAW_DEGREES = 0.5f;

	private float fieldOfViewDegrees = 90f;

	/// <summary>The full horizontal angle of the fan, in degrees.</summary>
	public float FieldOfViewDegrees
	{
		get
		{
			return fieldOfViewDegrees;
		}
		set
		{
			float clamped = Mathf.Clamp(value, 10f, 170f);
			if (Mathf.Abs(clamped - fieldOfViewDegrees) < REDRAW_DEGREES)
			{
				return;
			}
			fieldOfViewDegrees = clamped;
			SetVerticesDirty();
		}
	}

	protected override void Awake()
	{
		base.Awake();
		raycastTarget = false;
	}

	protected override void OnPopulateMesh(VertexHelper vh)
	{
		vh.Clear();
		Rect rect = GetPixelAdjustedRect();
		Vector2 apex = rect.center;
		float reach = Mathf.Min(rect.width, rect.height) * 0.5f;
		if (reach <= 0f)
		{
			return;
		}
		float half = fieldOfViewDegrees * 0.5f * Mathf.Deg2Rad;
		Color inner = color;
		inner.a *= APEX_OPACITY;
		Color outer = color;
		outer.a = 0f;
		// The fan: one apex vertex and an arc, the alpha falling from the apex to the rim.
		vh.AddVert(apex, inner, Vector2.zero);
		for (int i = 0; i <= ARC_SEGMENTS; i++)
		{
			float angle = -half + 2f * half * i / ARC_SEGMENTS;
			vh.AddVert(apex + Direction(angle) * reach, outer, Vector2.zero);
		}
		for (int i = 0; i < ARC_SEGMENTS; i++)
		{
			vh.AddTriangle(0, i + 1, i + 2);
		}
		AddEdge(vh, apex, -half, reach);
		AddEdge(vh, apex, half, reach);
	}

	private void AddEdge(VertexHelper vh, Vector2 apex, float angle, float reach)
	{
		Vector2 along = Direction(angle);
		Vector2 across = new Vector2(along.y, 0f - along.x) * (EDGE_PIXELS * 0.5f);
		Color near = color;
		near.a *= EDGE_OPACITY;
		Color far = color;
		far.a = 0f;
		int start = vh.currentVertCount;
		vh.AddVert(apex - across, near, Vector2.zero);
		vh.AddVert(apex + across, near, Vector2.zero);
		vh.AddVert(apex + along * reach + across, far, Vector2.zero);
		vh.AddVert(apex + along * reach - across, far, Vector2.zero);
		vh.AddTriangle(start, start + 1, start + 2);
		vh.AddTriangle(start, start + 2, start + 3);
	}

	// 0 radians points up the rect (+Y); a positive angle turns clockwise, as a yaw does.
	private static Vector2 Direction(float radians)
	{
		return new Vector2(Mathf.Sin(radians), Mathf.Cos(radians));
	}
}
