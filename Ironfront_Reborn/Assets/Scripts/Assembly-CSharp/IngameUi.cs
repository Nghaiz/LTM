using System.Collections;
using UnityEngine;
using UnityEngine.UI;

public class IngameUi : MonoBehaviour
{
	public static IngameUi instance;

	private Canvas canvas;

	public Text currentAmmo;

	public Text spareAmmo;

	public Text health;

	public Image weapon;

	public RawImage hitmarker;

	public RawImage damageVignette;

	public RawImage damageIndicator;

	public AnimationCurve vignetteIntensityCurve;

	public SoundBank healSounds;

	public SoundBank resupplySounds;

	public RawImage resupplyHealthIndicator;

	public RawImage resupplyAmmoIndicator;

	public RawImage vehicleHealthBackground;

	public RawImage vehicleHealth;

	public RawImage flagIndicatorParent;

	public RawImage flagIndicatorBackground;

	public RawImage flagIndicator;

	private AudioSource hitmarkerSound;

	private MinimapCamera minimapCamera;

	private Action hitmarkerAction = new Action(0.15f);

	private Action damageIndicatorAction = new Action(1.5f);

	private Action resupplyHealthAction = new Action(1.5f);

	private Action resupplyAmmoAction = new Action(1.5f);

	private Color damageIndicatorColor = Color.red;

	private Action vignetteAction = new Action(1f);

	private float vignetteIntensity;

	private Coroutine flashVehicleBarCoroutine;

	/// <summary>Marks a hit at normal severity. Every pre-V10 caller lands here unchanged.</summary>
	public static void Hit()
	{
		Hit(0);
	}

	/// <summary>
	/// Marks a hit, loud in proportion to what it was: 0 normal, 1 headshot, 2 kill.
	/// </summary>
	/// <remarks>
	/// <para>
	/// The parameterless form could not express the severity the networked hitmarker model
	/// computes, and a kill outranks a headshot when both are true. This overload is additive —
	/// <see cref="Hit()"/> delegates here with 0, so no existing caller changes behaviour.
	/// </para>
	/// <para>
	/// <c>int</c> rather than the <c>HitmarkerSeverity</c> enum on purpose: Assembly-CSharp
	/// takes no dependency on Ironfront.Net.Replication for a cosmetic. The mapping is the
	/// enum's own numeric order, which is why that enum is documented as ordered by loudness.
	/// </para>
	/// <para>
	/// Not to be confused with <c>Hit(Ray, RaycastHit)</c> on the projectile hierarchy — a
	/// different method on a different type.
	/// </para>
	/// </remarks>
	public static void Hit(int severity)
	{
		// Reached from every projectile, melee and explosion impact, so it runs on a dedicated
		// server for every bot-versus-bot hit. There is no HUD there to mark.
		if (instance == null)
		{
			return;
		}
		if (OptionsUi.GetOptions().hitmarkers)
		{
			instance.ShowHitmarker(severity);
		}
	}

	private void Awake()
	{
		instance = this;
		canvas = GetComponent<Canvas>();
		minimapCamera = Object.FindObjectOfType<MinimapCamera>();
		hitmarkerSound = hitmarker.GetComponent<AudioSource>();
		damageVignette.color = Color.clear;
		Hide();
	}

	public void SetAmmoText(int current, int spare)
	{
		currentAmmo.text = ((current == -1) ? string.Empty : current.ToString());
		if (spare >= 0)
		{
			spareAmmo.text = "/" + spare;
			return;
		}
		switch (spare)
		{
		case -1:
			spareAmmo.text = string.Empty;
			break;
		case -2:
			spareAmmo.text = "/∞";
			break;
		}
	}

	private void Update()
	{
		resupplyHealthIndicator.enabled = !resupplyHealthAction.TrueDone();
		resupplyAmmoIndicator.enabled = !resupplyAmmoAction.TrueDone();
		resupplyHealthIndicator.rectTransform.anchoredPosition = new Vector2(0f, resupplyHealthAction.Ratio() * 30f);
		resupplyAmmoIndicator.rectTransform.anchoredPosition = new Vector2(0f, resupplyAmmoAction.Ratio() * 30f);
		Color white = Color.white;
		white.a = Mathf.Clamp01(2f - 2f * resupplyHealthAction.Ratio());
		resupplyHealthIndicator.color = white;
		white.a = Mathf.Clamp01(2f - 2f * resupplyAmmoAction.Ratio());
		resupplyAmmoIndicator.color = white;
		if (Input.GetKeyDown(KeyCode.End))
		{
			canvas.enabled = !canvas.enabled;
		}
		Actor actor = FpsActorController.instance.actor;
		if (actor.IsSeated())
		{
			SetVehicleBarAmount(actor.seat.vehicle.GetHealthRatio());
		}
	}

