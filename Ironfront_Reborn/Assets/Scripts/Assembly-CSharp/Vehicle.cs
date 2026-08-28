using System;
using Ironfront.Net.Replication.Vehicles;
using Ironfront.Net.Unity;
using Ironfront.Net.Unity.Server;
using UnityEngine;

public partial class Vehicle : MonoBehaviour, Ironfront.Net.Unity.IGameplayVehicleBody
{
	/// <summary>
	/// The attacker id meaning "nobody in particular" -- world damage, decay, a crash.
	/// </summary>
	/// <remarks>
	/// A plain int, deliberately, and not the netcode's id width. Assembly-CSharp must not
	/// gain a compile-time dependency on the replication library's actor-id type to fix a
	/// pre-existing bug; V4's damage sink narrows it at its own seam, where the mapping
	/// between the two already lives.
	/// </remarks>
	public const int NoAttacker = -1;

	private const float HEAVY_DAMAGE_THRESHOLD = 900f;

	private const int RAM_MASK = 256;

	private const float EXPLODE_TIME = 0.3f;

	private const float CLEANUP_TIME = 15f;

	public const int LAYER = 12;

	private const float AUTO_DAMAGE_START_TIME = 50f;

	private const float AUTO_DAMAGE_PERIOD = 2f;

	private const float AUTO_DAMAGE_PERCENT = 0.07f;

	private const float RAM_MIN_SPEED = 3f;

	private static RaycastHit[] ramResults = new RaycastHit[16];

	public Actor.TargetType targetType = Actor.TargetType.Unarmored;

	public Seat[] seats;

	[NonSerialized]
	public int ownerTeam = -1;

	[NonSerialized]
	public int seatsClaimedByBots;

	[NonSerialized]
	public bool claimedByPlayer;

	[NonSerialized]
	public bool stuck;

	private Action takingFireAction = new Action(20f);

	/// <summary>
	/// This prefab's <c>Ironfront.Net.Protocol.VehicleIds</c> value, carried by
	/// <c>S_VEHICLE_SPAWN</c> so the receiving client instantiates the right prefab.
	/// </summary>
	/// <remarks>
	/// <para>
	/// Authored per prefab in the Inspector, and gated by <c>tools/SpecChecker</c> against
	/// <c>VehicleIds.cs</c> and <c>protocol-spec.md § 4.9</c> on every CI run. This is the
	/// <c>weaponId</c> arrangement exactly: the mapping has to exist somewhere the server can
	/// read, and a Unity YAML asset is not that place.
	/// </para>
	/// <para>
	/// A plain <c>byte</c> rather than the protocol's type, for the same reason
	/// <see cref="NoAttacker"/> is a plain int: Assembly-CSharp does not take a compile-time
	/// dependency on the netcode's id types to carry one serialized number.
	/// </para>
	/// </remarks>
	[SerializeField]
	private byte networkId;

	/// <summary>This prefab's vehicle-type id. 0 means unauthored, and never ships.</summary>
	public byte NetworkId => networkId;

	public float maxHealth = 1000f;

	public float crashDamageSpeedThrehshold = 2f;

	public float crashDamageMultiplier;

	public float spotChanceMultiplier = 3f;

	public float burnTime;

	public bool crashSkipsBurn;

	public bool directJavelinPath;

	public bool exitWhenTakingFire;

	private float health;

	/// <summary>
	/// Who last took health off this vehicle, or <see cref="NoAttacker"/>. V4's death event
	/// reads it; nothing writes it but <see cref="ApplyHealth"/>.
	/// </summary>
	private int _lastDamagedBy = NoAttacker;

	/// <summary>
	/// Mirrors whether <see cref="damageParticles"/> is currently emitting, so the ladder in
	/// <see cref="ApplyHealth"/> only calls Play/Stop on the transition. The shipped code
	/// called Play() on every damage tick below half health.
	/// </summary>
	private bool damageParticlesOn;

	[NonSerialized]
	public bool dead;

	[NonSerialized]
	public Rigidbody rigidbody;

	private VehicleSpawner spawner;

	public ParticleSystem damageParticles;

	public ParticleSystem burnParticles;

	public ParticleSystem deathParticles;

	public AudioSource fireAlarm;

	public Transform blockSensor;

	protected AudioSource audio;

	public AudioSource explosionSound;

	public AudioSource impactAudio;

	public AudioSource heavyDamageAudio;

	public Texture blip;

	public Vector2 avoidanceSize = Vector2.one;

	public float pathingRadius;

	public Vector3 ramSize = Vector3.one;

	public Vector3 ramOffset = Vector3.zero;

	private float avoidanceCoarseRadius;

	private bool reportedFirstDriver;

	[NonSerialized]
	public Collider[] colliders;

	private Vector3 blockSensorOrigin;

	private Action cannotRamAction = new Action(0.5f);

	private Action crashDamageCooldown = new Action(0.2f);

	private Action drainClaimAction = new Action(10f);

	[NonSerialized]
	public bool burning;

	private int stopBurningRepairs;

	public bool HasDriver()
	{
		return seats[0].IsOccupied();
	}

