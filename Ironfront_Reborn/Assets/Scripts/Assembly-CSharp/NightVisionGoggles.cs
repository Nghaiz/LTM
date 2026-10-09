using Ironfront.Net.Unity;
using UnityEngine;
using UnityEngine.UI;

/// <summary>
/// Night Mode's night vision (phase P32): every player carries goggles, switched with
/// the night-vision key (<see cref="GameAction.NightVision"/>, N unless rebound), that run on a battery the room's host sized in the lobby.
/// </summary>
/// <remarks>
/// <para>
/// <b>The original's effect, on a battery.</b> Switching on does what the original's
/// <c>NightVision</c> item did -- the green, brighter atmosphere (<see cref="TimeOfDay.ApplyNightvision"/>),
/// the camera noise, the enable and disable clips -- and the night's fog thins to
/// <see cref="TimeOfDay.nightVisionFogFactor"/> of itself. The battery
/// (<see cref="NightVisionBattery"/>) is what keeps it a decision: it drains while on, refills at a
/// third of that rate while off, and switches itself off when empty.
/// </para>
/// <para>
/// Added by <see cref="NightModeDirector"/> on a client in a Night Mode room, and removed with the
/// night. Builds its own overlay canvas, as the breath bubbles do.
/// </para>
/// </remarks>
public sealed class NightVisionGoggles : MonoBehaviour
{
	private const int Segments = 10;
	private const float PanelWidth = 286f;
	private const float PanelHeight = 50f;
	private const float BlinkPerSecond = 3f;

	private static readonly Color Ready = new Color(0.55f, 1f, 0.45f, 1f);
	private static readonly Color Low = new Color(1f, 0.7f, 0.25f, 1f);
	private static readonly Color Empty = new Color(1f, 0.3f, 0.25f, 1f);
	private static readonly Color Unlit = new Color(1f, 1f, 1f, 0.12f);
	private static readonly Color PanelInk = new Color(0.02f, 0.07f, 0.1f, 0.72f);

	private static Sprite goggleSprite;

	private NightVisionBattery battery = new NightVisionBattery(0f);
	private AudioClip enableClip;
	private AudioClip disableClip;
	private AudioClip lowClip;
	private AudioSource audioSource;
	private bool wasAlive;
	private bool warnedLow;

	private GameObject canvasObject;
	private CanvasGroup group;
	private Image icon;
	private Image[] segments;
	private Text stateText;

	/// <summary>The battery this pair of goggles runs on.</summary>
	public NightVisionBattery Battery => battery;

	/// <summary>Sizes the battery (seconds of night vision when full) and takes the original item's clips.</summary>
	public void Configure(float batterySeconds, AudioClip enable, AudioClip disable)
	{
		if (battery.IsOn)
		{
			SwitchOff(playClip: false);
		}
		battery = new NightVisionBattery(batterySeconds);
		enableClip = enable;
		disableClip = disable;
		warnedLow = false;
	}

	private void Awake()
	{
		audioSource = gameObject.AddComponent<AudioSource>();
		audioSource.playOnAwake = false;
		audioSource.spatialBlend = 0f;
		audioSource.volume = 0.7f;
		lowClip = MakeBeep();
		if (!Application.isBatchMode)
		{
			BuildHud();
		}
	}

	private void OnDisable()
	{
		if (battery.IsOn)
		{
			SwitchOff(playClip: false);
		}
		if (group != null)
		{
			group.alpha = 0f;
		}
	}

	private void OnDestroy()
	{
		if (canvasObject != null)
		{
			Destroy(canvasObject);
		}
	}

	private void Update()
	{
		bool alive = LocalPlayerAlive();
		if (alive && !wasAlive)
		{
			// A new life, a fresh battery: the kit is new.
			battery.Refill();
			warnedLow = false;
		}
		if (!alive && battery.IsOn)
		{
			SwitchOff(playClip: false);
		}
		wasAlive = alive;

		if (alive && GameKeys.Down(GameAction.NightVision) && !LocalTextEntry.OwnsKeyboard && !IngameMenuUi.IsOpen())
		{
			Toggle();
		}

		if (battery.Tick(Time.deltaTime))
		{
			// Ran out: the battery has already switched itself off; the picture follows.
			SwitchOff(playClip: true);
		}
		if (battery.IsOn && battery.IsLow && !warnedLow)
		{
			warnedLow = true;
			Play(lowClip);
		}
		if (!battery.IsLow)
		{
			warnedLow = false;
		}

		Draw(alive);
	}

