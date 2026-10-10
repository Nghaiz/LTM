using UnityEngine;

/// <summary>
/// A map picture by moonlight (Night Mode, phase P32): most of its colour gone, dark and blue, its
/// relief, paths and buildings still readable as shapes. One grade for every map: the baker applies
/// it to Forest Lake's baked picture, and <see cref="MinimapCamera"/> to the live shot Island and
/// Dustbowl keep from the original game.
/// </summary>
public static class MinimapNightGrade
{
	/// <summary>What the night keeps of each channel: dark, and bluer than the day.</summary>
	public static readonly Color Moonlit = new Color(0.18f, 0.21f, 0.3f);

	/// <summary>How much of the day's colour the night keeps.</summary>
	public const float Saturation = 0.35f;

	/// <summary>Extra light the brightest ground (paths, roofs) keeps at night.</summary>
	public const float HighlightGain = 1.35f;

	/// <summary><paramref name="day"/> by moonlight.</summary>
	public static Color Apply(Color day)
	{
		float grey = day.grayscale;
		Color muted = Color.Lerp(new Color(grey, grey, grey), day, Saturation);
		// A little more of the brightest ground than of the darkest: paths and roofs catch the moon,
		// a forest swallows it.
		float lift = Mathf.Lerp(1f, HighlightGain, Mathf.Clamp01((grey - 0.4f) / 0.5f));
		return new Color(muted.r * Moonlit.r * lift, muted.g * Moonlit.g * lift, muted.b * Moonlit.b * lift, 1f);
	}

	/// <summary>Every pixel of <paramref name="pixels"/> by moonlight, in place.</summary>
	public static void Apply(Color32[] pixels)
	{
		for (int i = 0; i < pixels.Length; i++)
		{
			pixels[i] = Apply((Color)pixels[i]);
		}
	}
}
