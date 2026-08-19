using Ironfront.Net.Replication.Vehicles;
using UnityEngine;

public class Helicopter : Vehicle
{
	private const float ROTOR_SPEED = 1000f;

	private const float ROTOR_SPEED_GAIN = 0.3f;

	private const float MAX_VOLUME = 0.5f;

	private const float BASE_ANGULAR_DRAG = 0.2f;

	private const float BASE_DRAG = 0.05f;

	private const float ANGULAR_DRAG_ALONG_WIND_GAIN = 0.01f;

	private const float DRAG_BROADSIDE_WIND_GAIN = 0.01f;

	private const float ALONG_WIND_LIFT = 0.03f;

	private const float MANOUVERABILITY_SCALE = 0.0069999998f;

	public Transform rotor;

	private Renderer solidRotor;

	private Renderer blurredRotor;

	public float rotorForce = 5f;

	public float manouverability = 1f;

	public float counterForceMultiplier = 0.3f;

	private float rotorSpeed;

	/// <summary>
	/// The helicopter's subtype tail: <c>rotorSpeed</c> as a normalized u16, low byte then high
	/// (protocol-spec.md section 4.10).
	/// </summary>
	/// <remarks>
	/// <b>The one subtype value that is not cosmetic.</b> <c>rotorSpeed</c> spins up and down
	/// over several seconds and multiplies every force this vehicle applies, so a client with no
	/// value for it either renders a stationary rotor on a flying helicopter or guesses -- and a
	/// guess that is wrong on the way up is wrong for the whole spin-up. Two bytes at the
	/// vehicle's band rate is the cheapest way to make it right.
	/// </remarks>
	public override void ReadNetworkSubtypeTail(out byte subtypeA, out byte subtypeB)
	{
		VehicleSubtypeTail.PackHelicopter(rotorSpeed, out subtypeA, out subtypeB);
	}

	/// <summary>
	/// Takes <c>rotorSpeed</c> from the snapshot. V5-D3, and the reason design section 5
	/// reserved a subtype tail at all.
	/// </summary>
	/// <remarks>
	/// <c>Update()</c> reads <c>rotorSpeed</c> for the audio pitch, the solid/blurred rotor
	/// swap and the rotor's own spin. A replicated helicopter integrates none of it, so without
	/// this every remote helicopter flies past with a stationary, solid rotor and silence.
	/// </remarks>
	public override void ApplyReplicatedSubtypeTail(byte subtypeA, byte subtypeB)
	{
		rotorSpeed = VehicleSubtypeTail.UnpackHelicopter(subtypeA, subtypeB);
	}

	private bool isAirborne;

	private Vector3 randomBurningTorque = Vector3.zero;

	protected override void Awake()
	{
		base.Awake();
		// A dedicated server strips renderers, so both of these are null there by design and
		// every later dereference has to survive it. rotor itself is a Transform and does
		// survive, but guarding it keeps the null story in one place rather than two.
		if (rotor != null)
		{
			solidRotor = rotor.GetComponent<Renderer>();
			Transform blurred = ((rotor.childCount > 0) ? rotor.GetChild(0) : null);
			if (blurred != null)
			{
				blurredRotor = blurred.GetComponent<Renderer>();
			}
		}
		rigidbody.maxAngularVelocity = 1.5f;
	}

	// Cosmetic only. rotorSpeed is read here but never written -- see FixedUpdate.
	private void Update()
	{
		audio.volume = rotorSpeed * 0.5f;
		audio.pitch = rotorSpeed;
		bool flag = rotorSpeed > 0.8f;
		if (solidRotor != null)
		{
			solidRotor.enabled = !flag;
		}
		if (blurredRotor != null)
		{
			blurredRotor.enabled = flag;
		}
		if (rotor != null)
		{
			rotor.Rotate(Vector3.forward * 1000f * rotorSpeed * Time.deltaTime);
		}
	}

	protected override void DriverEntered()
	{
		base.DriverEntered();
	}

	protected override void DriverExited()
	{
		base.DriverExited();
	}