	public Actor Driver()
	{
		return seats[0].occupant;
	}

	/// <summary>
	/// This seat's index in <see cref="seats"/>, or -1 when it belongs to another vehicle.
	/// </summary>
	/// <remarks>
	/// A hand-rolled scan rather than <c>Array.IndexOf</c>: <c>seats</c> holds at most eight
	/// entries and this runs on the fixed-step aim path, where the generic helper's type checks
	/// buy nothing.
	/// </remarks>
	public int SeatIndexOf(Seat seat)
	{
		if (seat == null || seats == null)
		{
			return -1;
		}
		for (int i = 0; i < seats.Length; i++)
		{
			if (seats[i] == seat)
			{
				return i;
			}
		}
		return -1;
	}

	public void MarkTakingFire()
	{
		takingFireAction.Start();
	}

	protected virtual void Awake()
	{
		rigidbody = GetComponent<Rigidbody>();
		audio = GetComponent<AudioSource>();
		ActorManager.RegisterVehicle(this);
		// Through ApplyHealth like every other write, so there is exactly one assignment to
		// health in this file and no second copy of the ladder to drift from it.
		ApplyHealth(maxHealth, 0f, NoAttacker);
		colliders = GetComponentsInChildren<Collider>();
		if (HasBlockSensor())
		{
			blockSensorOrigin = blockSensor.transform.localPosition;
		}
		cannotRamAction.Start();
		avoidanceCoarseRadius = avoidanceSize.magnitude;
	}

	private void CheckRam()
	{
		Vector3 vector = rigidbody.linearVelocity * Time.fixedDeltaTime;
		int num = Physics.BoxCastNonAlloc(base.transform.localToWorldMatrix.MultiplyPoint(ramOffset), ramSize, vector.normalized, ramResults, base.transform.rotation, vector.magnitude, 256);
		for (int i = 0; i < num; i++)
		{
			RaycastHit raycastHit = ramResults[i];
			Hitbox component = raycastHit.collider.GetComponent<Hitbox>();
			if (component.RigidbodyHit(rigidbody, raycastHit.point) && HasDriver() && !Driver().aiControlled)
			{
				IngameUi.Hit();
			}
		}
	}

	protected virtual void FixedUpdate()
	{
		if (rigidbody.linearVelocity.magnitude < 3f)
		{
			cannotRamAction.Start();
		}
		if (cannotRamAction.TrueDone())
		{
			CheckRam();
		}
		// NOT at server role. VehicleBurnClock counts the same burn in ticks and is the only
		// thing allowed to end it (V4-D11) -- leaving this running made two authorities race one
		// death, deduplicated only by the id pool, and this one usually won because FixedUpdate
		// runs at 60 Hz against the tick clock's 20. The server calls Die() through
		// IGameplayVehicleSource.Kill when its own clock expires.
		if (burning && !dead && !NetVehicleAuthority.ServerOwnsVehicleDeath)
		{
			// Time.deltaTime returns fixedDeltaTime inside the fixed loop, so this was correct
			// BY ACCIDENT and would have gone silently wrong the moment V4 drives the burn
			// countdown from the 30 Hz netcode accumulator instead. Zero behaviour change
			// today; that is the point.
			burnTime -= Time.fixedDeltaTime;
			if (burnTime < 0f)
			{
				Die();
			}
		}
		// The offline drain, unchanged. Its server-role counterpart is per-CLAIM expiry, and it
		// deliberately does NOT run here: BotSeatClaims.ReleaseExpired sweeps the whole global
		// table, so calling it from a per-vehicle FixedUpdate ran one global sweep per vehicle
		// per physics step -- sixteen times the necessary work at a full map, and none at all on
		// a map with no vehicles, because the trigger was coupled to the instance count rather
		// than to the clock it is actually measuring. ServerTickLoop owns it now, once a tick.
		if (drainClaimAction.TrueDone() && seatsClaimedByBots > 0)
		{
			DropSeatClaim();
			drainClaimAction.Start();
		}

		KeepInsideLevelBounds();
	}

	/// <summary>
	/// Pulls this vehicle back into the play area when it leaves it. Ledger <b>E-6</b>.
	/// </summary>
	/// <remarks>
	/// <para>
	/// <b>Server-only, because the fault it prevents is a wire fault.</b> Past the wire's
	/// ±2048 m, <c>Quantize.PackPos</c> clamps every snapshot to the boundary while this server
	/// keeps simulating the true position — so every client sees the vehicle pinned, forever,
	/// with nothing logged. Offline there is no wire and no observer to disagree with, so the
	/// same flight is merely eccentric and is left alone (D11's posture: single-player is not
	/// changed by a networking fix).
	/// </para>
	/// <para>
	/// <b>The outward velocity goes with the position.</b> Clamping alone leaves the rigidbody
	/// still pushing into the wall, so it re-crosses on the next step and the counter climbs
	/// once per physics tick — a clamp that fires 60 times a second reads as broken rather than
	/// as a boundary. Only the component pointing out is removed; motion ALONG the face is
	/// untouched, so a helicopter at the edge flies sideways rather than stopping dead.
	/// </para>
	/// </remarks>
	private void KeepInsideLevelBounds()
	{
		if (!NetContext.IsServer || rigidbody == null) return;

		if (!LevelBounds.ClampInside(rigidbody.position, out Vector3 inside)) return;

		Vector3 pushedBackBy = inside - rigidbody.position;
		rigidbody.position = inside;

		Vector3 velocity = rigidbody.linearVelocity;
		if (pushedBackBy.x != 0f) velocity.x = 0f;
		if (pushedBackBy.y != 0f) velocity.y = 0f;
		if (pushedBackBy.z != 0f) velocity.z = 0f;
		rigidbody.linearVelocity = velocity;
	}

