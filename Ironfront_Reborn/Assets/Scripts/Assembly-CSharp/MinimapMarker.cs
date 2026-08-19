using UnityEngine;
using UnityEngine.UI;

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

	/// <summary>Points this marker at a world transform and gives it a colour.</summary>
	public void Bind(Transform subject, Color color)
	{
		this.subject = subject;

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

		RectTransform rect = (RectTransform)base.transform;
		rect.anchoredPosition = Vector2.zero;

		SetColor(color);
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
		RectTransform rect = (RectTransform)base.transform;
		Vector2 anchor = new Vector2(viewport.x, viewport.y);
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
