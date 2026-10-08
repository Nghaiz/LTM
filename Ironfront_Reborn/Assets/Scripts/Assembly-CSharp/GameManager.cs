using System;
using Ironfront.Net.Unity;
using UnityEngine;
using UnityEngine.Audio;
using UnityEngine.SceneManagement;

public class GameManager : MonoBehaviour
{
	public const int MENU_LEVEL_INDEX = 1;

	public static GameManager instance;

	[NonSerialized]
	public bool ingame;

	[NonSerialized]
	public bool spectating;

	public GameObject ingameUiPrefab;

	public GameObject playerPrefab;

	public bool reverseMode;

	public bool assaultMode;

	public bool nightMode;

	public bool noVehicles;

	public int victoryPoints = 200;

	/// <summary>
	/// The offline match's victory rule: lead by <see cref="victoryPoints"/> (the original game's
	/// only rule) or be first to them. Chosen on the practice screen (owner, 2026-10-08); a
	/// networked match plays its room's rule instead.
	/// </summary>
	[NonSerialized]
	public Ironfront.Net.Protocol.VictoryRule victoryRule = Ironfront.Net.Protocol.VictoryRule.Margin;

	/// <summary>The offline Night Mode's night-vision battery, in seconds.</summary>
	[NonSerialized]
	public int nightVisionSeconds = Ironfront.Net.Protocol.RoomRules.DefaultNightVisionSeconds;

	/// <summary>The side the player fights on offline: 0 blue, 1 red. See <see cref="OfflinePlayerTeam"/>.</summary>
	[NonSerialized]
	public int practiceTeam;

	/// <summary>
	/// The offline player's team, the one answer for every offline reader: the practice screen's
	/// choice, and blue when there is no <c>GameManager</c>. It used to be a literal 0 in two
	/// places (<c>FpsActorController.Start</c>, <c>MinimapUi.UpdateSpawnPointButtons</c>), from
	/// when the human could only ever be blue.
	/// </summary>
	public static int OfflinePlayerTeam => instance != null && instance.practiceTeam == 1 ? 1 : 0;

	public AudioMixerGroup fpMixerGroup;

	public GameObject spectatorCameraPrefab;

	private float gameStartTime;

	private void Awake()
	{
		instance = this;
		UnityEngine.Object.DontDestroyOnLoad(base.gameObject);
		SceneManager.sceneLoaded += OnLevelLoaded;
	}

	private void OnDestroy()
	{
		SceneManager.sceneLoaded -= OnLevelLoaded;
	}

	private void Start()
	{
	}

	private void OnLevelLoaded(Scene scene, LoadSceneMode mode)
	{
		OnLevelIndexLoaded(scene.buildIndex);
	}

	private void OnLevelIndexLoaded(int levelIndex)
	{
		if (IngameLevel(levelIndex))
		{
			StartGame();
		}
		else
		{
			ingame = false;
		}
	}

	private bool IngameLevel(int level)
	{
		return level > 1;
	}

	private void StartGame()
	{
		ingame = true;

		// P27. The offline scoreboard is zeroed HERE because nothing else does it, and because
		// ScoreUi.Awake says so: "Resetting belongs to whatever starts a match, not to whatever
		// draws it." That reasoning is right and it is why the reset left Awake -- but the owner
		// it named was never given the call. `MatchScoreboard.Reset` shipped with zero callers,
		// its own summary reading "Called when a match starts".
		//
		// The original got this free. Its ScoreUi zeroed all four counters in Awake and
		// the HUD prefab is re-instantiated per match, so every match began at 0-0. Ours moved
		// the state onto a plain static that survives scene loads, so the second offline match in
		// a process opened holding the first one's score AND its latched `GameEnded` -- which
		// makes `Win()` early-return, so that match, and every later one, could never end. The
		// carried-over flags also multiply every kill through `ScoreMultiplier`.
		//
		// Before the HUD is instantiated below, deliberately: zero the match state before
		// anything that draws it exists, so the first paint cannot show the previous round.
		//
		// Networked play is unaffected either way -- there the server scores in
		// MatchStateMachine, `Changed` is subscribed offline only, and AddScore/AddFlag are
		// already gated on NetContext.IsOffline at both call sites.
		MatchScoreboard.Current.Reset();
		// The practice achievements' match (owner's list of 2026-10-09, item 4): its kills start
		// at zero, and how many bots it has decides one of them.
		if (Ironfront.Net.Unity.NetContext.IsOffline && ActorManager.instance != null)
		{
			Ironfront.Net.Unity.PracticeFeats.MatchStarted(ActorManager.instance.team0Bots + ActorManager.instance.team1Bots);
		}
		// The HUD, the player and the decal pool are the client's half of a match. A dedicated
		// server that instantiates them gets a Canvas nobody looks at, an FpsActorController
		// reaching for SceneryCamera.instance in Start, and a null-instance singleton behind
		// every UI call the bots make. Bots, pathfinding, cover and scoring are below the line
		// and still run -- phase-00 criterion 3 is "bots spawn and move on headless".
		if (LocalClient.Exists)
		{
			UnityEngine.Object.Instantiate(ingameUiPrefab);
			UnityEngine.Object.Instantiate(playerPrefab, new Vector3(0f, 1000f, 0f), Quaternion.identity);
		}
		ActorManager.instance.StartGame();
		CoverManager.instance.StartGame();
		// Each side's commander (phase P28): after ActorManager, whose spawn points it reads.
		BotCommander.EnsureOn(ActorManager.instance.gameObject).StartGame();
		// The vehicles scattered at random each match (phase P32): after ActorManager's flags.
		FieldSupplyDirector.EnsureOn(ActorManager.instance.gameObject).StartGame();
		// Night Mode (phase P32): before the scene's TimeOfDay starts, so it starts in the right dark.
		NightModeDirector.EnsureOn(ActorManager.instance.gameObject).StartGame();
		if (DecalManager.instance != null)
		{
			DecalManager.instance.StartGame();
		}
		gameStartTime = Time.time;
		if (LocalClient.Exists)
		{
			Invoke("OpenPlayerLoadout", 1f);
		}
	}

	private void OpenPlayerLoadout()
	{
		// Belt and braces. StartGame only schedules this when a client exists, but it is
		// scheduled by name through Invoke, so nothing would tell us if a future caller
		// stopped honouring that -- and every line below dereferences a client singleton.
		if (!LocalClient.Exists)
		{
			return;
		}
		// Holding S as the map finishes loading is the original game's way into spectator mode,
		// and offline only. Networked, it was a trap: S is also "walk back", a player holding it
		// through the load got a free camera with no loadout and no way to deploy, and nothing on
		// screen said why.
		if (Ironfront.Net.Unity.NetContext.IsOffline && Input.GetKey(KeyCode.S))
		{
			UnityEngine.Object.Instantiate(spectatorCameraPrefab, SceneryCamera.instance.transform.position, SceneryCamera.instance.transform.rotation);
			FpsActorController.instance.DisableCameras();
			FpsActorController.instance.DisableAudioListener();
			SceneryCamera.instance.camera.enabled = false;
			spectating = true;
		}
		else
		{
			FpsActorController.instance.OpenLoadoutWhileDead();
		}
	}

	public float ElapsedGameTime()
	{
		return Time.time - gameStartTime;
	}
}