	public void OccupantEntered(Seat seat)
	{
		if (seat == seats[0])
		{
			DriverEntered();
		}
		if (!seat.occupant.aiControlled)
		{
			claimedByPlayer = true;
			ownerTeam = seat.occupant.team;
			if (burning && fireAlarm != null)
			{
				fireAlarm.Play();
			}
		}
		CancelInvoke("AutoDamage");
	}

	/// <summary>
	/// Reserves a seat for a bot that has no identity to give. Offline only.
	/// </summary>
	/// <remarks>
	/// Kept so the shipped call sites compile unchanged, and it is the honest offline
	/// behaviour: with no netcode there is no actor id to key a claim on, and the counter is
	/// correct as long as nothing dies -- which offline, with one squad, it does not.
	/// </remarks>
	public void ClaimSeat()
	{
		ClaimSeat(null);
	}

	/// <summary>
	/// Reserves a seat for a named bot. V4-D10.
	/// </summary>
	/// <remarks>
	/// <para>
	/// <b>The identity is the whole fix.</b> The counter this replaces names nobody, so two bots
	/// claiming and one dying leaves it permanently wrong: nothing decrements, and the 10-second
	/// <c>drainClaimAction</c> then takes one off an anonymous pile. The vehicle reports itself
	/// full to the AI while a seat sits empty, and no client could reconcile it because there is
	/// nothing to reconcile against.
	/// </para>
	/// <para>
	/// A null bot, or a build with no netcode installed, falls through to the counter -- which
	/// is what keeps single-player byte-for-byte unchanged.
	/// </para>
	/// </remarks>
	public void ClaimSeat(Actor bot)
	{
		// Asked even when there is no bot to name. The authority answers "is this vehicle mine
		// to account for", NOT "did the claim land" -- and on a replicated vehicle
		// seatsClaimedByBots is not the source of truth, so incrementing it here because a
		// caller happened to pass null would put the claim somewhere ClaimedSeatCount does not
		// read. The count would then under-report and the vehicle would keep offering seats it
		// does not have.
		if (NetVehicleAuthority.TryClaimSeat(base.gameObject, (bot != null) ? bot.gameObject : null))
		{
			return;
		}
		seatsClaimedByBots = Mathf.Min(seatsClaimedByBots + 1, seats.Length);
		drainClaimAction.Start();
	}

	/// <summary>Releases an anonymous claim. Offline only -- see <see cref="ClaimSeat()"/>.</summary>
	public void DropSeatClaim()
	{
		DropSeatClaim(null);
	}

	/// <summary>Releases the claim held by a named bot. V4-D10.</summary>
	public void DropSeatClaim(Actor bot)
	{
		// The mirror of ClaimSeat's reasoning: a fall-through here would DECREMENT a counter
		// nothing reads while the claims table keeps holding the claim.
		if (NetVehicleAuthority.TryDropSeatClaim(base.gameObject, (bot != null) ? bot.gameObject : null))
		{
			return;
		}
		seatsClaimedByBots = Mathf.Max(seatsClaimedByBots - 1, 0);
	}

	/// <summary>
	/// Live bot claims on this vehicle. <b>Computed at server role, never stored</b>
	/// (code-conventions.md "No Derived Fields").
	/// </summary>
	/// <remarks>
	/// <see cref="seatsClaimedByBots"/> remains the offline field and is the fallback whenever
	/// the netcode is not installed or this vehicle was never replicated. There is no second
	/// stored copy: at server role the field is simply not read.
	/// </remarks>
	public int ClaimedSeatCount
	{
		get
		{
			int authoritative = NetVehicleAuthority.ClaimCount(base.gameObject);
			return authoritative >= 0 ? authoritative : seatsClaimedByBots;
		}
	}

	public bool HasUnclaimedSeats()
	{
		return ClaimedSeatCount < seats.Length;
	}

