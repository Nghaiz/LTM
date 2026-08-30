using System.Collections.Generic;
using Ironfront.Net.Protocol;
using Ironfront.Net.Unity;
using UnityEngine;
using UnityEngine.UI;

public class MinimapUi : MonoBehaviour
{
	private const float MINIMAP_SCALE = 1.3f;

	// Not -1: SpawnPoint.owner defaults to -1 and a CapturePoint can stay there (neutral,
	// uncaptured, non-assault mode -- CapturePoint.cs:91-100), so -1 would make a neutral
	// point's button interactable instead of leaving every button disabled (V10 D17).
	// TeamId.None can never equal a real owner value.
	private const int UNRESOLVED_TEAM = TeamId.None;

	public static MinimapUi instance;

	public RectTransform loadoutParent;

	public RectTransform ingameParent;

	public RawImage minimap;

	public GameObject minimapSpawnPointPrefab;

	public GameObject actorBlipPrefab;

	/// <summary>
	/// Drawn for a capture point. Falls back to <see cref="minimapSpawnPointPrefab"/> when
	/// unassigned. debt-closure phase 2 task 2d, ledger C-6.
	/// </summary>
	/// <remarks>
	/// Optional because phase 2 writes no prefabs or scenes — those are Phase 1's — so the
	/// marker has to work on a <c>MinimapUi</c> that predates its authoring. The fallback is a
	/// spawn-point icon, which is at least the right size and in the right place.
	/// </remarks>
	public GameObject capturePointMarkerPrefab;

	public Sprite spawnPointSprite;

	public Sprite spawnPointSelectedSprite;

	private Dictionary<SpawnPoint, Button> minimapSpawnPointButton;

	/// <summary>Live markers, keyed by the transform they follow, so one subject has one icon.</summary>
	private readonly Dictionary<Transform, MinimapMarker> markers =
		new Dictionary<Transform, MinimapMarker>();

	private SpawnPoint selectedSpawnPoint;

	private float minimapSize;

	private float minimapOpenness;

	private Vector2 minimapTargetAnchor;

	private void Awake()
	{
		instance = this;
		RectTransform rectTransform = minimap.rectTransform;
		float num = minimap.rectTransform.anchorMax.x - minimap.rectTransform.anchorMin.x;
		minimapSize = num * (float)Screen.width * 1.3f;
		minimapTargetAnchor = new Vector2(minimap.rectTransform.anchorMin.x, minimap.rectTransform.anchorMax.y);
	}

	/// <summary>
	/// An extra "hold the map open" signal, OR'd with the keyboard. Null for a shipped build.
	/// </summary>
	/// <remarks>
	/// <para>
	/// <b>Ledger X-61.</b> The map opened only while <c>Input.GetKey(KeyCode.M)</c> was true, and
	/// a scripted lane-B client cannot produce a physical key — so no run could ever grade a
	/// minimap check, and the icons shipped in P3 have no screenshot proving they draw. The
	/// instrument was not missing; the map simply could not be opened by the only thing that
	/// runs in a lane-B client.
	/// </para>
	/// <para>
	/// <b>A seam on the GAME side, not a workaround in the harness</b> — <c>plan.md</c> § 5
	/// rule 2 forbids the harness patching around a game behaviour, because a harness that works
	/// around something grades itself. This is the same shape the project already uses for every
	/// other scripted input: <c>FpsActorController.SetInputSource</c> and
	/// <c>NetPredictionClock.CombatButtonSource</c>. Nothing about the shipped behaviour changes
	/// — a null source leaves the keyboard as the only way in.
	/// </para>
	/// <para>
	/// Static, because a lane-B client installs it before any <c>MinimapUi</c> exists: the map is
	/// part of the in-match HUD and the harness runs from before the match is joined.
	/// </para>
	/// </remarks>
	public static System.Func<bool> HoldSource;

	/// <summary>
	/// How far the map is open, 0 closed to 1 fully open. Read-only; there is no setter.
	/// </summary>
	/// <remarks>
	/// <b>Without this the seam above proves nothing.</b> A programme could hold the map open and
	/// no artifact could say whether it opened, which is the shape of green this project has been
	/// caught by three times. The same arrangement as
	/// <c>NetClientLocalCombatDriver.IsInputSuppressedByDeath</c>: a read-only accessor on
	/// shipped gameplay code, exposing a flag the gameplay itself already writes, so the harness
	/// reads a value rather than inferring one.
	/// </remarks>
	public float Openness => minimapOpenness;

	private void Update()
	{
		bool held = Input.GetKey(KeyCode.M) || (HoldSource != null && HoldSource());
		float target = (!held) ? 0f : 1f;
		minimapOpenness = Mathf.MoveTowards(minimapOpenness, target, Time.deltaTime * 20f);
		ingameParent.anchorMin = new Vector2(0f, Mathf.Lerp(-1f, 0f, minimapOpenness));
		ingameParent.anchorMax = new Vector2(1f, Mathf.Lerp(0f, 1f, minimapOpenness));
	}

