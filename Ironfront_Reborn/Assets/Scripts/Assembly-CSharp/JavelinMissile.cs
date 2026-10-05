using System;
using UnityEngine;

/// <summary>
/// The launcher's guided missile: it leaves the tube, picks a clear way out past anything in front
/// of it, then chases what it was locked onto until it hits it.
/// </summary>
/// <remarks>
/// <para>
/// <b>Rewritten 2026-10-06 (owner, v4.3.0 playtest).</b> The shipped flight was the original
/// game's top attack: climb toward 200 m, dive once within 50 m of the target on the ground, and
/// give up into a ballistic fall a second into a dive that was opening rather than closing. Against
/// anything that moved it arrived where the target had been and fell in behind it, and a target
/// that turned hard made it quit. The owner asked for a missile that is "smart about its path",
/// finds a way around obstacles as it leaves the launcher, and then chases the target to the end,
/// where hitting an obstacle mid-chase is acceptable (a dodge behind a wall should work).
/// </para>
/// <para>
/// <b>Lead pursuit, turn-rate limited.</b> Each step it points at where the target will be after
/// the flight time that remains (capped at <see cref="maxLeadSeconds"/>), turning at most
/// <see cref="turnAcceleration"/> / speed radians a second. There is no loft and no giving up.
/// A locked soldier is struck by a proximity fuse (<see cref="proximityFuse"/>) around the chest,
/// because a body is thin and a near pass is a kill with this warhead; a vehicle is struck on
/// impact, which is what delivers the direct-hit damage.
/// </para>
/// <para>
/// <b>Server-owned online.</b> A client never flies its own copy (see
/// <c>ProjectileNetAnnouncer.IsServerDrawn</c>): it draws the server's missile from the 5 Hz
/// re-announcements and, between them, keeps turning at the rate the last two implied, so the
/// drawn missile curves with the real one rather than jumping every 200 ms.
/// </para>
/// </remarks>
public class JavelinMissile : Rocket
{
	/// <summary>Seconds the missile coasts out of the tube before its motor lights.</summary>
	private const float IGNITION_DELAY = 0.5f;

	/// <summary>Turns a near-zero velocity into a heading without dividing by it.</summary>
	private const float MIN_TURN_SPEED = 10f;

	/// <summary>Level geometry and vehicle hulls: what the way out of the tube has to clear.</summary>
	private const int OBSTACLE_MASK = (1 << 0) | (1 << 12);

	/// <summary>Degrees off the line to the target tried, in order, when that line is blocked.</summary>
	private static readonly float[] AvoidAngles = { 15f, 30f, 45f, 60f, 75f, 90f };

	public float ejectSpeed = 10f;

	/// <summary>Where a missile with no live target flies to: a marked point, or a lost target's last place.</summary>
	[NonSerialized]
	public Vector3 targetPoint;

	/// <summary>
	/// What the missile is chasing. Written by the launcher, and on a network that launcher is
	/// the server -- which enemy is locked is a gameplay decision. A client never learns the
	/// target, because the re-parameterization already carries the consequence: the velocity
	/// vector. V7-D6.
	/// </summary>
	[NonSerialized]
	public Transform target;

	/// <summary>The soldier the lock is on, when it is one; null for a vehicle or a marked point.</summary>
	[NonSerialized]
	public Actor targetActor;

	/// <summary>The vehicle the lock is on, when it is one.</summary>
	[NonSerialized]
	public Vehicle targetVehicle;

	/// <summary>Direct-hit damage on anything that is not a locked vehicle.</summary>
	public float damage = 800f;

	/// <summary>
	/// Direct-hit damage on the vehicle the lock is on. The original game paid this for a hit out
	/// of its top-attack dive; the dive is gone, and this keeps a locked hit on a tank as deadly
	/// as a dive that connected used to be.
	/// </summary>
	public float divingDamage = 1500f;

	public AudioClip flightSound;

	/// <summary>Sideways acceleration available for turning, m/s^2.</summary>
	public float turnAcceleration = 400f;

	/// <summary>How fast the motor brings the missile up to <c>configuration.speed</c>, m/s^2.</summary>
	public float boost = 150f;

	/// <summary>Seconds after ignition during which it steers around obstacles ahead.</summary>
	public float avoidSeconds = 2f;

	/// <summary>Metres ahead it looks for an obstacle while avoiding.</summary>
	public float avoidLookahead = 40f;

	/// <summary>Radius of the swept test, metres: the body and a little room.</summary>
	public float avoidRadius = 0.6f;

	/// <summary>Metres from a locked soldier's chest at which the warhead goes off.</summary>
	public float proximityFuse = 2f;

	/// <summary>The longest lead taken on a moving target, seconds.</summary>
	public float maxLeadSeconds = 2f;

	private bool thrustEnabled;

	private float ignitedAt;

	private Action thrustStartAction = new Action(IGNITION_DELAY);

	private Rigidbody targetBody;

	// Presented copy only: the last velocity the server announced, when, and the turn it implies.
	private Vector3 lastNetVelocity;

	private float lastNetTime = -1f;

	private Vector3 netTurnAxis;

