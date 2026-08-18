using System;
using Ironfront.Net.Replication.World;
using Ironfront.Net.Unity.Server;
using UnityEngine;

/// <summary>
/// Produces and replaces one vehicle.
/// </summary>
/// <remarks>
/// <para>
/// <b>Phase-V8 task 5 moved the lifecycle out of coroutines.</b> The countdown was
/// <c>Invoke("SpawnVehicle", spawnTime)</c> with no guard, and the blocked-pad wait was
/// <c>while (SpawnIsBlocked()) yield return new WaitForSeconds(1f)</c> with no bound — neither
/// reachable from any test, and both fatal on a dedicated server that stays up for days.
/// <see cref="VehicleSpawnScheduler"/> holds that state machine now, engine-free and covered by
/// <c>dotnet test</c>; what is left here is instantiating, destroying, and asking physics
/// whether the pad is clear.
/// </para>
/// <para>
/// <b>Offline is unaffected.</b> The scheduler runs in every role — it replaces the coroutine
/// rather than gating it — and the timings it reproduces are the original's: spawn immediately
/// at <c>Start</c>, re-test a blocked pad once a second, respawn <see cref="spawnTime"/> after
/// the triggering event.
/// </para>
/// </remarks>
public class VehicleSpawner : MonoBehaviour
{
	public enum RespawnType
	{
		AfterDestroyed = 0,
		AfterMoved = 1,
		Never = 2
	}

	private const int SPAWN_BLOCK_MASK = 5376;

	private static Collider[] spawnCollisions = new Collider[1];

	public float spawnTime = 16f;

	public RespawnType respawnType;

	public GameObject prefab;

	private Vehicle lastSpawnedVehicle;

	private bool lastSpawnedVehicleHasBeenUsed;

	private float collisionCheckRadius;

	private VehicleSpawnScheduler scheduler;

	// Cached once. A fresh lambda per Update would allocate one delegate per frame per spawner,
	// which on a map with thirty spawners is thirty allocations every frame for a predicate
	// that never changes.
	private Func<bool> spawnIsBlocked;

	private void Awake()
	{
		// The spawner's own marker mesh. A dedicated server strips renderers, so this is null
		// there by design -- and it was the first NRE a headless build hit, before any vehicle
		// existed to go wrong.
		Renderer marker = GetComponent<Renderer>();
		if (marker != null)
		{
			marker.enabled = false;
		}
		collisionCheckRadius = prefab.GetComponent<Vehicle>().avoidanceSize.magnitude;

		spawnIsBlocked = SpawnIsBlocked;
		scheduler = new VehicleSpawnScheduler((VehicleRespawnType)respawnType, spawnTime);
	}

	private void OnEnable()
	{
		NetWorldLifecycle.ResetRequested += OnWorldReset;
	}

	private void OnDisable()
	{
		NetWorldLifecycle.ResetRequested -= OnWorldReset;
	}

	private void Start()
	{
		RequestFirstSpawn();
	}

	private void Update()
	{
		VehicleSpawnStep step = scheduler.Tick(Time.deltaTime, spawnIsBlocked);

		if (step.ShouldSpawn)
		{
			SpawnVehicle();
			return;
		}

		if (step.GaveUp)
		{
			// Once, on the tick the budget ran out -- not once a second forever, which is what
			// the unbounded coroutine effectively did to anyone reading the log.
			Debug.LogWarning(
				$"[net] vehicle spawner '{name}' gave up after {scheduler.MaxBlockedRetries} "
				+ "blocked attempts; the pad is obstructed. It re-arms on the next vehicle "
				+ "death or world reset.");
		}
	}

	/// <summary>
	/// The opening spawn, unless vehicles are suppressed for this match.
	/// </summary>
	/// <remarks>
	/// No <c>GameManager</c> means nothing has suppressed vehicles, so spawn. Preserves the
	/// "spawn unless explicitly suppressed" intent rather than inverting it on a headless
	/// process that has no GameManager at all.
	/// </remarks>
	private void RequestFirstSpawn()
	{
		if (VehiclesAreSuppressed())
		{
			return;
		}

		scheduler.RequestSpawnNow();
	}

	private static bool VehiclesAreSuppressed()
	{
		return GameManager.instance != null && GameManager.instance.noVehicles;
	}

	private void SpawnVehicle()
	{
		lastSpawnedVehicle = ((GameObject)UnityEngine.Object.Instantiate(prefab, base.transform.position, base.transform.rotation)).GetComponent<Vehicle>();
		lastSpawnedVehicle.SetSpawner(this);
		lastSpawnedVehicleHasBeenUsed = false;
		scheduler.ReportSpawned();
	}

	private bool SpawnIsBlocked()
	{
		return Physics.OverlapSphereNonAlloc(base.transform.position, collisionCheckRadius, spawnCollisions, 5376) > 0;
	}

	public void VehicleDied(Vehicle vehicle)
	{
		scheduler.ReportVehicleDied(vehicle == lastSpawnedVehicle, lastSpawnedVehicleHasBeenUsed);
	}

	public void FirstDriverEntered(Vehicle vehicle)
	{
		if (vehicle == lastSpawnedVehicle)
		{
			lastSpawnedVehicleHasBeenUsed = true;
		}
		scheduler.ReportFirstDriverEntered(vehicle == lastSpawnedVehicle);
	}

	/// <summary>
	/// Tears this spawner's vehicle down between rounds, then re-arms it for the next one.
	/// </summary>
	/// <remarks>
	/// <para>
	/// <c>MatchController.WorldResetRequested</c> declared that "the spawner subscribes" and
	/// nothing ever did, so match two inherited match one's vehicles and its wrecks. Phase-V9's
	/// "five clean matches back to back" cannot pass without this.
	/// </para>
	/// <para>
	/// The vehicle is destroyed immediately and the replacement is SCHEDULED rather than spawned
	/// — so a count taken straight after the reset is zero, and the next round still opens with
	/// vehicles after the usual <see cref="spawnTime"/>, well inside warmup.
	/// </para>
	/// <para>
	/// <b><see cref="RespawnType.Never"/> is re-armed too.</b> "Never" bounds a spawner to one
	/// vehicle within a round; a reset IS the next round, and the original expressed that by
	/// reloading the scene. A persistent server has no scene reload, so leaving Never-spawners
	/// empty would mean rounds two through five are played on a map missing its heavy armour.
	/// </para>
	/// </remarks>
	private void OnWorldReset()
	{
		if (lastSpawnedVehicle != null)
		{
			UnityEngine.Object.Destroy(lastSpawnedVehicle.gameObject);
		}

		lastSpawnedVehicle = null;
		lastSpawnedVehicleHasBeenUsed = false;

		scheduler.ReportWorldReset();

		if (!VehiclesAreSuppressed())
		{
			scheduler.ScheduleRespawn();
		}
	}
}
