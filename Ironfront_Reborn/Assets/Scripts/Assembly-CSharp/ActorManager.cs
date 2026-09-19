using System;
using System.Collections;
using System.Collections.Generic;
using Ironfront.Net.Replication.Vehicles;
using UnityEngine;
using UnityEngine.SceneManagement;

public class ActorManager : MonoBehaviour
{
	private const int MIN_SQUAD_SIZE = 2;

	private const float MIN_DEAD_TIME = 6f;

	private const float AI_MAX_FIRST_SPAWN_TIME = 10f;

	/// <summary>
	/// Seconds a body stays down before <see cref="SpawnWave"/> will respawn it.
	/// </summary>
	/// <remarks>
	/// Named because <see cref="CreateAIActor"/> now has to subtract it to make a newly created
	/// bot eligible on the next wave; two copies of the same literal in one file, one of them
	/// load-bearing for a deploy time, is how the two silently drift apart.
	/// </remarks>
	private const float AI_SPAWN_WAVE_DEATH_GRACE = 6f;

	public static ActorManager instance;

	public float spawnTime = 10f;

	public int team0Bots = 16;

	public int team1Bots = 16;

	public GameObject actorPrefab;

	/// <summary>
	/// Whether this round's AI roster has been created yet.
	/// </summary>
	/// <remarks>
	/// The roster is filled ONCE per round, on the first <see cref="SpawnWave"/> tick after
	/// <c>NetBotRelease</c> opens. Not serialized and not public: it is round state, and a
	/// designer setting it in the inspector would mean "this map starts with no bots, ever".
	/// </remarks>
	[NonSerialized]
	private bool aiRosterFilled;

	[NonSerialized]
	public SpawnPoint[] spawnPoints;

	[NonSerialized]
	public List<Actor> actors;

	[NonSerialized]
	public Actor player;

	[NonSerialized]
	public List<Vehicle> vehicles;

	[NonSerialized]
	public bool debug;

	private Dictionary<int, List<Actor>> aliveActors;

	/// <summary>
	/// The local player's actor, or null. Null on a dedicated server, and on a client between
	/// level load and the player prefab registering itself.
	/// </summary>
	/// <remarks>
	/// The AI reads the player's position, team, health and resupply state in several places.
	/// Every one of those was a direct <c>instance.player</c> dereference and every one of them
	/// throws on a headless server, where no non-AI actor ever registers. Route reads through
	/// here and null-check, rather than assuming a player exists because one always did.
	/// </remarks>
	public static Actor Player => (instance != null) ? instance.player : null;

	public static void Register(Actor actor)
	{
		instance.actors.Add(actor);
		MinimapUi.AddActorBlip(actor);
		if (!actor.aiControlled)
		{
			instance.player = actor;
		}
	}

	// Ledger X-49. Every one of these three removals is now reached from an OnDestroy, so each
	// has to survive being called while the scene is being torn down: Unity destroys children in
	// no guaranteed order, so ActorManager may already be gone, and StartGame may never have run
	// at all (a headless server that loads the map and quits). A null-ref thrown out of OnDestroy
	// during quit is the kind of error that reads as a crash and is only ever noise.
	public static void Drop(Actor actor)
	{
		if (instance == null || instance.actors == null) return;

		instance.actors.Remove(actor);
	}

	private void Awake()
	{
		instance = this;

		// ALLOCATED HERE, not in StartGame(). `instance` was assigned in Awake while the three
		// registries below were built in StartGame(), which GameManager calls from its
		// sceneLoaded handler -- so between the map's Awakes and that handler there is a window
		// in which `instance` is non-null and `instance.vehicles` is null. The client's held
		// snapshot queue releases inside exactly that window
		// (ClientFlowBootstrap.OnSceneLoaded -> MasterSession.OnSceneReady ->
		// SnapshotHoldingQueue.Release -> RemoteVehicleRegistry.OnVehicleSpawn -> Instantiate ->
		// Vehicle.Awake -> RegisterVehicle), and every vehicle in the first batch threw out of
		// RegisterVehicle: 14 of them in tmp/playtest/client-2.log.
		//
		// The tell that this was patched once on the wrong side: DropVehicle and DropActor carry
		// null guards and RegisterVehicle and RegisterActor do not. Guarding the register half to
		// match would stop the exception and lose the vehicle -- it would exist as a GameObject
		// that nothing can damage, enter or clean up, which is worse than the throw because
		// nothing says so. DecalManager.AddDecal already warns about the same window; this is the
		// third instance of the shape, so it is fixed at the lifetime rather than at the call.
		//
		// `vehicles` and `aliveActors` are the two this actually fixes, because they are the two
		// OnLevelLoaded does not touch. `actors` is allocated here as well, but it does NOT stay
		// allocated and StartGame has to build it again -- see the remark on OnLevelLoaded, which
		// nulls it moments after this line runs, on this very same scene load.
		actors = new List<Actor>();
		vehicles = new List<Vehicle>();
		aliveActors = new Dictionary<int, List<Actor>>();
		aliveActors.Add(0, new List<Actor>());
		aliveActors.Add(1, new List<Actor>());

		AiActorController.SetupParameters();
		SceneManager.sceneLoaded += OnLevelLoaded;
		spawnTime = Mathf.Max(0.1f, spawnTime);
	}

