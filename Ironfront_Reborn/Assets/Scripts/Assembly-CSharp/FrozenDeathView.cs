using UnityEngine;
using UnityEngine.UI;

/// <summary>
/// The world behind the death screen, drawn once and held, so the deploy screen costs a menu's
/// frame rather than a match's.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why it exists.</b> After a death the loadout opens over the third-person corpse camera, and
/// that camera went on rendering the whole map at the player's preset behind a dimmed menu. The
/// owner's v4.6.4 playtest log (2026-11-07) has 195 frames of 170 to 550 ms, all inside the game
/// loop; dead, about one a second hitched, against one in ten alive. The corpse camera is often
/// high over the map -- a pilot is set down at his seat's exit -- which is the view P35 measured at
/// 157 to 1,294 ms of GPU a frame on the same machine (the joining screen, finding 2).
/// </para>
/// <para>
/// <b>Frozen, not switched off.</b> The view is rendered once into a texture the size of the
/// screen and shown under every other canvas; the camera stays enabled with nothing in its culling
/// mask, so <c>Camera.main</c> and everything that reads it behave as before, and clearing the
/// screen is all it costs. The moment the loadout closes or the camera changes (a deploy switches
/// to first person) the camera gets its mask back.
/// </para>
/// <para>
/// <b>Creating this canvas re-sorts every overlay canvas that ties on sorting order.</b> The
/// first freeze of a life in a scene creates it, and in v4.6.5 that put the HUD (then order 0,
/// like the loadout) over the death screen, where its full-screen damage vignette swallowed every
/// DEPLOY click (playtest 2026-11-09). The HUD now sorts strictly below the loadout
/// (<c>HudBelowLoadoutTests</c>); keep any canvas this adds off the menus' orders.
/// </para>
/// </remarks>
public sealed class FrozenDeathView
{
	private const string ObjectName = "Frozen Death View";

	/// <summary>Under every other overlay canvas: the loadout, the HUD and the readout draw over it.</summary>
	private const int SortingOrder = -1000;

	private Camera frozen;

	private int savedMask;

	private CameraClearFlags savedClear;

	private Color savedBackground;

	private RenderTexture picture;

	private Canvas canvas;

	private RawImage image;

	/// <summary>Whether a view is held right now.</summary>
	public bool IsFrozen => frozen != null;

	/// <summary>Freezes <paramref name="view"/> when <paramref name="wanted"/>, and lets it go when not.</summary>
	public void Tick(bool wanted, Camera view)
	{
		if (IsFrozen && (!wanted || view != frozen || !frozen.enabled))
		{
			Thaw();
		}
		if (wanted && !IsFrozen && view != null && view.enabled)
		{
			Freeze(view);
		}
	}

	private void Freeze(Camera view)
	{
		int width = Mathf.Max(1, Screen.width);
		int height = Mathf.Max(1, Screen.height);
		picture = new RenderTexture(width, height, 24)
		{
			name = ObjectName,
			antiAliasing = Mathf.Max(1, QualitySettings.antiAliasing)
		};
		RenderTexture previous = view.targetTexture;
		view.targetTexture = picture;
		view.Render();
		view.targetTexture = previous;

		EnsureCanvas();
		image.texture = picture;
		canvas.enabled = true;

		savedMask = view.cullingMask;
		savedClear = view.clearFlags;
		savedBackground = view.backgroundColor;
		view.cullingMask = 0;
		view.clearFlags = CameraClearFlags.SolidColor;
		view.backgroundColor = Color.black;
		frozen = view;
	}

	/// <summary>Gives the camera its view back and lets the picture go. Safe to call at any time.</summary>
	public void Thaw()
	{
		if (frozen != null)
		{
			frozen.cullingMask = savedMask;
			frozen.clearFlags = savedClear;
			frozen.backgroundColor = savedBackground;
			frozen = null;
		}
		if (canvas != null)
		{
			canvas.enabled = false;
		}
		if (image != null)
		{
			image.texture = null;
		}
		if (picture != null)
		{
			picture.Release();
			Object.Destroy(picture);
			picture = null;
		}
	}

	private void EnsureCanvas()
	{
		if (canvas != null)
		{
			return;
		}
		var host = new GameObject(ObjectName, typeof(RectTransform), typeof(Canvas), typeof(RawImage));
		canvas = host.GetComponent<Canvas>();
		canvas.renderMode = RenderMode.ScreenSpaceOverlay;
		canvas.sortingOrder = SortingOrder;
		image = host.GetComponent<RawImage>();
		image.raycastTarget = false;
		var rect = (RectTransform)image.transform;
		rect.anchorMin = Vector2.zero;
		rect.anchorMax = Vector2.one;
		rect.offsetMin = Vector2.zero;
		rect.offsetMax = Vector2.zero;
	}
}
