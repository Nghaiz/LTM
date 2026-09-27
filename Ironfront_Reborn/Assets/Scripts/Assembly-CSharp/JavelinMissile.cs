using System;
using UnityEngine;

public class JavelinMissile : Rocket
{
	private const float TURN_SPEED = 300f;

	private const float TARGET_ALTITUDE = 200f;

	private const float ACCELERATION = 110f;

	private const float ACCURATE_ACCELERATION = 160f;

	private const float DIVE_DISTANCE = 50f;

	private const float VELOCITY_COMPENSATION = 0.3f;

	public float ejectSpeed = 10f;

	[NonSerialized]
	public Vector3 targetPoint;

	/// <summary>
	/// What the missile is chasing. Written by the launcher, and on a network that launcher is
	/// the server -- which enemy is locked is a gameplay decision. A client never learns the
	/// target id, because the re-parameterization already carries the consequence: the velocity
	/// vector. V7-D6.
	/// </summary>
	[NonSerialized]
	public Transform target;

	private bool diving;

	private bool thrustEnabled;

	private bool missing;

	private Action thrustStartAction = new Action(0.5f);

	private Action cannotMissDiveAction = new Action(1f);

	private Action inaccurateDiveAction = new Action(3f);

	public float damage = 800f;

	public float divingDamage = 1500f;

	public AudioClip flightSound;

	protected override void Start()
	{
		// A missile a CLIENT presents has no source -- NetClientProjectilePresenter leaves it null
		// on purpose -- and its velocity is the server's, applied before this runs. The ejection
		// line read source.Velocity() and threw on the first server missile a client drew; it had
		// never run, because until 2026-09-27 no Javelin was ever launched online. Keep what the
		// presenter applied rather than inventing a launch the server did not make.
		Vector3 presented = velocity;
		base.Start();
		velocity = source != null
			? base.transform.forward * ejectSpeed + source.Velocity() * 0.9f
			: presented;
		thrustStartAction.Start();
		inaccurateDiveAction.Start();
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
		if (audioSource != null)
		{
			audioSource.PlayOneShot(flightSound);
		}
	}

	protected override void Update()
	{
		// V7-D6: the SERVER owns a guided flight. It re-sends S_PROJECTILE_SPAWN with this
		// missile's id and its current (position, velocity, remainingLifetime) at 5 Hz, and a
		// client re-seats the existing missile and coasts on plain ballistics in between -- the
		// visible error over 200 ms of turn at 300 deg/s, on a missile usually hundreds of
		// metres away, is bounded and small. Running the guidance locally as well would have the
		// client steering toward a target it was never told, and every re-seat would yank it.
		//
		// This stays inside V7-D5: every message is still the same 20-byte parameter set going
		// through the same decoder. There is no per-tick missile entry in the snapshot.
		// Only a PRESENTED copy -- no source, and a target the client was never told -- coasts. The
		// shooter's own cosmetic copy has a source and the target its launcher locked, and flies
		// the guidance itself, the way the shooter's own rocket flies true; coasting, it fell out of
		// the sky a few metres from the launcher while the server's missile flew on.
		if (Ironfront.Net.Unity.NetContext.IsClient && source == null)
		{
			// The motor lighting is presentation, so the presented copy runs it too: before this a
			// client's Javelin crossed the sky dark, trailless and silent.
			IgniteWhenDue();
			base.Update();
			return;
		}

		if (thrustStartAction.TrueDone())
		{
			IgniteWhenDue();
			Vector3 vector = ((!(target == null)) ? target.position : targetPoint);
			Vector3 rhs = vector - base.transform.position;
			Vector3 vector2 = Vector3.zero;
			if (!diving)
			{
				rhs.y = 0f;
				float value = 200f - base.transform.position.y;
				vector2 = (rhs.normalized + Vector3.up * Mathf.Clamp(value, 0f, 3f)).normalized * configuration.speed;
				if (rhs.magnitude < 50f)
				{
					StartDiving();
				}
			}
			else if (!missing)
			{
				vector2 = (rhs.normalized - velocity.normalized * 0.3f).normalized * configuration.speed;
				if (target != null && cannotMissDiveAction.Done() && Vector3.Dot(velocity, rhs) < 0f)
				{
					missing = true;
				}
			}
			if (!missing)
			{
				float num = ((!diving || !inaccurateDiveAction.TrueDone()) ? 110f : 160f);
				velocity = Vector3.MoveTowards(velocity, vector2, 110f * Time.deltaTime);
			}
			else
			{
				velocity += Physics.gravity * Time.deltaTime;
			}
			// The 800 -> 1500 mutation never crosses the wire and does not need to: it is read
			// only by Damage(), and V7-D3 already puts damage entirely on the server. A client's
			// copy of this number is never consulted by anything.
			bool flag = diving && Vector3.Dot(base.transform.forward, Vector3.down) > 0.8f;
			configuration.damage = ((!flag) ? damage : divingDamage);
			base.transform.rotation = Quaternion.RotateTowards(base.transform.rotation, Quaternion.LookRotation(velocity), 300f * Time.deltaTime);
		}
		base.Update();
	}

	/// <summary>
	/// Switches the missile to a direct attack. Server-side, per V7-D6.
	/// </summary>
	/// <remarks>
	/// A client calling this would change only its own prediction of a flight it does not own,
	/// and the next re-parameterization -- at most 200 ms away -- would overwrite the result.
	/// Refusing it here means the divergence never happens rather than being corrected.
	/// </remarks>
	public void ForceDirectMode()
	{
		if (Ironfront.Net.Unity.NetContext.IsClient) return;

		StartDiving();
	}

	private void StartDiving()
	{
		diving = true;
		cannotMissDiveAction.Start();
	}
}
