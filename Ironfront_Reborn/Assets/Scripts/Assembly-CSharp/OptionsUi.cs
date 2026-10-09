using System.Collections;
using Ironfront.Net.Unity;
using UnityEngine;
using UnityEngine.Audio;
using UnityEngine.UI;

public class OptionsUi : MonoBehaviour
{
	public class Options
	{
		public const int HELICOPTER_TYPE_BATTLEFIELD = 0;

		public const int HELICOPTER_TYPE_ARMA = 1;

		public const int HELICOPTER_TYPE_CUSTOM = 2;

		public const int DIFFICULTY_EASY = 0;

		public const int DIFFICULTY_NORMAL = 1;

		public const int DIFFICULTY_HARD = 2;

		public float mouseSensitivity;

		public float sniperMultiplier;

		public float helicopterSensitivity;

		public float vegetationDensity;

		public float vegetationDistance;

		public float masterVolume;

		public float fieldOfView;

		public bool mouseInvert;

		public bool hitmarkers;

		public bool heliInvertPitch;

		public bool heliInvertYaw;

		public bool heliInvertRoll;

		public bool heliInvertThrottle;

		public bool autoReload;

		public bool toggleAim;

		public bool toggleCrouch;

		public int helicopterType;

		public int difficulty;

		public static Options Load()
		{
			Options options = new Options();
			options.mouseSensitivity = Mathf.Max(0.05f, PlayerPrefs.GetFloat(GameOptionsStore.MouseSensitivityKey, GameOptionsStore.DefaultMouseSensitivity));
			options.sniperMultiplier = Mathf.Max(0.05f, PlayerPrefs.GetFloat(GameOptionsStore.ScopeMultiplierKey, GameOptionsStore.DefaultScopeMultiplier));
			options.mouseInvert = PlayerPrefs.GetInt(GameOptionsStore.InvertMouseKey, 0) == 1;
			options.helicopterType = PlayerPrefs.GetInt(GameOptionsStore.HelicopterStyleKey, GameOptionsStore.DefaultHelicopterStyle);
			options.helicopterSensitivity = PlayerPrefs.GetFloat(GameOptionsStore.HelicopterSensitivityKey, GameOptionsStore.DefaultHelicopterSensitivity);
			options.heliInvertPitch = PlayerPrefs.GetInt(GameOptionsStore.HelicopterInvertPitchKey, 0) == 1;
			options.heliInvertYaw = PlayerPrefs.GetInt(GameOptionsStore.HelicopterInvertYawKey, 0) == 1;
			options.heliInvertRoll = PlayerPrefs.GetInt(GameOptionsStore.HelicopterInvertRollKey, 0) == 1;
			options.heliInvertThrottle = PlayerPrefs.GetInt(GameOptionsStore.HelicopterInvertThrottleKey, 1) == 1;
			options.hitmarkers = PlayerPrefs.GetInt(GameOptionsStore.HitIndicatorsKey, 1) == 1;
			options.autoReload = PlayerPrefs.GetInt(GameOptionsStore.AutoReloadKey, 0) == 1;
			options.difficulty = PlayerPrefs.GetInt(GameOptionsStore.DifficultyKey, GameOptionsStore.DefaultDifficulty);
			// Fractions of the preset's grass, full when never set (Ironfront.Net.Unity.VegetationRules).
			options.vegetationDensity = Mathf.Clamp01(PlayerPrefs.GetFloat(VegetationRules.DensityKey, VegetationRules.DefaultDensity));
			options.vegetationDistance = Mathf.Clamp01(PlayerPrefs.GetFloat(VegetationRules.DistanceKey, VegetationRules.DefaultDistance));
			options.masterVolume = Mathf.Clamp01(PlayerPrefs.GetFloat(GameOptionsStore.MasterVolumeKey, GameOptionsStore.DefaultMasterVolume));
			options.toggleAim = PlayerPrefs.GetInt(GameOptionsStore.ToggleAimKey, 0) == 1;
			options.toggleCrouch = PlayerPrefs.GetInt(GameOptionsStore.ToggleCrouchKey, 0) == 1;
			options.fieldOfView = PlayerPrefs.GetFloat(GameOptionsStore.FieldOfViewKey, GameOptionsStore.DefaultFieldOfView);
			return options;
		}