	/// <summary>
	/// The two subtype-tail bytes this vehicle contributes to its snapshot entry
	/// (protocol-spec.md section 4.10).
	/// </summary>
	/// <remarks>
	/// <para>
	/// Virtual and zero by default, because the base <c>Vehicle</c> has neither a steering angle
	/// nor a rotor. <c>Car</c> and <c>Helicopter</c> override it; <c>Tank</c> and <c>Boat</c>
	/// carry no field the tail names, so their two bytes are honestly zero rather than a
	/// plausible-looking number derived from something else.
	/// </para>
	/// <para>
	/// <b>The encoding is not decided here.</b> <c>VehicleSubtypeTail</c> owns it, is engine-free,
	/// and has a test -- which matters most for the helicopter's rotor speed, a normalized u16
	/// split across two bytes whose byte order going backwards would produce a rotor that reads
	/// as spinning at some other entirely plausible speed.
	/// </para>
	/// </remarks>
	public virtual void ReadNetworkSubtypeTail(out byte subtypeA, out byte subtypeB)
	{
		subtypeA = 0;
		subtypeB = 0;
	}

	/// <summary>
	/// True when this vehicle's transform is written from the snapshot stream rather than
	/// simulated here. V5-D3.
	/// </summary>
	/// <remarks>
	/// Always false offline and on the server, so single-player and the authority behave
	/// exactly as they did before any of this existed.
	/// </remarks>
	public bool NetworkDriven { get; private set; }

	/// <summary>
	/// Hands this vehicle over to the replication layer, or takes it back.
	/// </summary>
	/// <remarks>
	/// <para>
	/// <b>The body goes kinematic, and that is the point (V5-D3).</b> A replicated vehicle whose
	/// <c>Rigidbody</c> is still dynamic runs local PhysX <i>against</i> the incoming snapshots:
	/// the solver pushes it one way, the next snapshot writes it back, and the result is jitter
	/// that looks exactly like a network problem and is not. Nothing above this layer can
	/// diagnose that, because every number on the wire is correct.
	/// </para>
	/// <para>
	/// <b>The drive path is disabled separately from the body</b> — each subtype's
	/// <c>FixedUpdate</c> returns early on <see cref="NetworkDriven"/> — because a kinematic body
	/// silently ignores <c>AddForce</c> but a <c>WheelCollider</c> does not, and a car whose
	/// wheels are still being torqued burns CPU steering a body it cannot move.
	/// </para>
	/// <para>
	/// Cosmetics that used to read local physics are driven from
	/// <see cref="ApplyReplicatedSubtypeTail"/> instead. That is why the snapshot entry carries
	/// a subtype tail at all.
	/// </para>
	/// </remarks>
	public void SetNetworkDriven(bool value)
	{
		if (NetworkDriven == value)
		{
			return;
		}

		NetworkDriven = value;

		if (rigidbody != null)
		{
			rigidbody.isKinematic = value;
		}
	}

	/// <summary>
	/// Writes the replicated cosmetic values a kinematic vehicle can no longer integrate for
	/// itself. The inverse of <see cref="ReadNetworkSubtypeTail"/>.
	/// </summary>
	/// <remarks>
	/// Base does nothing: a vehicle with no subtype tail has no cosmetic that depended on the
	/// simulation. Overridden where one did — <c>Car.steerAngle</c> and
	/// <c>Helicopter.rotorSpeed</c>, the two design section 5 reserved the tail for.
	/// </remarks>
	public virtual void ApplyReplicatedSubtypeTail(byte subtypeA, byte subtypeB)
	{
	}

	/// <summary>
	/// Writes the replicated state flags a kinematic vehicle can no longer sense for itself.
	/// </summary>
	/// <remarks>
	/// <c>Boat.inWater</c> is the one that matters — it is set from a buoyancy sample the remote
	/// path no longer takes, and the engine note and wake read it. <c>Helicopter.isAirborne</c>
	/// comes from a downward raycast, which a client can still afford to do locally against its
	/// own copy of the map; it is left alone rather than replicated for the sake of it.
	/// </remarks>
	public virtual void ApplyReplicatedFlags(bool inWater, bool airborne)
	{
	}

	public void OccupantLeft(Seat seat, Actor leaver)
	{
		if (seat == seats[0])
		{
			DriverExited();
		}
		if (!leaver.aiControlled)
		{
			claimedByPlayer = false;
			if (fireAlarm != null)
			{
				fireAlarm.Stop();
			}
		}
		if (IsEmpty())
		{
			// Cancel before scheduling. InvokeRepeating STACKS, and Repair used to arm this
			// unconditionally -- including on an occupied vehicle -- so an enter/repair/leave
			// cycle left two pending decays, and repeating the cycle left more.
			CancelInvoke("AutoDamage");
			InvokeRepeating("AutoDamage", AUTO_DAMAGE_START_TIME, AUTO_DAMAGE_PERIOD);
			ownerTeam = -1;
		}
	}

	private void AutoDamage()
	{
		Damage(maxHealth * AUTO_DAMAGE_PERCENT);
	}

	protected virtual void DriverEntered()
	{
		if (!reportedFirstDriver)
		{
			// spawner is only set by SetSpawner, which only VehicleSpawner.SpawnCoroutine
			// calls -- so any vehicle placed directly in a scene NREd here the first time a
			// driver entered it. reportedFirstDriver still latches either way.
			if (spawner != null)
			{
				spawner.FirstDriverEntered(this);
			}
			reportedFirstDriver = true;
		}
	}

	protected virtual void DriverExited()
	{
	}

	public float Health
	{
		get { return health; }
	}