	private void Start()
	{
		SetupMinimap();
		UpdateSpawnPointButtons();
	}

	private void SetupMinimap()
	{
		MinimapCamera minimapCamera = Object.FindObjectOfType<MinimapCamera>();
		if (minimapCamera == null)
		{
			Debug.LogWarning("No minimap camera found!");
			return;
		}
		minimap.texture = minimapCamera.Minimap();
		minimapSpawnPointButton = new Dictionary<SpawnPoint, Button>();
		Camera component = minimapCamera.GetComponent<Camera>();
		SpawnPoint[] spawnPoints = ActorManager.instance.spawnPoints;
		foreach (SpawnPoint spawnPoint in spawnPoints)
		{
			Button component2 = Object.Instantiate(minimapSpawnPointPrefab).GetComponent<Button>();
			RectTransform rectTransform = (RectTransform)component2.transform;
			Vector3 vector = component.WorldToViewportPoint(spawnPoint.transform.position);
			SpawnPoint anonSpawnPoint = spawnPoint;
			component2.onClick.AddListener(delegate
			{
				SelectSpawnPoint(anonSpawnPoint);
			});
			rectTransform.SetParent(minimap.rectTransform);
			Vector2 anchorMax = (rectTransform.anchorMin = new Vector2(vector.x, vector.y));
			rectTransform.anchorMax = anchorMax;
			rectTransform.anchoredPosition = Vector2.zero;
			minimapSpawnPointButton.Add(spawnPoint, component2);
		}
	}

	private void SelectSpawnPoint(SpawnPoint spawnPoint)
	{
		if (selectedSpawnPoint != null)
		{
			RemoveSpawnButtonHighlight(minimapSpawnPointButton[selectedSpawnPoint]);
		}
		selectedSpawnPoint = spawnPoint;
		AddSpawnButtonHighlight(minimapSpawnPointButton[selectedSpawnPoint]);
	}

	public static SpawnPoint SelectedSpawnPoint()
	{
		// Only the player picks a spawn point from a minimap. Bots use
		// ActorManager.RandomFrontlineSpawnPointForTeam through their own controller, and
		// AiActorController.SelectedSpawnPoint never comes here.
		if (instance == null || FpsActorController.instance == null)
		{
			return null;
		}
		if (LoadoutUi.IsOpen())
		{
			return null;
		}
		if (instance.selectedSpawnPoint == null)
		{
			return ActorManager.RandomFrontlineSpawnPointForTeam(FpsActorController.instance.actor.team);
		}
		if (instance.selectedSpawnPoint.owner != FpsActorController.instance.actor.team)
		{
			LoadoutUi.Show();
		}
		return instance.selectedSpawnPoint;
	}

	public static void UpdateSpawnPointButtons()
	{
		// The human is always team 0 offline, so this literal keeps offline single-player
		// byte-for-byte unchanged (V10 D16). Otherwise the local team comes from the
		// replicated snapshot, never from FpsActorController.playerTeam (V10 D17).
		int localTeam;
		if (NetContext.IsOffline)
		{
			localTeam = 0;
		}
		else if (NetPresenterGate.TryResolveLocalTeam(out byte team))
		{
			localTeam = team;
		}
		else
		{
			localTeam = UNRESOLVED_TEAM;
		}
		UpdateSpawnPointButtons(localTeam);
	}

	public static void UpdateSpawnPointButtons(int localTeam)
	{
		// Reached from CapturePoint whenever a flag changes hands, which happens on a server,
		// and network messages arrive before Start() has run SetupMinimap() -- guard the
		// button map too, not just instance (V10 Task 9 defect 2).
		if (instance == null)
		{
			return;
		}
		if (instance.minimapSpawnPointButton == null)
		{
			NetPresenterGate.WarnOnce(
				"minimap-spawn-buttons-not-ready",
				"[net] MinimapUi.UpdateSpawnPointButtons ran before SetupMinimap built its "
				+ "button map. Skipping this update.");
			return;
		}
		foreach (SpawnPoint key in instance.minimapSpawnPointButton.Keys)
		{
			int owner = key.owner;
			Button button = instance.minimapSpawnPointButton[key];
			ColorBlock colors = button.colors;
			Color color2 = (colors.normalColor = ColorScheme.TeamColor(owner));
			colors.highlightedColor = color2 + new Color(0.2f, 0.2f, 0.2f);
			colors.disabledColor = color2 * new Color(0.5f, 0.5f, 0.5f);
			colors.pressedColor = Color.white;
			button.colors = colors;
			button.interactable = owner == localTeam;
		}
	}