	/// <summary>On if the battery allows, off if on; a refusal beeps.</summary>
	public void Toggle()
	{
		if (battery.IsOn)
		{
			SwitchOff(playClip: true);
		}
		else if (battery.TrySwitchOn())
		{
			SwitchOn();
		}
		else
		{
			Play(lowClip);
		}
	}

	private void SwitchOn()
	{
		// Achievements v2: NAKED EYE, CREATURE OF THE NIGHT and GRAVEYARD SHIFT reward never doing this.
		Ironfront.Net.Unity.NightVisionReport.TurnedOn();
		if (TimeOfDay.instance != null)
		{
			TimeOfDay.instance.ApplyNightvision();
		}
		if (FpsActorController.instance != null)
		{
			FpsActorController.instance.EnableNoise();
		}
		Play(enableClip);
	}

	private void SwitchOff(bool playClip)
	{
		Ironfront.Net.Unity.NightVisionReport.TurnedOff();
		battery.SwitchOff();
		if (TimeOfDay.instance != null)
		{
			TimeOfDay.instance.ResetAtmosphere();
		}
		if (FpsActorController.instance != null)
		{
			FpsActorController.instance.DisableNoise();
		}
		if (playClip)
		{
			Play(disableClip);
		}
	}

	private void Play(AudioClip clip)
	{
		if (clip != null && audioSource != null)
		{
			audioSource.PlayOneShot(clip);
		}
	}

	private static bool LocalPlayerAlive()
	{
		FpsActorController controller = FpsActorController.instance;
		return controller != null && controller.actor != null && !controller.actor.dead;
	}

	private void Draw(bool alive)
	{
		if (group == null)
		{
			return;
		}
		group.alpha = alive ? 1f : 0f;
		if (!alive)
		{
			return;
		}

		float fraction = battery.Fraction;
		bool blinkOff = battery.IsLow && Mathf.Repeat(Time.unscaledTime * BlinkPerSecond, 1f) < 0.35f;
		Color ink = fraction <= 0f ? Empty : battery.IsLow ? (battery.IsOn ? Empty : Low) : Ready;
		int lit = Mathf.CeilToInt(fraction * Segments);
		for (int i = 0; i < segments.Length; i++)
		{
			segments[i].color = i < lit && !(blinkOff && i == lit - 1) ? ink : Unlit;
		}
		icon.color = battery.IsOn ? Ready : new Color(1f, 1f, 1f, 0.55f);
		stateText.text = StateWord();
		stateText.color = battery.IsOn ? Ready : battery.CanSwitchOn ? Color.white : Low;
	}

	private string StateWord()
	{
		if (battery.IsOn)
		{
			return "NIGHT VISION ON";
		}
		if (!battery.CanSwitchOn)
		{
			return "RECHARGING";
		}
		string key = "[" + GameKeys.Cap(GameAction.NightVision) + "] NIGHT VISION";
		return battery.Fraction < 1f ? key + "  ·  CHARGING" : key;
	}

