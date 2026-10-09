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

	/// <summary>
	/// The health meter's fill, sized by its right anchor as <see cref="vehicleHealth"/> is.
	/// Optional: a HUD without a meter shows the figure alone.
	/// </summary>
	public RectTransform healthBar;

	/// <summary>What <c>Actor</c> restores on every spawn, so a full meter.</summary>
	private const float FullHealth = 100f;

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

	/// <summary>
	/// One line while the local soldier stands at a supply cache that will not serve him:
	/// "ENEMY AMMO CACHE - TAKE QUARRY TO RESUPPLY HERE". Optional: a HUD without it says nothing.
	/// </summary>
	/// <remarks>
	/// A cache serves only the side holding its flag (<see cref="SupplyCache"/>, owner request
	/// 2026-10-03), and flags change hands all match, so the same crates refill a player one minute
	/// and ignore him the next. The radar dims a cache that is not his, but nobody reads the radar
	/// standing on the crate, and the owner's friend reported the result as a bug in the v4.3.0
	/// playtest: "sometimes standing by the ammo or health crate refills nothing".
	/// </remarks>
	public Text supplyHint;

	/// <summary>Seconds between looks for a cache: one is a place the soldier stands at.</summary>
	private const float SupplyHintInterval = 0.25f;

	private float nextSupplyHint;

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
		if (Ironfront.Net.Unity.GameKeys.Down(Ironfront.Net.Unity.GameAction.ToggleHud) && !Ironfront.Net.Unity.LocalTextEntry.OwnsKeyboard)
		{
			canvas.enabled = !canvas.enabled;
		}
		Actor actor = FpsActorController.instance.actor;
		if (actor.IsSeated())
		{
			SetVehicleBarAmount(actor.seat.vehicle.GetHealthRatio());
		}
		UpdateSupplyHint(actor);
	}

	private void UpdateSupplyHint(Actor actor)
	{
		if (supplyHint == null || Time.unscaledTime < nextSupplyHint)
		{
			return;
		}
		nextSupplyHint = Time.unscaledTime + SupplyHintInterval;
		string text = SupplyHintWording(actor);
		supplyHint.enabled = text != null;
		if (text != null)
		{
			supplyHint.text = text;
		}
	}

	/// <summary>Why the cache under this soldier gives him nothing, or null when it serves him or there is none.</summary>
	public static string SupplyHintWording(Actor actor)
	{
		if (actor == null || actor.dead || actor.IsSeated())
		{
			return null;
		}
		SupplyCache cache = SupplyCache.Reaching(actor.Position());
		if (cache == null || cache.ServedTeam == actor.team)
		{
			return null;
		}
		string what = cache.kind == SupplyKind.Medical ? "MEDICAL STATION" : "AMMO CACHE";
		string whose = cache.ServedTeam < 0 ? "NEUTRAL " : "ENEMY ";
		return whose + what + " - TAKE " + cache.FlagName + " TO RESUPPLY HERE";
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
		if (healthBar != null)
		{
			healthBar.anchorMax = new Vector2(Mathf.Clamp01(health / FullHealth), 1f);
		}
	}

	/// <summary>
	/// Whether the in-match HUD is drawn: false before deploying, in the menus, and while the End
	/// key has it hidden. <see cref="CornerMinimap"/> follows it.
	/// </summary>
	public static bool IsShown => instance != null && instance.canvas != null && instance.canvas.enabled;

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

	/// <summary>
	/// The capture-point name line: right-aligned under the top-right row (the side chip and the
	/// flag indicator), as wide as that row, so it reads as the row's caption and stays clear of the
	/// killfeed below (which starts 190 canvas units down; this ends near 122).
	/// </summary>
	/// <remarks>
	/// The row is <c>BuildMatchHud</c>'s: a 62-unit flag, a 10-unit gap and a 220-unit chip. Those
	/// constants live in the Editor assembly, which this cannot reference, hence the plain number.
	/// </remarks>
	private const float FlagNameWidth = 292f;

	private const float FlagNameHeight = 30f;

	private const float FlagNameGap = 6f;

	private const int FlagNameFontSize = 20;

	private static readonly Color FlagNameInk = new Color(0.95f, 0.96f, 0.93f);

	private static readonly Color FlagNameEdge = new Color(0f, 0f, 0f, 0.85f);

	private Text flagName;

	/// <summary>The point whose name the line shows, so leaving another point cannot clear it.</summary>
	private Object flagNameOwner;

	/// <summary>
	/// Names the capture point the player just stepped onto, under the flag indicator; a point with
	/// no name (<paramref name="wording"/> null) shows no line. Owner request 2026-10-09.
	/// </summary>
	/// <remarks>
	/// <b>A child of the flag indicator</b>, so it comes and goes with it and moves with it if the
	/// corner is ever rearranged, and built here in code (as <c>CornerMinimap</c> is) rather than
	/// in the prefab, which only the Editor can rebuild.
	/// </remarks>
	public void ShowFlagName(Object point, string wording)
	{
		flagNameOwner = point;
		if (string.IsNullOrEmpty(wording))
		{
			if (flagName != null)
			{
				flagName.gameObject.SetActive(false);
			}
			return;
		}
		if (flagName == null)
		{
			flagName = BuildFlagName();
		}
		flagName.text = wording;
		flagName.gameObject.SetActive(true);
	}

	/// <summary>Takes the line down, if it is still naming <paramref name="point"/>.</summary>
	public void ClearFlagName(Object point)
	{
		if (flagNameOwner != point)
		{
			return;
		}
		flagNameOwner = null;
		if (flagName != null)
		{
			flagName.gameObject.SetActive(false);
		}
	}

	private Text BuildFlagName()
	{
		Text text = new GameObject("Flag Name", typeof(RectTransform)).AddComponent<Text>();
		RectTransform rect = text.rectTransform;
		rect.SetParent(flagIndicatorParent.rectTransform, false);
		rect.anchorMin = new Vector2(1f, 0f);
		rect.anchorMax = new Vector2(1f, 0f);
		rect.pivot = new Vector2(1f, 1f);
		rect.anchoredPosition = new Vector2(0f, -FlagNameGap);
		rect.sizeDelta = new Vector2(FlagNameWidth, FlagNameHeight);
		text.font = health != null && health.font != null
			? health.font
			: Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
		text.fontSize = FlagNameFontSize;
		text.fontStyle = FontStyle.Bold;
		text.alignment = TextAnchor.UpperRight;
		text.horizontalOverflow = HorizontalWrapMode.Overflow;
		text.verticalOverflow = VerticalWrapMode.Overflow;
		text.color = FlagNameInk;
		text.raycastTarget = false;
		Outline edge = text.gameObject.AddComponent<Outline>();
		edge.effectColor = FlagNameEdge;
		edge.effectDistance = new Vector2(1.2f, -1.2f);
		return text;
	}
}
