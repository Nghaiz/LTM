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
	/// The longest the day probe's render is waited for before the setup carries on without it.
	/// </summary>
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
	/// Renders the day probe, and the night vision reflection under the green night vision
	/// atmosphere, without that atmosphere ever reaching the screen.
	/// </summary>
	/// <remarks>
	/// <para>
	/// <b>The night vision render is made in one call, inside one frame</b>
	/// (<see cref="RenderNightVisionReflection"/>). It used to be the second probe, rendered the way the
	/// first is -- scheduled, then waited for over frames -- with the night vision atmosphere on the
	/// whole scene for the wait, so every frame drawn meanwhile was green. On the Low preset realtime
	/// probes are off and the wait never ended early: every map load and room switch showed five
	/// seconds of green screen (a Linux player's report and log, 2026-11-10; reproduced in the Editor,
	/// 285 green frames over 5.0 s on Low, two on Ultra). Every platform had it.
	/// </para>
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
		// The Low preset switches realtime probes off: nothing would render, and the wait below would
		// only spend five seconds learning that.
		if (!QualitySettings.realtimeReflectionProbes)
		{
			yield break;
		}
		int normal = normalProbe.RenderProbe();
		yield return StartCoroutine(WaitForProbe(normalProbe, normal, "day"));
		// Copied once finished and a frame has passed. Copied later, after another render, the day
		// copy came out holding that render (read back in the Editor, 2026-10-02: the day copy's sky
		// 0.26/1.94/0.41, exactly the night vision probe's, against 0.41/0.54/0.65 for the day
		// probe itself).
		yield return null;
		RenderTexture day = normalProbe.IsFinishedRendering(normal) ? CopyOf(normalProbe) : null;
		RenderTexture night = day != null ? RenderNightVisionReflection(day) : null;
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

	/// <summary>
	/// The night vision reflection: the scene seen from <see cref="nightVisionProbe"/> under the night
	/// vision atmosphere, rendered into a cubemap shaped like <paramref name="day"/> in one call, the
	/// atmosphere put back before this returns. Null when the device cannot render it.
	/// </summary>
	/// <remarks>
	/// <para>
	/// A camera's <c>RenderToCubemap</c> is synchronous, so the green atmosphere exists only inside
	/// this method, which runs in a coroutine step, before the frame is drawn: no camera that shows the
	/// player anything renders while it is on.
	/// </para>
	/// <para>
	/// Its mips are filtered by <c>GenerateMips</c> rather than convolved as a probe's are, so a rough
	/// surface under night vision reflects a slightly sharper blur than by day. Night vision paints the
	/// whole scene green; that difference is not one anybody can see in it.
	/// </para>
	/// </remarks>
	private RenderTexture RenderNightVisionReflection(RenderTexture day)
	{
		if (nightVisionProbe == null || TimeOfDay.instance == null)
		{
			return null;
		}
		RenderTextureDescriptor shape = day.descriptor;
		shape.useMipMap = true;
		shape.autoGenerateMips = false;
		var night = new RenderTexture(shape)
		{
			name = nightVisionProbe.name + " (default reflection)",
		};
		night.Create();
		var host = new GameObject("Night Vision Reflection Camera")
		{
			hideFlags = HideFlags.HideAndDontSave,
		};
		host.transform.SetPositionAndRotation(nightVisionProbe.transform.position, Quaternion.identity);
		Camera camera = host.AddComponent<Camera>();
		camera.enabled = false;
		camera.cullingMask = nightVisionProbe.cullingMask;
		camera.nearClipPlane = nightVisionProbe.nearClipPlane;
		camera.farClipPlane = nightVisionProbe.farClipPlane;
		camera.clearFlags = nightVisionProbe.clearFlags == ReflectionProbeClearFlags.Skybox ? CameraClearFlags.Skybox : CameraClearFlags.SolidColor;
		camera.backgroundColor = nightVisionProbe.backgroundColor;
		camera.allowHDR = nightVisionProbe.hdr;
		camera.allowMSAA = false;
		bool rendered = false;
		TimeOfDay.instance.ApplyNightvision();
		try
		{
			rendered = camera.RenderToCubemap(night);
		}
		finally
		{
			TimeOfDay.instance.ResetAtmosphere();
			Object.Destroy(host);
		}
		if (!rendered)
		{
			Debug.LogWarning("[render] the night vision reflection could not be rendered on this device; night vision keeps the day's.");
			night.Release();
			Object.Destroy(night);
			return null;
		}
		night.GenerateMips();
		return night;
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
