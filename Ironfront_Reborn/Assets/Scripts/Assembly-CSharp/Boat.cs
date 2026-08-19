using System;
using UnityEngine;

public class Boat : Vehicle
{
	public float floatAcceleration = 10f;

	public float floatDepth = 0.5f;

	public float speed = 5f;

	public float turnSpeed = 5f;

	public float stability = 1f;

	public Transform[] floatingSamplers;

	[NonSerialized]
	public bool inWater;

	private float audioPitch = 1f;

	protected override void Awake()
	{
		base.Awake();
		rigidbody.centerOfMass = Vector3.down * stability;
	}

	protected override void DriverEntered()
	{
		base.DriverEntered();
		audio.Play();
		audio.pitch = 0f;
		audioPitch = 0f;
	}

	protected override void DriverExited()
	{
		base.DriverExited();
	}

	/// <summary>
	/// Takes <c>inWater</c> from the snapshot, because the buoyancy sample that used to set it
	/// does not run on a replicated hull. V5-D3.
	/// </summary>
	public override void ApplyReplicatedFlags(bool inWaterFlag, bool airborne)
	{
		inWater = inWaterFlag;
	}

	protected override void FixedUpdate()
	{
		base.FixedUpdate();
		// V5-D3: the hull is kinematic here, so buoyancy and thrust are forces nothing reads.
		// inWater comes from the replicated flag instead -- see ApplyReplicatedFlags, and note
		// that the sampler loop below is what used to set it.
		if (NetworkDriven)
		{
			return;
		}
		int num = 0;
		Transform[] array = floatingSamplers;
		foreach (Transform transform in array)
		{
			if (WaterLevel.InWater(transform.position))
			{
				float num2 = Mathf.Clamp01(WaterLevel.Depth(transform.position) / floatDepth) / (float)floatingSamplers.Length;
				rigidbody.AddForceAtPosition(Vector3.up * floatAcceleration * num2, transform.position + Vector3.up, ForceMode.Acceleration);
				num++;
			}
		}
		inWater = num >= 3;
		float target = ((!HasDriver()) ? 0f : 0.7f);
		if (inWater && HasDriver())
		{
			// Clamped: the raw axes went straight into AddForce/AddRelativeTorque, so a client
			// sending 10.0 got ten times the thrust and one sending NaN removed the boat from
			// the simulation.
			Vector2 vector = Vehicle.Clamp2(Driver().controller.BoatInput());
			if (vector.y < 0f)
			{
				vector.y *= 0.15f;
			}
			rigidbody.AddForce(base.transform.forward.ToGround().normalized * speed * vector.y, ForceMode.Acceleration);
			// Vector3.up, not transform.up. AddRelativeTorque interprets its argument in the
			// BODY's local space, but transform.up is a WORLD-space vector -- the two coincide
			// only while the hull is level and unrotated in yaw, so the moment the boat turned
			// or rolled, steering torque leaked into pitch and roll. The AddForce above is
			// correct as written because that call IS world-space.
			rigidbody.AddRelativeTorque(Vector3.up * turnSpeed * vector.x, ForceMode.Acceleration);
			target = 1f + Mathf.Clamp01(Mathf.Abs(vector.y) + Mathf.Abs(vector.x) * 0.5f);
		}
		audioPitch = Mathf.MoveTowards(audioPitch, target, Time.fixedDeltaTime);
		audio.pitch = audioPitch;
		if (audio.isPlaying && audioPitch == 0f)
		{
			audio.Stop();
		}
	}
}
