using System;
using Ironfront.Net.Replication.Vehicles;
using Ironfront.Net.Unity.Server;
using UnityEngine;

public class Vehicle : MonoBehaviour
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
		if (burning && !dead)
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
		// Per-claim expiry at server role, per-vehicle drain offline. The shipped drain takes
		// one claim off an anonymous pile every ten seconds, which is exactly why the count
		// cannot be trusted (V4-D10); with identities the deadline belongs to the claim.
		NetVehicleAuthority.ReleaseExpiredClaims();
		if (drainClaimAction.TrueDone() && seatsClaimedByBots > 0)
		{
			DropSeatClaim();
			drainClaimAction.Start();
		}
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
			}
		}
		bool result = health < maxHealth;
		ApplyHealth(Mathf.Min(health + amount, maxHealth), 0f, NoAttacker);
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
			if (burning && crashSkipsBurn && !dead)
			{
				Die();
			}
		}
	}

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