	private void OnDestroy()
	{
		SceneManager.sceneLoaded -= OnLevelLoaded;

		// NetWorldLifecycle is static and outlives the scene, so an un-removed handler would
		// keep a destroyed ActorManager alive and fire DespawnBots against a dead `actors` list
		// on the NEXT map's first round reset. Unconditional: -= on a handler that was never
		// added is a no-op, and StartGame's subscription is skipped on a client.
		Ironfront.Net.Unity.Server.NetWorldLifecycle.ResetRequested -= OnWorldResetRequested;
	}

	public void StartGame()
	{
		// `actors` MUST be rebuilt here, and this line is not the leftover it looks like. Awake
		// allocates it and then OnLevelLoaded, which Awake itself subscribes to, sets it back to
		// null on the same scene load -- so between those two callbacks the field is null and the
		// ONLY thing that has ever restored it is this line. Deleting it as redundant cost 14400
		// NullReferenceExceptions per client and 8136 on the server, out of SpawnWave and
		// Register, in a single four-client playtest. It is safe to build a fresh list because
		// Unity runs every Start after every sceneLoaded callback, so Actor.Start -> Register has
		// not run yet and nothing is discarded.
		//
		// `vehicles` and `aliveActors` are deliberately NOT rebuilt here. OnLevelLoaded leaves
		// them alone, so Awake's copies are still live -- and the client's held snapshot releases
		// BEFORE this runs, so a `new` or a `Clear()` would drop exactly the vehicles the Awake
		// allocation exists to keep.
		//
		// spawnPoints stays: it is a scan of the loaded scene and has nothing to find from Awake.
		actors = new List<Actor>();
		spawnPoints = UnityEngine.Object.FindObjectsOfType<SpawnPoint>();

		// A CLIENT DOES NOT POPULATE THE MATCH. Ledger X-82's other half.
		//
		// Both lines below are the offline game's roster: FillEmptySlotsWithAI Instantiates
		// team0Bots + team1Bots actorPrefabs, and the repeating SpawnWave places every dead one at
		// a spawn point of its own choosing, forever. On a networked client the roster belongs to
		// the server -- bots included -- and arrives as S_SPAWN_ACTOR plus snapshots, rendered by
		// RemoteActorRegistry onto bodies this side never spawns.
		//
		// Running them anyway is what the 2026-09-04 playtest recorded: 2 [spawn] ground-snap
		// warnings on client-1 and 10 on client-2, every one with ActorManager.SpawnWave ->
		// SpawnActorList -> CapturePoint.GetSpawnPosition in its stack -- i.e. each client was
		// simulating a private war of ~40 AI actors, scoring its own tickets and moving its own
		// capture points, while snapshots overwrote the same capture points from the server. That
		// disagreement is the "map does not load right" report: not a broken scene, two
		// simulations of it in one process.
		//
		// Offline is untouched, and so is the server: NetContext.IsClient is false in both.
		if (Ironfront.Net.Unity.NetContext.IsClient)
		{
			return;
		}

		// THE ROSTER IS NO LONGER FILLED HERE, and that is the whole fix for "every capture
		// point is already owned seconds into the match".
		//
		// FillEmptySlotsWithAI used to run on this line, at SCENE LOAD, while the capture
		// arithmetic waits for MatchPhase.Playing. So 32 bots had the whole of
		// WaitingForPlayers plus the 20s warmup to spread across the map, and the round opened
		// onto terrain they were already standing on. Measured on Dustbowl 2026-09-20: the map
		// opened correctly at one point per team, then the four neutral points fell to bots at
		// ~34s, ~65s, ~92s and ~115s while the three human clients captured nothing, and by
		// 118s nothing on the map was neutral. The objective game was played, and decided, by
		// bots before a player had finished choosing a loadout.
		//
		// The wave below now fills the roster on its first tick after NetBotRelease opens --
		// 30s after the first player body enters the world, and never before one does. Doing it
		// there rather than here also means the round-reset path gets the re-fill for free.
		Ironfront.Net.Unity.Server.NetWorldLifecycle.ResetRequested += OnWorldResetRequested;
		InvokeRepeating("SpawnWave", 1f, spawnTime);
	}

	/// <summary>
	/// Clears the round's bots and re-arms the release gate, so the next round opens as empty
	/// as the first one did.
	/// </summary>
	/// <remarks>
	/// <para>
	/// <c>MatchStateMachine.PerformReset</c> restores every capture point's opening owner and
	/// does nothing to the bodies. Without this the delay would protect the FIRST round only:
	/// round two reopens with the previous round's 32 bots standing exactly where they finished,
	/// and a gate anchored on "the first player spawn" has nothing left to delay.
	/// </para>
	/// <para>
	/// <b>Destroyed, not parked.</b> <c>NetServerActor.OnDisable</c> unregisters through
	/// <c>ServerActorRegistry</c>, which hands the actor id back to the quarantining pool — so
	/// destruction is also what keeps the id space honest. It runs on
	/// <c>NetWorldLifecycle.ResetRequested</c>, which fires BEFORE
	/// <c>ServerTickLoop.ResetForNewMatch</c> snapshots the still-live actors into its retained
	/// set, so these ids leave the retained count rather than being re-offered while still held
	/// (X-73, X-74).
	/// </para>
	/// </remarks>
	private void OnWorldResetRequested()
	{
		DespawnBots();
		Ironfront.Net.Unity.NetBotRelease.ResetForNewRound();
	}

