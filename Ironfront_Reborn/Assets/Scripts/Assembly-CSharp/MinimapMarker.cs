using UnityEngine;
using UnityEngine.UI;

/// <summary>Which authored prefab a <see cref="MinimapMarker"/> is drawn from.</summary>
/// <remarks>
/// The kinds differ by texture, not by behaviour — see <c>MinimapUi.SetMarker</c>. A body is a
/// replicated player, bot or vehicle: everything P3 task 3.4 puts on the map that is not an
/// <c>Actor</c>, and therefore everything <c>MinimapUi.AddActorBlip</c> structurally cannot draw.
/// </remarks>
public enum MinimapMarkerKind
{
	CapturePoint,
	Body,
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
/// <b>It anchors rather than moves.</b> Everything else on this minimap positions itself by
/// setting <c>anchorMin</c>/<c>anchorMax</c> from a viewport point, so a marker that used
/// <c>anchoredPosition</c> would drift against the spawn buttons the moment the minimap was
/// resized or re-parented between the loadout and ingame containers.
/// </para>
/// <para>
/// <b>A subject that is destroyed hides the marker rather than throwing.</b> A capture point is
/// unloaded with its scene, and its <c>OnDestroy</c> may or may not run before this one's — so
/// the null check is the contract, not a defensive habit.
/// </para>
/// </remarks>
public class MinimapMarker : MonoBehaviour
{
	private Transform subject;

	private Image image;

	private RawImage rawImage;

	private Color color = Color.white;

	// Set for a soldier's icon (FollowBody): turned to the body's heading, pinned to the map's
	// edge, and swapped for the vehicle's own icon while seated -- ActorBlip's rules for an Actor.
	private bool followsBody;

	private Transform vehicle;

	private Texture infantryTexture;

	private Texture vehicleTexture;

	private static readonly Vector3 VehicleScale = new Vector3(1.5f, 1.5f, 1.5f);

	/// <summary>Points this marker at a world transform and gives it a colour.</summary>
	public void Bind(Transform subject, Color color)
	{
		this.subject = subject;

		// Either graphic type: the fallback prefab is a spawn-point Button (an Image) while a
		// purpose-built marker is more likely a RawImage, and resolving both here is cheaper than
		// demanding one on a prefab this phase is not allowed to author.
		image = GetComponent<Image>();
		rawImage = GetComponent<RawImage>();
		infantryTexture = rawImage != null ? rawImage.texture : null;

		// A spawn-point prefab is a Button. Left interactable it would eat clicks meant for the
		// spawn point underneath and silently change where the player spawns.
		Button button = GetComponent<Button>();
		if (button != null)
		{
			button.interactable = false;
		}

		// The Body kind borrows the actorBlipPrefab, which carries an ActorBlip. Left alive it
		// runs its own LateUpdate against a null Actor and sets image.enabled = false EVERY
		// frame -- so the marker below would set the graphic visible and the borrowed component
		// would hide it again, and the icon would never appear. Same reasoning as the Button
		// above: a borrowed prefab's own behaviour has to be switched off, not merely ignored.
		ActorBlip blip = GetComponent<ActorBlip>();
		if (blip != null)
		{
			Object.Destroy(blip);
		}

		RectTransform rect = (RectTransform)base.transform;
		rect.anchoredPosition = Vector2.zero;

		SetColor(color);
	}

	/// <summary>
	/// Draws this marker as a soldier: turned to its heading, and wearing
	/// <paramref name="seatedIn"/>'s own icon at the vehicle's heading while seated.
	/// </summary>
	/// <remarks>
	/// The networked counterpart of <see cref="ActorBlip"/>'s LateUpdate. Before it a remote body's
	/// icon never turned and a seated one stayed a soldier (owner report 2026-09-29).
	/// </remarks>
	public void FollowBody(Transform seatedIn)
	{
		followsBody = true;
		if (seatedIn == vehicle)
		{
			return;
		}
		vehicle = seatedIn;
		Vehicle seated = seatedIn != null ? seatedIn.GetComponentInParent<Vehicle>() : null;
		vehicleTexture = seated != null ? seated.blip : null;
	}

	/// <summary>Recolours in place. Called on every capture-point flip.</summary>
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
		if (subject == null || MinimapCamera.instance == null)
		{
			SetVisible(false);
			return;
		}

		Vector3 viewport = MinimapCamera.instance.camera.WorldToViewportPoint(subject.position);

		// Behind the minimap camera (a helicopter above it, say), a perspective projection comes
		// back MIRRORED into the frame, so the map's clip mask cannot catch it; and an inactive
		// subject is not in the world at all.
		if (viewport.z <= 0f || !subject.gameObject.activeInHierarchy)
		{
			SetVisible(false);
			return;
		}

		RectTransform rect = (RectTransform)base.transform;
		Vector2 anchor = new Vector2(viewport.x, viewport.y);
		if (followsBody)
		{
			// ActorBlip's rules: pinned to the edge rather than lost off it, turned to the heading,
			// and the vehicle's own icon at the vehicle's heading while seated.
			anchor = new Vector2(Mathf.Clamp01(viewport.x), Mathf.Clamp01(viewport.y));
			bool inVehicle = vehicle != null && vehicleTexture != null;
			Transform heading = inVehicle ? vehicle : subject;
			rect.rotation = Quaternion.Euler(0f, 0f, 0f - heading.eulerAngles.y);
			rect.localScale = inVehicle ? VehicleScale : Vector3.one;
			if (rawImage != null)
			{
				rawImage.texture = inVehicle ? vehicleTexture : infantryTexture;
			}
		}
		rect.anchorMin = anchor;
		rect.anchorMax = anchor;
		SetVisible(true);
	}

	private void SetVisible(bool visible)
	{
		if (image != null)
		{
			image.enabled = visible;
		}
		if (rawImage != null)
		{
			rawImage.enabled = visible;
		}
	}
}
