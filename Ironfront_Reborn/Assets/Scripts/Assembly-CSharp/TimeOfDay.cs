using System;
using System.Collections.Generic;
using UnityEngine;

public class TimeOfDay : MonoBehaviour
{
	[Serializable]
	public class Atmosphere
	{
		public Color sky;

		public Color equator;

		public Color ground;

		public float fogDensity;

		public Color fog;

		public Material skyboxMaterial;
	}

	private const float NIGHT_VISION_AMOUNT = 0.7f;

	private const float NIGHT_VISION_EXTRA_EXPOSURE = 1.1f;

	private const float NIGHT_VISION_LIGHT_BASE = 0.4f;

	private const float NIGHT_VISION_LIGHT_MULTIPLIER = 4f;

	public static TimeOfDay instance;

	public Atmosphere nightAtmosphere;

	private Atmosphere atmosphere;

	// The day as the scene authored it, captured once, so a server or client that goes to night and
	// back for the next room (phase P32 Night Mode) returns to the same day.
	private Atmosphere dayAtmosphere;

	/// <summary>
	/// How much of the night's fog night vision leaves (phase P32): it amplifies light, so it sees
	/// further into the dark, though not as far as by day.
	/// </summary>
	public float nightVisionFogFactor = 1f;

	/// <summary>Whether the night is up: the Night child shown and its atmosphere applied.</summary>
	public bool IsNight { get; private set; }

	/// <summary>Whether Start has run: before it, a night is chosen by GameManager.nightMode.</summary>
	public bool Started { get; private set; }

	public bool testNight;

	private Light[] lights;

	private Dictionary<Light, float> lightIntensity;

	private void Awake()
	{
		instance = this;
	}

	private void Start()
	{
		if (GameManager.instance.nightMode)
		{
			ApplyNight();
		}
		else
		{
			ApplyDay();
		}
		CaptureLights();
		ReflectionProber.instance.SetupProbes();
		Started = true;
	}

	// Lights in the scene now, at their authored strength: what night vision multiplies. Taken
	// again after a switch, because the Day and Night children each hold a light the other hides.
	private void CaptureLights()
	{
		lights = UnityEngine.Object.FindObjectsOfType<Light>();
		lightIntensity = new Dictionary<Light, float>(lights.Length);
		Light[] array = lights;
		foreach (Light light in array)
		{
			lightIntensity.Add(light, light.intensity);
		}
	}

	private void ApplyDay()
	{
		base.transform.Find("Day").gameObject.SetActive(true);
		base.transform.Find("Night").gameObject.SetActive(false);
		if (dayAtmosphere == null)
		{
			dayAtmosphere = new Atmosphere();
			dayAtmosphere.sky = RenderSettings.ambientSkyColor;
			dayAtmosphere.equator = RenderSettings.ambientEquatorColor;
			dayAtmosphere.ground = RenderSettings.ambientGroundColor;
			dayAtmosphere.fog = RenderSettings.fogColor;
			dayAtmosphere.fogDensity = RenderSettings.fogDensity;
			dayAtmosphere.skyboxMaterial = RenderSettings.skybox;
		}
		IsNight = false;
		ApplyAtmosphere(dayAtmosphere);
	}

	private void ApplyNight()
	{
		ApplyNight(nightAtmosphere);
	}

	private void ApplyNight(Atmosphere night)
	{
		if (dayAtmosphere == null && !IsNight)
		{
			// Remember the day before the night replaces it, so SetNight(false) can bring it back.
			dayAtmosphere = new Atmosphere();
			dayAtmosphere.sky = RenderSettings.ambientSkyColor;
			dayAtmosphere.equator = RenderSettings.ambientEquatorColor;
			dayAtmosphere.ground = RenderSettings.ambientGroundColor;
			dayAtmosphere.fog = RenderSettings.fogColor;
			dayAtmosphere.fogDensity = RenderSettings.fogDensity;
			dayAtmosphere.skyboxMaterial = RenderSettings.skybox;
		}
		base.transform.Find("Day").gameObject.SetActive(false);
		base.transform.Find("Night").gameObject.SetActive(true);
		IsNight = true;
		ApplyAtmosphere(night);
	}

	/// <summary>
	/// Day or night while the map is up (phase P32 Night Mode): a game server hosts room after room
	/// on one loaded map, and a client learns the room's mode as it joins. <paramref name="night"/>
	/// may override the scene's own night atmosphere; null keeps it.
	/// </summary>
	public void SetNight(bool night, Atmosphere nightOverride = null)
	{
		if (night)
		{
			ApplyNight(nightOverride ?? nightAtmosphere);
		}
		else
		{
			ApplyDay();
		}
		CaptureLights();
		if (ReflectionProber.instance != null)
		{
			ReflectionProber.instance.SetupProbesAgain();
		}
	}

	private void ApplyAtmosphere(Atmosphere atmosphere)
	{
		this.atmosphere = atmosphere;
		RenderSettings.ambientSkyColor = atmosphere.sky;
		RenderSettings.ambientEquatorColor = atmosphere.equator;
		RenderSettings.ambientGroundColor = atmosphere.ground;
		RenderSettings.fogColor = atmosphere.fog;
		RenderSettings.fogDensity = atmosphere.fogDensity;
		RenderSettings.skybox = new Material(atmosphere.skyboxMaterial);
	}

	public void ApplyNightvision()
	{
		instance.BlendAtmosphereColor(Color.green, 0.7f, 1.1f);
		RenderSettings.fogDensity = atmosphere.fogDensity * nightVisionFogFactor;
		ReflectionProber.instance.SwitchToNightVision();
		Light[] array = lights;
		foreach (Light light in array)
		{
			light.intensity = lightIntensity[light] * 4f + 0.4f;
		}
	}

	private void BlendAtmosphereColor(Color target, float amount, float extraExposure)
	{
		RenderSettings.ambientSkyColor = (1f + extraExposure) * Color.Lerp(atmosphere.sky, target, amount);
		RenderSettings.ambientEquatorColor = (1f + extraExposure) * Color.Lerp(atmosphere.equator, target, amount);
		RenderSettings.ambientGroundColor = (1f + extraExposure) * Color.Lerp(atmosphere.ground, target, amount);
		RenderSettings.fogColor = Color.Lerp(atmosphere.fog, target, amount);
		// Procedural skies tint through _SkyTint; the panoramic, cubemap and six-sided skies through _Tint.
		string tint = RenderSettings.skybox.HasProperty("_SkyTint") ? "_SkyTint" : "_Tint";
		if (RenderSettings.skybox.HasProperty(tint))
		{
			RenderSettings.skybox.SetColor(tint, Color.Lerp(atmosphere.skyboxMaterial.GetColor(tint), target, amount));
		}
		if (RenderSettings.skybox.HasProperty("_Exposure"))
		{
			RenderSettings.skybox.SetFloat("_Exposure", atmosphere.skyboxMaterial.GetFloat("_Exposure") + extraExposure);
		}
	}

	public void ResetAtmosphere()
	{
		ApplyAtmosphere(atmosphere);
		ReflectionProber.instance.Reset();
		Light[] array = lights;
		foreach (Light light in array)
		{
			light.intensity = lightIntensity[light];
		}
	}
}
