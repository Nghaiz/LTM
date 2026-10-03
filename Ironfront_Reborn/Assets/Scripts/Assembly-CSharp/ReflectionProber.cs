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

	// The two renders, once made, as the scene's default reflection. See AdoptAsDefaultReflection.
	private RenderTexture normalReflection;

	private RenderTexture nightVisionReflection;

	private bool reflectingThroughDefault;

	private void Awake()
	{
		instance = this;
	}

	private void OnDestroy()
	{
		if (normalReflection != null)
		{
			normalReflection.Release();
			Object.Destroy(normalReflection);
		}
		if (nightVisionReflection != null)
		{
			nightVisionReflection.Release();
			Object.Destroy(nightVisionReflection);
		}
	}

	public void SetupProbes()
	{
		StartCoroutine(SetupProbesCoroutine());
	}

	/// <summary>
	/// Renders both probes again under the atmosphere now applied (phase P32 Night Mode: the map
	/// went from day to night, or back, after the first render). The probes come back on for it,
	/// and the previous renders are released once the new ones are adopted.
	/// </summary>
	public void SetupProbesAgain()
	{
		StopAllCoroutines();
		if (reflectingThroughDefault)
		{
			reflectingThroughDefault = false;
			normalProbe.enabled = true;
			nightVisionProbe.enabled = true;
			normalProbe.size = enabledBounds;
			nightVisionProbe.size = disabledBounds;
		}
		SetupProbes();
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
		// Each render is copied as soon as it is finished and a frame has passed, BEFORE the next
		// probe renders. Copied at the end, after both, the day copy came out holding the night
		// vision render (read back in the Editor, 2026-10-02: the day copy's sky 0.26/1.94/0.41,
		// exactly the night vision probe's, against 0.41/0.54/0.65 for the day probe itself).
		yield return null;
		RenderTexture day = normalProbe.IsFinishedRendering(normal) ? CopyOf(normalProbe) : null;
		TimeOfDay.instance.ApplyNightvision();
		int nightVision = nightVisionProbe.RenderProbe();
		yield return StartCoroutine(WaitForProbe(nightVisionProbe, nightVision, "night vision"));
		yield return null;
		RenderTexture night = nightVisionProbe.IsFinishedRendering(nightVision) ? CopyOf(nightVisionProbe) : null;
		TimeOfDay.instance.ResetAtmosphere();
		AdoptAsDefaultReflection(day, night);
	}

	/// <summary>
	/// Hands the two finished renders to <see cref="RenderSettings"/> as the scene's default
	/// reflection and switches the probes themselves off.
	/// </summary>
	/// <remarks>
	/// <para>
	/// Both probes are rendered once and cover the whole map (<see cref="enabledBounds"/>), so
	/// every object already reflected exactly the one cubemap -- but finding that out cost Unity a
	/// probe lookup per drawn object per frame: 2.3 ms of <c>SamplePerObjectReflectionProbes</c>,
	/// 3,497 calls a frame, in a 100-bot Forest Lake match (development build profile,
	/// 2026-10-02). As the default reflection the same cubemap reaches every object with no lookup
	/// at all.
	/// </para>
	/// <para>
	/// Copied out first: a probe's own render texture belongs to the probe, and a switched-off
	/// probe is free to let it go. The probe's intensity is carried over, because a probe ignores
	/// the scene's reflection intensity and the default reflection obeys it.
	/// </para>
	/// <para>
	/// A day render that never finished -- realtime probes switched off by the Low preset, or a
	/// device too slow for the wait -- leaves the probes exactly as they were.
	/// </para>
	/// </remarks>
	private void AdoptAsDefaultReflection(RenderTexture day, RenderTexture night)
	{
		if (day == null)
		{
			if (night != null)
			{
				night.Release();
				Object.Destroy(night);
			}
			return;
		}
		ReleaseUnless(normalReflection, day);
		ReleaseUnless(nightVisionReflection, night);
		normalReflection = day;
		nightVisionReflection = night;
		RenderSettings.defaultReflectionMode = DefaultReflectionMode.Custom;
		RenderSettings.customReflectionTexture = normalReflection;
		RenderSettings.reflectionIntensity = normalProbe.intensity;
		normalProbe.enabled = false;
		nightVisionProbe.enabled = false;
		reflectingThroughDefault = true;
	}

	// A render replaced by a newer one (SetupProbesAgain) is released rather than leaked.
	private static void ReleaseUnless(RenderTexture old, RenderTexture replacement)
	{
		if (old != null && old != replacement)
		{
			old.Release();
			Object.Destroy(old);
		}
	}

	private static RenderTexture CopyOf(ReflectionProbe probe)
	{
		RenderTexture source = probe.realtimeTexture;
		if (source == null || !source.IsCreated() || SystemInfo.copyTextureSupport == CopyTextureSupport.None)
		{
			return null;
		}
		var copy = new RenderTexture(source.descriptor)
		{
			name = probe.name + " (default reflection)",
		};
		copy.Create();
		for (int face = 0; face < 6; face++)
		{
			for (int mip = 0; mip < source.mipmapCount; mip++)
			{
				Graphics.CopyTexture(source, face, mip, copy, face, mip);
			}
		}
		return copy;
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
		if (reflectingThroughDefault)
		{
			if (nightVisionReflection != null)
			{
				RenderSettings.customReflectionTexture = nightVisionReflection;
			}
			return;
		}
		normalProbe.size = disabledBounds;
		nightVisionProbe.size = enabledBounds;
	}

	public void Reset()
	{
		if (reflectingThroughDefault)
		{
			RenderSettings.customReflectionTexture = normalReflection;
			return;
		}
		normalProbe.size = enabledBounds;
		nightVisionProbe.size = disabledBounds;
	}
}
