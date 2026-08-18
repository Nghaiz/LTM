using System.Collections.Generic;
using Ironfront.Net.Protocol;
using Ironfront.Net.Unity;
using Ironfront.Net.Unity.Client;
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

	public Sprite spawnPointSprite;

	public Sprite spawnPointSelectedSprite;

	private Dictionary<SpawnPoint, Button> minimapSpawnPointButton;

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

	private void Update()
	{
		float target = ((!Input.GetKey(KeyCode.M)) ? 0f : 1f);
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
		else if (NetClientPresenterGuard.TryResolveLocalTeam(out byte team))
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
			NetClientPresenterGuard.WarnOnce(
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
