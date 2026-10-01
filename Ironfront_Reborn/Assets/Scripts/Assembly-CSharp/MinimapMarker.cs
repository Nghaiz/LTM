using Ironfront.Net.Unity;
using UnityEngine;
using UnityEngine.UI;

/// <summary>Which authored prefab a <see cref="MinimapMarker"/> is drawn from, and how it moves.</summary>
/// <remarks>
/// A body is a replicated player or bot on foot, a vehicle a replicated or offline vehicle: what
/// <c>MinimapUi.AddActorBlip</c> structurally cannot draw because neither is a local
/// <c>Actor</c>. A capture point is a fixed marker that only recolours.
/// </remarks>
public enum MinimapMarkerKind
{
	CapturePoint,
	Body,
	Vehicle,
}

/// <summary>
/// One minimap icon that follows a world transform. debt-closure phase 2 task 2d, ledger C-6.
/// </summary>
/// <remarks>
/// <para>
/// The <see cref="Transform"/>-shaped counterpart to <see cref="ActorBlip"/>, which follows an
/// <see cref="Actor"/> and reads its team, weapon and seat. A capture point has none of those and
/// only needs a position and a colour, so it gets its own component rather than a widened
/// <c>ActorBlip</c> with half its fields unused.
/// </para>
/// <para>
/// <b>It moves inside fixed anchors, re-placed every frame from the map's current size</b>
/// (<see cref="MinimapUi.Place"/>). Until 2026-10-02 it re-anchored itself every frame, which
/// rebuilt its mesh every frame; recomputing the position from the parent's size still keeps it on
/// the spawn buttons, which are anchored, when the minimap is resized or re-parented between the
/// loadout and ingame containers.
/// </para>
/// <para>
/// <b>A subject that is destroyed hides the marker rather than throwing.</b> A capture point is
/// unloaded with its scene, and its <c>OnDestroy</c> may or may not run before this one's — so
/// the null check is the contract, not a defensive habit.
/// </para>
/// <para>
/// <b>Soldiers and vehicles are sized from the map</b> (<see cref="MinimapIconLayout"/>), turned to
/// their heading, pinned to the map's edge rather than lost off it, and leave a fading trail when
/// they move, so a glance shows where they are going as well as where they are.
/// </para>
/// </remarks>
public class MinimapMarker : MonoBehaviour
{
	private const int BodyTrailLength = 7;

	private const int VehicleTrailLength = 12;

	/// <summary>A vehicle's trail dots are this much larger than a soldier's.</summary>
	private const float VehicleTrailDotScale = 1.35f;

	private Transform subject;

	private Image image;

	private RawImage rawImage;

	private Color color = Color.white;

	private MinimapMarkerKind kind;

	private bool isHuman;

	private MinimapTrail trail;

	private RectTransform leader;

	private Image leaderImage;

	private RectTransform leaderTip;

	private Texture arrowTexture;

	private Vector3 lastPosition;

	private bool hasLastPosition;

	private Vector3 velocity;

	/// <summary>How quickly the measured velocity follows the real one; smooths snapshot steps.</summary>
	private const float VelocitySmoothing = 5f;

	/// <summary>Points this marker at a world transform and gives it a colour.</summary>
	public void Bind(Transform subject, Color color, MinimapMarkerKind kind, Texture trailDot, Texture arrow)
	{
		this.subject = subject;
		this.kind = kind;
		arrowTexture = arrow;

		// Either graphic type: the fallback prefab is a spawn-point Button (an Image) while a
		// purpose-built marker is more likely a RawImage, and resolving both here is cheaper than
		// demanding one on a prefab this phase is not allowed to author.
		image = GetComponent<Image>();
		rawImage = GetComponent<RawImage>();

		// A spawn-point prefab is a Button. Left interactable it would eat clicks meant for the
		// spawn point underneath and silently change where the player spawns.
		Button button = GetComponent<Button>();
		if (button != null)
		{
			button.interactable = false;
		}

		// The moving kinds never take a click: a soldier or a jeep over a spawn point on the
		// deploy screen must not stop the player picking that spawn point.
		if (kind != MinimapMarkerKind.CapturePoint)
		{
			if (image != null)
			{
				image.raycastTarget = false;
			}
			if (rawImage != null)
			{
				rawImage.raycastTarget = false;
			}
			trail = new MinimapTrail(
				MinimapUi.MapRect, trailDot,
				kind == MinimapMarkerKind.Vehicle ? VehicleTrailLength : BodyTrailLength);
		}

		// The Body kind borrows the actorBlipPrefab, which carries an ActorBlip. Left alive it runs
		// its own LateUpdate against a null Actor and hides the graphic EVERY frame, and this marker
		// shows it again: an OnDisable and an OnEnable per icon per frame, each re-registering the
		// graphic with the canvas. #381 dropped these lines; in a 100-bot match that was ~50 of each
		// every frame (v3.1.1 profile, 2026-10-02). Same reasoning as the Button above: a borrowed
		// prefab's own behaviour has to be switched off, not merely ignored.
		ActorBlip blip = GetComponent<ActorBlip>();
		if (blip != null)
		{
			Object.Destroy(blip);
		}

		RectTransform rect = (RectTransform)base.transform;
		rect.anchoredPosition = Vector2.zero;
		rect.localScale = Vector3.one;

		SetColor(color);
	}