	public float MaxHealth
	{
		get { return maxHealth; }
	}

	/// <summary>Who last damaged this vehicle, or <see cref="NoAttacker"/>.</summary>
	public int LastDamagedBy
	{
		get { return _lastDamagedBy; }
	}

	public void Damage(float amount)
	{
		Damage(amount, NoAttacker);
	}

	/// <summary>
	/// Damage with an attacker. V0 opens the parameter; V1 is what threads a real id into it
	/// from <c>ActorManager.Explode</c>, which is the only existing caller that has one.
	/// </summary>
	/// <remarks>
	/// <para>
	/// <b>The role guard, V4 task 5.</b> This is the choke point every vehicle damage source in
	/// the game already passes through -- the ram check, <c>AutoDamage</c>, explosions, bullets
	/// -- which is why the guard is here and not replicated across each of them.
	/// </para>
	/// <para>
	/// <b>Server:</b> the health change is routed through <c>ServerVehicleDamageSink</c>, which
	/// writes back through <see cref="SetHealthAuthoritative"/> and therefore runs this file's
	/// one health ladder exactly once. It does NOT fall through afterwards -- doing so would
	/// subtract the same damage a second time.
	/// </para>
	/// <para>
	/// <b>Client:</b> the local screenshake runs and nothing else. Health, burning and death all
	/// arrive from the server -- the snapshot's health byte and Burning flag, and
	/// <c>S_VEHICLE_DESPAWN</c> -- so a client that subtracted health here would render a wreck
	/// the server does not have, and would then be corrected in a visible jump.
	/// </para>
	/// <para>
	/// <b>Offline:</b> literally unchanged. <c>NetVehicleAuthority</c> is uninstalled, so
	/// <c>TryApplyDamage</c> is false and <c>IsClientSuppressed</c> is false, and the line below
	/// is the one this method shipped with. That is acceptance criterion 12, and it is a
	/// property of there being one branch rather than a role switch with an offline case
	/// somebody could get wrong.
	/// </para>
	/// </remarks>
	public void Damage(float amount, int attackerActorId)
	{
		if (NetVehicleAuthority.TryApplyDamage(base.gameObject, amount, attackerActorId))
		{
			return;
		}
		if (NetVehicleAuthority.IsClientSuppressed)
		{
			if (amount > HEAVY_DAMAGE_THRESHOLD)
			{
				HeavyDamage();
			}
			return;
		}
		ApplyHealth(health - amount, amount, attackerActorId);
	}

	/// <summary>
	/// Server/replication entry point: overwrite health outright. V4 is its only caller.
	/// </summary>
	/// <remarks>
	/// Passes <c>appliedDamage = 0</c> deliberately. <see cref="HeavyDamage"/> is a local
	/// screenshake keyed off a damage magnitude, and a corrective snapshot is not a hit --
	/// firing it on one would make a client shake every time the server nudged its HP.
	/// </remarks>
	public void SetHealthAuthoritative(float value)
	{
		ApplyHealth(Mathf.Clamp(value, 0f, maxHealth), 0f, NoAttacker);
	}

	/// <summary>
	/// THE only place <c>health</c> is written, and the only place the burning and particle
	/// ladder runs.
	/// </summary>
	/// <remarks>
	/// Two write paths each running their own ladder is the derived-state divergence
	/// development-principles.md forbids, and is the same shape phase-05 already removed once
	/// from NetServerActor.Health. The ladder below is the shipped one, moved rather than
	/// rewritten -- with one deliberate change: the particle call is edge-triggered, so a
	/// Repair that lifts the vehicle back over half health stops the smoke, which the shipped
	/// Repair did separately and Damage never did at all.
	/// </remarks>
	private void ApplyHealth(float newHealth, float appliedDamage, int attackerActorId)
	{
		health = Mathf.Clamp(newHealth, 0f, maxHealth);
		// Written on any DAMAGE, including unattributed damage, which clears it back to
		// NoAttacker. Writing only when an attacker is known would leave the field naming a
		// player who chipped the paint minutes before decay or a collision actually killed the
		// vehicle -- and V4's death event reads it.
		//
		// A repair, a snapshot correction and the Awake seed all pass appliedDamage = 0 and
		// leave it alone: none of them is a hit, in either direction.
		if (appliedDamage > 0f)
		{
			_lastDamagedBy = attackerActorId;
		}
		if (appliedDamage > HEAVY_DAMAGE_THRESHOLD)
		{
			HeavyDamage();
		}
		if (health <= 0f && !dead && !burning)
		{
			StartBurning();
		}
		bool showDamage = health < 0.5f * maxHealth;
		if (showDamage != damageParticlesOn)
		{
			damageParticlesOn = showDamage;
			// Null on a dedicated server, which strips particle systems.
			if (damageParticles != null)
			{
				if (showDamage)
				{
					damageParticles.Play();
				}
				else
				{
					damageParticles.Stop();
				}
			}
		}
	}

	protected virtual void StartBurning()
	{
		burning = true;
		stopBurningRepairs = 3;
		if (burnParticles != null)
		{
			burnParticles.Play();
		}
		if (fireAlarm != null && claimedByPlayer)
		{
			fireAlarm.Play();
		}
	}

