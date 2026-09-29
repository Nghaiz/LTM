using UnityEngine;
using UnityEngine.UI;

public class ActorBlip : MonoBehaviour
{
	private const float VEHICLE_BLIP_SIZE = 1.5f;

	public GameObject sightConePrefab;

	private Actor actor;

	private RawImage image;

	private RawImage sightCone;

	private Texture infantryBlip;

	private bool useSightCone;

	// The team the icon was last coloured for. A networked client's own body learns its team
	// from the first snapshot, after ActorManager.Register has already made this icon, so a
	// colour taken once at SetActor stayed the grey of "no team" for the whole match.
	private int colouredTeam = int.MinValue;

	private void Awake()
	{
		image = GetComponent<RawImage>();
		infantryBlip = image.texture;
		image.rectTransform.anchoredPosition = Vector2.zero;
		base.transform.SetAsFirstSibling();
	}

	public void SetActor(Actor actor, bool useSightCone)
	{
		if (actor.GetType() == typeof(ForcedAiTarget))
		{
			base.enabled = false;
			return;
		}
		this.actor = actor;
		Recolour();
		this.useSightCone = useSightCone;
		if (this.useSightCone)
		{
			sightCone = ((GameObject)Object.Instantiate(sightConePrefab, base.transform)).GetComponent<RawImage>();
			Vector2 vector = new Vector2(0.5f, 0.5f);
			sightCone.rectTransform.anchorMin = vector;
			sightCone.rectTransform.anchorMax = vector;
			sightCone.rectTransform.anchoredPosition = Vector2.zero;
		}
	}

	private void Recolour()
	{
		colouredTeam = actor.team;
		image.color = ColorScheme.BlipColor(actor.team, !actor.aiControlled);
	}

	private void LateUpdate()
	{
		if (actor != null && actor.team != colouredTeam)
		{
			Recolour();
		}
		if (actor != null && !actor.dead && (actor.team == FpsActorController.playerTeam || actor.IsHighlighted()))
		{
			RectTransform rectTransform = (RectTransform)base.transform;
			Vector3 vector = MinimapCamera.instance.camera.WorldToViewportPoint(actor.Position());
			// Pinned to the edge rather than clipped: the minimap frames the ground, and a boat or
			// helicopter out over the sea beyond it would otherwise vanish from the map.
			Vector2 anchorMax = (rectTransform.anchorMin = new Vector2(Mathf.Clamp01(vector.x), Mathf.Clamp01(vector.y)));
			rectTransform.anchorMax = anchorMax;
			if (actor.IsSeated())
			{
				rectTransform.rotation = Quaternion.Euler(0f, 0f, 0f - actor.seat.vehicle.transform.eulerAngles.y);
				image.texture = actor.seat.vehicle.blip;
				rectTransform.localScale = new Vector3(1.5f, 1.5f, 1.5f);
			}
			else
			{
				rectTransform.rotation = Quaternion.Euler(0f, 0f, 0f - Quaternion.LookRotation(actor.controller.FacingDirection()).eulerAngles.y);
				image.texture = infantryBlip;
				rectTransform.localScale = Vector3.one;
			}
			if (useSightCone)
			{
				RectTransform rectTransform2 = sightCone.rectTransform;
				rectTransform2.rotation = Quaternion.Euler(0f, 0f, 0f - Camera.main.transform.eulerAngles.y);
				sightCone.enabled = true;
			}
			image.enabled = true;
		}
		else
		{
			image.enabled = false;
			if (useSightCone)
			{
				sightCone.enabled = false;
			}
		}
	}
}