		public void Save()
		{
			PlayerPrefs.SetFloat(GameOptionsStore.MouseSensitivityKey, Mathf.Max(0.05f, instance.mouseSensitivity.value));
			PlayerPrefs.SetFloat(GameOptionsStore.ScopeMultiplierKey, Mathf.Max(0.05f, instance.sniperMultiplier.value));
			PlayerPrefs.SetInt(GameOptionsStore.InvertMouseKey, instance.mouseInvert.isOn ? 1 : 0);
			PlayerPrefs.SetInt(GameOptionsStore.HelicopterStyleKey, instance.helicopterType.value);
			PlayerPrefs.SetFloat(GameOptionsStore.HelicopterSensitivityKey, instance.helicopterSensitivity.value);
			PlayerPrefs.SetInt(GameOptionsStore.HelicopterInvertPitchKey, instance.heliInvertPitch.isOn ? 1 : 0);
			PlayerPrefs.SetInt(GameOptionsStore.HelicopterInvertYawKey, instance.heliInvertYaw.isOn ? 1 : 0);
			PlayerPrefs.SetInt(GameOptionsStore.HelicopterInvertRollKey, instance.heliInvertRoll.isOn ? 1 : 0);
			PlayerPrefs.SetInt(GameOptionsStore.HelicopterInvertThrottleKey, instance.heliInvertThrottle.isOn ? 1 : 0);
			PlayerPrefs.SetInt(GameOptionsStore.HitIndicatorsKey, instance.hitmarkers.isOn ? 1 : 0);
			PlayerPrefs.SetInt(GameOptionsStore.AutoReloadKey, instance.autoReload.isOn ? 1 : 0);
			PlayerPrefs.SetInt(GameOptionsStore.DifficultyKey, instance.difficulty.value);
			PlayerPrefs.SetFloat(VegetationRules.DensityKey, instance.vegetationDensity.value);
			PlayerPrefs.SetFloat(VegetationRules.DistanceKey, instance.vegetationDistance.value);
			PlayerPrefs.SetFloat(GameOptionsStore.MasterVolumeKey, instance.masterVolume.value);
			PlayerPrefs.SetInt(GameOptionsStore.ToggleAimKey, instance.toggleAim.isOn ? 1 : 0);
			PlayerPrefs.SetInt(GameOptionsStore.ToggleCrouchKey, instance.toggleCrouch.isOn ? 1 : 0);
			PlayerPrefs.SetFloat(GameOptionsStore.FieldOfViewKey, instance.fieldOfView.value);
			PlayerPrefs.Save();
		}
	}

	public static OptionsUi instance;

	private static Options options;

	private Canvas canvas;

	public AudioMixer audioMixer;

	public RawImage hitmarkerEffect;

	public Text fieldOfViewLabel;

	private AudioSource hitmarkerAudio;

	public Slider mouseSensitivity;

	public Slider sniperMultiplier;

	public Slider helicopterSensitivity;

	public Slider vegetationDensity;

	public Slider vegetationDistance;

	public Slider masterVolume;

	public Slider fieldOfView;

	public Toggle mouseInvert;

	public Toggle heliInvertPitch;

	public Toggle heliInvertYaw;

	public Toggle heliInvertRoll;

	public Toggle heliInvertThrottle;

	public Toggle hitmarkers;

	public Toggle autoReload;

	public Toggle toggleAim;

	public Toggle toggleCrouch;

	public Dropdown helicopterType;

	public Dropdown difficulty;

	public static void Show()
	{
		if (instance != null)
		{
			instance.canvas.enabled = true;
		}
	}

