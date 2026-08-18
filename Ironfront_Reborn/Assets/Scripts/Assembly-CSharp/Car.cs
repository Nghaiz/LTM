using System;
using Ironfront.Net.Replication.Vehicles;
using UnityEngine;

public class Car : Vehicle
{
	[Serializable]
	public class WheelConfiguration
	{
		public WheelCollider collider;

		public bool motor;

		public bool steer;
	}

	private const float STEER_RATE = 5f;

	private const float MIN_BRAKE_RPM = 10f;

	private const float BRAKE_TORQUE = 300f;

	private const float CAN_TURN_TOWARDS_DISTANCE_BIAS = 1f;

	public float extraStability = 0.5f;

	public float maxTorque = 300f;

	public float maxSteer = 40f;

	public float wheelSteerMultiplier = 3f;

	public float turningRadius = 5f;

	private float steerAngle;

	/// <summary>
	/// The car's subtype tail: <c>steerAngle</c> in degrees and a placeholder friction byte
	/// (protocol-spec.md section 4.10).
	/// </summary>
	/// <remarks>
	/// <para>
	/// <b>V5 needs this because a remote car is not locally simulated.</b> Its wheels are
	/// wherever the last snapshot put them, and there is nothing on the client that could derive
	/// a steering angle from a position and a velocity -- so without the tail every replicated
	/// car drives with its front wheels pointing dead ahead through every corner.
	/// </para>
	/// <para>
	/// <b><c>surfaceFriction</c> is 1 rather than measured.</b> The shipped <c>Car</c> keeps no
	/// single friction value -- it is per <c>WheelCollider</c> and changes with the surface
	/// under each one -- and averaging four of them into a byte would put a number on the wire
	/// that names nothing. A full 1.0 is the honest "no friction modifier reported"; measuring
	/// it belongs with whoever gives the client a use for it.
	/// </para>
	/// </remarks>
	public override void ReadNetworkSubtypeTail(out byte subtypeA, out byte subtypeB)
	{
		VehicleSubtypeTail.PackSteered(steerAngle, 1f, out subtypeA, out subtypeB);
	}

	public Transform steeringWheel;

	public WheelConfiguration[] wheels;

	private float enginePitch;

	// The one value that crosses the Update/FixedUpdate split: the drive block decides what
	// the engine should sound like, and the audio block (still per-frame, because it is
	// cosmetic) reads it. It was a local before the split.
	private float enginePitchTarget;

	protected override void Awake()
	{
		base.Awake();
		rigidbody.centerOfMass += Vector3.down * extraStability;
		WheelConfiguration[] array = wheels;
		foreach (WheelConfiguration wheelConfiguration in array)
		{
			wheelConfiguration.collider.motorTorque = 0f;
			wheelConfiguration.collider.brakeTorque = 120f;
		}
	}

	protected override void DriverEntered()
	{
		base.DriverEntered();
		WheelConfiguration[] array = wheels;
		foreach (WheelConfiguration wheelConfiguration in array)
		{
			wheelConfiguration.collider.brakeTorque = 0f;
		}
		audio.Play();
		enginePitch = 0f;
		audio.pitch = 0f;
	}

	protected override void DriverExited()
	{
		WheelConfiguration[] array = wheels;
		foreach (WheelConfiguration wheelConfiguration in array)
		{
			wheelConfiguration.collider.motorTorque = 0f;
			wheelConfiguration.collider.brakeTorque = 120f;
		}
	}

	private void UpdateVisuals(WheelConfiguration wheel)
	{
		WheelCollider collider = wheel.collider;
		Transform child = collider.transform.GetChild(0);
		Vector3 pos;
		Quaternion quat;
		collider.GetWorldPose(out pos, out quat);
		child.transform.position = pos;
		child.transform.rotation = quat;
	}

	// Every write below lands on a WheelCollider, which PhysX reads exactly once per fixed
	// step. Driving them from Update meant a 144 Hz client fed the solver the last of ~2.4
	// writes per step while a 30 Hz one fed it a value integrated over a step it never took,
	// so the same input produced different motion on every peer. Nothing here is cosmetic.
	protected override void FixedUpdate()
	{
		base.FixedUpdate();
		enginePitchTarget = ((!HasDriver()) ? 0f : 0.5f);
		if (HasDriver() && !burning)
		{
			Vector2 vector = Vehicle.Clamp2(Driver().controller.CarInput());
			float num = 0f;
			WheelConfiguration[] array = wheels;
			foreach (WheelConfiguration wheelConfiguration in array)
			{
				num += wheelConfiguration.collider.rpm;
			}
			num /= (float)wheels.Length;
			float target2 = vector.x * maxSteer;
			steerAngle = Mathf.MoveTowards(steerAngle, target2, 5f * maxSteer * Time.fixedDeltaTime);
			WheelConfiguration[] array2 = wheels;
			foreach (WheelConfiguration wheelConfiguration2 in array2)
			{
				if (wheelConfiguration2.motor)
				{
					wheelConfiguration2.collider.motorTorque = vector.y * maxTorque;
					if ((vector.y < 0f && wheelConfiguration2.collider.rpm > 10f) || (vector.y > 0f && wheelConfiguration2.collider.rpm < -10f))
					{
						wheelConfiguration2.collider.brakeTorque = 300f;
						enginePitchTarget = 0.5f;
					}
					else
					{
						wheelConfiguration2.collider.brakeTorque = 0f;
						enginePitchTarget = ((!(vector.y > 0f)) ? (0.5f + Mathf.Abs(vector.y) * 0.2f) : (0.5f + Mathf.Abs(vector.y) * 0.6f));
					}
				}
				if (wheelConfiguration2.steer)
				{
					wheelConfiguration2.collider.steerAngle = steerAngle;
				}
			}
		}
	}

	// Cosmetic only: the steering-wheel prop reads the steerAngle the fixed step integrated,
	// and the engine note chases the target the fixed step decided. Neither feeds physics.
	private void Update()
	{
		Vector3 localEulerAngles = steeringWheel.localEulerAngles;
		localEulerAngles.z = steerAngle * wheelSteerMultiplier;
		steeringWheel.localEulerAngles = localEulerAngles;
		enginePitch = Mathf.MoveTowards(enginePitch, enginePitchTarget, Time.deltaTime);
		audio.pitch = enginePitch;
		if (audio.isPlaying && enginePitch == 0f)
		{
			audio.Stop();
		}
	}

	public bool CanTurnTowards(Vector3 deltaPosition)
	{
		if (deltaPosition.magnitude < 4f)
		{
			return true;
		}
		float num = 2f * turningRadius * Vector3.Cross(deltaPosition.normalized, base.transform.forward).magnitude;
		return deltaPosition.magnitude > num + 1f;
	}

	private void LateUpdate()
	{
		WheelConfiguration[] array = wheels;
		foreach (WheelConfiguration wheel in array)
		{
			UpdateVisuals(wheel);
		}
	}

	public override void Die()
	{
		base.Die();
		WheelConfiguration[] array = wheels;
		foreach (WheelConfiguration wheelConfiguration in array)
		{
			wheelConfiguration.collider.gameObject.SetActive(false);
		}
		audio.Stop();
	}
}
