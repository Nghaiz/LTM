using Ironfront.Net.Unity;
using UnityEngine;
using UnityEngine.UI;

/// <summary>
/// A short trail of fading dots behind a moving minimap icon: which way it is going, and from the
/// spacing of the dots how fast. When a dot is kept, and how it fades, is
/// <see cref="MinimapTrailRules"/>'.
/// </summary>
/// <remarks>
/// <b>The dots live on the map, not on the icon.</b> They are children of the map picture,
/// placed by the same viewport projection as every icon, so they stay on the ground while the
/// icon moves on, are clipped by the map's mask, and travel with the map when it moves between
/// the deploy screen and the in-match overlay.
/// </remarks>
public sealed class MinimapTrail
{
	private readonly RectTransform map;

	private readonly Texture dotTexture;

	private readonly Vector3[] points;

	private readonly float[] stamps;

	private readonly RawImage[] dots;

	private int count;

	private int newest = -1;

	/// <param name="map">The map picture the dots are drawn on.</param>
	/// <param name="dotTexture">A round, white, soft-edged dot; tinted per icon.</param>
	/// <param name="capacity">How many positions, and so dots, the trail holds.</param>
	public MinimapTrail(RectTransform map, Texture dotTexture, int capacity)
	{
		this.map = map;
		this.dotTexture = dotTexture;
		capacity = Mathf.Max(1, capacity);
		points = new Vector3[capacity];
		stamps = new float[capacity];
		dots = new RawImage[capacity];
	}

	/// <summary>Offers the subject's position this frame; kept only if it is a new step.</summary>
	public void Record(Vector3 world, float now)
	{
		if (count > 0)
		{
			Vector3 last = points[newest];
			if (MinimapTrailRules.IsJump(last, world))
			{
				Clear();
			}
			else if (!MinimapTrailRules.ShouldSample(last, stamps[newest], world, now))
			{
				return;
			}
		}
		newest = (newest + 1) % points.Length;
		points[newest] = world;
		stamps[newest] = now;
		if (count < points.Length)
		{
			count++;
		}
	}

	/// <summary>Places, sizes and fades the dots for this frame; hides them all when not visible.</summary>
	public void Draw(Camera minimapCamera, Color color, float dotPixels, bool visible, float now)
	{
		for (int i = 0; i < dots.Length; i++)
		{
			RawImage dot = dots[i];
			bool show = visible && minimapCamera != null && i < count;
			float fade = 0f;
			Vector3 viewport = default(Vector3);
			if (show)
			{
				int index = (newest - i + points.Length) % points.Length;
				fade = MinimapTrailRules.FadeOf(now - stamps[index]);
				Vector3 projected = minimapCamera.WorldToViewportPoint(points[index]);
				Vector2 onMap = MinimapUi.ToMap(projected);
				viewport = new Vector3(onMap.x, onMap.y, projected.z);
				show = fade > 0.02f && projected.z > 0f && MinimapZoom.IsOnMap(onMap);
			}
			if (!show)
			{
				if (dot != null && dot.enabled)
				{
					dot.enabled = false;
				}
				continue;
			}
			if (dot == null)
			{
				dot = dots[i] = CreateDot();
				if (dot == null)
				{
					return;
				}
			}
			// Placed, scaled and faded without touching the dot's mesh: a moved transform, a scale
			// and a CanvasRenderer alpha only re-batch the canvas, where a new anchor, size or
			// colour regenerated every dot of every trail every frame (MinimapUi.Place). The colour
			// is written only when the icon's own colour changes.
			RectTransform rect = dot.rectTransform;
			MinimapUi.Place(rect, new Vector2(viewport.x, viewport.y));
			MinimapUi.SetSquareSize(rect, dotPixels);
			float scale = 0.45f + 0.55f * fade;
			rect.localScale = new Vector3(scale, scale, 1f);
			if (dot.color != color)
			{
				dot.color = color;
			}
			dot.canvasRenderer.SetAlpha(0.9f * fade);
			if (!dot.enabled)
			{
				dot.enabled = true;
			}
		}
	}

	/// <summary>Forgets every position, e.g. across a respawn.</summary>
	public void Clear()
	{
		count = 0;
		newest = -1;
	}

	/// <summary>Destroys the dots. Call when the icon itself goes away.</summary>
	public void Destroy()
	{
		for (int i = 0; i < dots.Length; i++)
		{
			if (dots[i] != null)
			{
				Object.Destroy(dots[i].gameObject);
			}
			dots[i] = null;
		}
		Clear();
	}

	private RawImage CreateDot()
	{
		if (map == null)
		{
			return null;
		}
		// Built here rather than from a prefab: a dot is a per-icon pooled visual, made lazily and
		// destroyed with its icon, never authored scene content.
		GameObject go = new GameObject("Trail Dot", typeof(RectTransform), typeof(CanvasRenderer), typeof(RawImage));
		RectTransform rect = (RectTransform)go.transform;
		rect.SetParent(map, false);
		// Under every icon, flag and spawn button, which are all drawn after it.
		rect.SetAsFirstSibling();
		rect.pivot = new Vector2(0.5f, 0.5f);
		rect.anchoredPosition = Vector2.zero;
		RawImage image = go.GetComponent<RawImage>();
		image.texture = dotTexture;
		image.raycastTarget = false;
		return image;
	}
}