	private void StopBurning()
	{
		burning = false;
		if (burnParticles != null)
		{
			burnParticles.Stop();
		}
		if (fireAlarm != null)
		{
			fireAlarm.Stop();
		}
	}

	public bool Repair(float amount)
	{
		if (dead)
		{
			return false;
		}
		if (burning)
		{
			stopBurningRepairs--;
			if (stopBurningRepairs == 0)
			{
				StopBurning();
				// The server's burn clock counts the SAME burn in ticks and knows nothing about
				// stopBurningRepairs. Without this it kept its countdown armed and despawned a
				// repaired, drivable, possibly occupied vehicle on schedule -- telling every
				// client it was gone while the GameObject stayed solid in the world.
				NetVehicleAuthority.ExtinguishBurn(base.gameObject);
			}
		}
		bool result = health < maxHealth;
		// Repair is a health write and needs the guard Damage has. Without it the scene's health
		// rose while the authoritative VehicleState.Health stayed where the last hit left it: the
		// snapshot kept shipping the stale byte, and the next ApplyDamage subtracted from the
		// stale value, so one more hit killed a fully repaired vehicle.
		//
		// The sink writes BOTH copies -- the record and, through SetHealthAuthoritative, the
		// scene -- exactly as the damage path does, so there is one writer and one ladder run.
		// Offline and on a client TryApplyRepair is false and the original line runs unchanged.
		if (!NetVehicleAuthority.TryApplyRepair(base.gameObject, amount))
		{
			ApplyHealth(Mathf.Min(health + amount, maxHealth), 0f, NoAttacker);
		}
		CancelInvoke("AutoDamage");
		// Only re-arm on an EMPTY vehicle. AutoDamage decays abandoned vehicles --
		// OccupantEntered cancels it and OccupantLeft schedules it only when empty -- and
		// Repair used to ignore both conditions, arming a second repeating invoke that
		// OccupantLeft then never cancelled. On a server where bots enter, repair and leave
		// continuously, the stacking is unbounded.
		if (IsEmpty())
		{
			InvokeRepeating("AutoDamage", AUTO_DAMAGE_START_TIME, AUTO_DAMAGE_PERIOD);
		}
		return result;
	}

	public virtual void Die()
	{
		dead = true;
		if (fireAlarm != null)
		{
			fireAlarm.Stop();
		}
		if (spawner != null)
		{
			spawner.VehicleDied(this);
		}
		ActorManager.DropVehicle(this);
		Seat[] array = seats;
		foreach (Seat seat in array)
		{
			if (seat.IsOccupied())
			{
				Actor occupant = seat.occupant;
				occupant.LeaveSeat();
				if (seat.enclosed)
				{
					occupant.Damage(200f, 200f, true, base.transform.position, Vector3.forward, Vector3.up * 10f);
				}
				else
				{
					occupant.Damage(0f, 200f, true, base.transform.position, Vector3.forward, Vector3.up * 10f);
				}
			}
			seat.gameObject.SetActive(false);
		}
		rigidbody.WakeUp();
		base.enabled = false;
		Invoke("Cleanup", 15f);
		Invoke("Explode", 0.3f);
	}

	private void OnCollisionEnter(Collision c)
	{
		float num = Mathf.Abs(Vector3.Dot(c.relativeVelocity, c.contacts[0].normal));
		if (crashDamageCooldown.TrueDone() && num > crashDamageSpeedThrehshold && c.collider.gameObject.layer != 8 && c.collider.gameObject.layer != 10)
		{
			float amount = (num - crashDamageSpeedThrehshold) * crashDamageMultiplier;
			Damage(amount);
			crashDamageCooldown.Start();
			// Cosmetic. The crash damage above is gameplay and stays outside the guard.
			if (impactAudio != null)
			{
				impactAudio.transform.position = c.contacts[0].point;
				impactAudio.pitch *= UnityEngine.Random.Range(0.9f, 1.1f);
				impactAudio.Play();
			}
			// Same guard, same reason. At server role ServerVehicleDamageSink already routed the
			// crash through VehicleBurnClock.KillImmediately, so letting the scene ALSO kill here
			// would announce the despawn from two places on two different clocks.
			if (burning && crashSkipsBurn && !dead && !NetVehicleAuthority.ServerOwnsVehicleDeath)
			{
				Die();
			}
		}
	}

