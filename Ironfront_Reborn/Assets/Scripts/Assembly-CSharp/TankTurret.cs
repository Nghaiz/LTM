using Ironfront.Net.Replication.Vehicles;
using UnityEngine;

public class TankTurret : MountedWeapon
{
	public Camera camera;

	public ConfigurableJoint towerJoint;

	public HingeJoint cannonJoint;

	public Renderer cannonRenderer;

	// Serialized so retuning a turret is a prefab edit rather than a rebuild. The defaults are
	// the shipped MAX_TURN_DELTA of 5 degrees PER FRAME multiplied by 60 -- i.e. the rate the
	// original game exhibits at its design framerate. That a 144 Hz client now gets the same
	// rate instead of 2.4x it is the accepted behaviour change recorded in phase-v0 D8.
	// PitchMin/PitchMax are overwritten from the cannon joint's own limits in Awake.
	public TurretAimLimits aimLimits = new TurretAimLimits
	{
		YawRateDegPerSec = 300f,
		PitchRateDegPerSec = 300f,
		PitchMin = -10f,
		PitchMax = 20f
	};

	// THE authoritative aim. The joint and the spring below are outputs of this pair; nothing
	// reads an angle back out of them. Replicated in V4.
	private TurretAimState _aim;

	private Rigidbody rigidbody;

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
		rigidbody = GetComponent<Rigidbody>();
		if (cannonJoint != null)
		{
			// Preserves the elevation clamp the shipped Update applied inline, and keeps it
			// the joint's own data rather than a second copy that can drift from it.
			JointLimits limits = cannonJoint.limits;
			aimLimits.PitchMin = limits.min;
			aimLimits.PitchMax = limits.max;
			_aim.Pitch = TurretAimCore.ClampPitch(cannonJoint.spring.targetPosition, aimLimits);
		}
		if (towerJoint != null)
		{
			// Seeded once, from the prefab's authored pose, so the turret does not snap to
			// zero on the first fixed step. This is the LAST read out of the joint.
			_aim.Yaw = TurretAimCore.WrapDegrees(towerJoint.targetRotation.eulerAngles.z);
		}
	}

	protected override void Update()
	{
		// Weapon.Update owns fire timing and must keep running per-frame. Turret slew moved to
		// FixedUpdate (phase-v0 D4) because it drives a ConfigurableJoint and a HingeJoint
		// spring, and those are physics.
		base.Update();
	}

	private void FixedUpdate()
	{
		if (towerJoint == null)
		{
			return;
		}
		Vector2 input = GetInput();
		// Signs match the shipped code, which subtracted the input from the accumulated angle.
		// The old +/-5 degree per-frame bound is now the rate multiplied by the fixed step:
		// 300 * (1/60) = 5, so the cap is unchanged at the design framerate and correct away
		// from it.
		TurretAimCore.Step(ref _aim, 0f - input.x, 0f - input.y, aimLimits, Time.fixedDeltaTime);
		towerJoint.targetRotation = Quaternion.Euler(0f, 0f, _aim.Yaw);
		JointSpring spring = cannonJoint.spring;
		spring.targetPosition = _aim.Pitch;
		cannonJoint.spring = spring;
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

	// protected virtual so V4 can override the source without touching the integration. The
	// body is unchanged in V0.
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
		return new Vector2(vector.x * 3f, vector.y * 3f);
	}

	protected override Projectile SpawnProjectile(Vector3 direction)
	{
		rigidbody.AddForceAtPosition(-configuration.muzzle.forward * configuration.kickback + Random.insideUnitSphere * configuration.randomKick, configuration.muzzle.position, ForceMode.Impulse);
		return base.SpawnProjectile(direction);
	}
}
