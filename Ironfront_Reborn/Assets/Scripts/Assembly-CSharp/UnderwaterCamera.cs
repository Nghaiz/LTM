using UnityEngine;
using UnityEngine.UI;

/// <summary>
/// The dark blue wash over the view while this camera is under water.
/// </summary>
/// <remarks>
/// <para>
/// <b>Under the menus, not over them.</b> The original drew it in <c>OnGUI</c>, and IMGUI draws after
/// every uGUI canvas: when a player died in water the corpse camera sat under the surface and the
/// wash covered the whole death screen, loadout and DEPLOY included, at 72 % (owner's screenshot,
/// 2026-11-10; it had always done it). It covered the HUD the same way, breath bar and all, while the
/// player was under. It is a canvas now, sorted over the frozen death view and under the HUD and
/// every menu (<see cref="SortingOrder"/>).
/// </para>
/// <para>
/// The colour is what the original showed: <c>GUI.color</c> (0.2, 0.2, 0.5, 0.9) over a new
/// <c>Texture2D</c>, whose texels start at 0.804 grey.
/// </para>
/// </remarks>
public class UnderwaterCamera : MonoBehaviour
{
	/// <summary>Over <c>FrozenDeathView</c> (-1000), under the HUD (-1) and the menus (0 and up).</summary>
	public const int SortingOrder = -500;

	/// <summary>The original's wash: (0.2, 0.2, 0.5, 0.9) times 0.804.</summary>
	public static readonly Color Tint = new Color(0.161f, 0.161f, 0.402f, 0.724f);

	private Camera camera;

	private Canvas canvas;

	private void Awake()
	{
		camera = GetComponent<Camera>();
	}

	private void LateUpdate()
	{
		bool under = camera != null && camera.enabled && WaterLevel.InWater(base.transform.position);
		if (under && canvas == null)
		{
			canvas = CreateCanvas();
		}
		if (canvas != null && canvas.enabled != under)
		{
			canvas.enabled = under;
		}
	}

	private void OnDisable()
	{
		if (canvas != null)
		{
			canvas.enabled = false;
		}
	}

	private void OnDestroy()
	{
		if (canvas != null)
		{
			Object.Destroy(canvas.gameObject);
		}
	}

	private static Canvas CreateCanvas()
	{
		var host = new GameObject("Underwater Tint", typeof(RectTransform), typeof(Canvas), typeof(Image));
		Canvas made = host.GetComponent<Canvas>();
		made.renderMode = RenderMode.ScreenSpaceOverlay;
		made.sortingOrder = SortingOrder;
		Image wash = host.GetComponent<Image>();
		wash.color = Tint;
		wash.raycastTarget = false;
		var rect = (RectTransform)wash.transform;
		rect.anchorMin = Vector2.zero;
		rect.anchorMax = Vector2.one;
		rect.offsetMin = Vector2.zero;
		rect.offsetMax = Vector2.zero;
		return made;
	}
}
