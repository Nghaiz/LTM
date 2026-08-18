using Ironfront.Net.Replication.Vehicles;
using UnityEngine;

public class TankTurret : MountedWeapon
{
	/// <summary>
	/// The shipped MAX_TURN_DELTA: the most the turret could move in one RENDERED frame.
	/// </summary>
	/// <remarks>
	/// It survives as the conversion constant between the old per-frame world and the rate
	/// world, and nothing else. <c>aimLimits.YawRateDegPerSec</c> defaults to this times 60,
	/// so at the original's design framerate the two agree exactly.
	/// </remarks>
	private const float LEGACY_STEP_DEG = 5f;

	private const float LEGACY_FRAME_RATE = 60f;

	public Camera camera;

	public ConfigurableJoint towerJoint;

	public HingeJoint cannonJoint;

	public Renderer cannonRenderer;

	// Serialized so retuning a turret is a prefab edit rather than a rebuild. LEGACY_STEP_DEG
	// x 60 -- i.e. the rate the original game exhibits when the player saturates its per-frame
	// cap. That a 144 Hz client now saturates at the same rate instead of 2.4x it is the
	// accepted behaviour change recorded in phase-v0 D8.
	//
	// PitchMin/PitchMax are OVERWRITTEN from the cannon joint's own limits in Awake -- the
	// joint is the single source of truth for the elevation stops, so serializing them here
	// would be a second copy that can drift. Only the two rates are prefab-tunable.
	public TurretAimLimits aimLimits = new TurretAimLimits
	{
		YawRateDegPerSec = LEGACY_STEP_DEG * LEGACY_FRAME_RATE,
		PitchRateDegPerSec = LEGACY_STEP_DEG * LEGACY_FRAME_RATE,
		PitchMin = -10f,
		PitchMax = 20f
	};

	// THE authoritative aim. The joint and the spring below are outputs of this pair; nothing
	// reads an angle back out of them after Awake. Replicated in V4.
	private TurretAimState _aim;