	private void RemoveSpawnButtonHighlight(Button b)
	{
		b.image.sprite = spawnPointSprite;
	}

	private void AddSpawnButtonHighlight(Button b)
	{
		b.image.sprite = spawnPointSelectedSprite;
	}

	public static void PinToLoadoutScreen()
	{
		if (instance == null)
		{
			return;
		}
		instance.minimap.rectTransform.SetParent(instance.loadoutParent, false);
	}

	public static void PinToIngameScreen()
	{
		if (instance == null)
		{
			return;
		}
		instance.minimap.rectTransform.SetParent(instance.ingameParent, false);
	}

	/// <summary>
	/// Places or recolours a marker that follows a transform. debt-closure phase 2 task 2d,
	/// ledger C-6.
	/// </summary>
	/// <remarks>
	/// <para>
	/// <b>Transform-based, and that is the whole gap this closes.</b> Before this the minimap
	/// had exactly two ways to draw anything: the <see cref="SpawnPoint"/> buttons
	/// <c>SetupMinimap</c> builds once at <c>Start</c>, and <see cref="AddActorBlip"/>, which is
	/// add-only and takes an <see cref="Actor"/>. A capture point is neither — it is a
	/// <c>Transform</c> whose colour changes when it flips hands — so there was no API it could
	/// use and it drew nothing.
	/// </para>
	/// <para>
	/// <b>Idempotent by subject.</b> Called again for a transform that already has a marker, it
	/// recolours rather than stacking a second icon: a capture point calls this on every flip,
	/// and an add-only API would leave one icon per capture by the end of a round.
	/// </para>
	/// </remarks>
	public static void SetMarker(Transform subject, Color color)
	{
		SetMarker(subject, color, MinimapMarkerKind.CapturePoint);
	}

	/// <summary>
	/// As <see cref="SetMarker(Transform, Color)"/>, choosing which authored prefab draws it.
	/// </summary>
	/// <remarks>
	/// <para>
	/// <b>Two kinds, not two APIs.</b> P3 task 3.4 needs an icon for every replicated body, and
	/// a replicated body is a <c>Transform</c> with a team — the same shape a capture point is,
	/// and the shape <see cref="MinimapMarker"/> was built for. What differs is only which
	/// texture it wears, so the kind selects a prefab and nothing else branches.
	/// </para>
	/// <para>
	/// <b>Both prefabs are already authored fields</b>, so this adds no new way for the gate to
	/// find a null: <see cref="capturePointMarkerPrefab"/> is P3 task 3.3's authoring and
	/// <see cref="actorBlipPrefab"/> has been assigned since the original game. Adding a third
	/// serialized field per kind would have been a third thing to leave unassigned.
	/// </para>
	/// </remarks>
	public static void SetMarker(Transform subject, Color color, MinimapMarkerKind kind)
	{
		if (instance == null || subject == null)
		{
			return;
		}

		MinimapMarker existing;
		if (instance.markers.TryGetValue(subject, out existing) && existing != null)
		{
			existing.SetColor(color);
			return;
		}

		GameObject prefab = ((kind == MinimapMarkerKind.Body)
			? instance.actorBlipPrefab
			: instance.capturePointMarkerPrefab) ?? instance.minimapSpawnPointPrefab;

		if (prefab == null)
		{
			NetPresenterGate.WarnOnce(
				"minimap-no-marker-prefab",
				"[minimap] MinimapUi has no prefab for a " + kind + " marker and no "
				+ "minimapSpawnPointPrefab to fall back on, so it draws nothing.");
			return;
		}

		var marker = ((GameObject)Object.Instantiate(prefab, instance.minimap.rectTransform))
			.AddComponent<MinimapMarker>();
		marker.Bind(subject, color);
		instance.markers[subject] = marker;
	}

	/// <summary>Drops a marker. Safe for a subject that never had one.</summary>
	public static void RemoveMarker(Transform subject)
	{
		if (instance == null || subject == null)
		{
			return;
		}

		MinimapMarker marker;
		if (!instance.markers.TryGetValue(subject, out marker))
		{
			return;
		}

		instance.markers.Remove(subject);
		if (marker != null)
		{
			Object.Destroy(marker.gameObject);
		}
	}

	public static void AddActorBlip(Actor actor)
	{
		// On the registration path of every actor (ActorManager.Register), so this is the first
		// UI call a headless server makes -- once per bot, before anything has moved.
		if (instance == null)
		{
			return;
		}
		ActorBlip component = ((GameObject)Object.Instantiate(instance.actorBlipPrefab, instance.minimap.rectTransform)).GetComponent<ActorBlip>();
		component.SetActor(actor, !actor.aiControlled);
	}
}