	protected override void FixedUpdate()
	{
		// V5-D3. Everything past base.FixedUpdate() is force, and rotorSpeed is integrated from
		// a driver input this peer does not have -- so on a replicated helicopter both are
		// wrong and the rotor speed arrives on the wire instead (ApplyReplicatedSubtypeTail).
		// The base call still runs: the ram check and the seat-claim drain are not drive path.
		if (NetworkDriven)
		{
			// isAirborne stays local. It is a downward raycast against this client's own copy
			// of the map, which is cheap and correct here, and spending a wire bit on it would
			// buy nothing.
			isAirborne = !Physics.Raycast(base.transform.position, Vector3.down, 3f);
			base.FixedUpdate();
			return;
		}
		// rotorSpeed multiplies EVERY force below, so integrating it at render rate made lift
		// itself framerate-dependent -- the single largest divergence source in the vehicle
		// set. It is integrated before base.FixedUpdate() so the forces further down read the
		// value this step produced.
		if (HasDriver())
		{
			rotorSpeed = Mathf.Clamp01(rotorSpeed + Time.fixedDeltaTime * 0.3f);
			// Damage-per-frame, and a gameplay bug rather than only a determinism one: this
			// was nominally 30 HP/s only because deltaTime sums to one second per second, and
			// it fired at render rate on a client that has no damage authority. V4 makes this
			// call server-only; the move is what reduces that to one line.
			if (base.transform.up.y < 0f)
			{
				Damage(Time.fixedDeltaTime * 30f);
			}
		}
		else
		{
			rotorSpeed = Mathf.Clamp01(rotorSpeed - Time.fixedDeltaTime * 0.3f);
		}
		// A physics query read by ShouldBeAvoided(), which the AI consults. It belongs at
		// physics rate.
		isAirborne = !Physics.Raycast(base.transform.position, Vector3.down, 3f);
		base.FixedUpdate();
		Vector3 normalized = (base.transform.forward + 0.15f * base.transform.up).normalized;
		float num = Vector3.Dot(normalized, rigidbody.linearVelocity);
		float magnitude = Vector3.Cross(normalized, rigidbody.linearVelocity).magnitude;
		rigidbody.angularDamping = 0.2f + num * 0.01f;
		rigidbody.linearDamping = 0.05f + magnitude * 0.01f;
		rigidbody.AddForce(base.transform.up * num * 0.03f, ForceMode.Acceleration);
		if (HasDriver())
		{
			Vector4 vector = Vehicle.Clamp4(Driver().controller.HelicopterInput()) * rotorSpeed;
			float y = vector.y;
			Vector3 vector2 = new Vector3(vector.w, vector.x, 0f - vector.z) * manouverability * 0.0069999998f;
			Vector3 normalized2 = (base.transform.up + base.transform.forward * 0.05f).normalized;
			Vector3 vector3 = Vector3.Project(normalized2, Vector3.up);
			normalized2 = (normalized2 - 0.05f * vector3).normalized;
			float t = Mathf.Clamp01(0f - Vector3.Dot(normalized2, rigidbody.linearVelocity.normalized));
			float num2 = 1f + Mathf.Lerp(0f, counterForceMultiplier, t);
			if (burning)
			{
				rigidbody.AddForce(0.3f * normalized2 * (y * rotorForce * num2 - Physics.gravity.y - 0.5f), ForceMode.Acceleration);
				rigidbody.AddRelativeTorque(randomBurningTorque + 0.5f * vector2, ForceMode.VelocityChange);
			}
			else
			{
				rigidbody.AddForce(normalized2 * (y * rotorForce * num2 - Physics.gravity.y - 0.5f), ForceMode.Acceleration);
				rigidbody.AddRelativeTorque(vector2, ForceMode.VelocityChange);
			}
		}
	}

	protected override void StartBurning()
	{
		base.StartBurning();
		randomBurningTorque = Random.insideUnitSphere * 0.005f + Vector3.up * 0.025f;
	}

	public override void Die()
	{
		base.Die();
		rotor.gameObject.SetActive(false);
		audio.Stop();
	}

	public override bool ShouldBeAvoided()
	{
		return !isAirborne && base.ShouldBeAvoided();
	}
}
