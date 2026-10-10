using UnityEngine;

// Not compiled into the dedicated server: IMGUI is stripped there. See ScopedWeapon.
#if !UNITY_SERVER
/// <summary>
/// The line under a rifle scope's crosshair (owner's run of 2026-10-10, phase P38): the power, the
/// zero, the rangefinder's distance, and the breath while it is held or being caught.
/// </summary>
/// <remarks>
/// <para>
/// Added by <see cref="ScopedWeapon"/> to the local player's first-person rifle the first time
/// it aims, beside <see cref="ScopeBlackout"/> and for that class's reason: an <c>OnGUI</c> on
/// every bot's rifle costs two IMGUI passes a frame each.
/// </para>
/// <para>
/// The text is rebuilt only when a number on it changes, so a still scope allocates nothing.
/// </para>
/// </remarks>
public sealed class ScopeReadout : MonoBehaviour
{
	internal ScopedWeapon weapon;

	private GUIStyle text;
	private GUIStyle shadow;
	private int fontSize;
	private string line = string.Empty;
	private int shownMagnification = -1;
	private int shownZero = -1;
	private int shownRange = -2;

	private static readonly Color TextColour = new Color(0.91f, 0.96f, 0.9f, 1f);
	private static readonly Color BarBack = new Color(0f, 0f, 0f, 0.55f);
	private static readonly Color BarHeld = new Color(0.55f, 0.85f, 1f, 0.95f);
	private static readonly Color BarGasping = new Color(1f, 0.35f, 0.25f, 0.95f);

	private void OnGUI()
	{
		if (weapon == null || !weapon.ShowsReadout || Event.current.type != EventType.Repaint)
		{
			return;
		}
		EnsureStyles();
		RefreshLine();

		float height = fontSize * 1.6f;
		var row = new Rect(0f, Screen.height * 0.8f, Screen.width, height);
		GUI.Label(new Rect(row.x + 2f, row.y + 2f, row.width, row.height), line, shadow);
		GUI.Label(row, line, text);

		if (weapon.HoldingBreath || weapon.OutOfBreath || weapon.Breath < 0.999f)
		{
			float width = Screen.width * 0.09f;
			var bar = new Rect((Screen.width - width) * 0.5f, row.yMax + 2f, width, Mathf.Max(3f, fontSize * 0.22f));
			Fill(bar, BarBack);
			Fill(new Rect(bar.x, bar.y, bar.width * Mathf.Clamp01(weapon.Breath), bar.height),
				weapon.OutOfBreath ? BarGasping : BarHeld);
		}
	}

	private void RefreshLine()
	{
		int magnification = Mathf.RoundToInt(weapon.CurrentMagnification);
		int zero = weapon.AdjustableZero ? weapon.ZeroMetres : 0;
		int range = weapon.HasRangefinder ? (weapon.RangeMetres < 0f ? -1 : Mathf.RoundToInt(weapon.RangeMetres)) : -2;
		if (magnification == shownMagnification && zero == shownZero && range == shownRange)
		{
			return;
		}
		shownMagnification = magnification;
		shownZero = zero;
		shownRange = range;

		string power = magnification + "x";
		string zeroText = zero > 0 ? "     ZERO " + zero + " m" : string.Empty;
		string rangeText = range == -2 ? string.Empty : range < 0 ? "     RANGE ---" : "     RANGE " + range + " m";
		line = power + zeroText + rangeText;
	}

	private void EnsureStyles()
	{
		int size = Mathf.Max(12, Mathf.RoundToInt(Screen.height * 0.022f));
		if (text != null && size == fontSize)
		{
			return;
		}
		fontSize = size;
		text = new GUIStyle(GUI.skin.label)
		{
			alignment = TextAnchor.MiddleCenter,
			fontSize = size,
			fontStyle = FontStyle.Bold,
		};
		text.normal.textColor = TextColour;
		shadow = new GUIStyle(text);
		shadow.normal.textColor = new Color(0f, 0f, 0f, 0.85f);
	}

	private static void Fill(Rect rect, Color colour)
	{
		Color previous = GUI.color;
		GUI.color = colour;
		GUI.DrawTexture(rect, Texture2D.whiteTexture);
		GUI.color = previous;
	}
}
#endif
