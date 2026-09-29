using Ironfront.Net.Unity;
using UnityEngine;
using UnityEngine.UI;

/// <summary>
/// A local <see cref="Actor"/>'s minimap icon: the player's own arrow, or an offline bot's.
/// </summary>
/// <remarks>
/// <para>
/// <b>The player's own icon is built to be found first</b> (owner report 2026-09-29): a larger
/// white arrow on top of every other icon, a pulsing ring in the team's colour behind it, and a
/// view cone as wide as the camera really sees. The original drew a 16 px additive arrow UNDER
/// every flag, so a player standing on a capture point had no icon at all.
/// </para>
/// <para>
/// <b>The look direction comes from whichever camera is drawing</b>, not from
/// <c>Camera.main</c> alone. A seated player whose vehicle camera was not tagged MainCamera left
/// <c>Camera.main</c> null, and this threw a NullReferenceException every frame: 1162 of them in
/// one sinking helicopter on the v2.0.0 release (friend's log, 2026-09-29, the red wall in the
/// console).
/// </para>
/// </remarks>
public class ActorBlip : MonoBehaviour
{
	/// <summary>The player's view cone and team ring, drawn under the arrow.</summary>
	public GameObject sightConePrefab;

	/// <summary>The player's own arrow: the soldier arrow with a heavier rim, drawn white.</summary>
	public Texture selfBlip;

	/// <summary>A soft round dot for movement trails; read by <see cref="MinimapMarker"/> too.</summary>
	public Texture trailDot;

	private const int SelfTrailLength = 9;

	private const int SoldierTrailLength = 7;

	/// <summary>How far the view cone's colour is lightened from the player's own shade.</summary>
	private const float ViewConeLightening = 0.35f;

	private Actor actor;

	private RawImage image;

	private Texture infantryBlip;

	private bool isSelf;

	private RectTransform selfDecoration;

	private MinimapViewCone viewCone;

	private RawImage halo;

	private MinimapTrail trail;

	// The team the icon was last coloured for. A networked client's own body learns its team
	// from the first snapshot, after ActorManager.Register has already made this icon, so a
	// colour taken once at SetActor stayed the grey of "no team" for the whole match.
	private int colouredTeam = int.MinValue;

	private void Awake()
	{
		image = GetComponent<RawImage>();
		infantryBlip = image.texture;
		image.raycastTarget = false;
		image.rectTransform.anchoredPosition = Vector2.zero;
	}

	public void SetActor(Actor actor, bool isSelf)
	{
		if (actor.GetType() == typeof(ForcedAiTarget))
		{
			base.enabled = false;
			return;
		}
		this.actor = actor;
		this.isSelf = isSelf;
		trail = new MinimapTrail(MinimapUi.MapRect, trailDot, isSelf ? SelfTrailLength : SoldierTrailLength);
		if (isSelf)
		{
			RectTransform layer = MinimapUi.SelfLayer;
			if (layer != null)
			{
				base.transform.SetParent(layer, false);
			}
			if (selfBlip != null)
			{
				image.texture = selfBlip;
			}
			if (sightConePrefab != null)
			{
				GameObject decoration = Object.Instantiate(sightConePrefab, base.transform.parent);
				selfDecoration = (RectTransform)decoration.transform;
				selfDecoration.pivot = new Vector2(0.5f, 0.5f);
				selfDecoration.anchoredPosition = Vector2.zero;
				viewCone = decoration.GetComponent<MinimapViewCone>();
				halo = decoration.GetComponentInChildren<RawImage>(true);
			}
			// The arrow above its own cone and ring, and above every other icon on the map.
			base.transform.SetAsLastSibling();
		}
		Recolour();
	}

	private void Recolour()
	{
		colouredTeam = actor.team;
		// The player's own arrow is its side's colour made bright and light (owner ruling
		// 2026-09-29: not white), larger than a team-mate's and with a pulsing ring behind it, so it
		// is the first icon found and never taken for anyone else's.
		image.color = isSelf ? ColorScheme.SelfBlipColor(actor.team) : ColorScheme.BlipColor(actor.team, !actor.aiControlled);
		if (halo != null)
		{
			halo.color = ColorScheme.TeamColor(actor.team);
		}
		if (viewCone != null)
		{
			viewCone.color = Color.Lerp(ColorScheme.SelfBlipColor(actor.team), Color.white, ViewConeLightening);
		}
	}