	private float netTurnDegreesPerSecond;

	/// <summary>Locks the missile onto a soldier or a vehicle; anything else leaves it on <see cref="targetPoint"/>.</summary>
	public void Lock(Component locked)
	{
		targetActor = locked as Actor;
		targetVehicle = locked as Vehicle;
		target = locked != null && (targetActor != null || targetVehicle != null) ? locked.transform : null;
		targetBody = targetVehicle != null ? targetVehicle.GetComponent<Rigidbody>() : null;
		if (target != null)
		{
			targetPoint = AimPoint(out _);
		}
	}

	protected override void Start()
	{
		// A missile a CLIENT presents has no source -- NetClientProjectilePresenter leaves it null
		// on purpose -- and its velocity is the server's, applied before this runs. Keep what the
		// presenter applied rather than inventing a launch the server did not make.
		Vector3 presented = velocity;
		// A motor, not a shell: the missile never drops, on the server or in a client's drawing,
		// which is what lets a presented copy coast straight between re-announcements. Set here
		// rather than on the prefab, whose asset predates Unity's current prefab format and is
		// rewritten wholesale by any save.
		configuration.straightDistance = float.MaxValue;
		base.Start();
		velocity = source != null
			? base.transform.forward * ejectSpeed + source.Velocity() * 0.9f
			: presented;
		thrustStartAction.Start();
		light.enabled = false;
		// A dedicated server strips particle systems and audio (see Vehicle.cs and Weapon.Start),
		// which ExplodingProjectile already allows for; the flight's server half is the guidance
		// below, and the trail and the sound are only ever for somebody watching.
		if (trailParticles != null)
		{
			trailParticles.Stop(true);
		}
	}

	private void IgniteWhenDue()
	{
		if (thrustEnabled || !thrustStartAction.TrueDone())
		{
			return;
		}
		light.enabled = true;
		if (trailParticles != null)
		{
			trailParticles.Play(true);
		}
		thrustEnabled = true;
		ignitedAt = Time.time;
		if (audioSource != null)
		{
			audioSource.PlayOneShot(flightSound);
		}
	}

	protected override void Update()
	{
		// V7-D6: the SERVER owns a guided flight and re-sends it at 5 Hz. Only a PRESENTED copy --
		// no source, a target this client was never told -- gets here, and it keeps turning at the
		// rate the last two announcements implied until the next one re-seats it.
		if (Ironfront.Net.Unity.NetContext.IsClient && source == null)
		{
			IgniteWhenDue();
			ExtrapolateTurn();
			base.Update();
			return;
		}

		if (thrustStartAction.TrueDone())
		{
			IgniteWhenDue();
			if (Guide())
			{
				return;
			}
		}
		base.Update();
	}

	/// <summary>One step of guidance. True when the proximity fuse fired and the flight is over.</summary>
	private bool Guide()
	{
		Vector3 position = base.transform.position;
		Vector3 aim = AimPoint(out Vector3 aimVelocity);
		Vector3 toAim = aim - position;
		float distance = toAim.magnitude;

		if (targetActor != null && distance <= proximityFuse)
		{
			light.enabled = false;
			Explode(position, -velocity.normalized);
			return true;
		}

		float speed = Mathf.MoveTowards(velocity.magnitude, configuration.speed, boost * Time.deltaTime);
		Vector3 desired = LeadHeading(position, speed, aim, aimVelocity, maxLeadSeconds, base.transform.forward);

		if (Time.time - ignitedAt < avoidSeconds)
		{
			float reach = Mathf.Min(avoidLookahead, distance - avoidRadius * 2f);
			Transform ownVehicle = source != null && source.IsSeated() ? source.seat.vehicle.transform : null;
			desired = ClearHeading(position, desired, reach, avoidRadius, target != null ? target.root : null, ownVehicle);
		}

		velocity = Turn(velocity, desired, speed, turnAcceleration, Time.deltaTime, base.transform.forward);

		// Read only by Damage(), and V7-D3 puts damage entirely on the server, so a client's copy
		// of this number is never consulted.
		configuration.damage = targetVehicle != null ? divingDamage : damage;
		base.transform.rotation = Quaternion.LookRotation(velocity);
		return false;
	}

	/// <summary>
	/// Where the missile is going and how fast that place moves: a live soldier's chest, a live
	/// vehicle's centre of mass, or the last such point once the target is gone.
	/// </summary>
	private Vector3 AimPoint(out Vector3 aimVelocity)
	{
		aimVelocity = Vector3.zero;
		if (targetActor != null)
		{
			if (targetActor.dead || !targetActor.gameObject.activeInHierarchy)
			{
				ForgetTarget();
				return targetPoint;
			}
			aimVelocity = targetActor.Velocity();
			targetPoint = targetActor.Position() + Vector3.up * Javelin.ChestHeight;
			return targetPoint;
		}
		if (targetVehicle != null)
		{
			if (targetVehicle.dead)
			{
				ForgetTarget();
				return targetPoint;
			}
			aimVelocity = targetVehicle.Velocity();
			targetPoint = targetBody != null ? targetBody.worldCenterOfMass : targetVehicle.transform.position;
			return targetPoint;
		}
		return targetPoint;
	}

