using System.Collections.Generic;
using Ironfront.Net.Unity;
using UnityEngine;
using UnityEngine.UI;

/// <summary>
/// The always-on round radar in the top-left corner, in the shooter convention: the ground around
/// the player turns with the camera, the player's arrow sits in the middle pointing up, and only
/// what is within <see cref="RadiusMetres"/> is drawn. The M map is unchanged.
/// </summary>
/// <remarks>
/// <para>
/// <b>Player-relative, not north-up</b> (owner report 2026-10-03: the first north-up version with
/// every flag pinned to its rim was "wrong content"). A shooter's radar answers "what is around me
/// and which way", so it turns with the view the way Battlefield's and Call of Duty's do; the
/// compass letters on the bezel keep north findable and the M map stays north-up for planning.
/// </para>
/// <para>
/// <b>Nothing out of range is drawn.</b> A flag beyond the rim is simply not on the radar; the
/// M map and the top-right flag indicator cover the rest of the battlefield.
/// </para>
/// <para>
/// <b>Built in code, by <see cref="MinimapUi"/>.</b> It needs the M map's picture (one render of the
/// level from <see cref="MinimapCamera"/>) and the same icons, so it reads both from
/// <see cref="MinimapUi"/> instead of rendering or deciding visibility a second time: an enemy hidden
/// on the M map is hidden here too.
/// </para>
/// </remarks>
public class CornerMinimap : MonoBehaviour
{
	/// <summary>Diameter of the map disc on a 1920x1080 reference screen, bezel excluded.</summary>
	public const float Diameter = 216f;

	/// <summary>Gap from the screen's top and left edges.</summary>
	public const float Margin = 24f;

	/// <summary>World metres from the centre to the rim.</summary>
	public const float RadiusMetres = 110f;

	/// <summary>Wide enough to carry the compass letters without covering the map.</summary>
	private const float BezelWidth = 20f;

	/// <summary>
	/// One soldier's icon: the M map's smallest soldier (<see cref="MinimapIconLayout.MinSoldierPixels"/>),
	/// so every other icon below keeps the M map's proportions to it.
	/// </summary>
	private static readonly float Soldier = MinimapIconLayout.MinSoldierPixels;

	private static readonly float FlagPixels = Soldier * MinimapIconLayout.FlagScale;

	private static readonly float HumanPixels = Soldier * MinimapIconLayout.HumanScale;

	private static readonly float VehiclePixels = Soldier * MinimapIconLayout.VehicleScale;

	private static readonly float SelfPixels = Soldier * MinimapIconLayout.SelfScale;

	/// <summary>A supply cache's icon: a soldier's size, smaller than a flag.</summary>
	private static readonly float SupplyPixels = Soldier * 1.1f;

	/// <summary>A cache that does not serve the player's side: still findable, plainly off.</summary>
	private static readonly Color InactiveSupplyTint = new Color(0.42f, 0.42f, 0.42f, 1f);

	/// <summary>A crate left in the field (phase P32): gold, because it serves whoever reaches it.</summary>
	private static readonly Color FieldCrateTint = new Color(1f, 0.78f, 0.25f, 1f);

	private static Texture2D ammoIcon;

	private static Texture2D medicalIcon;

	/// <summary>The M map lightens the player's colour this much for the view cone (ActorBlip).</summary>
	private const float ViewConeLightening = 0.35f;

	private const float FadeSpeed = 8f;

	/// <summary>The M map's own tint on the same picture.</summary>
	private static readonly Color MapTint = new Color(1f, 1f, 1f, 0.94f);

	private static readonly Color BezelFill = new Color(0.03f, 0.05f, 0.08f, 0.88f);

	private static readonly Color BezelEdge = new Color(0.47f, 0.72f, 0.92f, 0.55f);

	private static readonly Color NorthInk = new Color(1f, 0.46f, 0.1f, 1f);

	private static readonly Color CardinalInk = new Color(0.9f, 0.95f, 1f, 0.85f);

	private static Texture2D circle;


	private static readonly Dictionary<int, Texture2D> rings = new Dictionary<int, Texture2D>();

	private RectTransform root;

	private CanvasGroup group;

	private RectTransform mapPivot;

	private RawImage map;

	private RectTransform iconLayer;

	private RawImage selfImage;

	private Texture arrowTexture;