	/// <summary>Destroys every AI body and empties the roster.</summary>
	/// <remarks>
	/// Iterated over a COPY: <c>Actor.OnDestroy</c> unregisters from <see cref="actors"/>, so
	/// walking the live list while destroying from it skips every second entry.
	/// </remarks>
	private void DespawnBots()
	{
		if (actors == null)
		{
			return;
		}
		List<Actor> snapshot = new List<Actor>(actors);
		int destroyed = 0;
		foreach (Actor actor in snapshot)
		{
			// A body a connection holds is not ours to destroy even when it is AI-controlled:
			// aiControlled is frozen in Awake from the controller's type and a player slot is
			// built from the same AI character prefab, so it stays true for a claimed slot for
			// the whole match. The same IsClaimed / AvailableForPlayers test SpawnWave uses.
			if (actor == null || !actor.aiControlled)
			{
				continue;
			}
			var replicated = actor.GetComponent<Ironfront.Net.Unity.Server.NetServerActor>();
			if (replicated != null && (replicated.AvailableForPlayers || replicated.IsClaimed))
			{
				continue;
			}
			// DEACTIVATE FIRST, and the ordering is the whole point.
			//
			// Object.Destroy is deferred to the end of the frame, so NetServerActor.OnDisable --
			// which is what unregisters and hands the actor id back to the pool -- would not run
			// until after MatchController.OnResetRequested had already called
			// ResetForNewMatch, whose FIRST act is to snapshot every still-live actor into its
			// retained-id set. The ids would be retained for a round they no longer belong to.
			// SetActive(false) fires OnDisable synchronously, so the registry is correct by the
			// time that snapshot is taken -- which is exactly what OnResetRequested's own
			// "the world is torn down first" comment promises.
			actor.gameObject.SetActive(false);
			UnityEngine.Object.Destroy(actor.gameObject);
			destroyed++;
		}
		aiRosterFilled = false;
		Debug.Log($"[net] round reset: {destroyed} bot(s) despawned, roster re-armed.");
	}

	private void FillEmptySlotsWithAI()
	{
		for (int i = 0; i < team0Bots; i++)
		{
			CreateAIActor(0, (float)i / (float)team0Bots);
		}
		for (int j = 0; j < team1Bots; j++)
		{
			CreateAIActor(1, (float)j / (float)team1Bots);
		}
	}

	private void CreateAIActor(int team, float fillRatio)
	{
		Actor component = UnityEngine.Object.Instantiate(actorPrefab).GetComponent<Actor>();
		component.SetTeam(team);

		// DEPLOY ON THE NEXT WAVE, not sixteen seconds from now.
		//
		// A fresh Actor is dead, and SpawnWave's own test is `deathTimestamp + 6f < Time.time`,
		// so the old `Time.time + max(spawnTime, 10f)` held every bot out of the world for ~16s
		// after creation. That stagger existed because the roster was built at scene load and
		// wanted the map quiet for a moment. The release gate now owns that wait, and owns it
		// properly -- so stacking the old one on top would put bots in the world 46s after the
		// first player spawned when the rule says 30.
		//
		// The subtraction is what makes the strict `<` above true on the very next tick.
		component.deathTimestamp = Time.time - (AI_SPAWN_WAVE_DEATH_GRACE + 1f);
		component.lqUpdatePhase = fillRatio * 0.2f;
	}