	// Mouse motion accumulated since the last fixed step. Input.GetAxis("Mouse X") is the
	// delta since the last RENDERED frame and is refreshed once per Update, so sampling it
	// straight from FixedUpdate would drop ~65% of the player's motion at 144 fps and
	// double-count it at 30 -- trading the framerate dependence this phase removes for a
	// lossier one. Accumulating in Update and draining here conserves the total.
	private Vector2 _pendingMouseAim;

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
		// The ONLY reads of an engine angle in this file, and they run once. Seeding from the
		// authored pose is what stops the turret snapping to zero on the first fixed step;
		// every subsequent frame drives the joint FROM _aim and never reads it back.
		if (cannonJoint != null)
		{
			JointLimits limits = cannonJoint.limits;
			aimLimits.PitchMin = limits.min;
			aimLimits.PitchMax = limits.max;
			_aim.Pitch = TurretAimCore.ClampPitch(cannonJoint.spring.targetPosition, aimLimits);
		}
		if (towerJoint != null)
		{
			_aim.Yaw = TurretAimCore.WrapDegrees(towerJoint.targetRotation.eulerAngles.z);
		}
	}

	protected override void Update()
	{
		// Weapon.Update owns fire timing and must keep running per-frame. Turret slew moved to
		// FixedUpdate (phase-v0 D4) because it drives a ConfigurableJoint and a HingeJoint
		// spring, and those are physics. Only the mouse latch stays here, because the value it
		// latches only exists per rendered frame.
		base.Update();
		AccumulateMouseAim();
	}

	private void FixedUpdate()
	{
		if (towerJoint == null)
		{
			return;
		}
		Vector2 demand = GetInput();
		// Signs match the shipped code, which subtracted the input from the accumulated angle.
		TurretAimCore.Step(ref _aim, 0f - demand.x, 0f - demand.y, aimLimits, Time.fixedDeltaTime);
		towerJoint.targetRotation = Quaternion.Euler(0f, 0f, _aim.Yaw);
		JointSpring spring = cannonJoint.spring;
		spring.targetPosition = _aim.Pitch;
		cannonJoint.spring = spring;
	}

	public override void Unholster()
	{
		base.Unholster();
		_pendingMouseAim = Vector2.zero;
		// V10 task 3 (A16): was `!user.aiControlled`, so a remote human entering this turret
		// disabled the LOCAL player's cameras.
		if (Ironfront.Net.Unity.Client.NetClientPresenterGuard.IsLocalActor(user))
		{
			FpsActorController.instance.DisableCameras();
			camera.enabled = true;
		}
	}

	public override void Holster()
	{
		base.Holster();
		// Drop motion the player made before letting go, so it cannot be applied on remount.
		_pendingMouseAim = Vector2.zero;
		camera.enabled = false;
		// V10 task 3 (A16): the mirror of Unholster's guard above.
		if (Ironfront.Net.Unity.Client.NetClientPresenterGuard.IsLocalActor(user))
		{
			FpsActorController.instance.EnableCameras();
		}
	}

	private void AccumulateMouseAim()
	{
		if (user == null || user.aiControlled)
		{
			return;
		}
		_pendingMouseAim += new Vector2(Input.GetAxis("Mouse X"), Input.GetAxis("Mouse Y") * (float)((!OptionsUi.GetOptions().mouseInvert) ? 1 : (-1))) * OptionsUi.GetOptions().mouseSensitivity * 4f;
	}

	/// <summary>
	/// This step's aim demand, NORMALIZED so that 1 means "traverse at the turret's full rate".
	/// </summary>
	/// <remarks>
	/// <para>
	/// <b>The normalization is the whole port, and getting it wrong is silent.</b> The shipped
	/// line was <c>Clamp(z - input.x, z - 5f, z + 5f)</c>, whose bounds are derived from the
	/// value being clamped -- so algebraically it is <c>z -= Clamp(input.x, -5f, +5f)</c>: a
	/// 1:1 mouse-degrees mapping with a speed limit, NOT a rate at full deflection. Feeding
	/// that raw number into a rate integrator that clamps it to [-1, 1] first would multiply
	/// player sensitivity by 5 and turn the bots' proportional aim controller into bang-bang.
	/// </para>
	/// <para>
	/// The two sources convert differently because they are different quantities:
	/// </para>
	/// <list type="bullet">
	/// <item>
	/// <b>Mouse</b> is a DISTANCE already summed over the frames since the last step, so it
	/// divides by the arc this step can cover. Total degrees per second of real hand movement
	/// is then identical at every framerate, and the rate acts as the ceiling it always was.
	/// </item>
	/// <item>
	/// <b>Bot facing</b> is a STATE -- an error term the shipped code multiplied by 3 and let
	/// saturate at 5 -- so it divides by that constant. At the design framerate this is
	/// exactly the original: <c>clamp(err * 3.6, -6, 6)</c> per 50 Hz step is
	/// <c>clamp(err * 180, -300, 300)</c> deg/s, the same number the old per-frame form gives.
	/// </item>
	/// </list>
	/// <para>
	/// <c>protected virtual</c> so V4 can replace the source with the wire without touching the
	/// integration -- and the wire carries this normalized demand, not raw mouse degrees.
	/// </para>
	/// </remarks>
	protected virtual Vector2 GetInput()
	{
		if (user == null)
		{
			return Vector2.zero;
		}
		if (user.aiControlled)
		{
			Vector3 vector = configuration.muzzle.worldToLocalMatrix.MultiplyVector(user.controller.FacingDirection());
			return new Vector2(vector.x * 3f, vector.y * 3f) / LEGACY_STEP_DEG;
		}
		Vector2 drained = _pendingMouseAim;
		_pendingMouseAim = Vector2.zero;
		float yawArc = aimLimits.YawRateDegPerSec * Time.fixedDeltaTime;
		float pitchArc = aimLimits.PitchRateDegPerSec * Time.fixedDeltaTime;
		return new Vector2((yawArc > 0f) ? (drained.x / yawArc) : 0f, (pitchArc > 0f) ? (drained.y / pitchArc) : 0f);
	}

	protected override Projectile SpawnProjectile(Vector3 direction)
	{
		rigidbody.AddForceAtPosition(-configuration.muzzle.forward * configuration.kickback + Random.insideUnitSphere * configuration.randomKick, configuration.muzzle.position, ForceMode.Impulse);
		return base.SpawnProjectile(direction);
	}
}