	private Texture flagTexture;

	private Rect flagUv = new Rect(0f, 0f, 1f, 1f);

	private MinimapViewCone viewCone;

	private RectTransform viewConeRect;

	private RawImage halo;

	private readonly Text[] cardinals = new Text[4];

	private readonly List<RawImage> pool = new List<RawImage>();

	private int used;

	/// <summary>Adds the radar under <paramref name="canvasRoot"/> and returns it.</summary>
	public static CornerMinimap Create(RectTransform canvasRoot, Texture picture, MinimapUi map)
	{
		var go = new GameObject("Corner Minimap", typeof(RectTransform), typeof(CanvasGroup));
		var rect = (RectTransform)go.transform;
		rect.SetParent(canvasRoot, false);
		rect.anchorMin = new Vector2(0f, 1f);
		rect.anchorMax = new Vector2(0f, 1f);
		rect.pivot = new Vector2(0f, 1f);
		rect.anchoredPosition = new Vector2(Margin + BezelWidth, -Margin - BezelWidth);
		rect.sizeDelta = new Vector2(Diameter, Diameter);
		// Under the M map, which slides up over it.
		rect.SetAsFirstSibling();
		// Its own canvas: the radar moves every icon every frame, and a nested canvas rebuilds only
		// the radar's mesh instead of everything the M map's canvas holds. Drawn in hierarchy order.
		go.AddComponent<Canvas>();
		CornerMinimap radar = go.AddComponent<CornerMinimap>();
		radar.Build(picture, map);
		return radar;
	}