	private void SpawnWave()
	{
		// NO BOT EXISTS BEFORE A HUMAN IS IN THE WORLD, AND NONE FOR 30s AFTER THE FIRST ONE.
		//
		// GATED PER ACTOR, NOT BY RETURNING EARLY, and that distinction is the whole reason
		// single-player still works. Offline the PLAYER's own body is spawned by this wave --
		// GameManager.StartGame instantiates the prefab at (0, 1000, 0) and it sits dead in
		// `actors` until a wave picks it up -- so a blanket early-out here would hold the player
		// out of the world, which would mean the anchor never fires, which would mean the wave
		// never opens: a deadlock that ends single-player at the loadout screen. Networked
		// players never come through here at all (ServerCombatBridge.PlaceAtSpawn places them,
		// and the claimed/available test below skips their slots), so the ONLY thing this gate
		// may hold back is an AI body.
		bool botsReleased = Ironfront.Net.Unity.NetBotRelease.IsReleased;

		// First tick past the gate: build the roster the old StartGame call used to build at
		// scene load. Doing it here rather than at the release edge means the round-reset path
		// re-fills for free -- DespawnBots clears the flag and this rebuilds on the next tick
		// after the new round's first player spawns.
		if (botsReleased && !aiRosterFilled)
		{
			aiRosterFilled = true;
			FillEmptySlotsWithAI();
			Debug.Log(
				$"[net] bots released: {team0Bots} for team 0, {team1Bots} for team 1, "
				+ $"{Ironfront.Net.Unity.NetBotRelease.DelaySeconds:F0}s after the first player "
				+ "body entered the world.");
		}

		List<Actor> list = new List<Actor>();
		foreach (Actor actor in actors)
		{
			if (actor.dead && actor.deathTimestamp + AI_SPAWN_WAVE_DEATH_GRACE < Time.time)
			{
				// The gate, applied to AI bodies only. See the remark at the top of this method
				// for why a human body must never be held here.
				if (actor.aiControlled && !botsReleased)
				{
					continue;
				}

				// A BODY A CONNECTION HOLDS IS NOT THE BOT WAVE'S TO SPAWN.
				//
				// ServerTickLoop.OnClientConnected parks a claimed slot at Health 0 / IsAlive
				// false on purpose -- "a join is no longer a spawn" -- so the body is placed and
				// armed for the FIRST time when that connection's own C_SPAWN_REQUEST arrives,
				// carrying the loadout the player chose. Actor.dead is exactly what this wave
				// looks for, and deathTimestamp is whatever the slot's previous occupant left
				// behind, so on a server that has been up for more than six seconds the wave
				// respawned the player's body itself, within one spawnTime of the join.
				//
				// That is a spawn the deploy path never authorised, and it took the client with
				// it. Actor.SpawnAt sets dead = false, so the next snapshot reported IsAlive to
				// a client still waiting to deploy; ClientCombatState.SetAlive raised Respawned,
				// NetClientLocalCombatDriver.OnRespawned read it as "the server placed this
				// body" and called EnterDeployedView, and deployedView makes
				// OpenLoadoutWhileDead return early -- so the loadout screen GameManager opens
				// one second into the map never appeared, no Deploy was ever pressed, and no
				// C_SPAWN_REQUEST was ever sent. MoveToSpawnPoint therefore never ran and the
				// only body the player could see was the Player Fps Actor prefab
				// GameManager.StartGame instantiates at (0, 1000, 0), falling onto the edge of
				// the heightmap. Measured on 2026-09-04: client-1.log has "deploy granted for
				// actor 33" raised from OnSnapshotApplied -> ApplySnapshot -> SetAlive, no
				// "deploy requested" anywhere, and game-server.log has no "placed at spawn
				// point" and neither MoveToSpawnPoint warning.
				//
				// IsClaimed, not aiControlled: Actor.aiControlled is decided once in Awake from
				// the controller's type, and a player slot is built from the same AI character
				// prefab a bot is, so it stays true for the whole match. Release() clears
				// IsClaimed when the connection goes, which is what hands the slot back to the
				// bot brain rather than leaving one more inert mannequin standing in the map.
				//
				// AvailableForPlayers covers the OTHER half, and the server's own boot line says
				// what it is for: "player slot pool filled: 16 claimable bodies, all parked (bot
				// brain suspended until claimed). Map bots are unaffected." A pool body starts
				// dead with deathTimestamp at its default 0, so six seconds into the match this
				// wave spawned all sixteen of them -- bodies with a suspended brain, standing
				// still at a spawn point, invisible to every client because IsAnnounceable
				// excludes an unclaimed slot, and simulated by the server for the whole round.
				// The three "server over budget" windows in the same run are that, in part.
				// Bots leave the flag off, so a map bot still spawns and re-spawns as before.
				//
				// Offline is untouched: the local Player Fps Actor carries no NetServerActor, so
				// GetComponent answers null and the single-player wave behaves as it always has.
				var replicated = actor.GetComponent<Ironfront.Net.Unity.Server.NetServerActor>();
				if (replicated != null && (replicated.AvailableForPlayers || replicated.IsClaimed))
				{
					continue;
				}

				list.Add(actor);
			}
		}
		StartCoroutine(SpawnActorList(list));
	}

	private IEnumerator SpawnActorList(List<Actor> actorsToSpawn)
	{
		Dictionary<SpawnPoint, List<Actor>> spawnedActors = new Dictionary<SpawnPoint, List<Actor>>();
		SpawnPoint[] array = spawnPoints;
		foreach (SpawnPoint spawnPoint in array)
		{
			spawnedActors.Add(spawnPoint, new List<Actor>());
		}
		foreach (Actor actor in actorsToSpawn)
		{
			SpawnPoint spawnPoint3 = actor.controller.SelectedSpawnPoint();
			if (spawnPoint3 != null)
			{
				actor.SpawnAt(spawnPoint3.GetSpawnPosition());
				spawnedActors[spawnPoint3].Add(actor);

				// THE OFFLINE BOT-RELEASE ANCHOR, and the counterpart to the one in
				// ServerCombatBridge.PlaceAtSpawn. Single-player has no deploy handshake: the
				// player's body enters the world right here, on the wave, so this is the only
				// moment that answers "a human is now in the map".
				//
				// Guarded on aiControlled rather than on NetContext.IsOffline: a bot must never
				// anchor its own release, and on the networked server no human body reaches
				// this loop anyway, so the test states the real condition instead of the
				// circumstance that usually implies it.
				if (!actor.aiControlled)
				{
					Ironfront.Net.Unity.NetBotRelease.NotifyPlayerSpawned();
				}
			}
		}
		SpawnPoint[] array2 = spawnPoints;
		foreach (SpawnPoint spawnPoint2 in array2)
		{
			List<AiActorController> aiSquad = new List<AiActorController>();
			int members = 0;
			int squadSize = UnityEngine.Random.Range(2, UnityEngine.Random.Range(2, spawnPoint2.maxSquadSize + 2));
			float squadReadyTime = 0f;
			foreach (Actor spawnedActor in spawnedActors[spawnPoint2])
			{
				if (spawnedActor.aiControlled)
				{
					aiSquad.Add((AiActorController)spawnedActor.controller);
					members++;
					if (members >= squadSize)
					{
						new Squad(aiSquad, squadReadyTime);
						squadSize = UnityEngine.Random.Range(2, UnityEngine.Random.Range(2, spawnPoint2.maxSquadSize + 2));
						aiSquad = new List<AiActorController>();
						members = 0;
						squadReadyTime += 0.3f;
					}
				}
			}
			if (aiSquad.Count > 0)
			{
				new Squad(aiSquad, squadReadyTime);
			}
		}
		yield break;
	}