	public static void Hide()
	{
		if (instance != null)
		{
			instance.canvas.enabled = false;
		}
	}

	public static bool IsOpen()
	{
		return instance != null && instance.canvas.enabled;
	}

	public static void SaveAndClose()
	{
		if (instance != null)
		{
			instance.Save();
		}
	}

	/// <summary>
	/// Re-reads every option from <c>PlayerPrefs</c> and applies it to the running game: the
	/// settings screen's Apply, in the menu and in a match (<see cref="Ironfront.Net.Unity.GameOptionsStore"/>).
	/// </summary>
	public static void ReloadFromPrefs()
	{
		options = Options.Load();
		if (instance != null)
		{
			instance.ApplyOptions();
		}
		if (PlayerFpParent.instance != null)
		{
			PlayerFpParent.instance.SetupVerticalFov(options.fieldOfView);
		}
	}

	[RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
	private static void InstallApplyHandler()
	{
		GameOptionsStore.ApplyHandler = ReloadFromPrefs;
	}

	public static Options GetOptions()
	{
		if (options == null)
		{
			options = Options.Load();
		}
		return options;
	}

	private void Awake()
	{
		if (instance != null)
		{
			Object.Destroy(instance.gameObject);
		}
		instance = this;
		Object.DontDestroyOnLoad(base.gameObject);
		canvas = GetComponent<Canvas>();
		hitmarkerAudio = hitmarkerEffect.GetComponent<AudioSource>();
		Load();
		Hide();
	}

	private void Start()
	{
		ApplyOptions();
	}

	private void Load()
	{
		Show();
		options = Options.Load();
		mouseSensitivity.value = options.mouseSensitivity;
		sniperMultiplier.value = options.sniperMultiplier;
		mouseInvert.isOn = options.mouseInvert;
		helicopterType.value = options.helicopterType;
		helicopterSensitivity.value = options.helicopterSensitivity;
		heliInvertPitch.isOn = options.heliInvertPitch;
		heliInvertYaw.isOn = options.heliInvertYaw;
		heliInvertRoll.isOn = options.heliInvertRoll;
		heliInvertThrottle.isOn = options.heliInvertThrottle;
		hitmarkers.isOn = options.hitmarkers;
		autoReload.isOn = options.autoReload;
		difficulty.value = options.difficulty;
		vegetationDensity.value = options.vegetationDensity;
		vegetationDistance.value = options.vegetationDistance;
		masterVolume.value = options.masterVolume;
		toggleAim.isOn = options.toggleAim;
		toggleCrouch.isOn = options.toggleCrouch;
		fieldOfView.value = options.fieldOfView;
		ApplyOptions();
	}

	private void Update()
	{
		vegetationDistance.interactable = vegetationDensity.value >= 0.01f;
	}

	public void Cancel()
	{
		Load();
		Hide();
	}

	public void Save()
	{
		options.Save();
		Load();
		Hide();
	}

	private void ApplyOptions()
	{
		if (DetailObjectQuality.instance != null)
		{
			DetailObjectQuality.instance.ApplyQuality();
		}
		float num = GetOptions().masterVolume;
		float value = 0f - (Mathf.Pow(80f, 1f - num) - 1f);
		audioMixer.SetFloat("volume", value);
	}

	public void ToggleHitmarker()
	{
		if (hitmarkers.isOn)
		{
			CancelInvoke();
			StartCoroutine(Hitmarker());
		}
	}

	private IEnumerator Hitmarker()
	{
		hitmarkerEffect.enabled = true;
		hitmarkerAudio.Play();
		yield return new WaitForSecondsRealtime(0.2f);
		hitmarkerEffect.enabled = false;
	}

	public void UpdateFieldOfView()
	{
		string text = Mathf.RoundToInt(fieldOfView.value).ToString();
		fieldOfViewLabel.text = text;
		if (PlayerFpParent.instance != null)
		{
			PlayerFpParent.instance.SetupVerticalFov(fieldOfView.value);
		}
	}
}
