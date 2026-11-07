using Ironfront.Net.Unity;
using UnityEngine;

/// <summary>
/// Thins the map's haze in the picture a magnified scope draws, and only there
/// (<see cref="ScopeProfiles.HazeScale"/>).
/// </summary>
/// <remarks>
/// <para>
/// <b>Why it exists.</b> Owner report 2026-11-08: "the sniper cannot see 800 to 1,000 m". The
/// maps' haze is exponential squared: on Forest Lake (density 0.001) a soldier 900 m off kept 44%
/// of his colour against the haze, on Island (0.003) one 500 m off kept 10%, whatever the scope's
/// power. Through 25x the haze is a fifth as thick and the same soldier keeps 97%.
/// </para>
/// <para>
/// <b>Set for one camera's render and put back after it.</b> The density is changed in that
/// camera's <c>onPreRender</c> and restored in its <c>onPostRender</c>, so nothing that reads
/// <see cref="RenderSettings.fogDensity"/> in an Update sees the thinner value: bots judge sight
/// through the map's haze (<c>AiActorController.SightFogDensity</c>), and Night Mode's
/// <c>TimeOfDay</c> keeps owning the density it sets. A scope that is not held this frame or the
/// last changes nothing, so a rifle dropped mid-aim cannot leave the world thin.
/// </para>
/// </remarks>
public static class ScopeHaze
{
	private static Camera held;

	private static float heldScale = 1f;

	private static int heldFrame = -10;

	private static bool hooked;

	private static Camera appliedTo;

	private static float savedDensity;

	/// <summary>Asks for <paramref name="view"/>'s picture this frame to be drawn through a scope of <paramref name="magnification"/>.</summary>
	public static void Hold(Camera view, float magnification)
	{
		if (view == null)
		{
			return;
		}
		held = view;
		heldScale = ScopeProfiles.HazeScale(magnification);
		heldFrame = Time.frameCount;
		if (!hooked)
		{
			Camera.onPreRender += OnPreRender;
			Camera.onPostRender += OnPostRender;
			hooked = true;
		}
	}

	/// <summary>Whether a scope is thinning the haze now, for tests and diagnostics.</summary>
	public static bool IsHeld => held != null && Time.frameCount - heldFrame <= 1 && heldScale < 1f;

	private static void OnPreRender(Camera camera)
	{
		if (!ReferenceEquals(appliedTo, null) && appliedTo == null)
		{
			// Destroyed between its two callbacks: put the haze back before anything else draws.
			RenderSettings.fogDensity = savedDensity;
			appliedTo = null;
		}
		if (appliedTo != null || camera != held || !IsHeld)
		{
			return;
		}
		savedDensity = RenderSettings.fogDensity;
		RenderSettings.fogDensity = savedDensity * heldScale;
		appliedTo = camera;
	}

	private static void OnPostRender(Camera camera)
	{
		if (appliedTo == null || camera != appliedTo)
		{
			return;
		}
		RenderSettings.fogDensity = savedDensity;
		appliedTo = null;
	}
}