	/// <summary>
	/// The wreck goes off: an impulse that throws it, and a blast that hurts what is near it.
	/// </summary>
	/// <remarks>
	/// <para>
	/// <b>debt-closure phase 2 task 2f closes ledger C-10.</b> V1-D5 handed "should a wreck do
	/// blast damage" to V4 as a gameplay decision and V4 did not take it, so
	/// <c>ExplosionKind.Vehicle</c> shipped with zero producers — declared on the wire, mapped by
	/// the client's effect table, and emitted by nothing. The decision is taken here: <b>a wreck
	/// damages.</b> Taking cover behind a burning vehicle is now dangerous, which is the intended
	/// consequence and the balance note this change owes.
	/// </para>
	/// <para>
	/// <b>Unguarded, exactly like <c>ExplodingProjectile.Explode</c>.</b>
	/// <c>ActorManager.Explode</c> owns the three-way role split at its own choke point: offline
	/// unchanged, the server deciding and announcing <c>S_EXPLOSION</c>, and a client applying no
	/// health damage while keeping the corpse ragdoll impulse (AD-4). A second role guard here
	/// would be a second copy of that rule.
	/// </para>
	/// <para>
	/// <b>Not a chain-detonation hazard.</b> This runs from <c>Invoke("Explode", 0.3f)</c> in
	/// <see cref="Die"/> — a later frame on a fresh stack — so it never re-enters an
	/// <c>ActorManager.Explode</c> that is still running. A wreck that kills a neighbour makes
	/// that neighbour explode 0.3 s later, which is a sequence rather than a recursion.
	/// </para>
	/// </remarks>
	/// <summary>
	/// This wreck's blast. Optional: unassigned falls back to the kind's defaults.
	/// </summary>
	/// <remarks>debt-closure phase 2 task 2f, ledger C-10. See <c>WreckExplosion</c>.</remarks>
	public ExplodingProjectile.ExplosionConfiguration wreckExplosion;

	protected virtual void Explode()
	{
		// The impulse is gameplay -- it is what throws the wreck -- so it runs unguarded. Only
		// the three cosmetic calls below it are optional on a stripped headless build.
		rigidbody.WakeUp();
		rigidbody.AddForce((UnityEngine.Random.insideUnitSphere + Vector3.up) * 2000f, ForceMode.Impulse);
		rigidbody.AddTorque(UnityEngine.Random.insideUnitSphere * 500f, ForceMode.Impulse);
		if (deathParticles != null)
		{
			deathParticles.Play();
		}
		if (audio != null)
		{
			audio.Stop();
			audio.pitch = 1f;
			audio.volume = 1f;
		}
		if (explosionSound != null)
		{
			explosionSound.Play();
		}
		// Last, after the impulse and the cosmetics: ActorManager.Explode can kill actors and
		// other vehicles, and running it first would mean a wreck whose own throw and particles
		// depended on what its blast happened to reach.
		ActorManager.Explode(
			base.transform.position, WreckExplosion(), null,
			Ironfront.Net.Protocol.ExplosionKind.Vehicle);
	}

	/// <summary>
	/// The wreck's blast, from <see cref="wreckExplosion"/> or from this kind's defaults.
	/// </summary>
	/// <remarks>
	/// <b>Defaults are built in code, and that is deliberate rather than lazy.</b> Every vehicle
	/// prefab in the game predates this field, phase 2 authors no prefabs (they are Phase 1's),
	/// and an unauthored <c>ExplosionConfiguration</c> has null <c>AnimationCurve</c>s — so
	/// reading it straight would throw a NullReferenceException inside every wreck. The curves
	/// run 1 at the centre to 0 at the edge, because <c>ExplosionRanges</c> hands out
	/// <c>t = distance / range</c>. Author <see cref="wreckExplosion"/> per prefab to tune it.
	/// </remarks>
	private ExplodingProjectile.ExplosionConfiguration WreckExplosion()
	{
		if (wreckExplosion == null)
		{
			wreckExplosion = new ExplodingProjectile.ExplosionConfiguration();
		}
		if (wreckExplosion.damageFalloff == null || wreckExplosion.damageFalloff.length == 0)
		{
			wreckExplosion.damageFalloff = AnimationCurve.Linear(0f, 1f, 1f, 0f);
		}
		if (wreckExplosion.balanceFalloff == null || wreckExplosion.balanceFalloff.length == 0)
		{
			wreckExplosion.balanceFalloff = AnimationCurve.Linear(0f, 1f, 1f, 0f);
		}
		return wreckExplosion;
	}

	private void Cleanup()
	{
		UnityEngine.Object.Destroy(base.gameObject);
	}

	public Vector3 Velocity()
	{
		return rigidbody.linearVelocity;
	}

	public Vector3 LocalVelocity()
	{
		return base.transform.worldToLocalMatrix.MultiplyVector(Velocity());
	}

	public void SetSpawner(VehicleSpawner spawner)
	{
		this.spawner = spawner;
	}

	// Both re-implemented over VehicleInputClamp.Axis so all four vehicles share ONE
	// validation boundary. Mathf.Clamp(float.NaN, -1f, 1f) returns NaN -- both comparisons
	// inside it are false -- so the shipped versions were range limiters a hostile client
	// walked straight through, propagating NaN into Rigidbody.AddForce and removing the
	// vehicle from the PhysX simulation entirely.
	protected static Vector2 Clamp2(Vector2 v)
	{
		return new Vector2(VehicleInputClamp.Axis(v.x), VehicleInputClamp.Axis(v.y));
	}

	protected static Vector4 Clamp4(Vector4 v)
	{
		return new Vector4(VehicleInputClamp.Axis(v.x), VehicleInputClamp.Axis(v.y), VehicleInputClamp.Axis(v.z), VehicleInputClamp.Axis(v.w));
	}