	/// <summary>
	/// Adds an actor to its team's alive register. Ledger <b>X-59</b>.
	/// </summary>
	/// <remarks>
	/// <para>
	/// <b>The guard REPORTS a second producer; it is not the fix for the one that was found.</b>
	/// X-59 was a body killed through <c>ActorGameplaySource.IsDead</c>, which wrote the flag and
	/// left the register, so the next spawn wave registered the body a second time; that window
	/// is closed at the seam, where the reason lives. What this refuses is the NEXT double-add,
	/// from a path nobody has enumerated yet.
	/// </para>
	/// <para>
	/// <b>Refused loudly rather than silently deduplicated.</b> A quiet membership test would
	/// make the storm unproducible and the cause unfindable, which is how X-59 survived a gate
	/// that read "zero throws at any site". One <c>LogError</c> naming the body is a defect
	/// report; sixty <c>ArgumentException</c>s out of a coroutine are not.
	/// </para>
	/// </remarks>
	public static void SetAlive(Actor actor)
	{
		List<Actor> onTeam = instance.aliveActors[actor.team];

		if (onTeam.Contains(actor))
		{
			Debug.LogError(
				$"[actors] '{actor.name}' is already in team {actor.team}'s alive register, so "
				+ "something registered it twice without a death in between (X-59's family). "
				+ "Refusing the second entry: it would throw out of every "
				+ "FindPotentialTargets on the opposing team for the rest of the match.");
			return;
		}

		onTeam.Add(actor);
	}

	public static void SetDead(Actor actor)
	{
		if (instance == null || instance.aliveActors == null) return;
		if (!instance.aliveActors.TryGetValue(actor.team, out List<Actor> onTeam)) return;

		onTeam.Remove(actor);
	}

	public static List<Actor> AliveActorsOnTeam(int team)
	{
		return instance.aliveActors[team];
	}

	public static SpawnPoint RandomSpawnPointForTeam(int team)
	{
		int num = UnityEngine.Random.Range(0, instance.spawnPoints.Length);
		for (int i = 0; i < instance.spawnPoints.Length; i++)
		{
			int num2 = (num + i) % instance.spawnPoints.Length;
			if (instance.spawnPoints[num2].owner == team)
			{
				return instance.spawnPoints[num2];
			}
		}
		return null;
	}

	public static SpawnPoint RandomFrontlineSpawnPointForTeam(int team)
	{
		int num = UnityEngine.Random.Range(0, instance.spawnPoints.Length);
		for (int i = 0; i < instance.spawnPoints.Length; i++)
		{
			int num2 = (num + i) % instance.spawnPoints.Length;
			SpawnPoint spawnPoint = instance.spawnPoints[num2];
			if (spawnPoint.owner == team && (!spawnPoint.IsSafe() || spawnPoint.IsFrontLine()))
			{
				return spawnPoint;
			}
		}
		return RandomSpawnPointForTeam(team);
	}

	public static bool HasSpawnPoint(int team)
	{
		SpawnPoint[] array = instance.spawnPoints;
		foreach (SpawnPoint spawnPoint in array)
		{
			if (spawnPoint.owner == team)
			{
				return true;
			}
		}
		return false;
	}

	public static SpawnPoint ClosestSpawnPoint(Vector3 position)
	{
		SpawnPoint result = null;
		float num = 9999999f;
		SpawnPoint[] array = instance.spawnPoints;
		foreach (SpawnPoint spawnPoint in array)
		{
			float num2 = Vector3.Distance(position, spawnPoint.transform.position);
			if (num2 < num)
			{
				num = num2;
				result = spawnPoint;
			}
		}
		return result;
	}

	public static SpawnPoint RandomEnemySpawnPoint(int team)
	{
		int num = UnityEngine.Random.Range(0, instance.spawnPoints.Length);
		for (int i = 0; i < instance.spawnPoints.Length; i++)
		{
			int num2 = (num + i) % instance.spawnPoints.Length;
			if (instance.spawnPoints[num2].owner != team)
			{
				return instance.spawnPoints[num2];
			}
		}
		return null;
	}

	public static List<Actor> AliveActorsInRange(Vector3 point, float range)
	{
		List<Actor> list = new List<Actor>();
		foreach (Actor actor in instance.actors)
		{
			if (!actor.dead && Vector3.Distance(point, actor.Position()) < range)
			{
				list.Add(actor);
			}
		}
		return list;
	}

