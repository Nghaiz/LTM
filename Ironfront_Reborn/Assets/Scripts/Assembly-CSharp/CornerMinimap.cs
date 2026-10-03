using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;

/// <summary>
/// The always-on round radar in the top-left corner: the ground around the player, zoomed in,
/// with the flags, team-mates and vehicles the full map shows. The M map is unchanged.
/// </summary>
/// <remarks>
/// <para>
/// <b>Built in code, by <see cref="MinimapUi"/>, on its own Canvas.</b> It needs the same picture
/// the M map draws (one render of the level from <see cref="MinimapCamera"/>) and the same icons,
/// so it reads both from <see cref="MinimapUi"/> instead of rendering or deciding visibility a
/// second time: an enemy hidden on the M map is hidden here too.
/// </para>
/// <para>
/// <b>North up, the player's arrow turning.</b> The same orientation as the M map, so the two
/// never disagree about which way a flag lies.
/// </para>
/// </remarks>
public class CornerMinimap : MonoBehaviour
{
	/// <summary>Diameter on a 1920x1080 reference screen.</summary>
	public const float Diameter = 236f;

	/// <summary>Gap from the screen's top and left edges.</summary>
	public const float Margin = 22f;

	/// <summary>World metres from the centre to the rim.</summary>
	public const float RadiusMetres = 120f;

	private const float RimWidth = 5f;

	private const float FlagPixels = 20f;

	private const float SoldierPixels = 9f;

	private const float HumanPixels = 11f;

	private const float VehiclePixels = 20f;

	private const float SelfPixels = 22f;

	/// <summary>A flag beyond the rim is pinned just inside it, so its direction still reads.</summary>
	private const float FlagRimInset = 0.88f;

	private const float FadeSpeed = 8f;

	private static Texture2D circle;

	private RectTransform root;

	private CanvasGroup group;

	private RawImage map;

	private RectTransform iconLayer;

	private RectTransform self;

	private RawImage selfImage;

	private readonly List<RawImage> pool = new List<RawImage>();

	private int used;

	/// <summary>Adds the radar under <paramref name="canvasRoot"/> and returns it.</summary>
	public static CornerMinimap Create(RectTransform canvasRoot, Texture picture, Texture arrow)
	{
		var go = new GameObject("Corner Minimap", typeof(RectTransform), typeof(CanvasGroup));
		var rect = (RectTransform)go.transform;
		rect.SetParent(canvasRoot, false);
		rect.anchorMin = new Vector2(0f, 1f);
		rect.anchorMax = new Vector2(0f, 1f);
		rect.pivot = new Vector2(0f, 1f);
		rect.anchoredPosition = new Vector2(Margin, -Margin);
		rect.sizeDelta = new Vector2(Diameter, Diameter);
		// Under the M map, which slides up over it.
		rect.SetAsFirstSibling();
		CornerMinimap radar = go.AddComponent<CornerMinimap>();
		radar.Build(picture, arrow);
		return radar;
	}

	private void Build(Texture picture, Texture arrow)
	{
		root = (RectTransform)transform;
		group = GetComponent<CanvasGroup>();
		group.alpha = 0f;
		group.interactable = false;
		group.blocksRaycasts = false;

		// A dark rim, then the map masked to a disc inside it.
		RawImage rim = Disc("Rim", root, new Color(0.03f, 0.04f, 0.06f, 0.82f));
		Stretch(rim.rectTransform, -RimWidth);

		RawImage mask = Disc("Map Mask", root, Color.white);
		Stretch(mask.rectTransform, 0f);
		mask.gameObject.AddComponent<Mask>().showMaskGraphic = false;

		map = Plain("Map", mask.rectTransform, picture);
		map.color = new Color(1f, 1f, 1f, 0.94f);
		Stretch(map.rectTransform, 0f);

		// A faint inner shade so icons at the rim keep their contrast.
		RawImage shade = Disc("Range Ring", mask.rectTransform, new Color(1f, 1f, 1f, 0.12f));
		Centre(shade.rectTransform, Vector2.zero, new Vector2(Diameter * 0.5f, Diameter * 0.5f));
		RawImage hole = Disc("Range Ring Hole", shade.rectTransform, new Color(0f, 0f, 0f, 0f));
		Stretch(hole.rectTransform, -1.5f);

		iconLayer = new GameObject("Icons", typeof(RectTransform)).GetComponent<RectTransform>();
		iconLayer.SetParent(mask.rectTransform, false);
		Stretch(iconLayer, 0f);

		selfImage = Plain("Self", root, arrow);
		self = selfImage.rectTransform;
		Centre(self, Vector2.zero, new Vector2(SelfPixels, SelfPixels));
		Outline outline = selfImage.gameObject.AddComponent<Outline>();
		outline.effectColor = new Color(0f, 0f, 0f, 0.8f);
		outline.effectDistance = new Vector2(1f, -1f);

		Text north = new GameObject("North", typeof(RectTransform)).AddComponent<Text>();
		north.rectTransform.SetParent(root, false);
		north.rectTransform.anchorMin = new Vector2(0.5f, 1f);
		north.rectTransform.anchorMax = new Vector2(0.5f, 1f);
		north.rectTransform.pivot = new Vector2(0.5f, 0.5f);
		north.rectTransform.anchoredPosition = new Vector2(0f, -10f);
		north.rectTransform.sizeDelta = new Vector2(24f, 20f);
		north.font = HudFont();
		north.fontSize = 15;
		north.fontStyle = FontStyle.Bold;
		north.alignment = TextAnchor.MiddleCenter;
		north.color = new Color(1f, 1f, 1f, 0.9f);
		north.text = "N";
		north.raycastTarget = false;
		north.gameObject.AddComponent<Shadow>().effectColor = new Color(0f, 0f, 0f, 0.9f);
	}

