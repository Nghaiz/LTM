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

	/// <summary>
	/// Vehicle icons are drawn in this child of the map, under the soldiers and the flags.
	/// </summary>
	public RectTransform vehicleLayer;

	/// <summary>Soldier icons, player and bot, other than the player's own.</summary>
	public RectTransform soldierLayer;

	/// <summary>The player's own arrow, view cone and halo: above everything else on the map.</summary>
	public RectTransform selfLayer;

	/// <summary>
	/// A dark veil over the whole screen behind the in-match map, faded in with it, so the map
	/// reads against the scene instead of over it.
	/// </summary>
	public Graphic ingameBackdrop;

	/// <summary>How dark <see cref="ingameBackdrop"/> gets with the map fully open.</summary>
	private const float BACKDROP_OPACITY = 0.45f;

	/// <summary>How often markers whose subject was destroyed without a RemoveMarker are swept.</summary>
	private const float PRUNE_SECONDS = 2f;

	private float nextPrune;

	private readonly List<Transform> pruneScratch = new List<Transform>();

	private Dictionary<SpawnPoint, Button> minimapSpawnPointButton;

	/// <summary>The team the spawn buttons were last made interactable for.</summary>
	private int appliedLocalTeam = UNRESOLVED_TEAM;

	/// <summary>Live markers, keyed by the transform they follow, so one subject has one icon.</summary>
	private readonly Dictionary<Transform, MinimapMarker> markers =
		new Dictionary<Transform, MinimapMarker>();

	private SpawnPoint selectedSpawnPoint;

	private float minimapSize;

	private float minimapOpenness;

	private Vector2 minimapTargetAnchor;

	/// <summary>How far the in-match map is zoomed in, 1 being the whole map; eased toward <see cref="targetZoom"/>.</summary>
	private float zoom = MinimapZoom.MinZoom;

	private float targetZoom = MinimapZoom.MinZoom;

	private Vector2 zoomCentre = new Vector2(0.5f, 0.5f);

	/// <summary>The part of the map picture drawn right now, as a UV rect; all of it unzoomed.</summary>
	private Rect view = new Rect(0f, 0f, 1f, 1f);

	/// <summary>Each spawn button's point in the minimap camera's viewport, so it can follow the zoom.</summary>
	private readonly Dictionary<Button, Vector2> spawnButtonViewport = new Dictionary<Button, Vector2>();

	/// <summary>How quickly the map eases to a new zoom; a wheel notch settles in about a quarter second.</summary>
	private const float ZOOM_EASE = 12f;

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
	///
	/// Static and <c>-1</c> when there is no map, for <c>SetMarker</c>'s reason: the callers are
	/// outside this assembly and hold no instance. Zero is a real value meaning "closed", and a
	/// HUD that does not exist is not a closed map.
	/// </remarks>
	public static float CurrentOpenness => instance != null ? instance.minimapOpenness : -1f;

	private void Update()
	{
		bool held = (Input.GetKey(KeyCode.M) && !LocalTextEntry.OwnsKeyboard)
			|| (HoldSource != null && HoldSource());
		float target = (!held) ? 0f : 1f;
		minimapOpenness = Mathf.MoveTowards(minimapOpenness, target, Time.deltaTime * 20f);
		ingameParent.anchorMin = new Vector2(0f, Mathf.Lerp(-1f, 0f, minimapOpenness));
		ingameParent.anchorMax = new Vector2(1f, Mathf.Lerp(0f, 1f, minimapOpenness));
		if (ingameBackdrop != null)
		{
			Color veil = ingameBackdrop.color;
			veil.a = BACKDROP_OPACITY * minimapOpenness;
			ingameBackdrop.color = veil;
			ingameBackdrop.enabled = minimapOpenness > 0.001f;
		}
		UpdateZoom(held);

		// Networked, the buttons are built before any snapshot names this player's team, so every
		// one of them came up non-interactable -- and nothing refreshed them until a flag changed
		// hands, so the loadout's minimap never let a player pick where to deploy (2026-09-27,
		// every deploy logged "flag any"). Re-applied the moment the team is known or changes.
		if (!NetContext.IsOffline && minimapSpawnPointButton != null
			&& NetPresenterGate.TryResolveLocalTeam(out byte team) && team != appliedLocalTeam)
		{
			UpdateSpawnPointButtons(team);
		}
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
			KeepUnderOwnIcon(rectTransform);
			spawnButtonViewport[component2] = new Vector2(vector.x, vector.y);
			Vector2 anchorMax = (rectTransform.anchorMin = ToMap(vector));
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

	/// <summary>
	/// The flag the player clicked on the minimap, if any, whatever state the loadout screen is
	/// in. A networked deploy reads it while the screen is still open, which is exactly when
	/// <see cref="SelectedSpawnPoint"/> answers null.
	/// </summary>
	public static bool TryGetPickedSpawnPoint(out SpawnPoint spawnPoint)
	{
		spawnPoint = instance != null ? instance.selectedSpawnPoint : null;
		return spawnPoint != null;
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
		instance.appliedLocalTeam = localTeam;
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

		GameObject prefab = ((kind != MinimapMarkerKind.CapturePoint)
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

		GameObject icon = (GameObject)Object.Instantiate(prefab, instance.LayerFor(kind));
		if (kind == MinimapMarkerKind.CapturePoint)
		{
			instance.KeepUnderOwnIcon((RectTransform)icon.transform);
		}
		// The borrowed actor-blip prefab carries the trail's dot texture; read it before Bind
		// switches that prefab's own ActorBlip off.
		ActorBlip borrowed = icon.GetComponent<ActorBlip>();
		Texture trailDot = borrowed != null ? borrowed.trailDot : null;
		RawImage borrowedImage = icon.GetComponent<RawImage>();
		Texture arrow = borrowed != null && borrowedImage != null ? borrowedImage.texture : null;
		var marker = icon.AddComponent<MinimapMarker>();
		marker.Bind(subject, color, kind, trailDot, arrow);
		instance.markers[subject] = marker;
	}

	/// <summary>
	/// Draws, or updates, a replicated soldier's icon: its colour and its heading, the way
	/// <see cref="ActorBlip"/> draws an <see cref="Actor"/>. A seated soldier has no icon of its
	/// own; its vehicle's stands for the crew.
	/// </summary>
	public static void SetBodyMarker(Transform subject, Color color, bool isHuman)
	{
		SetMarker(subject, color, MinimapMarkerKind.Body);
		MinimapMarker marker;
		if (instance != null && subject != null && instance.markers.TryGetValue(subject, out marker) && marker != null)
		{
			marker.SetHuman(isHuman);
		}
	}

	/// <summary>
	/// Draws, or updates, a vehicle's own icon: its silhouette from <see cref="Vehicle.blip"/>,
	/// turned to its heading, in <paramref name="color"/>.
	/// </summary>
	public static void SetVehicleMarker(Transform vehicle, Color color)
	{
		if (instance == null || vehicle == null)
		{
			return;
		}
		bool isNew = !instance.markers.ContainsKey(vehicle);
		SetMarker(vehicle, color, MinimapMarkerKind.Vehicle);
		MinimapMarker marker;
		if (isNew && instance.markers.TryGetValue(vehicle, out marker) && marker != null)
		{
			Vehicle body = vehicle.GetComponentInParent<Vehicle>();
			marker.SetTexture(body != null ? body.blip : null);
		}
	}

	/// <summary>
	/// Where a point of the minimap camera's viewport lands on the map as it is drawn right now,
	/// zoomed or not, 0-1 on each axis.
	/// </summary>
	public static Vector2 ToMap(Vector3 viewport)
	{
		Vector2 point = new Vector2(viewport.x, viewport.y);
		return instance != null ? MinimapZoom.ToMap(point, instance.view) : point;
	}

	/// <summary>
	/// True while the in-match map is zoomed in. An icon beyond the view is then hidden: pinning
	/// every off-screen soldier to the edge of a zoomed map would line its border with icons.
	/// </summary>
	public static bool IsZoomed => instance != null && instance.zoom > MinimapZoom.MinZoom + 0.01f;

	/// <summary>How many times the whole-map scale the map is drawn at right now.</summary>
	public static float Zoom => instance != null ? instance.zoom : MinimapZoom.MinZoom;

	/// <summary>
	/// While the in-match map is held open the mouse wheel zooms it, and must not also switch the
	/// weapon in the player's hands (<c>FpsActorController</c> asks).
	/// </summary>
	public static bool OwnsScrollWheel => instance != null && instance.minimapOpenness > 0.5f;

	// The wheel zooms the in-match map while M is held, around the player. The deploy screen's
	// copy always shows the whole map: that is where a player picks a flag anywhere on it.
	private void UpdateZoom(bool held)
	{
		bool onLoadout = minimap.rectTransform.parent == loadoutParent;
		if (held && !onLoadout)
		{
			float notches = Input.mouseScrollDelta.y;
			if (notches != 0f)
			{
				targetZoom = MinimapZoom.Next(targetZoom, notches);
			}
		}
		float wanted = onLoadout ? MinimapZoom.MinZoom : targetZoom;
		zoom = Mathf.Lerp(zoom, wanted, 1f - Mathf.Exp((0f - Time.unscaledDeltaTime) * ZOOM_EASE));
		if (Mathf.Abs(zoom - wanted) < 0.001f)
		{
			zoom = wanted;
		}
		Vector2 player;
		if (TryLocalViewport(out player))
		{
			zoomCentre = player;
		}
		view = MinimapZoom.ViewRect(zoomCentre, zoom);
		minimap.uvRect = view;
	}

	private static bool TryLocalViewport(out Vector2 viewport)
	{
		viewport = new Vector2(0.5f, 0.5f);
		FpsActorController player = FpsActorController.instance;
		if (player == null || player.actor == null || player.actor.dead || MinimapCamera.instance == null)
		{
			return false;
		}
		Vector3 point = MinimapCamera.instance.camera.WorldToViewportPoint(player.actor.Position());
		viewport = new Vector2(Mathf.Clamp01(point.x), Mathf.Clamp01(point.y));
		return true;
	}

	/// <summary>The map picture every icon is placed on, or null with no HUD.</summary>
	public static RectTransform MapRect => instance != null ? instance.minimap.rectTransform : null;

	/// <summary>How wide the map is drawn right now, in canvas pixels; icons size from it.</summary>
	public static float MapWidth => instance != null ? instance.minimap.rectTransform.rect.width : 0f;

	/// <summary>Where the player's own arrow and its decorations are drawn.</summary>
	public static RectTransform SelfLayer => instance != null ? instance.LayerOrMap(instance.selfLayer) : null;

	private RectTransform LayerFor(MinimapMarkerKind kind)
	{
		switch (kind)
		{
		case MinimapMarkerKind.Vehicle:
			return LayerOrMap(vehicleLayer);
		case MinimapMarkerKind.Body:
			return LayerOrMap(soldierLayer);
		default:
			return minimap.rectTransform;
		}
	}

	// A HUD authored before the icon layers existed still draws every icon, just without the
	// vehicles-under-soldiers order; say so once rather than silently.
	private RectTransform LayerOrMap(RectTransform layer)
	{
		if (layer != null)
		{
			return layer;
		}
		NetPresenterGate.WarnOnce(
			"minimap-no-icon-layers",
			"[minimap] MinimapUi has no icon layers assigned, so vehicles, soldiers and the player's "
			+ "own arrow are drawn in creation order and may cover each other. Re-run the HUD "
			+ "prefab's minimap setup.");
		return minimap.rectTransform;
	}

	// Flags and spawn buttons go over the vehicles and soldiers and under only the player's own
	// arrow: a flag is where the fight is and what a player deploys on, and it must never be buried
	// by the icons crowding it (owner report 2026-09-29).
	private void KeepUnderOwnIcon(RectTransform element)
	{
		if (selfLayer != null && selfLayer.parent == element.parent)
		{
			element.SetSiblingIndex(selfLayer.GetSiblingIndex());
		}
	}

	private void LateUpdate()
	{
		if (NetContext.IsOffline && ActorManager.instance != null && ActorManager.instance.vehicles != null)
		{
			MarkOfflineVehicles();
		}
		LayoutSpawnButtons();
		if (Time.unscaledTime >= nextPrune)
		{
			nextPrune = Time.unscaledTime + PRUNE_SECONDS;
			PruneOrphanedMarkers();
		}
	}

	// The spawn buttons grow with the map like every other icon (24 px was a fixed size on a map
	// now half as large again on the deploy screen) and follow the zoom like every other icon.
	private void LayoutSpawnButtons()
	{
		if (minimapSpawnPointButton == null)
		{
			return;
		}
		float flag = MinimapIconLayout.SoldierPixels(MapWidth) * MinimapIconLayout.FlagScale;
		Vector2 size = new Vector2(flag, flag);
		foreach (Button button in minimapSpawnPointButton.Values)
		{
			if (button == null)
			{
				continue;
			}
			RectTransform rect = (RectTransform)button.transform;
			rect.sizeDelta = size;
			Vector2 viewport;
			if (spawnButtonViewport.TryGetValue(button, out viewport))
			{
				Vector2 onMap = ToMap(viewport);
				rect.anchorMin = onMap;
				rect.anchorMax = onMap;
			}
		}
	}

	/// <summary>
	/// Offline, the same vehicle icons the networked client draws (RemoteActorRegistry), from the
	/// vehicles' own seats: a crewed vehicle in its crew's shade when the crew itself would show;
	/// an empty one or a wreck not at all.
	/// </summary>
	private void MarkOfflineVehicles()
	{
		List<Vehicle> vehicles = ActorManager.instance.vehicles;
		for (int i = 0; i < vehicles.Count; i++)
		{
			Vehicle vehicle = vehicles[i];
			if (vehicle == null)
			{
				continue;
			}
			if (vehicle.dead)
			{
				RemoveMarker(vehicle.transform);
				continue;
			}
			bool crewed = false;
			bool shown = false;
			int team = -1;
			if (vehicle.seats != null)
			{
				foreach (Seat seat in vehicle.seats)
				{
					Actor occupant = seat != null ? seat.occupant : null;
					if (occupant == null || occupant.dead)
					{
						continue;
					}
					crewed = true;
					team = occupant.team;
					shown |= !occupant.aiControlled || occupant.team == FpsActorController.playerTeam || occupant.IsHighlighted();
				}
			}
			if (!crewed || !shown)
			{
				RemoveMarker(vehicle.transform);
				continue;
			}
			SetVehicleMarker(vehicle.transform, ColorScheme.BlipColor(team, false));
		}
	}

	// A subject destroyed without a RemoveMarker (a wreck cleaned up offline, a flag unloaded
	// with its scene) leaves an entry keyed by a dead transform. Collected first and removed
	// after: writing to a Dictionary while enumerating it throws on Mono.
	private void PruneOrphanedMarkers()
	{
		pruneScratch.Clear();
		foreach (KeyValuePair<Transform, MinimapMarker> entry in markers)
		{
			if (entry.Key == null || entry.Value == null)
			{
				pruneScratch.Add(entry.Key);
			}
		}
		for (int i = 0; i < pruneScratch.Count; i++)
		{
			MinimapMarker orphan;
			if (markers.TryGetValue(pruneScratch[i], out orphan) && orphan != null)
			{
				Object.Destroy(orphan.gameObject);
			}
			markers.Remove(pruneScratch[i]);
		}
		pruneScratch.Clear();
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
		ActorBlip component = ((GameObject)Object.Instantiate(instance.actorBlipPrefab, instance.LayerOrMap(instance.soldierLayer))).GetComponent<ActorBlip>();
		component.SetActor(actor, !actor.aiControlled);
	}
}