	public static List<Actor> ActorsInRange(Vector3 point, float range)
	{
		List<Actor> list = new List<Actor>();
		foreach (Actor actor in instance.actors)
		{
			if (Vector3.Distance(point, actor.Position()) < range)
			{
				list.Add(actor);
			}
		}
		return list;
	}

	// V1 task 3. The allocating overload above is left exactly as it was for its existing
	// callers; only Explode moves to this one. A grenade volley is precisely when a GC spike is
	// least welcome, and the fix costs one reusable field at the call site.
	public static void ActorsInRange(Vector3 point, float range, List<Actor> into)
	{
		into.Clear();
		for (int i = 0; i < instance.actors.Count; i++)
		{
			Actor actor = instance.actors[i];
			if (Vector3.Distance(point, actor.Position()) < range)
			{
				into.Add(actor);
			}
		}
	}

	// V7 task 7. The same argument as the overload above, for the resupply sweep: both
	// Ammobox.Resupply and Medipack.Resupply ran AliveActorsInRange on a three-second repeat,
	// per deployable, each call allocating a fresh List<Actor> that lived exactly as long as one
	// foreach. A bag and a medipack dropped together on a busy point is a steady GC drip for the
	// whole of their lifetimes.
	public static void AliveActorsInRange(Vector3 point, float range, List<Actor> into)
	{
		into.Clear();
		for (int i = 0; i < instance.actors.Count; i++)
		{
			Actor actor = instance.actors[i];
			if (!actor.dead && Vector3.Distance(point, actor.Position()) < range)
			{
				into.Add(actor);
			}
		}
	}

	public static void RegisterProjectile(Projectile p)
	{
		// This method exists to warn the ENEMY team's AI that something is incoming, and "the
		// enemy team" is read off the shooter. A projectile with no shooter names no team, so
		// there is nothing here to warn and no fallback that would be honest -- guessing a team
		// would make bots duck away from a tracer nobody fired at them.
		//
		// It is not a rare case. Every projectile NetClientProjectilePresenter spawns carries
		// `source == null` on purpose (V7-D3: damage, and therefore attribution, is the
		// server's), and Projectile.Start calls this for all of them because `warnsEnemyAi`
		// defaults true. That is 64 of the 95 NullReferenceExceptions three clients threw in
		// artifacts/lane-b/p4-combat-01, and the reason a player's Development Console filled up
		// within seconds of the first rocket.
		//
		// Returning early rather than warning on a client is also correct on its own terms: the
		// AI runs on the server, so a client-side cosmetic tracer has no business steering it.
		if (p.source == null)
		{
			return;
		}
		// A network body exists before its first authoritative snapshot can assign team 0/1.
		// A locally predicted projectile may therefore start during that short UNKNOWN_TEAM
		// window.  `1 - (-1)` is team 2, which is not a key in aliveActors and used to throw once
		// per bullet. There is no honest enemy AI to warn until the shooter's team is known.
		if (p.source.team != 0 && p.source.team != 1)
		{
			return;
		}
		Ray ray = new Ray(p.transform.position, p.transform.forward);
		float num = 9999f;
		RaycastHit hitInfo;
		if (Physics.Raycast(ray, out hitInfo, 9999f, 1))
		{
			num = hitInfo.distance;
		}
		int team = 1 - p.source.team;
		foreach (Actor item in AliveActorsOnTeam(team))
		{
			if (!item.aiControlled)
			{
				continue;
			}
			Vector3 lhs = p.transform.position - item.Position();
			float magnitude = lhs.magnitude;
			float num2 = magnitude / p.configuration.speed;
			lhs -= item.Velocity() * num2;
			float num3 = Vector3.Dot(lhs, p.transform.forward);
			if (!(Mathf.Abs(num3) > num + 5f))
			{
				float num4 = (0f - num3) / p.configuration.speed;
				Vector3 vector = Physics.gravity * Mathf.Pow(num4, 2f) / 2f;
				Vector3 b = p.transform.position + p.transform.forward * p.configuration.speed * num4 + vector;
				Vector3 a = item.Position() + item.Velocity() * num4;
				float num5 = Vector3.Distance(a, b);
				if (num5 < 5f)
				{
					instance.StartCoroutine(instance.MarkTakingFire((AiActorController)item.controller, -p.transform.forward, num4));
				}
			}
		}
	}

	private IEnumerator MarkTakingFire(AiActorController ai, Vector3 direction, float duration)
	{
		yield return new WaitForSeconds(duration + AiActorController.PARAMETERS.TAKING_FIRE_REACTION_TIME);
		ai.MarkTakingFireFrom(direction);
	}

	public static void RegisterVehicle(Vehicle vehicle)
	{
		instance.vehicles.Add(vehicle);
	}

	public static void DropVehicle(Vehicle vehicle)
	{
		if (instance == null || instance.vehicles == null) return;

		instance.vehicles.Remove(vehicle);
	}

