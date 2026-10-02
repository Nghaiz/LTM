using UnityEngine;

// Not compiled into the dedicated server: IMGUI is stripped there. See ScopedWeapon.
#if !UNITY_SERVER
/// <summary>
/// Draws a scoped weapon's blackout while the scope comes up: the one IMGUI draw a
/// <see cref="ScopedWeapon"/> makes, on the first-person weapon only.
/// </summary>
/// <remarks>
/// It used to be <c>ScopedWeapon.OnGUI</c>, and Unity calls an <c>OnGUI</c> twice a frame
/// (layout and repaint) on every enabled instance that has one. Every bot carries a scoped rifle,
/// so that was 34 instances drawing nothing in a 100-bot match, 68 IMGUI passes a frame (about
/// 1.2 ms in a development profile, 2026-10-02). <see cref="ScopedWeapon.SetAiming"/> adds this
/// to the weapon the first time it aims in first person.
/// </remarks>
public sealed class ScopeBlackout : MonoBehaviour
{
	internal ScopedWeapon weapon;

	private void OnGUI()
	{
		if (weapon == null)
		{
			return;
		}
		float alpha = weapon.BlackoutAlpha();
		if (alpha <= 0f)
		{
			return;
		}
		Color black = Color.black;
		black.a = alpha;
		GUI.color = black;
		GUI.DrawTexture(new Rect(0f, 0f, Screen.width, Screen.height), weapon.BlackoutTexture);
	}
}
#endif