	public Seat GetEmptySeat()
	{
		Seat[] array = seats;
		foreach (Seat seat in array)
		{
			if (!seat.IsOccupied())
			{
				return seat;
			}
		}
		return null;
	}

	public int EmptySeats()
	{
		int num = 0;
		Seat[] array = seats;
		foreach (Seat seat in array)
		{
			if (!seat.IsOccupied())
			{
				num++;
			}
		}
		return num;
	}

	public bool IsFull()
	{
		Seat[] array = seats;
		foreach (Seat seat in array)
		{
			if (!seat.IsOccupied())
			{
				return false;
			}
		}
		return true;
	}

	public bool IsEmpty()
	{
		Seat[] array = seats;
		foreach (Seat seat in array)
		{
			if (seat.IsOccupied())
			{
				return false;
			}
		}
		return true;
	}

	public bool HasBlockSensor()
	{
		return blockSensor != null;
	}

	public int BlockTest(Collider[] outColliders, float extrapolationTime, int mask)
	{
		float num = Mathf.Max(0.1f, LocalVelocity().z * extrapolationTime);
		Vector3 v = blockSensorOrigin;
		v.z += num / 2f;
		Vector3 localScale = blockSensor.localScale;
		localScale.z = num;
		Vector3 vector = base.transform.localToWorldMatrix.MultiplyPoint(v);
		blockSensor.transform.position = vector;
		blockSensor.transform.localScale = localScale;
		return Physics.OverlapBoxNonAlloc(vector, localScale / 2f, outColliders, blockSensor.rotation, mask);
	}

	public bool CoarseLineOverlap(Vector3 origin, Vector3 target, float lineRadius = 0f)
	{
		Vector3 point = SMath.LineSegmentVsPointClosest(origin, target, base.transform.position);
		return IsCoarseOverlapping(point, lineRadius);
	}

	public bool IsCoarseOverlapping(Vector3 point, float lineRadius = 0f)
	{
		return Vector3.Distance(base.transform.position, point) < avoidanceCoarseRadius + lineRadius;
	}

	/// <summary>
	/// Leaves ActorManager's vehicle register on the way out. Ledger <b>X-49</b>.
	/// </summary>
	/// <remarks>
	/// <c>DropVehicle</c> was already called from <c>Die</c>, so a vehicle that BURNED left the
	/// list — and a vehicle that was destroyed without dying did not. See <c>Actor.OnDestroy</c>
	/// for the full account; this is the same defect one register over, and it is what put a
	/// destroyed vehicle in front of <see cref="IsStill"/>'s <c>rigidbody</c> read below.
	/// </remarks>
	private void OnDestroy()
	{
		ActorManager.DropVehicle(this);
	}

	public bool IsStill()
	{
		return rigidbody.linearVelocity.magnitude < 0.2f;
	}

	public virtual bool ShouldBeAvoided()
	{
		return IsStill();
	}

	public float GetHealthRatio()
	{
		return health / maxHealth;
	}

	protected virtual void HeavyDamage()
	{
		// claimedByPlayer is false on a server, so this is guarded already -- by a flag that
		// means "a player is in it", not "a player exists". Say what is actually required.
		if (heavyDamageAudio != null && claimedByPlayer && FpsActorController.instance != null)
		{
			heavyDamageAudio.Play();
			FpsActorController.instance.Deafen();
			FpsActorController.instance.fpParent.ApplyScreenshake(20f, 3);
		}
	}

	public bool AiShouldEnter()
	{
		return !stuck && !IsFull() && !burning && !dead && HasUnclaimedSeats() && takingFireAction.TrueDone() && !WaterLevel.InWater(base.transform.position);
	}

	private void OnGUI()
	{
		// instance was dereferenced BEFORE the Camera.main guard on the same line, so a
		// headless build with no ActorManager NREd here every OnGUI.
		if (ActorManager.instance != null && ActorManager.instance.debug && !dead && Camera.main != null)
		{
			float num = Vector3.Dot(base.transform.position - Camera.main.transform.position, Camera.main.transform.forward);
			if (num > 1f && num < 100f)
			{
				Vector3 vector = Camera.main.WorldToScreenPoint(base.transform.position + Vector3.up * ramSize.y * 4f);
				GUI.skin.label.alignment = TextAnchor.UpperCenter;
				GUI.Label(new Rect(vector.x - 100f, (float)Screen.height - vector.y, 200f, 50f), "AI Claimed seats: " + ClaimedSeatCount + "/" + seats.Length);
				GUI.Label(new Rect(vector.x - 100f, (float)Screen.height - vector.y + 20f, 200f, 50f), "Stuck: " + stuck);
				GUI.Label(new Rect(vector.x - 100f, (float)Screen.height - vector.y + 40f, 200f, 50f), "Taking fire: " + !takingFireAction.TrueDone());
				GUI.Label(new Rect(vector.x - 100f, (float)Screen.height - vector.y + 60f, 200f, 50f), "AI should enter? " + AiShouldEnter());
			}
		}
	}
}