	private void Build(Texture picture, MinimapUi source)
	{
		root = (RectTransform)transform;
		// The M map's own artwork: the soldier arrow, the player's arrow and view cone, the flag.
		ActorBlip blip = source.actorBlipPrefab != null ? source.actorBlipPrefab.GetComponent<ActorBlip>() : null;
		RawImage blipImage = source.actorBlipPrefab != null ? source.actorBlipPrefab.GetComponent<RawImage>() : null;
		arrowTexture = blipImage != null ? blipImage.texture : null;
		Texture selfTexture = blip != null && blip.selfBlip != null ? blip.selfBlip : arrowTexture;
		ReadFlagArtwork(source.capturePointMarkerPrefab != null ? source.capturePointMarkerPrefab : source.minimapSpawnPointPrefab);
		group = GetComponent<CanvasGroup>();
		group.alpha = 0f;
		group.interactable = false;
		group.blocksRaycasts = false;

		// A dark backing behind the whole disc; the bezel ring itself is drawn OVER the map below,
		// because a stencil mask cuts a hard, stepped edge and the ring hides it.
		RawImage backing = Disc("Backing", root, BezelFill);
		Stretch(backing.rectTransform, -BezelWidth);

		RawImage mask = Disc("Map Mask", root, Color.white);
		Stretch(mask.rectTransform, 0f);
		mask.gameObject.AddComponent<Mask>().showMaskGraphic = false;

		// The picture turns under a fixed mask, so it is drawn on a square big enough that a
		// turned copy still covers the whole disc.
		mapPivot = new GameObject("Map Pivot", typeof(RectTransform)).GetComponent<RectTransform>();
		mapPivot.SetParent(mask.rectTransform, false);
		Centre(mapPivot, Vector2.zero, new Vector2(Diameter * Mathf.Sqrt(2f), Diameter * Mathf.Sqrt(2f)));
		map = Plain("Map", mapPivot, picture);
		map.color = MapTint;
		Stretch(map.rectTransform, 0f);

		if (blip != null && blip.sightConePrefab != null)
		{
			GameObject decoration = Object.Instantiate(blip.sightConePrefab, mask.rectTransform, false);
			viewConeRect = (RectTransform)decoration.transform;
			float reach = Soldier * MinimapIconLayout.ViewConeReachScale;
			Centre(viewConeRect, Vector2.zero, new Vector2(reach * 2f, reach * 2f));
			viewCone = decoration.GetComponent<MinimapViewCone>();
			halo = decoration.GetComponentInChildren<RawImage>(true);
			if (halo != null)
			{
				MinimapUi.SetSquareSize(halo.rectTransform, Soldier * MinimapIconLayout.HaloScale);
			}
		}

		iconLayer = new GameObject("Icons", typeof(RectTransform)).GetComponent<RectTransform>();
		iconLayer.SetParent(mask.rectTransform, false);
		Stretch(iconLayer, 0f);

		float outer = Diameter * 0.5f + BezelWidth;
		RawImage bezel = Plain("Bezel", root, Ring((Diameter * 0.5f - 1.5f) / outer));
		bezel.color = BezelFill;
		Stretch(bezel.rectTransform, -BezelWidth);
		RawImage edgeLine = Plain("Bezel Edge", root, Ring((outer - 1.5f) / (outer + 0.5f)));
		edgeLine.color = BezelEdge;
		Stretch(edgeLine.rectTransform, -BezelWidth - 0.5f);
		RawImage innerLine = Plain("Bezel Inner Edge", root, Ring((Diameter * 0.5f - 1.5f) / (Diameter * 0.5f)));
		innerLine.color = new Color(BezelEdge.r, BezelEdge.g, BezelEdge.b, 0.3f);
		Stretch(innerLine.rectTransform, 0f);

		selfImage = Plain("Self", root, selfTexture);
		Centre(selfImage.rectTransform, Vector2.zero, new Vector2(SelfPixels, SelfPixels));
		Outline outline = selfImage.gameObject.AddComponent<Outline>();
		outline.effectColor = new Color(0f, 0f, 0f, 0.85f);
		outline.effectDistance = new Vector2(1.2f, -1.2f);

		string[] letters = { "N", "E", "S", "W" };
		for (int i = 0; i < 4; i++)
		{
			Text letter = new GameObject("Cardinal " + letters[i], typeof(RectTransform)).AddComponent<Text>();
			letter.rectTransform.SetParent(root, false);
			Centre(letter.rectTransform, Vector2.zero, new Vector2(28f, 26f));
			letter.font = HudFont();
			letter.fontSize = i == 0 ? 20 : 17;
			letter.fontStyle = FontStyle.Bold;
			letter.alignment = TextAnchor.MiddleCenter;
			letter.color = i == 0 ? NorthInk : CardinalInk;
			letter.text = letters[i];
			letter.raycastTarget = false;
			Outline letterEdge = letter.gameObject.AddComponent<Outline>();
			letterEdge.effectColor = new Color(0f, 0f, 0f, 0.95f);
			letterEdge.effectDistance = new Vector2(1.2f, -1.2f);
			cardinals[i] = letter;
		}
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

		float heading = Heading();
		// The pivot is sqrt(2) times the disc, so its window is too.
		float pivotHalf = halfUv * Mathf.Sqrt(2f);
		map.uvRect = new Rect(centre.x - pivotHalf, centre.y - pivotHalf, pivotHalf * 2f, pivotHalf * 2f);
		// Counter-clockwise by the heading turns the direction the player faces to the top.
		mapPivot.localRotation = Quaternion.Euler(0f, 0f, heading);

		selfImage.color = ColorScheme.SelfBlipColor(actor.team);
		if (viewCone != null)
		{
			viewCone.color = Color.Lerp(ColorScheme.SelfBlipColor(actor.team), Color.white, ViewConeLightening);
			viewCone.FieldOfViewDegrees = MinimapIconLayout.HorizontalFieldOfView(Camera.main);
		}
		if (halo != null)
		{
			halo.color = ColorScheme.TeamColor(actor.team);
			halo.canvasRenderer.SetAlpha(Mathf.Lerp(0.45f, 0.95f, Mathf.PingPong(Time.unscaledTime * 1.2f, 1f)));
		}

		float rim = Diameter * 0.5f + BezelWidth * 0.5f;
		for (int i = 0; i < 4; i++)
		{
			Vector2 direction = Turn(new Vector2(Mathf.Sin(i * 90f * Mathf.Deg2Rad), Mathf.Cos(i * 90f * Mathf.Deg2Rad)), heading);
			cardinals[i].rectTransform.anchoredPosition = direction * rim;
		}

		used = 0;
		DrawSupplies(camera, centre, halfUv, heading, actor.team);
		DrawFlags(camera, centre, halfUv, heading);
		Dictionary<Transform, MinimapMarker>.ValueCollection markers = MinimapUi.Markers;
		if (markers != null)
		{
			foreach (MinimapMarker marker in markers)
			{
				if (marker != null && marker.Subject != null && marker.Subject.gameObject.activeInHierarchy)
				{
					DrawMarker(marker, camera, centre, halfUv, heading);
				}
			}
		}
		if (!Ironfront.Net.Unity.NetContext.IsClient)
		{
			DrawLocalTeamMates(actor, camera, centre, halfUv, heading);
		}
		for (int i = used; i < pool.Count; i++)
		{
			if (pool[i].enabled)
			{
				pool[i].enabled = false;
			}
		}
	}