	private void BuildHud()
	{
		canvasObject = new GameObject("Night Vision HUD", typeof(RectTransform));
		DontDestroyOnLoad(canvasObject);
		Canvas canvas = canvasObject.AddComponent<Canvas>();
		canvas.renderMode = RenderMode.ScreenSpaceOverlay;
		canvas.sortingOrder = 12;
		CanvasScaler scaler = canvasObject.AddComponent<CanvasScaler>();
		scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
		scaler.referenceResolution = new Vector2(1920f, 1080f);
		scaler.matchWidthOrHeight = 0.5f;
		group = canvasObject.AddComponent<CanvasGroup>();
		group.alpha = 0f;
		group.interactable = false;
		group.blocksRaycasts = false;

		// Bottom left, above the health readout and the breath bubbles.
		RectTransform panel = NewRect("Panel", canvasObject.transform);
		panel.anchorMin = panel.anchorMax = panel.pivot = Vector2.zero;
		panel.anchoredPosition = new Vector2(24f, 132f);
		panel.sizeDelta = new Vector2(PanelWidth, PanelHeight);
		Image back = panel.gameObject.AddComponent<Image>();
		back.color = PanelInk;
		back.raycastTarget = false;

		icon = NewRect("Goggles", panel).gameObject.AddComponent<Image>();
		icon.sprite = GoggleSprite();
		icon.raycastTarget = false;
		Place(icon.rectTransform, new Vector2(10f, 13f), new Vector2(40f, 24f));

		stateText = NewRect("State", panel).gameObject.AddComponent<Text>();
		stateText.font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
		stateText.fontSize = 13;
		stateText.fontStyle = FontStyle.Bold;
		stateText.alignment = TextAnchor.MiddleLeft;
		stateText.raycastTarget = false;
		Place(stateText.rectTransform, new Vector2(60f, 27f), new Vector2(PanelWidth - 70f, 18f));

		segments = new Image[Segments];
		float step = (PanelWidth - 70f) / Segments;
		for (int i = 0; i < Segments; i++)
		{
			Image segment = NewRect("Cell " + i, panel).gameObject.AddComponent<Image>();
			segment.raycastTarget = false;
			Place(segment.rectTransform, new Vector2(60f + (i * step), 9f), new Vector2(step - 3f, 12f));
			segments[i] = segment;
		}
	}

	private static RectTransform NewRect(string name, Transform parent)
	{
		RectTransform rect = new GameObject(name, typeof(RectTransform)).GetComponent<RectTransform>();
		rect.SetParent(parent, false);
		return rect;
	}

	private static void Place(RectTransform rect, Vector2 bottomLeft, Vector2 size)
	{
		rect.anchorMin = rect.anchorMax = rect.pivot = Vector2.zero;
		rect.anchoredPosition = bottomLeft;
		rect.sizeDelta = size;
	}

	// Two lenses on a strap, drawn once: the goggles' icon.
	private static Sprite GoggleSprite()
	{
		if (goggleSprite != null)
		{
			return goggleSprite;
		}
		const int width = 80;
		const int height = 48;
		Texture2D texture = new Texture2D(width, height, TextureFormat.RGBA32, false)
		{
			name = "Night Vision Goggles",
			wrapMode = TextureWrapMode.Clamp,
			filterMode = FilterMode.Bilinear
		};
		Color32[] pixels = new Color32[width * height];
		Vector2 left = new Vector2(22f, 24f);
		Vector2 right = new Vector2(58f, 24f);
		const float outer = 17f;
		const float inner = 11f;
		for (int y = 0; y < height; y++)
		{
			for (int x = 0; x < width; x++)
			{
				Vector2 p = new Vector2(x, y);
				float dl = Vector2.Distance(p, left);
				float dr = Vector2.Distance(p, right);
				float d = Mathf.Min(dl, dr);
				float a = 0f;
				if (d < outer)
				{
					// The rim solid, the lens a faint glass.
					a = d > inner ? Mathf.Clamp01(outer - d) : 0.3f;
				}
				else if (Mathf.Abs(y - 24f) < 2.5f && x > 36f && x < 44f)
				{
					a = 1f;
				}
				pixels[(y * width) + x] = new Color32(255, 255, 255, (byte)(a * 255f));
			}
		}
		texture.SetPixels32(pixels);
		texture.Apply();
		goggleSprite = Sprite.Create(texture, new Rect(0f, 0f, width, height), new Vector2(0.5f, 0.5f), 100f);
		return goggleSprite;
	}

	// A short double beep for "low" and "cannot switch on": synthesised, so no asset carries it.
	private static AudioClip MakeBeep()
	{
		const int rate = 22050;
		int samples = rate / 4;
		float[] data = new float[samples];
		for (int i = 0; i < samples; i++)
		{
			float t = (float)i / rate;
			bool sounding = t < 0.07f || (t > 0.12f && t < 0.19f);
			data[i] = sounding ? 0.25f * Mathf.Sin(2f * Mathf.PI * 1800f * t) : 0f;
		}
		AudioClip clip = AudioClip.Create("Night Vision Low", samples, 1, rate, false);
		clip.SetData(data, 0);
		return clip;
	}
}