	private void ForgetTarget()
	{
		targetActor = null;
		targetVehicle = null;
		targetBody = null;
		target = null;
	}

	/// <summary>
	/// The heading that meets <paramref name="aim"/>, moving at <paramref name="aimVelocity"/>, after
	/// the flight time left at <paramref name="speed"/> (at most <paramref name="maxLeadSeconds"/>).
	/// </summary>
	public static Vector3 LeadHeading(Vector3 position, float speed, Vector3 aim, Vector3 aimVelocity, float maxLeadSeconds, Vector3 fallback)
	{
		float lead = Mathf.Min(Vector3.Distance(aim, position) / Mathf.Max(speed, MIN_TURN_SPEED), maxLeadSeconds);
		Vector3 desired = aim + aimVelocity * lead - position;
		return desired.sqrMagnitude > 1e-6f ? desired.normalized : fallback;
	}

	/// <summary>
	/// <paramref name="velocity"/> turned toward <paramref name="desired"/> by what
	/// <paramref name="turnAcceleration"/> allows in <paramref name="dt"/>, at <paramref name="speed"/>.
	/// </summary>
	public static Vector3 Turn(Vector3 velocity, Vector3 desired, float speed, float turnAcceleration, float dt, Vector3 fallback)
	{
		Vector3 heading = velocity.sqrMagnitude > 1e-4f ? velocity.normalized : fallback;
		float maxTurn = turnAcceleration / Mathf.Max(speed, MIN_TURN_SPEED) * dt;
		return Vector3.RotateTowards(heading, desired, maxTurn, 0f) * speed;
	}

	/// <summary>
	/// <paramref name="desired"/> if it is clear for <paramref name="reach"/> metres, else the
	/// nearest clear heading off it: up first, then left and right, widening by
	/// <see cref="AvoidAngles"/>; straight up when nothing is clear. Hits on
	/// <paramref name="ignoreA"/> or <paramref name="ignoreB"/> (the target, the launcher's own
	/// vehicle) do not count.
	/// </summary>
	public static Vector3 ClearHeading(Vector3 position, Vector3 desired, float reach, float radius, Transform ignoreA, Transform ignoreB)
	{
		if (reach <= radius || IsClear(position, desired, reach, radius, ignoreA, ignoreB))
		{
			return desired;
		}
		Vector3 right = Vector3.Cross(Vector3.up, desired);
		right = right.sqrMagnitude > 1e-4f ? right.normalized : Vector3.right;
		for (int i = 0; i < AvoidAngles.Length; i++)
		{
			float angle = AvoidAngles[i];
			Vector3 up = Quaternion.AngleAxis(0f - angle, right) * desired;
			if (IsClear(position, up, reach, radius, ignoreA, ignoreB))
			{
				return up;
			}
			Vector3 left = Quaternion.AngleAxis(0f - angle, Vector3.up) * desired;
			if (IsClear(position, left, reach, radius, ignoreA, ignoreB))
			{
				return left;
			}
			Vector3 rightward = Quaternion.AngleAxis(angle, Vector3.up) * desired;
			if (IsClear(position, rightward, reach, radius, ignoreA, ignoreB))
			{
				return rightward;
			}
		}
		return Vector3.up;
	}

	private static bool IsClear(Vector3 position, Vector3 direction, float reach, float radius, Transform ignoreA, Transform ignoreB)
	{
		if (!Physics.SphereCast(position, radius, direction, out RaycastHit hit, reach, OBSTACLE_MASK, QueryTriggerInteraction.Ignore))
		{
			return true;
		}
		Transform struck = hit.transform;
		return (ignoreA != null && struck.IsChildOf(ignoreA)) || (ignoreB != null && struck.IsChildOf(ignoreB));
	}

	protected override void OnNetVelocity(Vector3 announced)
	{
		float now = Time.time;
		float interval = now - lastNetTime;
		if (lastNetTime >= 0f && interval > 0.05f && interval < 1f
			&& lastNetVelocity.sqrMagnitude > 1f && announced.sqrMagnitude > 1f)
		{
			float maxDegrees = turnAcceleration / Mathf.Max(announced.magnitude, MIN_TURN_SPEED) * Mathf.Rad2Deg;
			netTurnDegreesPerSecond = Mathf.Min(Vector3.Angle(lastNetVelocity, announced) / interval, maxDegrees);
			netTurnAxis = Vector3.Cross(lastNetVelocity, announced);
		}
		lastNetVelocity = announced;
		lastNetTime = now;
	}

	private void ExtrapolateTurn()
	{
		if (!thrustEnabled || netTurnDegreesPerSecond <= 0f || netTurnAxis.sqrMagnitude < 1e-6f)
		{
			return;
		}
		velocity = Quaternion.AngleAxis(netTurnDegreesPerSecond * Time.deltaTime, netTurnAxis.normalized) * velocity;
		if (velocity.sqrMagnitude > 1e-4f)
		{
			base.transform.rotation = Quaternion.LookRotation(velocity);
		}
	}
}