	// V1 task 3. Reused across blasts so ActorsInRange stops allocating a List per explosion.
	// Static because Explode is; single-threaded, like everything else on the Unity main loop.
	//
	// Load-bearing precondition: Explode is NOT re-entrant, and it is still not after
	// debt-closure phase 2 gave Vehicle.Explode a blast of its own (ledger C-10). Vehicle.Damage
	// ends in Die(), which reaches Explode through Invoke("Explode", 0.3f) -- a later frame and a
	// fresh stack -- so the chain detonation this comment warned about is sequential rather than
	// nested and this buffer is never re-entered. Actor.Damage still ends in a ragdoll.
	// If a future caller DOES call Explode from inside Explode synchronously, this buffer is the
	// thing that breaks, silently, by having its contents replaced mid-loop -- give that caller
	// its own list rather than making this one deeper.
	private static readonly List<Actor> _explosionVictims = new List<Actor>();

	// debt-closure phase 2 task 2f (ledger C-10). A SNAPSHOT of instance.vehicles, because the
	// loop below now damages vehicles that can die inside it: Vehicle.Damage -> Die() ->
	// ActorManager.DropVehicle removes the entry mid-iteration, and an index-walked List that
	// shrinks under you skips the next element. Before a wreck could blast, one vehicle dying to
	// a blast was already enough to trigger this -- the claim in the comment above the loop that
	// nothing removes from the list during it was already false -- but a wreck detonating inside
	// a cluster of vehicles is what makes it routine.
	//
	// Vehicle.Explode itself is NOT re-entrant into this method: Die() reaches it through
	// Invoke("Explode", 0.3f), so the wreck's own blast is a fresh top-level call on a later
	// frame and _explosionVictims above is never nested.
	private static readonly List<Vehicle> _explosionVehicles = new List<Vehicle>();

	// Same snapshot discipline, for ExplosiveProp.Live (ledger C-11).
	private static readonly List<ExplosiveProp> _explosionProps = new List<ExplosiveProp>();