	private void LateUpdate()
	{
		bool showing = ShouldShow(out Actor actor);
		group.alpha = Mathf.MoveTowards(group.alpha, showing ? 1f : 0f, Time.unscaledDeltaTime * FadeSpeed);
		if (!showing || MinimapCamera.instance == null)
		{
			return;
		}

		Camera camera = MinimapCamera.instance.camera;
		Vector3 here = actor.Position();
		Vector3 centre = camera.WorldToViewportPoint(here);
		Vector3 edge = camera.WorldToViewportPoint(here + Vector3.right * RadiusMetres);
		float halfUv = Mathf.Abs(edge.x - centre.x);
		if (halfUv <= 1e-5f)
		{
			return;
		}
		map.uvRect = new Rect(centre.x - halfUv, centre.y - halfUv, halfUv * 2f, halfUv * 2f);

		int team = actor.team;
		selfImage.color = ColorScheme.SelfBlipColor(team);
		MinimapUi.SetHeading(self, Heading());

		used = 0;
		Dictionary<Transform, MinimapMarker>.ValueCollection markers = MinimapUi.Markers;
		if (markers != null)
		{
			foreach (MinimapMarker marker in markers)
			{
				if (marker != null && marker.Subject != null && marker.Subject.gameObject.activeInHierarchy)
				{
					DrawMarker(marker, camera, centre, halfUv);
				}
			}
		}
		for (int i = used; i < pool.Count; i++)
		{
			if (pool[i].enabled)
			{
				pool[i].enabled = false;
			}
		}
	}

	private void DrawMarker(MinimapMarker marker, Camera camera, Vector3 centre, float halfUv)
	{
		Vector3 viewport = camera.WorldToViewportPoint(marker.Subject.position);
		// -1..1 across the disc, +y north.
		Vector2 offset = new Vector2(viewport.x - centre.x, viewport.y - centre.y) / halfUv;
		float reach = offset.magnitude;
		float size;
		Texture picture = null;
		bool turn = false;
		switch (marker.Kind)
		{
		case MinimapMarkerKind.CapturePoint:
			size = FlagPixels;
			if (reach > FlagRimInset)
			{
				offset *= FlagRimInset / reach;
			}
			break;
		case MinimapMarkerKind.Vehicle:
			if (reach > 1f)
			{
				return;
			}
			size = VehiclePixels;
			picture = marker.Picture;
			turn = true;
			break;
		default:
			if (reach > 1f)
			{
				return;
			}
			size = marker.IsHuman ? HumanPixels : SoldierPixels;
			break;
		}

		RawImage icon = NextIcon();
		icon.texture = picture != null ? picture : Circle();
		Color colour = marker.Color;
		colour.a = 1f;
		icon.color = colour;
		RectTransform rect = icon.rectTransform;
		MinimapUi.SetSquareSize(rect, size);
		rect.anchoredPosition = offset * (Diameter * 0.5f);
		if (turn)
		{
			Vector3 forward = marker.Subject.forward;
			forward.y = 0f;
			MinimapUi.SetHeading(rect, forward.sqrMagnitude > 1e-6f ? Mathf.Atan2(forward.x, forward.z) * Mathf.Rad2Deg : 0f);
		}
		else if (rect.localRotation != Quaternion.identity)
		{
			rect.localRotation = Quaternion.identity;
		}
		// Flags over everything else, as on the M map.
		if (marker.Kind == MinimapMarkerKind.CapturePoint)
		{
			rect.SetAsLastSibling();
		}
	}