	/// <summary>Where a world point lands on the disc, -1..1 with the view up; false beyond the rim.</summary>
	private static bool OnRadar(Camera camera, Vector3 world, Vector3 centre, float halfUv, float heading, float inset, out Vector2 offset)
	{
		Vector3 viewport = camera.WorldToViewportPoint(world);
		offset = Turn(new Vector2(viewport.x - centre.x, viewport.y - centre.y) / halfUv, heading);
		return offset.sqrMagnitude <= inset * inset;
	}

	private void DrawMarker(MinimapMarker marker, Camera camera, Vector3 centre, float halfUv, float heading)
	{
		Vector2 offset;
		switch (marker.Kind)
		{
		case MinimapMarkerKind.CapturePoint:
			// Drawn by DrawFlags from the spawn points themselves.
			break;
		case MinimapMarkerKind.Vehicle:
			if (OnRadar(camera, marker.Subject.position, centre, halfUv, heading, 0.95f, out offset))
			{
				Place(marker.Picture != null ? marker.Picture : Circle(), marker.Color, VehiclePixels, offset, marker.Subject, heading, false, FullUv);
			}
			break;
		default:
			if (OnRadar(camera, marker.Subject.position, centre, halfUv, heading, 0.95f, out offset))
			{
				Texture picture = marker.Picture != null ? marker.Picture : arrowTexture;
				Place(picture != null ? picture : Circle(), marker.Color, marker.IsHuman ? HumanPixels : Soldier, offset, picture != null ? marker.Subject : null, heading, false, FullUv);
			}
			break;
		}
	}

	// Every ammo dump and medical station in range: bright when it serves the player's side, dimmed
	// while its flag is neutral or the enemy's, so the radar says where to refill and which crates
	// are worth retaking.
	private void DrawSupplies(Camera camera, Vector3 centre, float halfUv, float heading, int team)
	{
		IReadOnlyList<SupplyCache> caches = SupplyCache.All;
		for (int i = 0; i < caches.Count; i++)
		{
			SupplyCache cache = caches[i];
			if (cache != null && OnRadar(camera, cache.transform.position, centre, halfUv, heading, 0.95f, out Vector2 offset))
			{
				Color tint = cache.ServedTeam == team ? Color.white : InactiveSupplyTint;
				Place(SupplyIcon(cache.kind), tint, SupplyPixels, offset, null, heading, false, FullUv);
			}
		}

		IReadOnlyList<FieldCrate> crates = FieldCrate.All;
		for (int i = 0; i < crates.Count; i++)
		{
			FieldCrate crate = crates[i];
			if (crate != null && OnRadar(camera, crate.transform.position, centre, halfUv, heading, 0.95f, out Vector2 offset))
			{
				Place(SupplyIcon(crate.kind), FieldCrateTint, SupplyPixels, offset, null, heading, false, FullUv);
			}
		}
	}

	// Every flag and HQ, read off the spawn points the M map draws its buttons from, in the owner's
	// colour. Not from the markers: a point gets its marker from CapturePoint.SetOwner, and the
	// first SetOwner (at Start) runs before this HUD exists, so a flag that has not changed hands
	// since the map loaded -- every HQ, every flag at kick-off -- has no marker at all. Seen online
	// 2026-10-03: standing on a held flag with no flag on the radar.
	private void DrawFlags(Camera camera, Vector3 centre, float halfUv, float heading)
	{
		if (ActorManager.instance == null || ActorManager.instance.spawnPoints == null)
		{
			return;
		}
		Texture picture = flagTexture != null ? flagTexture : Circle();
		Rect uv = flagTexture != null ? flagUv : FullUv;
		foreach (SpawnPoint point in ActorManager.instance.spawnPoints)
		{
			if (point != null && OnRadar(camera, point.transform.position, centre, halfUv, heading, 0.92f, out Vector2 offset))
			{
				Place(picture, ColorScheme.TeamColor(point.owner), FlagPixels, offset, null, heading, true, uv);
			}
		}
	}