	/// <summary>A player rather than a bot: drawn a little larger, as well as lighter.</summary>
	public void SetHuman(bool isHuman)
	{
		this.isHuman = isHuman;
	}

	/// <summary>Wears <paramref name="texture"/> — a vehicle's own silhouette — instead of the prefab's.</summary>
	public void SetTexture(Texture texture)
	{
		if (texture != null && rawImage != null && rawImage.texture != texture)
		{
			rawImage.texture = texture;
		}
	}

	/// <summary>Recolours in place. Called on every capture-point flip and crew change.</summary>
	public void SetColor(Color color)
	{
		this.color = color;
		if (image != null)
		{
			image.color = color;
		}
		if (rawImage != null)
		{
			rawImage.color = color;
		}
	}

	private void LateUpdate()
	{
		MinimapCamera minimapCamera = MinimapCamera.instance;
		if (subject == null || minimapCamera == null)
		{
			Hide();
			return;
		}

		Vector3 position = subject.position;
		Vector3 viewport = minimapCamera.camera.WorldToViewportPoint(position);

		// Behind the minimap camera (a helicopter above it, say), a perspective projection comes
		// back MIRRORED into the frame, so the map's clip mask cannot catch it; and an inactive
		// subject is not in the world at all.
		if (viewport.z <= 0f || !subject.gameObject.activeInHierarchy)
		{
			Hide();
			return;
		}

		RectTransform rect = (RectTransform)base.transform;
		// Where it lands on the map as drawn now: the zoom moves every icon, not just the picture.
		Vector2 anchor = MinimapUi.ToMap(viewport);
		if (kind == MinimapMarkerKind.CapturePoint)
		{
			// Not pinned: a flag off a zoomed view is simply off it, and the map's mask clips it.
			float flag = MinimapIconLayout.SoldierPixels(MinimapUi.MapWidth) * MinimapIconLayout.FlagScale;
			MinimapUi.SetSquareSize(rect, flag);
		}
		else
		{
			if (MinimapUi.IsZoomed && !MinimapZoom.IsOnMap(anchor))
			{
				// Zoomed in, something out of view is out of view; pinning every such soldier to
				// the edge would line the map's border with icons.
				Hide();
				return;
			}
			// ActorBlip's rules: pinned to the edge rather than lost off it, and turned to the
			// heading. The heading is the forward vector flattened, not eulerAngles.y, which
			// jumps by 180 degrees on a vehicle lying on its side or roof.
			anchor = new Vector2(Mathf.Clamp01(anchor.x), Mathf.Clamp01(anchor.y));
			Vector3 forward = subject.forward;
			forward.y = 0f;
			if (forward.sqrMagnitude > 1e-6f)
			{
				MinimapUi.SetHeading(rect, Mathf.Atan2(forward.x, forward.z) * Mathf.Rad2Deg);
			}

			float soldier = MinimapIconLayout.SoldierPixels(MinimapUi.MapWidth);
			float size = kind == MinimapMarkerKind.Vehicle
				? soldier * MinimapIconLayout.VehicleScale
				: (isHuman ? soldier * MinimapIconLayout.HumanScale : soldier);
			MinimapUi.SetSquareSize(rect, size);
			if (kind == MinimapMarkerKind.Vehicle)
			{
				TrackVelocity(position);
				DrawLeader(minimapCamera.camera, position, forward, size);
			}

			if (trail != null)
			{
				float now = Time.time;
				float dot = soldier * MinimapIconLayout.TrailDotScale
					* (kind == MinimapMarkerKind.Vehicle ? VehicleTrailDotScale : 1f);
				trail.Record(position, now);
				trail.Draw(minimapCamera.camera, color, dot, true, now);
			}
		}
		MinimapUi.Place(rect, anchor);
		SetVisible(true);
	}

	private void TrackVelocity(Vector3 position)
	{
		float dt = Time.deltaTime;
		if (hasLastPosition && dt > 0f)
		{
			Vector3 step = position - lastPosition;
			// A respawned or teleported vehicle did not travel there; drop the reading.
			velocity = MinimapTrailRules.IsJump(lastPosition, position)
				? Vector3.zero
				: Vector3.Lerp(velocity, step / dt, 1f - Mathf.Exp((0f - dt) * VelocitySmoothing));
		}
		lastPosition = position;
		hasLastPosition = true;
	}