	private void LateUpdate()
	{
		Vector2 vector = minimapCamera.camera.WorldToViewportPoint(FpsActorController.instance.actor.Position());
		hitmarker.enabled = !hitmarkerAction.Done();
		Color white = Color.white;
		if (vignetteAction.Done())
		{
			white.a = 0f;
		}
		else
		{
			float num = Mathf.Lerp(0.5f, 0f, Mathf.Clamp01(vignetteAction.Ratio() * 10f));
			white.g -= num;
			white.b -= num;
			white.a = Mathf.Lerp(0f, vignetteIntensity, vignetteIntensityCurve.Evaluate(vignetteAction.Ratio()));
		}
		damageVignette.color = white;
		white = damageIndicatorColor;
		white.a = Mathf.Clamp01(3f - 3f * damageIndicatorAction.Ratio());
		damageIndicator.color = white;
	}

	public void SetWeapon(Weapon weapon)
	{
		this.weapon.sprite = weapon.uiSprite;
	}

	public void SetHealth(float health)
	{
		this.health.text = Mathf.CeilToInt(health).ToString();
	}

	public void Hide()
	{
		canvas.enabled = false;
	}

	public void Show()
	{
		canvas.enabled = true;
	}

	private void ShowHitmarker(int severity)
	{
		if (hitmarkerAction.Done())
		{
			hitmarkerAction.Start();
			// Severity rides the pitch rather than a second clip: a headshot ticks higher and a
			// kill higher still, off the one authored sound. The colour is client-track work
			// (E7) -- the audio is what the shipped component can already express.
			hitmarkerSound.pitch = 1f + 0.15f * Mathf.Clamp(severity, 0, 2);
			hitmarkerSound.Play();
		}
	}

	public void FlashVehicleBar(float amount)
	{
		if (flashVehicleBarCoroutine != null)
		{
			StopCoroutine(flashVehicleBarCoroutine);
		}
		flashVehicleBarCoroutine = StartCoroutine(FlashVehicleBarCoroutine(amount));
	}

	private IEnumerator FlashVehicleBarCoroutine(float amount)
	{
		ShowVehicleBar(amount, false);
		yield return new WaitForSeconds(2f);
		HideVehicleBar(false);
	}

	public void ShowVehicleBar(float amount, bool cancelCoroutine = true)
	{
		if (cancelCoroutine && flashVehicleBarCoroutine != null)
		{
			StopCoroutine(flashVehicleBarCoroutine);
		}
		vehicleHealth.enabled = true;
		vehicleHealthBackground.enabled = true;
		SetVehicleBarAmount(amount);
	}

	public void HideVehicleBar(bool cancelCoroutine = true)
	{
		if (cancelCoroutine && flashVehicleBarCoroutine != null)
		{
			StopCoroutine(flashVehicleBarCoroutine);
		}
		vehicleHealth.enabled = false;
		vehicleHealthBackground.enabled = false;
	}

	public void SetVehicleBarAmount(float amount)
	{
		vehicleHealth.rectTransform.anchorMax = new Vector2(amount, 1f);
		vehicleHealth.uvRect = new Rect(0f, 0f, 6f * amount, 1f);
	}

	public void ShowVignette(float intensity, float duration)
	{
		vignetteIntensity = intensity;
		vignetteAction.StartLifetime(duration);
	}

	public void ShowDamageIndicator(float angle, bool onlyBalanceDamage)
	{
		damageIndicatorAction.Start();
		damageIndicator.rectTransform.rotation = Quaternion.Euler(0f, 0f, angle);
		damageIndicatorColor = ((!onlyBalanceDamage) ? Color.red : Color.yellow);
	}

	public void Heal()
	{
		healSounds.PlayRandom();
		resupplyHealthAction.Start();
	}

	public void Resupply()
	{
		resupplySounds.PlayRandom();
		resupplyAmmoAction.Start();
	}

	public void ShowFlagIndicator()
	{
		flagIndicatorParent.gameObject.SetActive(true);
	}

	public void SetFlagIndicator(float amount, int owner)
	{
		if (flagIndicatorParent.gameObject.activeInHierarchy)
		{
			Color color = ColorScheme.TeamColor(owner);
			flagIndicatorBackground.rectTransform.anchorMax = new Vector2(1f, amount);
			flagIndicatorBackground.uvRect = new Rect(0f, 0f, 1f, amount);
			flagIndicatorBackground.color = color;
			flagIndicator.color = color;
		}
	}

	public void HideFlagIndicator()
	{
		flagIndicatorParent.gameObject.SetActive(false);
	}
}
