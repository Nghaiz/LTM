using System.Collections.Generic;
using Ironfront.Net.Unity;
using UnityEngine;
using UnityEngine.UI;

/// <summary>
/// The capture points' names on the map, under their flags: on the M map and on the deploy
/// screen's map, which are the same picture. Owner request 2026-10-09 (Forest Lake).
/// </summary>
/// <remarks>
/// <para>
/// <b>Only named points get a label.</b> A point is named by its authored
/// <see cref="CapturePoint.mapLabel"/>; a map whose points are all blank (Dustbowl and Island
/// today) builds no layer at all.
/// </para>
/// <para>
/// <b>Kept out of the way</b> (the request: easy to read, covering as little as possible). The
/// layer sits under the vehicle and soldier layers, so an icon at a base always draws over the
/// base's name; a label sits just clear of its flag, never on it; it is small, light on a dark rim
/// so it reads on grass, rock and water alike; and it takes no click, so the deploy screen's flag
/// buttons under it still pick a spawn. The rules are <see cref="CapturePointLabelRules"/>.
/// </para>
/// <para>
/// <b>Built in code</b>, like <see cref="CornerMinimap"/>: one runtime view over the scene's
/// points, with nothing to author per map but the names themselves.
/// </para>
/// </remarks>
public sealed class MinimapPointLabels : MonoBehaviour
{
	private static readonly Color LabelInk = new Color(0.95f, 0.96f, 0.93f, 0.92f);

	private static readonly Color LabelEdge = new Color(0.02f, 0.03f, 0.05f, 0.9f);

	private sealed class Label
	{
		public Transform point;

		public Text text;

		public RectTransform rect;
	}

	private readonly List<Label> labels = new List<Label>();

	private RectTransform layer;

	private int fontPixels = -1;

	/// <summary>
	/// Builds the label layer on <paramref name="map"/>, under <paramref name="iconsAbove"/> (the
	/// lowest icon layer), or does nothing when no capture point in the scene is named.
	/// </summary>
	public static MinimapPointLabels Create(RectTransform map, RectTransform iconsAbove)
	{
		CapturePoint[] points = Object.FindObjectsOfType<CapturePoint>();
		List<CapturePoint> named = new List<CapturePoint>();
		foreach (CapturePoint point in points)
		{
			if (point != null && point.MapLabelWording != null)
			{
				named.Add(point);
			}
		}
		if (named.Count == 0)
		{
			return null;
		}

		GameObject host = new GameObject("Point Labels", typeof(RectTransform), typeof(CanvasGroup));
		RectTransform layer = (RectTransform)host.transform;
		layer.SetParent(map, false);
		layer.anchorMin = Vector2.zero;
		layer.anchorMax = Vector2.one;
		layer.offsetMin = Vector2.zero;
		layer.offsetMax = Vector2.zero;
		CanvasGroup group = host.GetComponent<CanvasGroup>();
		group.blocksRaycasts = false;
		group.interactable = false;
		layer.SetSiblingIndex(iconsAbove != null && iconsAbove.parent == map ? iconsAbove.GetSiblingIndex() : 0);

		MinimapPointLabels view = host.AddComponent<MinimapPointLabels>();
		view.layer = layer;
		Font font = CornerMinimap.HudFont();
		foreach (CapturePoint point in named)
		{
			view.labels.Add(BuildLabel(layer, point, font));
		}
		return view;
	}

	private static Label BuildLabel(RectTransform layer, CapturePoint point, Font font)
	{
		Text text = new GameObject("Label " + point.name, typeof(RectTransform)).AddComponent<Text>();
		RectTransform rect = text.rectTransform;
		rect.SetParent(layer, false);
		rect.anchorMin = Vector2.zero;
		rect.anchorMax = Vector2.zero;
		text.font = font;
		text.fontStyle = FontStyle.Bold;
		text.alignment = TextAnchor.MiddleCenter;
		text.horizontalOverflow = HorizontalWrapMode.Overflow;
		text.verticalOverflow = VerticalWrapMode.Overflow;
		text.color = LabelInk;
		text.raycastTarget = false;
		text.text = point.MapLabelWording;
		Outline edge = text.gameObject.AddComponent<Outline>();
		edge.effectColor = LabelEdge;
		edge.effectDistance = new Vector2(1f, -1f);
		return new Label { point = point.transform, text = text, rect = rect };
	}

	private void LateUpdate()
	{
		// Closed, the map is parked below the screen and nobody reads it: leave the labels where
		// they are, as the icons do (MinimapUi.IsShowing).
		MinimapCamera minimapCamera = MinimapCamera.instance;
		if (!MinimapUi.IsShowing || minimapCamera == null)
		{
			return;
		}

		Vector2 mapSize = layer.rect.size;
		int font = CapturePointLabelRules.FontPixels(mapSize.x);
		bool resized = font != fontPixels;
		fontPixels = font;
		float flag = MinimapIconLayout.SoldierPixels(mapSize.x) * MinimapIconLayout.FlagScale;

		for (int i = 0; i < labels.Count; i++)
		{
			Label label = labels[i];
			if (label.point == null)
			{
				SetVisible(label, false);
				continue;
			}
			if (resized)
			{
				label.text.fontSize = font;
				label.rect.sizeDelta = new Vector2(label.text.preferredWidth + 4f, font + 4f);
			}

			Vector3 viewport = minimapCamera.camera.WorldToViewportPoint(label.point.position);
			Vector2 anchor = MinimapUi.ToMap(viewport);
			// A base off a zoomed view is off it; its name is not pinned to the edge.
			if (viewport.z <= 0f || !MinimapZoom.IsOnMap(anchor))
			{
				SetVisible(label, false);
				continue;
			}

			Vector2 size = label.rect.sizeDelta;
			float flagY = anchor.y * mapSize.y;
			bool below = CapturePointLabelRules.FitsBelow(flagY, flag, size.y);
			float offset = flag * 0.5f + CapturePointLabelRules.GapPixels + size.y * 0.5f;
			float x = CapturePointLabelRules.ClampCentre(anchor.x * mapSize.x, size.x * 0.5f, mapSize.x);
			Vector2 position = new Vector2(x, below ? flagY - offset : flagY + offset);
			if ((label.rect.anchoredPosition - position).sqrMagnitude > 0.01f)
			{
				label.rect.anchoredPosition = position;
			}
			SetVisible(label, true);
		}
	}

	private static void SetVisible(Label label, bool visible)
	{
		if (label.text.enabled != visible)
		{
			label.text.enabled = visible;
		}
	}
}