	private RawImage NextIcon()
	{
		if (used < pool.Count)
		{
			RawImage reused = pool[used++];
			if (!reused.enabled)
			{
				reused.enabled = true;
			}
			return reused;
		}
		RawImage icon = Plain("Icon", iconLayer, Circle());
		RectTransform rect = icon.rectTransform;
		rect.anchorMin = new Vector2(0.5f, 0.5f);
		rect.anchorMax = new Vector2(0.5f, 0.5f);
		rect.pivot = new Vector2(0.5f, 0.5f);
		Outline outline = icon.gameObject.AddComponent<Outline>();
		outline.effectColor = new Color(0.02f, 0.03f, 0.05f, 0.85f);
		outline.effectDistance = new Vector2(1f, -1f);
		pool.Add(icon);
		used++;
		return icon;
	}

	private static bool ShouldShow(out Actor actor)
	{
		actor = null;
		FpsActorController player = FpsActorController.instance;
		if (player == null || player.actor == null || player.actor.dead)
		{
			return false;
		}
		if (!IngameUi.IsShown || LoadoutUi.IsOpen() || MinimapUi.CurrentOpenness > 0.02f)
		{
			return false;
		}
		actor = player.actor;
		return true;
	}

	/// <summary>Where the player is looking, in compass degrees: the camera, not the body.</summary>
	private static float Heading()
	{
		FpsActorController player = FpsActorController.instance;
		Camera view = Camera.main;
		if (view == null && player != null)
		{
			view = player.fpCamera;
		}
		Vector3 forward = view != null ? view.transform.forward : player.actor.transform.forward;
		forward.y = 0f;
		return forward.sqrMagnitude > 1e-6f ? Mathf.Atan2(forward.x, forward.z) * Mathf.Rad2Deg : 0f;
	}

	private static RawImage Disc(string name, RectTransform parent, Color colour)
	{
		RawImage image = Plain(name, parent, Circle());
		image.color = colour;
		return image;
	}

	private static RawImage Plain(string name, RectTransform parent, Texture texture)
	{
		var go = new GameObject(name, typeof(RectTransform), typeof(CanvasRenderer), typeof(RawImage));
		go.transform.SetParent(parent, false);
		RawImage image = go.GetComponent<RawImage>();
		image.texture = texture;
		image.raycastTarget = false;
		return image;
	}

	private static void Stretch(RectTransform rect, float inset)
	{
		rect.anchorMin = Vector2.zero;
		rect.anchorMax = Vector2.one;
		rect.offsetMin = new Vector2(inset, inset);
		rect.offsetMax = new Vector2(-inset, -inset);
	}

	private static void Centre(RectTransform rect, Vector2 position, Vector2 size)
	{
		rect.anchorMin = new Vector2(0.5f, 0.5f);
		rect.anchorMax = new Vector2(0.5f, 0.5f);
		rect.pivot = new Vector2(0.5f, 0.5f);
		rect.anchoredPosition = position;
		rect.sizeDelta = size;
	}

	private static Font HudFont()
	{
		IngameUi hud = IngameUi.instance;
		if (hud != null && hud.health != null && hud.health.font != null)
		{
			return hud.health.font;
		}
		return Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
	}

	/// <summary>A white disc with a soft one-texel edge, made once.</summary>
	private static Texture2D Circle()
	{
		if (circle != null)
		{
			return circle;
		}
		const int size = 128;
		circle = new Texture2D(size, size, TextureFormat.RGBA32, false);
		circle.name = "Corner Minimap Disc";
		circle.wrapMode = TextureWrapMode.Clamp;
		var pixels = new Color32[size * size];
		float radius = size * 0.5f - 1f;
		for (int y = 0; y < size; y++)
		{
			for (int x = 0; x < size; x++)
			{
				float dx = x + 0.5f - size * 0.5f;
				float dy = y + 0.5f - size * 0.5f;
				float alpha = Mathf.Clamp01(radius - Mathf.Sqrt(dx * dx + dy * dy) + 0.5f);
				pixels[y * size + x] = new Color32(255, 255, 255, (byte)(alpha * 255f));
			}
		}
		circle.SetPixels32(pixels);
		circle.Apply();
		return circle;
	}
}
