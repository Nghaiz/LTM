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

	public static ActorManager instance;

	public float spawnTime = 10f;

	public int team0Bots = 16;

	public int team1Bots = 16;

	public GameObject actorPrefab;

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

	public static void Drop(Actor actor)
	{
		instance.actors.Remove(actor);
	}

	private void Awake()
	{
		instance = this;
		AiActorController.SetupParameters();
		SceneManager.sceneLoaded += OnLevelLoaded;
		spawnTime = Mathf.Max(0.1f, spawnTime);
	}

	private void OnDestroy()
	{
		SceneManager.sceneLoaded -= OnLevelLoaded;
	}

	public void StartGame()
	{
		actors = new List<Actor>();
		spawnPoints = UnityEngine.Object.FindObjectsOfType<SpawnPoint>();
		vehicles = new List<Vehicle>();
		aliveActors = new Dictionary<int, List<Actor>>();
		aliveActors.Add(0, new List<Actor>());
		aliveActors.Add(1, new List<Actor>());
		FillEmptySlotsWithAI();
		InvokeRepeating("SpawnWave", 1f, spawnTime);
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
		component.deathTimestamp = Time.time + Mathf.Max(spawnTime, 10f);
		component.lqUpdatePhase = fillRatio * 0.2f;
	}

	private void SpawnWave()
	{
		List<Actor> list = new List<Actor>();
		foreach (Actor actor in actors)
		{
			if (actor.dead && actor.deathTimestamp + 6f < Time.time)
			{
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

	public static void SetAlive(Actor actor)
	{
		instance.aliveActors[actor.team].Add(actor);
	}

	public static void SetDead(Actor actor)
	{
		instance.aliveActors[actor.team].Remove(actor);
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
					item.Damage(configuration.damage * num, configuration.balanceDamage * num2, false, item.CenterPosition(), vector.normalized, vector.normalized * configuration.force * num2);
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
		Ironfront.Net.Unity.Client.ClientCombatEvents.PredictExplosion(
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

	private void OnLevelLoaded(Scene arg0, LoadSceneMode arg1)
	{
		actors = null;
		CancelInvoke();
	}
}
