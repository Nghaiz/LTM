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

	/// <summary>
	/// Drives the steering-wheel prop from the replicated steer angle. V5-D3.
	/// </summary>
	/// <remarks>
	/// <c>Update()</c> reads <c>steerAngle</c> to rotate the wheel prop, and on a replicated
	/// car nothing integrates it any more -- so without this every remote car drives past with
	/// its steering wheel dead centre through every corner. The engine note of a replicated car
	/// follows the speed the snapshots move it at (<see cref="UpdateEngineNote"/>).
	/// </remarks>
	public override void ApplyReplicatedSubtypeTail(byte subtypeA, byte subtypeB)
	{
		steerAngle = VehicleSubtypeTail.UnpackSteerAngle(subtypeA);
	}

	public Transform steeringWheel;

	public WheelConfiguration[] wheels;

	private float enginePitch;

	// The one value that crosses the Update/FixedUpdate split: the drive block decides what
	// the engine should sound like, and the audio block (still per-frame, because it is
	// cosmetic) reads it. It was a local before the split.
	private float enginePitchTarget;

	/// <summary>The note of an engine idling with a driver aboard.</summary>
	private const float IdlePitch = 0.5f;

	/// <summary>How far the note climbs over idle at <see cref="ReplicatedNoteTopSpeed"/> on a car this peer does not drive.</summary>
	private const float ReplicatedPitchRange = 0.6f;

	/// <summary>The speed, m/s, a replicated car's note tops out at (72 km/h).</summary>
	private const float ReplicatedNoteTopSpeed = 20f;

	private Vector3 lastNotePosition;

	private bool hasLastNotePosition;

	private float replicatedSpeed;

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
		// The engine starts from cold only if it had stopped; one still spinning down keeps its
		// note. UpdateEngineNote starts the clip -- see its remark for why not here.
		if (!audio.isPlaying)
		{
			enginePitch = 0f;
			audio.pitch = 0f;
		}
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
		LogDrive();
		// V5-D3. The body is kinematic on a replicated client, so every WheelCollider write
		// below steers something PhysX will not move -- and steerAngle would be integrated
		// from an input this peer does not have, which is exactly what the subtype tail
		// replaces. Update() and LateUpdate() keep running: the steering-wheel prop, the
		// engine note and the wheel meshes all read values, not forces.
		if (NetworkDriven)
		{
			return;
		}
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

	private static bool? _driveLogging;

	private float _nextDriveLog;

	// One line a second per driven car, when IRONFRONT_LOG_VEHICLE=1: what this car's physics
	// actually read from its driver and what the wheels did with it. The client's [veh-correct]
	// line showed a server car standing still under a driver holding full throttle and steer
	// (2026-09-23); this is the line that says whether the input or the wheels are at fault.
	private void LogDrive()
	{
		if (_driveLogging == null)
		{
			_driveLogging = System.Environment.GetEnvironmentVariable("IRONFRONT_LOG_VEHICLE") == "1";
		}
		if (_driveLogging != true || Time.time < _nextDriveLog)
		{
			return;
		}
		_nextDriveLog = Time.time + 1f;
		bool driven = HasDriver();
		if (!driven && seats[0].occupant == null)
		{
			return;
		}
		Vector2 input = driven ? Vehicle.Clamp2(Driver().controller.CarInput()) : Vector2.zero;
		float brake = 0f;
		float averageRpm = 0f;
		bool grounded = false;
		foreach (WheelConfiguration wheel in wheels)
		{
			brake = Mathf.Max(brake, wheel.collider.brakeTorque);
			averageRpm += wheel.collider.rpm / wheels.Length;
			grounded |= wheel.collider.isGrounded;
		}
		Debug.Log($"[car-drive] t={Time.time:F2} name={base.gameObject.name} role={(Ironfront.Net.Unity.NetContext.IsServer ? "server" : Ironfront.Net.Unity.NetContext.IsClient ? "client" : "offline")} "
			+ $"hasDriver={driven} networkDriven={NetworkDriven} burning={burning} input=({input.x:F2},{input.y:F2}) steerAngle={steerAngle:F1} rpm={averageRpm:F0} brake={brake:F0} grounded={grounded} "
			+ $"kinematic={rigidbody.isKinematic} speed={rigidbody.linearVelocity.magnitude:F1} pos={rigidbody.position}");
	}

	// Cosmetic only: the steering-wheel prop reads the steerAngle the fixed step integrated,
	// and the engine note chases the target the fixed step decided. Neither feeds physics.
	private void Update()
	{
		Vector3 localEulerAngles = steeringWheel.localEulerAngles;
		localEulerAngles.z = steerAngle * wheelSteerMultiplier;
		steeringWheel.localEulerAngles = localEulerAngles;
		UpdateEngineNote();
	}

	/// <summary>
	/// Whether the engine runs, and the note it runs at. The clip plays while a driver is aboard
	/// and stops only once the engine is off and has spun down.
	/// </summary>
	/// <remarks>
	/// <para>
	/// <b>Why it exists</b> (owner report 2026-11-07: "the jeep and the motorbike sometimes lose
	/// their engine sound while driving, online and offline"). The clip was started only in
	/// <c>DriverEntered</c>, at pitch 0, and stopped here whenever the pitch stood at 0. When a
	/// driver got in during an Update that ran before this car's, the fixed step had not yet
	/// raised the target from 0: the frame stopped the clip it had just started, and nothing
	/// ever started it again, so the whole drive was silent. On a client the target came from
	/// the snapshot tail as <c>HasDriver() ? 0.5 : 0</c>, and <c>HasDriver</c> is false on a car
	/// someone else drives, and for a moment on one's own during a seat change: the note fell
	/// to 0, the clip stopped, and the engine stayed silent until the driver got out and in.
	/// </para>
	/// <para>
	/// Now the engine's state decides the clip, every frame: a driver aboard
	/// (<see cref="Vehicle.DriverAboard"/>, which counts a remote driver) starts it if it is not
	/// playing, never below idle; the fixed step's throttle raises the note on a car this peer
	/// drives, and on one it does not, the speed the snapshots move it at.
	/// </para>
	/// </remarks>
	private void UpdateEngineNote()
	{
		bool running = !dead && DriverAboard();
		float target = 0f;
		if (running)
		{
			target = NetworkDriven
				? IdlePitch + ReplicatedPitchRange * Mathf.Clamp01(ReplicatedSpeed() / ReplicatedNoteTopSpeed)
				: Mathf.Max(IdlePitch, enginePitchTarget);
			if (!audio.isPlaying && audio.isActiveAndEnabled)
			{
				audio.Play();
			}
		}
		enginePitch = Mathf.MoveTowards(enginePitch, target, Time.deltaTime);
		audio.pitch = enginePitch;
		if (!running && enginePitch == 0f && audio.isPlaying)
		{
			audio.Stop();
		}
	}

	/// <summary>How fast the snapshots move this car, m/s, smoothed over a few frames.</summary>
	private float ReplicatedSpeed()
	{
		Vector3 position = base.transform.position;
		if (hasLastNotePosition && Time.deltaTime > 0f)
		{
			float now = (position - lastNotePosition).magnitude / Time.deltaTime;
			replicatedSpeed = Mathf.Lerp(replicatedSpeed, now, Mathf.Clamp01(Time.deltaTime * 8f));
		}
		lastNotePosition = position;
		hasLastNotePosition = true;
		return replicatedSpeed;
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
		if (!WheelsSimulated())
		{
			return;
		}
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