	// Offline, or a host on its own server, the M map draws soldiers through ActorBlip rather than
	// markers, so team-mates are read off the actor list. Enemies stay off, as ActorBlip keeps them
	// off the map.
	private void DrawLocalTeamMates(Actor player, Camera camera, Vector3 centre, float halfUv, float heading)
	{
		if (ActorManager.instance == null || ActorManager.instance.actors == null)
		{
			return;
		}
		List<Actor> actors = ActorManager.instance.actors;
		for (int i = 0; i < actors.Count; i++)
		{
			Actor mate = actors[i];
			if (mate == null || mate == player || mate.dead || mate.team != player.team || mate.IsSeated())
			{
				continue;
			}
			if (OnRadar(camera, mate.Position(), centre, halfUv, heading, 0.95f, out Vector2 offset))
			{
				Texture picture = arrowTexture != null ? arrowTexture : Circle();
				Place(picture, ColorScheme.BlipColor(mate.team, !mate.aiControlled), mate.aiControlled ? Soldier : HumanPixels, offset, arrowTexture != null ? mate.transform : null, heading, false, FullUv);
			}
		}
	}

	private static readonly Rect FullUv = new Rect(0f, 0f, 1f, 1f);

	private void Place(Texture picture, Color colour, float size, Vector2 offset, Transform turnWith, float heading, bool onTop, Rect uv)
	{
		RawImage icon = NextIcon();
		if (icon.texture != picture)
		{
			icon.texture = picture;
		}
		if (icon.uvRect != uv)
		{
			icon.uvRect = uv;
		}
		colour.a = 1f;
		icon.color = colour;
		RectTransform rect = icon.rectTransform;
		MinimapUi.SetSquareSize(rect, size);
		rect.anchoredPosition = offset * (Diameter * 0.5f);
		float turn = 0f;
		if (turnWith != null)
		{
			Vector3 forward = turnWith.forward;
			forward.y = 0f;
			if (forward.sqrMagnitude > 1e-6f)
			{
				turn = Mathf.Atan2(forward.x, forward.z) * Mathf.Rad2Deg - heading;
			}
		}
		rect.localRotation = Quaternion.Euler(0f, 0f, 0f - turn);
		if (onTop)
		{
			rect.SetAsLastSibling();
		}
	}