	private void LateUpdate()
	{
		if (actor != null && actor.team != colouredTeam)
		{
			Recolour();
		}
		MinimapCamera minimapCamera = MinimapCamera.instance;
		bool visible = actor != null && !actor.dead && minimapCamera != null
			&& (actor.team == FpsActorController.playerTeam || actor.IsHighlighted());
		// A seated soldier is drawn by its vehicle's icon; the player keeps the arrow on top of it.
		if (visible && !isSelf && actor.IsSeated())
		{
			visible = false;
		}
		if (!visible)
		{
			Hide();
			return;
		}

		RectTransform rectTransform = (RectTransform)base.transform;
		Vector3 position = actor.Position();
		Vector2 onMap = MinimapUi.ToMap(minimapCamera.camera.WorldToViewportPoint(position));
		// Zoomed in, a soldier out of view is hidden rather than pinned to the edge; the player's
		// own arrow is always in view, since the zoom is centred on it.
		if (!isSelf && MinimapUi.IsZoomed && !MinimapZoom.IsOnMap(onMap))
		{
			Hide();
			return;
		}
		// Pinned to the edge rather than clipped: the minimap frames the ground, and a boat or
		// helicopter out over the sea beyond it would otherwise vanish from the map.
		Vector2 anchor = new Vector2(Mathf.Clamp01(onMap.x), Mathf.Clamp01(onMap.y));
		rectTransform.anchorMin = anchor;
		rectTransform.anchorMax = anchor;

		float soldier = MinimapIconLayout.SoldierPixels(MinimapUi.MapWidth);
		float size = isSelf
			? soldier * MinimapIconLayout.SelfScale
			: (actor.aiControlled ? soldier : soldier * MinimapIconLayout.HumanScale);
		rectTransform.sizeDelta = new Vector2(size, size);
		rectTransform.localScale = Vector3.one;

		Camera look = isSelf ? LookCamera() : null;
		float heading;
		if (TryHeading(look, out heading))
		{
			rectTransform.rotation = Quaternion.Euler(0f, 0f, 0f - heading);
		}
		image.enabled = true;

		float now = Time.time;
		if (trail != null)
		{
			trail.Record(position, now);
			trail.Draw(minimapCamera.camera, image.color, soldier * MinimapIconLayout.TrailDotScale, true, now);
		}

		if (selfDecoration != null)
		{
			DrawSelfDecoration(anchor, soldier, heading, look);
		}
	}

	private void DrawSelfDecoration(Vector2 anchor, float soldier, float heading, Camera look)
	{
		selfDecoration.gameObject.SetActive(true);
		selfDecoration.anchorMin = anchor;
		selfDecoration.anchorMax = anchor;
		float reach = soldier * MinimapIconLayout.ViewConeReachScale;
		selfDecoration.sizeDelta = new Vector2(reach * 2f, reach * 2f);
		selfDecoration.rotation = Quaternion.Euler(0f, 0f, 0f - heading);
		if (viewCone != null)
		{
			viewCone.FieldOfViewDegrees = MinimapIconLayout.HorizontalFieldOfView(look);
		}
		if (halo != null)
		{
			float ring = soldier * MinimapIconLayout.HaloScale;
			RectTransform haloRect = halo.rectTransform;
			haloRect.sizeDelta = new Vector2(ring, ring);
			Color shade = halo.color;
			// A slow pulse: enough to catch the eye on a busy map, too slow to read as an alert.
			shade.a = Mathf.Lerp(0.45f, 0.95f, Mathf.PingPong(Time.unscaledTime * 1.2f, 1f));
			halo.color = shade;
		}
	}

	/// <summary>
	/// The icon's heading in degrees: where the player LOOKS for the player's own arrow, where
	/// the soldier faces for anyone else.
	/// </summary>
	private bool TryHeading(Camera look, out float heading)
	{
		if (look != null)
		{
			heading = look.transform.eulerAngles.y;
			return true;
		}
		Vector3 facing = actor.controller != null ? actor.controller.FacingDirection() : Vector3.zero;
		facing.y = 0f;
		if (facing.sqrMagnitude < 1e-6f)
		{
			heading = 0f;
			return false;
		}
		heading = Mathf.Atan2(facing.x, facing.z) * Mathf.Rad2Deg;
		return true;
	}

	/// <summary>
	/// The camera the player is looking through: the tagged main camera, else whichever of the
	/// player's own cameras is enabled, else none.
	/// </summary>
	private static Camera LookCamera()
	{
		Camera main = Camera.main;
		if (main != null)
		{
			return main;
		}
		FpsActorController player = FpsActorController.instance;
		if (player != null)
		{
			if (player.fpCamera != null && player.fpCamera.isActiveAndEnabled)
			{
				return player.fpCamera;
			}
			if (player.tpCamera != null && player.tpCamera.isActiveAndEnabled)
			{
				return player.tpCamera;
			}
		}
		return null;
	}

	private void Hide()
	{
		if (image != null)
		{
			image.enabled = false;
		}
		if (selfDecoration != null && selfDecoration.gameObject.activeSelf)
		{
			selfDecoration.gameObject.SetActive(false);
		}
		if (trail != null)
		{
			trail.Draw(null, Color.clear, 0f, false, Time.time);
		}
	}

	private void OnDestroy()
	{
		if (trail != null)
		{
			trail.Destroy();
			trail = null;
		}
		if (selfDecoration != null)
		{
			Object.Destroy(selfDecoration.gameObject);
		}
	}
}
