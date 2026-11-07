using Ironfront.Net.Unity;
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
/// <para>
/// <b>The controls, and a zero that does not match the range</b> (2026-11-08). The owner found
/// the sniper's zoom "too short" at the scope's first power: nothing on the screen said the wheel
/// steps it up, or which keys move the zero. For the first seconds of every look through the
/// scope a second line names them, read from the player's own key bindings. And when the
/// rangefinder reads a distance the zero is 50 m or more off, and the dial can turn toward it,
/// the zero turns amber: the round will land high or low of the crosshair there.
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
	private string shadowLine = string.Empty;
	private GUIStyle hint;
	private GUIStyle hintShadow;
	private string hintLine = string.Empty;
	private float scopedSince = -1f;

	/// <summary>How long the controls line stays up after the scope comes up, seconds.</summary>
	private const float HintSeconds = 5f;

	/// <summary>The last second of it fades.</summary>
	private const float HintFadeSeconds = 1f;

	/// <summary>A zero this far or more from the rangefinder's reading shows in amber, metres.</summary>
	internal const int ZeroMismatchMetres = 50;

	private const string MismatchColour = "#FFB347";

	private static readonly Color TextColour = new Color(0.91f, 0.96f, 0.9f, 1f);
	private static readonly Color BarBack = new Color(0f, 0f, 0f, 0.55f);
	private static readonly Color BarHeld = new Color(0.55f, 0.85f, 1f, 0.95f);
	private static readonly Color BarGasping = new Color(1f, 0.35f, 0.25f, 0.95f);

	private void OnGUI()
	{
		if (weapon == null || !weapon.ShowsReadout)
		{
			scopedSince = -1f;
			return;
		}
		if (Event.current.type != EventType.Repaint)
		{
			return;
		}
		if (scopedSince < 0f)
		{
			scopedSince = Time.unscaledTime;
			hintLine = HintLine(weapon);
		}
		EnsureStyles();
		RefreshLine();

		float height = fontSize * 1.6f;
		var row = new Rect(0f, Screen.height * 0.8f, Screen.width, height);
		GUI.Label(new Rect(row.x + 2f, row.y + 2f, row.width, row.height), shadowLine, shadow);
		GUI.Label(row, line, text);

		float shownFor = Time.unscaledTime - scopedSince;
		if (shownFor < HintSeconds && hintLine.Length > 0)
		{
			float alpha = Mathf.Clamp01((HintSeconds - shownFor) / HintFadeSeconds);
			var hintRow = new Rect(0f, row.yMax + fontSize * 0.9f, Screen.width, height * 0.8f);
			Color previous = GUI.color;
			GUI.color = new Color(1f, 1f, 1f, alpha);
			GUI.Label(new Rect(hintRow.x + 1f, hintRow.y + 1f, hintRow.width, hintRow.height), hintLine, hintShadow);
			GUI.Label(hintRow, hintLine, hint);
			GUI.color = previous;
		}

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
		string zeroText = zero > 0 ? "ZERO " + zero + " m" : string.Empty;
		string rangeText = range == -2 ? string.Empty : range < 0 ? "     RANGE ---" : "     RANGE " + range + " m";
		bool mismatch = ZeroWantsDialling(zero, range, weapon.ZeroMinMetres, weapon.ZeroMaxMetres);
		string gap = zero > 0 ? "     " : string.Empty;
		shadowLine = power + gap + zeroText + rangeText;
		line = mismatch
			? power + gap + "<color=" + MismatchColour + ">" + zeroText + "</color>" + rangeText
			: shadowLine;
	}

	/// <summary>
	/// Whether the zero is <see cref="ZeroMismatchMetres"/> or more off the measured range AND the
	/// dial can move toward it: at 14 m with the zero at its 100 m stop there is nothing to turn,
	/// and amber there would only be noise (seen in the Editor, 2026-11-08).
	/// </summary>
	internal static bool ZeroWantsDialling(int zero, int range, int zeroMin, int zeroMax)
	{
		if (zero <= 0 || range < 0 || Mathf.Abs(range - zero) < ZeroMismatchMetres)
		{
			return false;
		}
		return range > zero ? zero < zeroMax : zero > zeroMin;
	}

	/// <summary>The scope's controls as this player has them bound: "WHEEL  ZOOM 6-35x     PGUP / PGDN  ZERO     SHIFT  HOLD BREATH".</summary>
	internal static string HintLine(ScopedWeapon weapon)
	{
		var parts = new System.Collections.Generic.List<string>(3);
		if (weapon.VariableZoom)
		{
			parts.Add("WHEEL  ZOOM " + Mathf.RoundToInt(weapon.LowestMagnification) + "-" + Mathf.RoundToInt(weapon.HighestMagnification) + "x");
		}
		if (weapon.AdjustableZero)
		{
			parts.Add(GameKeys.Cap(GameAction.ZeroUp) + " / " + GameKeys.Cap(GameAction.ZeroDown) + "  ZERO");
		}
		parts.Add(GameKeys.Cap(GameAction.Sprint) + "  HOLD BREATH");
		return string.Join("     ", parts);
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
		text.richText = true;
		shadow = new GUIStyle(text) { richText = false };
		shadow.normal.textColor = new Color(0f, 0f, 0f, 0.85f);
		hint = new GUIStyle(text)
		{
			fontSize = Mathf.Max(11, Mathf.RoundToInt(size * 0.72f)),
			fontStyle = FontStyle.Bold,
			richText = false,
		};
		hint.normal.textColor = new Color(TextColour.r, TextColour.g, TextColour.b, 0.8f);
		hintShadow = new GUIStyle(hint);
		hintShadow.normal.textColor = new Color(0f, 0f, 0f, 0.75f);
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