	/// <summary>Turns a north-up offset by the view heading, so what the camera faces is up.</summary>
	private static Vector2 Turn(Vector2 northUp, float heading)
	{
		float radians = heading * Mathf.Deg2Rad;
		float cos = Mathf.Cos(radians);
		float sin = Mathf.Sin(radians);
		return new Vector2(northUp.x * cos - northUp.y * sin, northUp.x * sin + northUp.y * cos);
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
		outline.effectColor = new Color(0.02f, 0.03f, 0.05f, 0.9f);
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
		circle = NewTexture("Corner Minimap Disc", size, size);
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

	/// <summary>
	/// The M map's flag picture, from whichever graphic its flag prefab carries: a RawImage's
	/// texture, or an Image's sprite (with that sprite's place in its atlas).
	/// </summary>
	private void ReadFlagArtwork(GameObject prefab)
	{
		if (prefab == null)
		{
			return;
		}
		RawImage raw = prefab.GetComponent<RawImage>();
		if (raw != null && raw.texture != null)
		{
			flagTexture = raw.texture;
			flagUv = raw.uvRect;
			return;
		}
		Image image = prefab.GetComponent<Image>();
		if (image != null && image.sprite != null)
		{
			Sprite sprite = image.sprite;
			flagTexture = sprite.texture;
			Rect r = sprite.textureRect;
			flagUv = new Rect(r.x / flagTexture.width, r.y / flagTexture.height, r.width / flagTexture.width, r.height / flagTexture.height);
		}
	}

	/// <summary>
	/// A white annulus whose inner edge sits at <paramref name="innerRatio"/> of its radius, both
	/// edges anti-aliased, made once per ratio.
	/// </summary>
	private static Texture2D Ring(float innerRatio)
	{
		int key = Mathf.RoundToInt(innerRatio * 1000f);
		if (rings.TryGetValue(key, out Texture2D made) && made != null)
		{
			return made;
		}
		const int size = 512;
		Texture2D ring = NewTexture("Corner Minimap Ring " + key, size, size);
		var pixels = new Color32[size * size];
		float outer = size * 0.5f - 1f;
		float inner = outer * innerRatio;
		for (int y = 0; y < size; y++)
		{
			for (int x = 0; x < size; x++)
			{
				float dx = x + 0.5f - size * 0.5f;
				float dy = y + 0.5f - size * 0.5f;
				float distance = Mathf.Sqrt(dx * dx + dy * dy);
				float alpha = Mathf.Clamp01(outer - distance + 0.5f) * Mathf.Clamp01(distance - inner + 0.5f);
				pixels[y * size + x] = new Color32(255, 255, 255, (byte)(alpha * 255f));
			}
		}
		ring.SetPixels32(pixels);
		ring.Apply();
		rings[key] = ring;
		return ring;
	}

	/// <summary>
	/// The supply icons, drawn once: a white tile with a red cross for medical, an olive tile with
	/// three brass rounds for ammunition. Both carry their own colours, so the tint only dims them.
	/// </summary>
	private static Texture2D SupplyIcon(SupplyKind kind)
	{
		if (kind == SupplyKind.Medical)
		{
			return medicalIcon != null ? medicalIcon : (medicalIcon = DrawSupplyIcon(kind));
		}
		return ammoIcon != null ? ammoIcon : (ammoIcon = DrawSupplyIcon(kind));
	}

	private static Texture2D DrawSupplyIcon(SupplyKind kind)
	{
		const int size = 64;
		const float corner = 12f;
		bool medical = kind == SupplyKind.Medical;
		Color tile = medical ? new Color(0.96f, 0.96f, 0.94f) : new Color(0.27f, 0.33f, 0.18f);
		Color mark = medical ? new Color(0.86f, 0.1f, 0.1f) : new Color(0.95f, 0.76f, 0.3f);
		Texture2D texture = NewTexture(medical ? "Corner Minimap Medical" : "Corner Minimap Ammo", size, size);
		var pixels = new Color32[size * size];
		for (int y = 0; y < size; y++)
		{
			for (int x = 0; x < size; x++)
			{
				float px = x + 0.5f;
				float py = y + 0.5f;
				// Rounded square, one-texel soft edge.
				float dx = Mathf.Max(Mathf.Max(corner - px, px - (size - corner)), 0f);
				float dy = Mathf.Max(Mathf.Max(corner - py, py - (size - corner)), 0f);
				float alpha = Mathf.Clamp01(corner - Mathf.Sqrt(dx * dx + dy * dy) + 0.5f);
				bool onMark = medical ? OnCross(px, py, size) : OnRounds(px, py, size);
				Color colour = onMark ? mark : tile;
				colour.a = alpha;
				pixels[y * size + x] = colour;
			}
		}
		texture.SetPixels32(pixels);
		texture.Apply();
		return texture;
	}

	private static bool OnCross(float x, float y, int size)
	{
		float c = size * 0.5f;
		float arm = size * 0.33f;
		float half = size * 0.11f;
		return (Mathf.Abs(x - c) < half && Mathf.Abs(y - c) < arm) || (Mathf.Abs(y - c) < half && Mathf.Abs(x - c) < arm);
	}

	// Three upright rounds: a body and a pointed tip each.
	private static bool OnRounds(float x, float y, int size)
	{
		float bottom = size * 0.2f;
		float shoulder = size * 0.62f;
		float top = size * 0.8f;
		float halfWidth = size * 0.07f;
		for (int i = -1; i <= 1; i++)
		{
			float centre = size * 0.5f + i * size * 0.22f;
			float across = Mathf.Abs(x - centre);
			if (y >= bottom && y <= shoulder && across <= halfWidth)
			{
				return true;
			}
			if (y > shoulder && y <= top && across <= halfWidth * (top - y) / (top - shoulder))
			{
				return true;
			}
		}
		return false;
	}

	private static Texture2D NewTexture(string name, int width, int height)
	{
		var texture = new Texture2D(width, height, TextureFormat.RGBA32, false);
		texture.name = name;
		texture.wrapMode = TextureWrapMode.Clamp;
		return texture;
	}
}
