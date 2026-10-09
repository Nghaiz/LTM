using UnityEngine;

/// <summary>
/// Shows and hides a menu canvas, so that a menu the player cannot see is also one the keyboard
/// cannot reach.
/// </summary>
/// <remarks>
/// <para>
/// The Esc menu and the deploy screen hide by switching their <see cref="Canvas"/> off. That stops
/// them drawing and nothing else: every button under it stays enabled, interactable and on Unity's
/// list of selectables, so automatic navigation from a button that IS on screen steps onto a hidden
/// one, and Submit (Return, keypad Enter, Space) presses it. Measured in a practice match on
/// 2026-10-09: on the Esc menu, D from ACHIEVEMENTS selected the hidden DEPLOY; on the deploy
/// screen, W from a weapon slot selected the hidden RESUME, whose press locks the cursor over the
/// deploy screen. <c>MatchMenuKeyGuard</c> keeps the keys out of every menu while none is up; this
/// keeps them out of the hidden one while another is.
/// </para>
/// <para>
/// The <see cref="CanvasGroup"/> beside the canvas, authored in <c>Ingame UI Container.prefab</c>,
/// follows it: a hidden menu's buttons are non-interactable, which takes them out of navigation and
/// turns a press on one into nothing. A canvas without one is logged as an error rather than given a
/// group here, so the prefab stays the single place the menus are built.
/// </para>
/// </remarks>
public static class MenuCanvas
{
	public static void SetShown(Canvas canvas, bool shown)
	{
		canvas.enabled = shown;
		CanvasGroup group = canvas.GetComponent<CanvasGroup>();
		if (group == null)
		{
			Debug.LogError("[ui] menu canvas '" + canvas.name + "' has no CanvasGroup, so its buttons stay pressable while hidden; add one beside the Canvas in the prefab.");
			return;
		}
		group.interactable = shown;
		group.blocksRaycasts = shown;
	}
}
