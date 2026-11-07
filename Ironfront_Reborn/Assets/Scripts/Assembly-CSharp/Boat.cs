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
			UpdateReplicatedEngineNote();
			return;
		}
		int num = 0;
		// How far the lowest DRY sampler stands above the water; infinity with no water under it.
		float driestHeight = float.PositiveInfinity;
		Transform[] array = floatingSamplers;
		foreach (Transform transform in array)
		{
			float depth = WaterLevel.Depth(transform.position);
			if (depth >= 0f)
			{
				float num2 = Mathf.Clamp01(depth / floatDepth) / (float)floatingSamplers.Length;
				rigidbody.AddForceAtPosition(Vector3.up * floatAcceleration * num2, transform.position + Vector3.up, ForceMode.Acceleration);
				num++;
				driestHeight = 0f;
			}
			else if (-depth < driestHeight)
			{
				driestHeight = -depth;
			}
		}
		inWater = num >= 3;
		float wetFraction = (floatingSamplers.Length > 0) ? ((float)num / (float)floatingSamplers.Length) : 0f;
		float target = ((!HasDriver()) ? 0f : 0.7f);
		float reach = Ironfront.Net.Unity.BoatThrust.Reach(wetFraction, driestHeight);
		if (reach > 0f && HasDriver())
		{
			// Clamped: the raw axes went straight into AddForce/AddRelativeTorque, so a client
			// sending 10.0 got ten times the thrust and one sending NaN removed the boat from
			// the simulation.
			Vector2 vector = Vehicle.Clamp2(Driver().controller.BoatInput());
			// BoatThrust, not "three samplers wet or nothing": a hull with its bow on a beach had
			// no thrust either way and stayed there (2026-11-07). Astern is the original 15 %
			// afloat and full power beached, which is what slides it back off.
			float drive = Ironfront.Net.Unity.BoatThrust.Drive(wetFraction, driestHeight, vector.y);
			rigidbody.AddForce(base.transform.forward.ToGround().normalized * speed * drive, ForceMode.Acceleration);
			// Vector3.up, not transform.up. AddRelativeTorque interprets its argument in the
			// BODY's local space, but transform.up is a WORLD-space vector -- the two coincide
			// only while the hull is level and unrotated in yaw, so the moment the boat turned
			// or rolled, steering torque leaked into pitch and roll. The AddForce above is
			// correct as written because that call IS world-space.
			rigidbody.AddRelativeTorque(Vector3.up * turnSpeed * vector.x * reach, ForceMode.Acceleration);
			target = 1f + Mathf.Clamp01(Mathf.Abs(vector.y) + Mathf.Abs(vector.x) * 0.5f);
		}
		audioPitch = Mathf.MoveTowards(audioPitch, target, Time.fixedDeltaTime);
		audio.pitch = audioPitch;
		if (audio.isPlaying && audioPitch == 0f)
		{
			audio.Stop();
		}
	}

	private Vector3 lastReplicatedPosition;

	private bool hasLastReplicatedPosition;

	/// <summary>
	/// The engine note of a hull this peer does not simulate: idle with a driver aboard, rising
	/// with the speed the snapshots move it at.
	/// </summary>
	/// <remarks>
	/// The note used to be set only after the early return above, so on a client the boat's
	/// engine started at pitch 0 when its driver sat down and stayed there: a running engine
	/// nobody could hear (2026-11-07). And it asked <see cref="Vehicle.HasDriver"/>, which on a
	/// client is true only for this client's own player: a boat someone else drove was silent
	/// (2026-11-08); <see cref="Vehicle.DriverAboard"/> counts the remote crew.
	/// </remarks>
	private void UpdateReplicatedEngineNote()
	{
		Vector3 position = base.transform.position;
		float speed = hasLastReplicatedPosition ? ((position - lastReplicatedPosition).magnitude / Time.fixedDeltaTime) : 0f;
		lastReplicatedPosition = position;
		hasLastReplicatedPosition = true;
		float target = DriverAboard() ? (1f + Mathf.Clamp01(speed / 10f)) : 0f;
		if (target > 0f && !audio.isPlaying)
		{
			audio.Play();
		}
		audioPitch = Mathf.MoveTowards(audioPitch, target, Time.fixedDeltaTime);
		audio.pitch = audioPitch;
		if (audio.isPlaying && audioPitch == 0f)
		{
			audio.Stop();
		}
	}
}
