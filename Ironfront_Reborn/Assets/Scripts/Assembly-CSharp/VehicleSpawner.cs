using System;
using System.Collections.Generic;
using Ironfront.Net.Protocol;
using Ironfront.Net.Replication.Combat;
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

	/// <summary>
	/// The collider the LAST probe of THIS pad returned, or null when that probe returned
	/// nothing -- or never ran.
	/// </summary>
	/// <remarks>
	/// <see cref="spawnCollisions"/> cannot answer this. It is static, so every pad on the
	/// map writes the same slot, and <c>OverlapSphereNonAlloc</c> leaves entries it does not
	/// fill exactly as it found them -- so reading it at give-up time could name a collider
	/// from another pad, from another frame, or from no query of this refusal at all.
	/// </remarks>
	private Collider lastProbeBlocker;

	/// <summary>Whether the last refusal came from physics rather than from the id pool.</summary>
	/// <remarks>
	/// The bit that decides whether an operator should go and look at the pad at all: the
	/// capacity branch of <see cref="SpawnIsBlocked"/> returns "blocked" without asking
	/// physics anything, and a pad refused that way may be completely clear.
	/// </remarks>
	private bool lastProbeRan;

	public float spawnTime = 16f;

	public RespawnType respawnType;

	public GameObject prefab;

	private Vehicle lastSpawnedVehicle;

	private bool lastSpawnedVehicleHasBeenUsed;

	private float collisionCheckRadius;

	private VehicleSpawnScheduler scheduler;

	/// <summary>
	/// Names this pad in a log line. Not on the wire -- <c>S_VEHICLE_SPAWN</c> carries no
	/// spawner field -- so it exists only so "this vehicle was not replicated" can say which
	/// of the map's fourteen pads to go and look at.
	/// </summary>
	private ushort spawnerId;

	/// <summary>
	/// The network id of <see cref="lastSpawnedVehicle"/>, or 0 when it was not replicated:
	/// offline, on a client, on an unauthored prefab, or with every vehicle id in use.
	/// </summary>
	/// <remarks>
	/// Held here rather than on <see cref="Vehicle"/> because the despawn has to be reportable
	/// after the GameObject is destroyed, and a field on a destroyed component is not readable.
	/// </remarks>
	private ushort lastSpawnedVehicleNetId;

	/// <summary>So a pad that cannot replicate says so once, not once per respawn forever.</summary>
	private bool warnedAboutUnreplicatedSpawn;

	/// <summary>
	/// The network ids of vehicles this pad spawned and then SUPERSEDED -- still alive, still
	/// replicated, no longer <see cref="lastSpawnedVehicle"/>.
	/// </summary>
	/// <remarks>
	/// <para>
	/// <b>X-70, the half that made the exhaustion permanent.</b> A <c>respawnType</c> of
	/// <c>AfterMoved</c> schedules a replacement the moment the first driver enters, so the
	/// original is alive and driven away when <see cref="SpawnVehicle"/> overwrites
	/// <see cref="lastSpawnedVehicle"/> and <see cref="AnnounceSpawn"/> overwrites
	/// <see cref="lastSpawnedVehicleNetId"/>. When that superseded vehicle later died,
	/// <see cref="VehicleDied"/>'s <c>vehicle == lastSpawnedVehicle</c> guard was false, so
	/// <c>ReportDespawned</c> never ran: the id was held for the rest of the round and every
	/// client kept a ghost vehicle that was never removed and never updated again.
	/// </para>
	/// <para>
	/// Keyed by the component rather than held on <c>Vehicle</c> for the same reason
	/// <see cref="lastSpawnedVehicleNetId"/> is: the despawn has to be reportable from a
	/// callback that fires as the object goes away.
	/// </para>
	/// </remarks>
	private readonly Dictionary<Vehicle, SupersededVehicle> supersededNetIds =
		new Dictionary<Vehicle, SupersededVehicle>();

	/// <summary>
	/// One superseded vehicle: the id it is holding, and since when nobody has been sitting in
	/// it.
	/// </summary>
	/// <remarks>
	/// The timestamp is what separates "driven away a moment ago" from "abandoned", and it lives
	/// beside the id rather than on <c>Vehicle</c> for the reason the dictionary itself gives:
	/// the bookkeeping has to survive the object going away.
	/// </remarks>
	private readonly struct SupersededVehicle
	{
		public readonly ushort NetId;

		/// <summary><c>Time.time</c> when this vehicle last had somebody in it.</summary>
		public readonly float LastOccupiedAt;

		public SupersededVehicle(ushort netId, float lastOccupiedAt)
		{
			NetId          = netId;
			LastOccupiedAt = lastOccupiedAt;
		}

		public SupersededVehicle OccupiedNow(float now) => new SupersededVehicle(NetId, now);
	}

	/// <summary>
	/// How long a superseded vehicle may sit empty before this pad takes its id back. Zero or
	/// less disables reclamation for this pad.
	/// </summary>
	/// <remarks>
	/// <para>
	/// <b>This is the bound on a population that otherwise only grows.</b> An
	/// <c>AfterMoved</c> pad schedules a replacement the moment the first driver enters, so the
	/// original stays alive holding its id, and that id comes back only when the vehicle DIES.
	/// A bot that drives one away and leaves it standing never dies, so the id never returns.
	/// Measured on 2026-09-18 against two pods with no human players: both maps had spent every
	/// one of <c>MAX_VEHICLES</c>'s 24 ids inside four and a half hours, from fourteen authored
	/// pads, and every pad after that was refused with <c>CAPACITY</c>. A long-running server
	/// therefore stops producing vehicles at all, which is the "no client saw any vehicle"
	/// session of 2026-09-17.
	/// </para>
	/// <para>
	/// <b>Ninety seconds, and the number is per pad on purpose.</b> A pad whose vehicle is meant
	/// to be parked and used as cover can be authored longer without changing anyone else.
	/// </para>
	/// </remarks>
	[Tooltip("Seconds a superseded vehicle may sit empty before this pad reclaims its network "
	         + "id. 0 or less disables reclamation for this pad.")]
	public float reclaimAbandonedAfterSeconds = 90f;

	/// <summary>
	/// How close a living actor has to be to keep an abandoned vehicle from being reclaimed.
	/// </summary>
	/// <remarks>
	/// <b>Without this, reclamation eats a player's parked jeep.</b> Somebody who drives to a
	/// flag, gets out and spends two minutes capturing it has a vehicle that is empty and is
	/// emphatically not abandoned. An empty-seat test alone cannot tell those apart; standing
	/// next to it can. Bot litter is abandoned precisely because the bot walked off.
	/// </remarks>
	[Tooltip("A living actor within this many metres keeps an empty vehicle from being "
	         + "reclaimed, so a player's parked vehicle is never taken.")]
	public float reclaimKeepAliveRadius = 30f;

	// Reclamation runs on a slow cadence rather than every frame: the threshold is measured in
	// tens of seconds, and the alternative is a seat scan plus a radius query per pad per frame
	// for an answer that cannot change meaningfully inside one.
	private const float ReclaimSweepInterval = 2f;

	private float nextReclaimSweepAt;

	// Both reused across sweeps, for ActorManager.ActorsInRange's buffer-overload reason: a
	// fresh List per sweep per pad is a steady GC drip for the life of the process.
	private readonly List<Vehicle> reclaimCandidates = new List<Vehicle>();

	/// <summary>
	/// Vehicles seen occupied during a sweep, whose <c>LastOccupiedAt</c> is refreshed AFTER the
	/// enumeration finishes.
	/// </summary>
	/// <remarks>
	/// <para>
	/// <b>This list exists because the refresh used to happen inside the loop.</b>
	/// <see cref="SweepAbandonedVehicles"/>'s own comment already said "collected first, mutated
	/// after: the dictionary cannot be written to while it is being enumerated" — and then the
	/// occupied branch wrote <c>supersededNetIds[vehicle]</c> mid-enumeration anyway.
	/// </para>
	/// <para>
	/// <b>It throws on Unity and would not throw on modern .NET</b>, which is why it survived
	/// review. Since .NET Core 3.0 overwriting an EXISTING key's value does not invalidate a
	/// dictionary enumerator; on Mono, which is what ships in the player, any write bumps the
	/// version and the next <c>MoveNext</c> raises <c>InvalidOperationException: Collection was
	/// modified</c>. Measured on a 131s lane-B match 2026-09-20: 131 throws before this fix.
	/// </para>
	/// <para>
	/// <b>What it cost.</b> The exception escaped <c>Update</c>, so every sweep aborted at the
	/// first occupied superseded vehicle — the reclaim never reached the entries after it, which
	/// is exactly the id-pool leak this sweep was added to close.
	/// </para>
	/// <para>
	/// Pre-allocated beside <see cref="reclaimCandidates"/> and cleared per sweep, for the same
	/// reason: this runs out of <c>Update</c> and must not allocate per frame.
	/// </para>
	/// </remarks>
	private readonly List<Vehicle> reclaimStillOccupied = new List<Vehicle>();

	private static readonly List<Actor> reclaimNearbyActors = new List<Actor>();

	/// <summary>
	/// The budget behind the <c>[vehicle-spawn-state]</c> line protocol 10 § 8.3 asks for.
	/// </summary>
	/// <remarks>
	/// Per spawner rather than one static budget for the map: a shared one would let the pad
	/// that respawns fastest spend the whole allowance and silence the thirteen pads a reader
	/// is comparing it against, which is the opposite of what the line is for.
	/// </remarks>
	private readonly Ironfront.Net.Replication.Vehicles.VehicleSpawnStateLog spawnStateLog =
		new Ironfront.Net.Replication.Vehicles.VehicleSpawnStateLog();

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
		spawnerId = NetVehicleLifecycle.RegisterSpawner();
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
		// A network client receives authoritative vehicles through RemoteVehicleRegistry. Running
		// the scene spawner here as well creates a second physical vehicle on the same pad. The two
		// rigidbodies explode apart, take crash damage and appear to be burning on the first frame.
		// Offline keeps the original spawner; the dedicated/listen server remains the sole owner.
		if (Ironfront.Net.Unity.NetContext.IsClient)
		{
			enabled = false;
			return;
		}

		RequestFirstSpawn();
	}

	private void Update()
	{
		// BEFORE the scheduler tick, so an id freed this sweep is available to the spawn the
		// same tick may ask for. The other order costs a full retry interval on the one pad
		// that is most starved -- and SpawnIsBlocked asks the pool directly, so it would read
		// the pre-sweep answer and defer for nothing.
		SweepAbandonedVehicles();

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
			// spawnerId, not just name: Dustbowl authors 'Vehicle Spawner (2)' FOUR times and
			// 'Vehicle Spawner (1)' twice, so the name alone cannot say which pad to go and
			// look at -- which is the whole reason spawnerId exists. X-70.
			//
			// And the blocker is NAMED, with the one bit that decides whether to investigate:
			// whether a physics query ran at all, and whether what it found is a living body
			// (allowed, § 7) or a corpse that kept its colliders (the § 2.4 defect). 'The pad is
			// obstructed by Bone_002' is true of both, so it sent every reader to check by hand.
			Debug.LogWarning(
				$"[net] vehicle spawner '{name}' (id {spawnerId}) gave up after "
				+ $"{scheduler.MaxBlockedRetries} blocked attempts. {DescribeBlocker()} "
				+ "Fast retries are paused; the pad will be checked silently every 10 seconds "
				+ "and also re-arms on lifecycle events.");
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

	/// <summary>
	/// Whether this pad should stand down.
	/// </summary>
	/// <remarks>
	/// <b>A client never spawns its own vehicles (V5).</b> Every vehicle in a networked world
	/// arrives from <c>S_VEHICLE_SPAWN</c> and is instantiated by <c>RemoteVehicleRegistry</c>,
	/// with the id the server gave it. Letting the local pad run too would put two vehicles here
	/// — one replicated, one simulated locally from a spawn timer with no reason to agree with
	/// the server's — and neither would look wrong on its own. Offline and on the server this is
	/// exactly the check it always was.
	/// </remarks>
	private static bool VehiclesAreSuppressed()
	{
		if (Ironfront.Net.Unity.NetContext.IsClient)
		{
			return true;
		}

		return GameManager.instance != null && GameManager.instance.noVehicles;
	}

	private void SpawnVehicle()
	{
		// Re-checked here and not only in RequestFirstSpawn: the scheduler keeps asking, and the
		// role can be declared after this component's Awake if the map scene loaded first.
		if (VehiclesAreSuppressed())
		{
			return;
		}

		// Protocol 10 § 8.2: nothing is instantiated when no id can be allocated. SpawnIsBlocked
		// already defers on this and asking again is not belt-and-braces -- a sibling pad's
		// Update can take the last id between that probe and this line, and the object this
		// method is about to create would then be exactly the phantom X-70 was.
		if (!NetVehicleLifecycle.CanReplicateAnotherVehicle)
		{
			DeferForLackOfAnId();
			return;
		}

		Vehicle spawned = ((GameObject)UnityEngine.Object.Instantiate(prefab, base.transform.position, base.transform.rotation)).GetComponent<Vehicle>();

		// ANNOUNCED BEFORE IT IS COMMITTED, and that ordering is the fix. The old order
		// overwrote lastSpawnedVehicle and only then asked for an id, so a refusal left a
		// vehicle standing on the pad with id 0 -- solid on the server, addressable by nobody,
		// and blocking its own replacement for the rest of the round. Announcing first leaves a
		// refusal with nothing to undo but one Destroy.
		ushort netId = AnnounceSpawn(spawned);

		if (netId == 0 && NetVehicleLifecycle.IsReplicating)
		{
			UnityEngine.Object.Destroy(spawned.gameObject);
			DeferForLackOfAnId();
			return;
		}

		// Hand the outgoing vehicle its own id BEFORE the fields that hold it are overwritten.
		// X-70: without this the id is orphaned -- never released, never despawned -- because
		// VehicleDied's guard compares against lastSpawnedVehicle, which is about to change.
		if (lastSpawnedVehicle != null && lastSpawnedVehicleNetId != 0)
		{
			// Seeded as occupied NOW rather than at 0, so the reclaim clock starts from the
			// moment of supersession. An AfterMoved pad supersedes because a driver got IN, so
			// "last occupied" is this instant by construction; seeding 0 would make a vehicle
			// that is being driven right now eligible for reclamation on the first sweep, and
			// the sweep's own emptiness test is the only thing that would save it.
			supersededNetIds[lastSpawnedVehicle] =
				new SupersededVehicle(lastSpawnedVehicleNetId, Time.time);
		}

		lastSpawnedVehicle = spawned;
		lastSpawnedVehicleNetId = netId;
		lastSpawnedVehicle.SetSpawner(this);
		lastSpawnedVehicleHasBeenUsed = false;
		scheduler.ReportSpawned();

		LogFirstState(spawned, netId);
	}

	/// <summary>
	/// Holds the request instead of dropping it, because there was no id to pay for it.
	/// </summary>
	/// <remarks>
	/// Protocol 10 § 8.2 allows refusing OR holding, and names dropping as the thing that
	/// produced "the vehicle exists, nobody can see it". Holding reuses the retry budget the
	/// obstructed-pad case already has, so an exhausted pool costs a late vehicle rather than
	/// an unaddressable one -- and the budget is what keeps a permanently unpayable pad (an
	/// unauthored prefab) from retrying once a second for the life of the process.
	/// </remarks>
	private void DeferForLackOfAnId()
	{
		if (!scheduler.ReportSpawnRefused())
		{
			return;
		}

		Debug.LogWarning(
			$"[net] vehicle spawner '{name}' (id {spawnerId}) gave up after "
			+ $"{scheduler.MaxBlockedRetries} attempts with no vehicle id to spare, so it "
			+ $"produced nothing rather than a vehicle with id 0. "
			+ $"{NetVehicleLifecycle.DescribeSpawnRefusal()} The pad is probed silently every "
			+ "10 seconds and re-arms as soon as a despawn anywhere on the map frees an id.");
	}

	/// <summary>
	/// Writes the one line protocol 10 § 8.3 asks for about each vehicle's first state.
	/// </summary>
	/// <remarks>
	/// <para>
	/// <b>It exists to settle an argument, not to diagnose one.</b> Players report smoke on
	/// freshly spawned vehicles. If this line and the first snapshot both say full health with
	/// no flags, the smoke is a client particle bug and the evidence goes to the client side --
	/// and § 16 forbids the other answer outright: lowering a vehicle's health to make the
	/// particles stop is falsifying the instrument to match the complaint.
	/// </para>
	/// <para>
	/// A failed invariant is an ERROR rather than a louder version of the same line, because
	/// the two mean opposite things about whose defect it is.
	/// </para>
	/// </remarks>
	private void LogFirstState(Vehicle vehicle, ushort netId)
	{
		VehicleStateFlags flags = VehicleStateFlags.None;
		if (vehicle.burning) flags |= VehicleStateFlags.Burning;
		if (vehicle.dead) flags |= VehicleStateFlags.Dead;

		VehicleIds.TryGetKind(vehicle.NetworkId, out VehicleKind kind);
		Vector3 at = base.transform.position;

		// driver=0 is not an assumption: the vehicle was instantiated on the line above and
		// nothing has had a frame in which to enter it.
		if (!spawnStateLog.TryFormat(
			netId, spawnerId, kind, vehicle.Health, vehicle.maxHealth, flags,
			driverActorId: 0,
			new Ironfront.Net.Replication.Movement.Vec3(at.x, at.y, at.z),
			Time.time, out string line))
		{
			return;
		}

		if (Ironfront.Net.Replication.Vehicles.VehicleSpawnStateLog.IsFreshlySpawned(
			vehicle.Health, vehicle.maxHealth, flags))
		{
			Debug.Log(line);
			return;
		}

		Debug.LogError(
			line + " -- this vehicle was NOT born at full health with no flags, so the smoke "
			+ "players report is the server's and the damage source has to be found before "
			+ "the handover. Do not lower maxHealth to make it match.");
	}

	/// <summary>
	/// Tells the netcode a vehicle now exists here, and remembers the id it was given.
	/// </summary>
	/// <remarks>
	/// <para>
	/// Reported from the transform this spawner instantiated at, not from the vehicle's own
	/// transform: a rigidbody can have been stepped by physics before this line runs, and the
	/// position on the wire should be the pad, which is what every client will interpolate
	/// away from.
	/// </para>
	/// <para>
	/// <b>Id 0 is a real answer, not a failure.</b> Offline and on a client the sink is the
	/// null object and 0 means "there is no network" -- so this method is silent there. It is
	/// only worth a line when the server IS replicating and this particular pad still got
	/// nothing, which means an unauthored prefab or an exhausted id pool.
	/// </para>
	/// </remarks>
	private ushort AnnounceSpawn(Vehicle vehicle)
	{
		ushort netId = NetVehicleLifecycle.ReportSpawned(
			vehicle.gameObject,
			spawnerId,
			vehicle.NetworkId,
			vehicle.seats != null ? vehicle.seats.Length : 0,
			base.transform.position,
			base.transform.rotation);

		if (netId != 0 || !NetVehicleLifecycle.IsReplicating) return netId;
		if (warnedAboutUnreplicatedSpawn) return netId;

		warnedAboutUnreplicatedSpawn = true;

		// The counters, not the two candidates. X-70: this line used to say "either the prefab's
		// networkId is unauthored or every vehicle id is in use", and a reader took the first
		// branch for a prefab that had carried an id since the commit which introduced the
		// field. A message that offers a choice is a message that gets chosen wrongly.
		Debug.LogError(
			$"[net] vehicle spawner '{name}' (id {spawnerId}) could not replicate "
			+ $"'{prefab.name}' (networkId {vehicle.NetworkId}), so nothing was spawned. "
			+ $"{NetVehicleLifecycle.DescribeSpawnRefusal()}");

		return netId;
	}

	private bool SpawnIsBlocked()
	{
		// X-70's capacity half, and it is a BLOCK rather than a refusal on purpose: deferring
		// reuses the retry budget the obstruction case already has, so the replacement arrives
		// a few seconds later once a quarantined id drains -- the difference between a late
		// vehicle and a phantom one.
		//
		// UNCONDITIONAL as of protocol 10, and it was not. It used to fire only when this pad
		// needed a SECOND id alongside one it already held, on the reasoning that a pad whose
		// vehicle had died released its id on the way out and so re-used capacity rather than
		// adding to it. That reasoning is about THIS pad and the pool is shared: a pad whose
		// vehicle died into a 150-tick quarantine, on a map where every other id is live, took
		// the narrow branch and spawned anyway -- with id 0. Raising MAX_VEHICLES to 24 widens
		// the margin and does not close that hole; only asking the pool every time does.
		if (!NetVehicleLifecycle.CanReplicateAnotherVehicle)
		{
			// No physics query runs on this branch, so there is no blocker -- and the give-up
			// line named one anyway. spawnCollisions is STATIC, shared by every pad on the map,
			// and OverlapSphereNonAlloc does not clear entries it does not fill, so whatever an
			// earlier query left sat there waiting to be reported as this pad's obstruction.
			// A capacity refusal and an obstruction need opposite responses -- wait for an id
			// versus go and look at the pad -- and the old line rendered them identically.
			lastProbeRan     = false;
			lastProbeBlocker = null;
			return true;
		}

		// SPAWN_BLOCK_MASK, not the literal 5376 a second time. The constant was declared and
		// the call site re-spelled it, so the two could drift with nothing to notice.
		lastProbeRan = true;
		int hits = Physics.OverlapSphereNonAlloc(
			base.transform.position, collisionCheckRadius, spawnCollisions, SPAWN_BLOCK_MASK);

		// Copied out of the shared scratch now, while it is certainly this pad's answer.
		lastProbeBlocker = hits > 0 ? spawnCollisions[0] : null;
		return hits > 0;
	}

	/// <summary>
	/// The gave-up line's verdict: whether a physics query ran at all, and if it did, whether
	/// what it found belongs to a living body, a corpse, or nothing that is an actor.
	/// </summary>
	/// <remarks>
	/// <para>
	/// <b>It reads <see cref="lastProbeBlocker"/>, not <c>spawnCollisions[0]</c>, and that is
	/// the repair.</b> A message that says "obstructed" without saying by what is the
	/// instrument failure X-70 itself was -- but naming the shared scratch slot replaced it
	/// with a worse one, a name that can belong to another pad's query or to no query of this
	/// refusal at all. See <see cref="SpawnIsBlocked"/>.
	/// </para>
	/// <para>
	/// <b>The live-versus-dead bit is the one an operator acts on.</b> A living bot standing on
	/// a pad is allowed to block it (protocol-10 handoff § 7) and the pad is merely waiting; a
	/// corpse that still carries its colliders is the § 2.4 cleanup defect. Without that bit
	/// the next reader repeats the whole investigation. The verdict and its wording live in
	/// <c>CorpseColliderLedger</c> because this file is <c>Assembly-CSharp</c>, which no test
	/// project can reference -- out there the sentence is graded by <c>dotnet test</c>.
	/// </para>
	/// </remarks>
	private string DescribeBlocker()
	{
		Collider blocker = lastProbeBlocker;

		bool belongsToActor    = false;
		bool actorIsAlive      = false;
		bool collidersDisabled = false;
		ushort actorId         = 0;

		if (blocker != null)
		{
			// InParent: the blocking collider is a ragdoll bone several levels below the body
			// that carries the NetServerActor, and the bone has no component of its own.
			NetServerActor owner = blocker.GetComponentInParent<NetServerActor>();
			if (owner != null)
			{
				belongsToActor    = true;
				actorId           = owner.ActorId;
				actorIsAlive      = owner.IsAlive;
				collidersDisabled = owner.CorpseCollidersDisabled;
			}
		}

		PadBlockerKind kind = CorpseColliderLedger.ClassifyPadBlocker(
			lastProbeRan, blocker != null, belongsToActor, actorIsAlive, collidersDisabled);

		string described = blocker != null
			? $"'{blocker.gameObject.name}' (layer {LayerMask.LayerToName(blocker.gameObject.layer)})"
			: string.Empty;

		return CorpseColliderLedger.DescribePadBlocker(kind, described, actorId);
	}

	/// <summary>
	/// Takes back the network id of any vehicle this pad superseded and nobody is using.
	/// </summary>
	/// <remarks>
	/// <para>
	/// <b>This is the only thing that bounds the superseded population.</b> See
	/// <see cref="reclaimAbandonedAfterSeconds"/> for the measurement that says it has to exist.
	/// A pad's CURRENT vehicle is never a candidate: reclaiming it would fight the spawner that
	/// just placed it.
	/// </para>
	/// <para>
	/// <b>A key that has gone null is reclaimed immediately, and that is a second leak closed.</b>
	/// <c>Vehicle.OnDestroy</c> only leaves <c>ActorManager</c>'s register; it reports no despawn
	/// and returns no id. So a superseded vehicle destroyed by anything other than its own
	/// <c>Die()</c> path -- a scene teardown, a <c>Destroy</c> from elsewhere -- used to hold its
	/// id for the life of the process with no object left to notice. There is nothing to wait
	/// for in that case: the object is already gone, so the timer does not apply.
	/// </para>
	/// <para>
	/// <b>Silent on a client and offline.</b> <c>Start</c> disables this component at client
	/// role, and the whole sweep is behind <c>IsReplicating</c> -- there is no id pool to be out
	/// of in single-player, so reclaiming would destroy vehicles the original game keeps.
	/// </para>
	/// <para>
	/// <b>Reported before destroyed</b>, the ordering <see cref="OnWorldReset"/> already uses: a
	/// client that gets the despawn first removes its copy cleanly rather than having the
	/// snapshot stream stop under one it still holds.
	/// </para>
	/// </remarks>
	private void SweepAbandonedVehicles()
	{
		if (supersededNetIds.Count == 0) return;
		if (!NetVehicleLifecycle.IsReplicating) return;

		float now = Time.time;
		if (now < nextReclaimSweepAt) return;
		nextReclaimSweepAt = now + ReclaimSweepInterval;

		reclaimCandidates.Clear();
		reclaimStillOccupied.Clear();

		// Collected first, mutated after: the dictionary cannot be written to while it is being
		// enumerated, and both branches below write to it.
		//
		// The occupied branch used to break that rule in place, which is what made this sweep
		// throw on Mono and abort at the first occupied entry. See reclaimStillOccupied.
		foreach (KeyValuePair<Vehicle, SupersededVehicle> entry in supersededNetIds)
		{
			Vehicle vehicle = entry.Key;

			// Destroyed out from under us. Unity's overloaded == is what makes this readable as
			// a null; the dictionary still holds the dead key.
			if (vehicle == null)
			{
				reclaimCandidates.Add(vehicle);
				continue;
			}

			if (reclaimAbandonedAfterSeconds <= 0f) continue;

			if (!vehicle.IsEmpty())
			{
				reclaimStillOccupied.Add(vehicle);
				continue;
			}

			if (now - entry.Value.LastOccupiedAt < reclaimAbandonedAfterSeconds) continue;
			if (SomebodyIsStandingBy(vehicle)) continue;

			reclaimCandidates.Add(vehicle);
		}

		// The deferred half of the occupied branch. Safe here because the enumeration above has
		// finished; TryGetValue guards the entry having been removed in between.
		for (int i = 0; i < reclaimStillOccupied.Count; i++)
		{
			Vehicle occupied = reclaimStillOccupied[i];
			if (occupied == null) continue;
			if (!supersededNetIds.TryGetValue(occupied, out SupersededVehicle seen)) continue;

			supersededNetIds[occupied] = seen.OccupiedNow(now);
		}

		reclaimStillOccupied.Clear();

		for (int i = 0; i < reclaimCandidates.Count; i++)
		{
			Vehicle vehicle = reclaimCandidates[i];
			if (!supersededNetIds.TryGetValue(vehicle, out SupersededVehicle superseded)) continue;

			supersededNetIds.Remove(vehicle);
			NetVehicleLifecycle.ReportDespawned(superseded.NetId, VehicleDespawnReason.Reclaimed);

			if (vehicle == null) continue;

			Debug.Log(
				$"[net] vehicle spawner '{name}' (id {spawnerId}) reclaimed id {superseded.NetId}: "
				+ $"empty for {now - superseded.LastOccupiedAt:F0}s with nobody within "
				+ $"{reclaimKeepAliveRadius:F0}m. {NetVehicleLifecycle.DescribeSpawnRefusal()}");

			// EjectOccupants even though IsEmpty just said there are none: X-55/X-56's rule is
			// about a HALF-booked seat, where the seat records an occupant that does not think
			// it is seated there. IsEmpty reads the same seats, so the two agree -- and the one
			// case where they would not is exactly the one that welds a body to a hierarchy
			// about to be destroyed.
			vehicle.EjectOccupants();
			UnityEngine.Object.Destroy(vehicle.gameObject);
		}

		reclaimCandidates.Clear();
	}

	/// <summary>
	/// True when a living actor is close enough that this vehicle is parked rather than
	/// abandoned.
	/// </summary>
	private bool SomebodyIsStandingBy(Vehicle vehicle)
	{
		if (reclaimKeepAliveRadius <= 0f) return false;

		ActorManager.AliveActorsInRange(
			vehicle.transform.position, reclaimKeepAliveRadius, reclaimNearbyActors);
		return reclaimNearbyActors.Count > 0;
	}

	public void VehicleDied(Vehicle vehicle)
	{
		// A superseded vehicle -- alive and driven away when this pad respawned. Its id is the
		// one X-70 leaked: released here, and its despawn put on the wire, so the clients that
		// have been rendering it since stop. Checked before the lastSpawnedVehicle branch
		// because the two sets are disjoint and this one used to fall through it entirely.
		if (vehicle != null && supersededNetIds.TryGetValue(vehicle, out SupersededVehicle superseded))
		{
			supersededNetIds.Remove(vehicle);
			NetVehicleLifecycle.ReportDespawned(superseded.NetId, VehicleDespawnReason.Destroyed);
		}

		if (vehicle == lastSpawnedVehicle)
		{
			// Before the scheduler, because ReportVehicleDied is what may schedule the
			// replacement -- and a replacement announced before this despawn would tell every
			// client to remove the vehicle that had just arrived.
			NetVehicleLifecycle.ReportDespawned(
				lastSpawnedVehicleNetId, VehicleDespawnReason.Destroyed);
			lastSpawnedVehicleNetId = 0;
		}

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
		// The superseded vehicles FIRST, and they were not torn down at all before. This method
		// destroyed lastSpawnedVehicle and nothing else, so an AfterMoved pad whose original had
		// been driven away left that original standing into the next round with its id never
		// released -- X-70's leak one event over, and the mapping protocol 10 § 8.2 requires a
		// reset to clear. Ordered ahead of the current vehicle because these are the older
		// claims: a client applying the two despawns in arrival order removes the ghost before
		// the vehicle it can still see.
		foreach (KeyValuePair<Vehicle, SupersededVehicle> superseded in supersededNetIds)
		{
			NetVehicleLifecycle.ReportDespawned(
				superseded.Value.NetId, VehicleDespawnReason.WorldReset);

			if (superseded.Key != null)
			{
				// EjectOccupants before Destroy, for X-55/X-56's reason below.
				superseded.Key.EjectOccupants();
				UnityEngine.Object.Destroy(superseded.Key.gameObject);
			}
		}

		supersededNetIds.Clear();

		if (lastSpawnedVehicle != null)
		{
			// Ledger X-55/X-56. BEFORE the Destroy, and it has to be before: a seated actor is a
			// CHILD of this vehicle, so destroying it first takes every rider with it -- silently,
			// without Actor.Die, leaving each one in its Squad's roster to be dereferenced once per
			// frame by every squad-mate that outlived the round. Rescuing them from Vehicle's own
			// OnDestroy is not available: by then Unity has already committed to the hierarchy.
			lastSpawnedVehicle.EjectOccupants();
			UnityEngine.Object.Destroy(lastSpawnedVehicle.gameObject);
		}

		// Reported whether or not the GameObject was still alive: a vehicle destroyed by
		// something other than its own death path (a scene teardown, a Destroy from anywhere)
		// leaves the id held and the client's copy standing. Reason WorldReset rather than Destroyed
		// so a client can tear the round down without playing fourteen explosions.
		NetVehicleLifecycle.ReportDespawned(lastSpawnedVehicleNetId, VehicleDespawnReason.WorldReset);

		lastSpawnedVehicleNetId = 0;
		lastSpawnedVehicle = null;
		lastSpawnedVehicleHasBeenUsed = false;

		scheduler.ReportWorldReset();

		if (!VehiclesAreSuppressed())
		{
			scheduler.ScheduleRespawn();
		}
	}
}