	// The speed leader: a line from the icon's edge toward where the vehicle will be in a few
	// seconds. A child of the icon, so it is turned by the difference between where the vehicle
	// points and where it is going; that difference is exactly a sliding helicopter or a drifting
	// jeep.
	private void DrawLeader(Camera minimapCamera, Vector3 position, Vector3 flatForward, float iconPixels)
	{
		Vector3 flatVelocity = velocity;
		flatVelocity.y = 0f;
		float length = MinimapIconLayout.LeaderPixels(
			flatVelocity.magnitude, PixelsPerMetre(minimapCamera, position), iconPixels);
		if (length <= 0f)
		{
			HideLeader();
			return;
		}
		if (leader == null)
		{
			// Built here rather than from a prefab: one plain line per vehicle icon, created the
			// first time that vehicle moves and destroyed with its icon.
			GameObject line = new GameObject("Speed Leader", typeof(RectTransform), typeof(CanvasRenderer), typeof(Image));
			leader = (RectTransform)line.transform;
			leader.SetParent(base.transform, false);
			leader.anchorMin = new Vector2(0.5f, 0.5f);
			leader.anchorMax = new Vector2(0.5f, 0.5f);
			leader.pivot = new Vector2(0.5f, 0f);
			leaderImage = line.GetComponent<Image>();
			leaderImage.raycastTarget = false;
			// A dark rim, as every icon has, so a blue line still reads on the blue sea.
			Outline rim = line.AddComponent<Outline>();
			rim.effectColor = new Color(0.05f, 0.06f, 0.08f, 0.7f);
			rim.effectDistance = new Vector2(1f, -1f);
			if (arrowTexture != null)
			{
				GameObject tip = new GameObject("Tip", typeof(RectTransform), typeof(CanvasRenderer), typeof(RawImage));
				leaderTip = (RectTransform)tip.transform;
				leaderTip.SetParent(leader, false);
				leaderTip.anchorMin = new Vector2(0.5f, 1f);
				leaderTip.anchorMax = new Vector2(0.5f, 1f);
				leaderTip.pivot = new Vector2(0.5f, 0.35f);
				RawImage tipImage = tip.GetComponent<RawImage>();
				tipImage.texture = arrowTexture;
				tipImage.raycastTarget = false;
			}
		}
		float heading = Mathf.Atan2(flatForward.x, flatForward.z) * Mathf.Rad2Deg;
		float course = Mathf.Atan2(flatVelocity.x, flatVelocity.z) * Mathf.Rad2Deg;
		float relative = (course - heading) * Mathf.Deg2Rad;
		leader.localRotation = Quaternion.Euler(0f, 0f, (heading - course));
		leader.localPosition = new Vector3(Mathf.Sin(relative), Mathf.Cos(relative), 0f) * (iconPixels * 0.45f);
		leader.sizeDelta = new Vector2(MinimapIconLayout.LeaderWidthPixels, length);
		leader.localScale = Vector3.one;
		Color ink = color;
		ink.a = Mathf.Max(ink.a, 0.85f);
		leaderImage.color = ink;
		if (!leader.gameObject.activeSelf)
		{
			leader.gameObject.SetActive(true);
		}
		if (leaderTip != null)
		{
			float tip = MinimapIconLayout.SoldierPixels(MinimapUi.MapWidth) * MinimapIconLayout.LeaderTipScale;
			leaderTip.sizeDelta = new Vector2(tip, tip);
			leaderTip.GetComponent<RawImage>().color = ink;
		}
	}

	// Canvas pixels per world metre at this point of the map as drawn now: 100 m projected through
	// the minimap camera and the zoom, so a perspective camera, an orthographic one and a zoomed
	// map all answer correctly.
	private static float PixelsPerMetre(Camera minimapCamera, Vector3 position)
	{
		Vector2 here = MinimapUi.ToMap(minimapCamera.WorldToViewportPoint(position));
		Vector2 there = MinimapUi.ToMap(minimapCamera.WorldToViewportPoint(position + Vector3.right * 100f));
		return Mathf.Abs(there.x - here.x) * MinimapUi.MapWidth / 100f;
	}

	private void HideLeader()
	{
		if (leader != null && leader.gameObject.activeSelf)
		{
			leader.gameObject.SetActive(false);
		}
	}

	private void Hide()
	{
		SetVisible(false);
		HideLeader();
		hasLastPosition = false;
		if (trail != null)
		{
			trail.Draw(null, color, 0f, false, Time.time);
		}
	}

	private void OnDestroy()
	{
		if (trail != null)
		{
			trail.Destroy();
			trail = null;
		}
	}

	private void SetVisible(bool visible)
	{
		if (image != null && image.enabled != visible)
		{
			image.enabled = visible;
		}
		if (rawImage != null && rawImage.enabled != visible)
		{
			rawImage.enabled = visible;
		}
	}
}
