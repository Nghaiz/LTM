using Ironfront.Net.Replication.Vehicles;
using UnityEngine;

public class MountedTurret : MountedWeapon
{
	public Camera camera;

	public Transform towerTransform;

	public Transform turretTransform;

	// The shipped MAX_TURN_DELTA of 10 degrees PER FRAME multiplied by 60, and the -40/15
	// elevation stops that were inline literals. Serialized, so per-prefab tuning is data.
	public TurretAimLimits aimLimits = new TurretAimLimits
	{
		YawRateDegPerSec = 600f,
		PitchRateDegPerSec = 600f,
		PitchMin = -40f,
		PitchMax = 15f
	};

	// THE authoritative aim. Both transforms below are outputs of this pair.
	private TurretAimState _aim;

	public float Yaw
	{
		get { return _aim.Yaw; }
	}

	public float Pitch
	{
		get { return _aim.Pitch; }
	}

	/// <summary>Server/replication entry point. V0 adds it; V4 and V6 are its only callers.</summary>
	public void SetAim(float yaw, float pitch)
	{
		_aim.Yaw = TurretAimCore.WrapDegrees(yaw);
		_aim.Pitch = TurretAimCore.ClampPitch(pitch, aimLimits);
	}

	protected override void Awake()
	{
		base.Awake();
		// Seeded once from the prefab's authored pose so the turret does not snap on the first
		// step. These are the LAST reads out of a localEulerAngles.
		if (towerTransform != null)
		{
			_aim.Yaw = TurretAimCore.WrapDegrees(towerTransform.localEulerAngles.z);
		}
		if (turretTransform != null)
		{
			_aim.Pitch = TurretAimCore.ClampPitch(Mathf.DeltaAngle(0f, turretTransform.localEulerAngles.x), aimLimits);
		}
	}

	// These are plain Transforms with no rigidbody, so applying per-frame is free smoothness
	// and costs nothing in determinism -- the value being applied was integrated at a fixed
	// rate in FixedUpdate (phase-v0 D4).
	protected override void Update()
	{
		base.Update();
		if (towerTransform != null)
		{
			Vector3 localEulerAngles = towerTransform.localEulerAngles;
			localEulerAngles.z = _aim.Yaw;
			towerTransform.localEulerAngles = localEulerAngles;
		}
		if (turretTransform != null)
		{
			Vector3 localEulerAngles2 = turretTransform.localEulerAngles;
			localEulerAngles2.x = _aim.Pitch;
			turretTransform.localEulerAngles = localEulerAngles2;
		}
	}

	private void FixedUpdate()
	{
		if (user == null)
		{
			return;
		}
		Vector2 raw = GetInput();
		// The shipped code clamped the raw mouse delta to a magnitude of 10 and added it as
		// degrees. Clamping to 1 and letting the 600 deg/s rate scale it is the same arc at
		// the design framerate (600 / 60 = 10) and the correct one everywhere else.
		float x;
		float y;
		VehicleInputClamp.Magnitude(raw.x, raw.y, 1f, out x, out y);
		TurretAimCore.Step(ref _aim, x, 0f - y, aimLimits, Time.fixedDeltaTime);
	}

	public override void Unholster()
	{
		base.Unholster();
		if (!user.aiControlled)
		{
			FpsActorController.instance.DisableCameras();
			camera.enabled = true;
		}
	}

	public override void Holster()
	{
		base.Holster();
		camera.enabled = false;
		if (!user.aiControlled)
		{
			FpsActorController.instance.EnableCameras();
		}
	}

	// protected virtual so V4 can override the source without touching the integration.
	protected virtual Vector2 GetInput()
	{
		if (user == null)
		{
			return Vector2.zero;
		}
		if (!user.aiControlled)
		{
			return new Vector2(Input.GetAxis("Mouse X"), Input.GetAxis("Mouse Y") * (float)((!OptionsUi.GetOptions().mouseInvert) ? 1 : (-1))) * OptionsUi.GetOptions().mouseSensitivity * 4f;
		}
		Vector3 vector = configuration.muzzle.worldToLocalMatrix.MultiplyVector(user.controller.FacingDirection());
		return new Vector2(vector.x * 5f, vector.y * 5f);
	}
}