	// V1 task 3. The three-way role split, on the ONE choke point rather than on each of the
	// callers that funnel into it -- the identical argument that put phase-05's guard on
	// Actor.Damage rather than on its six damage sources (D1). V7 adds more callers; none of
	// them will need to know about any of this.
	//
	//   Offline -- unchanged, byte for byte. The single-player game does exactly what it did.
	//   Server  -- decides everything. Actor damage already routes through the authoritative
	//              sink via phase-05 task 6's guard inside Actor.Damage, so this method adds no
	//              second damage path for actors (D2); what it adds is the attacker id on the
	//              vehicle loop and one S_EXPLOSION at the end.
	//   Client  -- applies NO health damage to actors or vehicles. Health arrives in snapshots.
	//              Corpse ragdoll impulse is KEPT, because corpses are never replicated (AD-4)
	//              and their ragdoll is legitimately local. The cosmetic is predicted for the
	//              local player's own blast (V10 D13) and otherwise waits for S_EXPLOSION.
	//
	// The return value goes false on a client, and that is correct rather than a lost hitmarker:
	// both callers use it only to fire IngameUi.Hit(), and V10 already drives the client's
	// hitmarker from S_HIT_CONFIRM in NetClientCombatPresenter. Returning true here would draw a
	// second, locally-guessed one the server never agreed to.
	//
	// The client vehicle guard changes nothing visible today -- vehicle health is not replicated
	// until V4. It is one branch bought now so that when V4 starts streaming that health, the
	// client is not subtracting damage locally AND receiving the authoritative value; the
	// symptom would be a stuttering health bar blamed on V4's interpolation rather than on a
	// line written here.
	public static bool Explode(
		Vector3 point,
		ExplodingProjectile.ExplosionConfiguration configuration,
		Actor source,
		Ironfront.Net.Protocol.ExplosionKind kind)
	{
		// The query radius is balanceRange (9 m) but the damage falloff was normalized against
		// damageRange (6 m) with Mathf.Clamp01, which SATURATES rather than excludes: an actor
		// at 8 m got t = 1.33 -> 1.0 and took exactly what one at 6.001 m took. The 6-9 m band
		// was a flat plateau at the curve's endpoint, not a falloff, and the real damage
		// cut-off was the wider radius. The vehicle loop below always got this right by
		// testing the distance first; routing both through ExplosionRanges makes the cut-off
		// impossible to skip.
		bool isClient = Ironfront.Net.Unity.NetContext.IsClient;
		ExplosionRanges ranges = new ExplosionRanges(configuration.damageRange, configuration.balanceRange);
		ActorsInRange(point, configuration.balanceRange, _explosionVictims);
		bool result = false;
		for (int i = 0; i < _explosionVictims.Count; i++)
		{
			Actor item = _explosionVictims[i];
			Vector3 vector = item.CenterPosition() - point;
			float magnitude = vector.magnitude;
			float damageT;
			// Balance disruption and knockback still reach the full balanceRange -- that is
			// the existing and correct intent of the wider query. Only the damage term gains
			// the cut-off.
			float num = (ranges.TryGetDamageT(magnitude, out damageT) ? configuration.damageFalloff.Evaluate(damageT) : 0f);
			float num2 = configuration.balanceFalloff.Evaluate(ranges.GetBalanceT(magnitude));
			if (!item.dead)
			{
				// Skipped wholesale on a client rather than relying on Actor.Damage's own
				// ownsHealth guard. That guard would already refuse the health subtraction, but
				// it would still run the hit feedback -- blood decals, knockback, a stagger --
				// for a blast the server may not agree reached this actor at all. A remote
				// actor's reaction is the snapshot's to describe.
				if (!isClient)
				{
					item.DamageAttributed(configuration.damage * num, configuration.balanceDamage * num2, false, item.CenterPosition(), vector.normalized, vector.normalized * configuration.force * num2, source);
					result = true;
				}
			}
			else
			{
				// Kept at every role. Corpses are never replicated (AD-4), so a client's
				// ragdoll is the only ragdoll that corpse will ever have.
				item.ApplyRigidbodyForce(vector.normalized * configuration.force * num2);
			}
		}
		// Copied into a reused buffer rather than instance.vehicles.ToArray(), which allocated a
		// second array per blast -- and rather than indexing the live list, which this loop can
		// now shorten under itself (see _explosionVehicles).
		_explosionVehicles.Clear();
		_explosionVehicles.AddRange(instance.vehicles);
		for (int i = 0; i < _explosionVehicles.Count; i++)
		{
			Vehicle vehicle = _explosionVehicles[i];
			// A vehicle killed earlier in this same blast is already gone. Destroyed Unity
			// objects compare equal to null, which is exactly what the snapshot cannot know.
			if (vehicle == null)
			{
				continue;
			}
			float num3 = Vector3.Distance(vehicle.transform.position, point);
			float vehicleDamageT;
			if (ranges.TryGetDamageT(num3, out vehicleDamageT))
			{
				float num4 = configuration.damageFalloff.Evaluate(vehicleDamageT);
				if (!isClient)
				{
					// The attacker slot Vehicle.Damage(float, int) opens is threaded here in V1,
					// now that Explode is a server-authoritative path. V0 opened the parameter;
					// this is what fills it.
					vehicle.Damage(configuration.damage * num4, ResolveAttackerId(source));
					result = true;
				}
			}
		}
		// debt-closure phase 2 task 2f (ledger C-11): props in range take the blast too, which is
		// what makes a row of fuel drums chain. Snapshotted for the vehicle loop's reason --
		// ExplosiveProp.Damage lights a fuse and a detonation deregisters -- and skipped on a
		// client, where a prop's destruction is the server's to decide and arrives as
		// S_EXPLOSION. The detonation itself is deferred by the prop's fuse, so this never
		// re-enters Explode.
		if (!isClient)
		{
			_explosionProps.Clear();
			_explosionProps.AddRange(ExplosiveProp.Live);
			for (int i = 0; i < _explosionProps.Count; i++)
			{
				ExplosiveProp prop = _explosionProps[i];
				if (prop == null)
				{
					continue;
				}
				float propDamageT;
				if (ranges.TryGetDamageT(
						Vector3.Distance(prop.transform.position, point), out propDamageT))
				{
					prop.Damage(configuration.damage * configuration.damageFalloff.Evaluate(propDamageT));
				}
			}
		}

		// Once per blast, after both loops -- never once per victim. One grenade among four
		// people is one explosion and four deaths, and the deaths travel separately through
		// Actor.Damage and phase-05's existing path.
		Ironfront.Net.Unity.Server.ServerCombatEvents.ReportExplosion(
			source, point, configuration.damageRange, kind);

		// Client only, and only for this client's own blast: draw it now rather than a
		// round-trip late, and suppress the confirming S_EXPLOSION when it lands (V10 D13,
		// taking V1 D6's own recorded fallback clause).
		Ironfront.Net.Unity.NetClientBindings.PredictExplosion(
			source, point, configuration.damageRange, kind);

		return result;
	}

	// Vehicle.Damage takes an int attacker slot with a NoAttacker sentinel, so an unattributed
	// blast -- a world explosive, or a source with no network identity -- is recorded as having
	// no attacker rather than as actor 0, which is a real id.
	private static int ResolveAttackerId(Actor source)
	{
		if (source == null) return Vehicle.NoAttacker;

		var replicated = source.GetComponent<Ironfront.Net.Unity.Server.NetServerActor>();
		return replicated != null ? replicated.ActorId : Vehicle.NoAttacker;
	}

	/// <summary>Drops the previous level's actors and stops the spawn timer.</summary>
	/// <remarks>
	/// <para>
	/// <b>This runs on the scene load that CREATED this instance, and nulls a field Awake had
	/// just allocated.</b> Awake subscribes to sceneLoaded, and Unity then raises it for that
	/// same load, so a fresh per-scene ActorManager nulls its own list within one frame of
	/// building it. Nothing here is aware of that; it reads as "clean up the last level".
	/// </para>
	/// <para>
	/// StartGame is what puts `actors` back, which is why that allocation cannot be removed no
	/// matter how redundant it looks beside Awake's. Removing it was measured at 14400
	/// NullReferenceExceptions per client out of SpawnWave and Register.
	/// </para>
	/// <para>
	/// Left as a null rather than an empty list on purpose: changing it would be a behaviour
	/// change to the legacy single-player lifecycle with no test covering it, and the repair
	/// belongs in StartGame where it has always been.
	/// </para>
	/// </remarks>
	private void OnLevelLoaded(Scene arg0, LoadSceneMode arg1)
	{
		actors = null;
		CancelInvoke();
	}
}
