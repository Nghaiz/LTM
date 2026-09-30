using System.Collections;
using UnityEngine;
using UnityEngine.Rendering;

public class ReflectionProber : MonoBehaviour
{
	public static ReflectionProber instance;

	public ReflectionProbe normalProbe;

	public ReflectionProbe nightVisionProbe;

	private Vector3 enabledBounds = new Vector3(9999999f, 9999999f, 9999999f);

	private Vector3 disabledBounds = new Vector3(0f, 0f, 0f);

	/// <summary>
	/// The longest one probe render is waited for before the setup carries on regardless.
	/// </summary>
	/// <remarks>
	/// The night vision atmosphere is applied between the two renders and only reset after the
	/// second. A probe that never reports finished -- disabled, or asked to render outside
	/// realtime mode -- would otherwise leave that atmosphere on for the rest of the match, a
	/// worse outcome than the green reflections the wait exists to prevent.
	/// </remarks>
	private const float MaxProbeWaitSeconds = 5f;

	private void Awake()
	{
		instance = this;
	}

	public void SetupProbes()
	{
		StartCoroutine(SetupProbesCoroutine());
	}

	/// <summary>
	/// Renders each probe under its own atmosphere: the day's for the normal probe, the green
	/// night vision tint for the other.
	/// </summary>
	/// <remarks>
	/// <para>
	/// <b>Waits for each render to finish, not for the end of a frame.</b> RenderProbe only
	/// schedules the render, and the probe is drawn during a later frame. Waiting one frame was
	/// not enough: night vision was applied before the normal probe was drawn, so the day's
	/// probe held the green tint, and every surface that reflects it -- metal, foliage, rock,
	/// the player's own weapon -- was drawn green for the whole match.
	/// </para>
	/// <para>
	/// A process with no graphics device renders no probes, and nobody would see them.
	/// </para>
	/// </remarks>
	private IEnumerator SetupProbesCoroutine()
	{
		if (SystemInfo.graphicsDeviceType == GraphicsDeviceType.Null)
		{
			yield break;
		}
		int normal = normalProbe.RenderProbe();
		yield return StartCoroutine(WaitForProbe(normalProbe, normal, "day"));
		TimeOfDay.instance.ApplyNightvision();
		int nightVision = nightVisionProbe.RenderProbe();
		yield return StartCoroutine(WaitForProbe(nightVisionProbe, nightVision, "night vision"));
		TimeOfDay.instance.ResetAtmosphere();
	}

	private static IEnumerator WaitForProbe(ReflectionProbe probe, int renderId, string label)
	{
		float deadline = Time.realtimeSinceStartup + MaxProbeWaitSeconds;
		while (!probe.IsFinishedRendering(renderId))
		{
			if (Time.realtimeSinceStartup > deadline)
			{
				Debug.LogWarning($"[render] the {label} reflection probe did not finish within {MaxProbeWaitSeconds} s; carrying on.");
				yield break;
			}
			yield return null;
		}
	}

	public void SwitchToNightVision()
	{
		normalProbe.size = disabledBounds;
		nightVisionProbe.size = enabledBounds;
	}

	public void Reset()
	{
		normalProbe.size = enabledBounds;
		nightVisionProbe.size = disabledBounds;
	}
}
