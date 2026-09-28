using Ironfront.Net.Replication.Vehicles;
using Ironfront.Net.Unity;
using UnityEngine;

public class MountedTurret : MountedWeapon
{
	/// <summary>
	/// The shipped MAX_TURN_DELTA: the most the turret could move in one RENDERED frame, and
	/// the magnitude the shipped <c>Vector2.ClampMagnitude</c> bounded the pair to.
	/// </summary>
	/// <remarks>See <see cref="GetInput"/> — it survives only as the conversion constant.</remarks>
	private const float LEGACY_STEP_DEG = 10f;

	private const float LEGACY_FRAME_RATE = 60f;

	public Camera camera;

	public Transform towerTransform;

	public Transform turretTransform;

	// LEGACY_STEP_DEG x 60, and the -40/15 elevation stops that were inline literals at the
	// old :23. Serialized, so per-prefab tuning is data rather than a rebuild. Unlike
	// TankTurret these stops have no joint to read them from, so the client track owns them.
	public TurretAimLimits aimLimits = new TurretAimLimits
	{
		YawRateDegPerSec = LEGACY_STEP_DEG * LEGACY_FRAME_RATE,
		PitchRateDegPerSec = LEGACY_STEP_DEG * LEGACY_FRAME_RATE,
		PitchMin = -40f,
		PitchMax = 15f
	};

	// THE authoritative aim. Both transforms below are outputs of this pair.
	private TurretAimState _aim;

	// See TankTurret._pendingMouseAim: Input.GetAxis is a per-RENDERED-frame delta and cannot
	// be sampled from a fixed step without dropping or double-counting it.
	private Vector2 _pendingMouseAim;

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
		// The ONLY reads of an engine angle in this file, and they run once. Seeding from the
		// authored pose is what stops the gun snapping to zero the first time somebody mounts
		// it; every step after this drives the transforms FROM _aim.
		//
		// Mathf.DeltaAngle survives here and only here: localEulerAngles reports [0, 360), and
		// the stops are signed. The shipped code ran this conversion EVERY frame because it
		// re-read the transform every frame; it runs once now.
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
	// and costs nothing in determinism -- the value applied was integrated at a fixed rate in
	// FixedUpdate (phase-v0 D4).
	//
	// Still gated on `user != null`, exactly as the shipped code was. An unmanned turret keeps
	// its authored pose rather than being snapped to the clamped seed on the first frame of
	// the level. V4 widens this when a server starts aiming unmanned turrets; that is V4's
	// call to make, not a side effect of V0.
	protected override void Update()
	{
		base.Update();
		if (user == null)
		{
			return;
		}
		AccumulateMouseAim();
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
		StepNetAim();
	}

	/// <summary>
	/// Advances the aim for one fixed step, from whichever source this role owns. V6 task 2.
	/// </summary>
	/// <remarks>
	/// <para>
	/// The three cases, and the one rule that ties them together — <b>the integration never
	/// moves</b>. Offline and the local gunner run <c>TurretAimCore.Step</c> over their own
	/// demand; the server runs <c>StepToward</c> over the occupant's requested pose at the same
	/// slew rate; a remote client takes the decoded pose outright because it is drawing a result
	/// rather than deciding one. Nothing here reads an angle back out of a joint, which is the
	/// invariant V0 established and the reason a turret can be replicated at all.
	/// </para>
	/// <para>
	/// <b>The local gunner publishes what it integrated.</b> That value becomes the turret half of
	/// the next <c>C_VEHICLE_INPUT</c>, which the server then walks toward — so a client that
	/// writes a snap into it buys one step's arc, not a snap (acceptance criterion 2).
	/// </para>
	/// </remarks>
	private void StepNetAim()
	{
		ResolveNetSeat();

		if (netVehicleId != 0)
		{
			NetTurretAim.Declare(netVehicleId, netSeatIndex, aimLimits, _aim.Yaw, _aim.Pitch);
		}

		if (NetTurretAim.TryResolve(
				netVehicleId, netSeatIndex, netLocallyOccupied,
				out TurretAimSource source, out float yaw, out float pitch))
		{
			if (source == TurretAimSource.RemotePose)
			{
				// Applied, not integrated. Running the policy here would integrate a second time
				// from an input this peer does not have.
				SetAim(yaw, pitch);
				return;
			}

			if (source == TurretAimSource.ServerTarget)
			{
				TurretAimCore.StepToward(ref _aim, yaw, pitch, aimLimits, Time.fixedDeltaTime);
				return;
			}
		}

		StepLocalAim();

		if (netLocallyOccupied)
		{
			NetTurretAim.PublishLocal(_aim.Yaw, _aim.Pitch);
		}
	}

	/// <summary>The shipped integration, unchanged: offline, and the local gunner (D9).</summary>
	private void StepLocalAim()
	{
		Vector2 raw = GetInput();
		// The shipped code bounded the PAIR's magnitude, not each axis, so a diagonal drag
		// could not exceed the cap either. Normalized, that bound is 1.
		float x;
		float y;
		VehicleInputClamp.Magnitude(raw.x, raw.y, 1f, out x, out y);
		TurretAimCore.Step(ref _aim, x, 0f - y, aimLimits, Time.fixedDeltaTime);
	}

	public override void Unholster()
	{
		base.Unholster();
		_pendingMouseAim = Vector2.zero;
		// V10 task 3 (A16): was `!user.aiControlled`, so a remote human entering this turret
		// disabled the LOCAL player's cameras.
		if (NetWeaponAuthority.CosmeticHalfRunsHere
			&& Ironfront.Net.Unity.NetPresenterGate.IsLocalActor(user))
		{
			FpsActorController.instance.DisableCameras();
			if (camera != null)
			{
				camera.enabled = true;
			}
		}
	}

	public override void Holster()
	{
		base.Holster();
		_pendingMouseAim = Vector2.zero;
		// V6 task 2: `camera` is a serialized reference a stripped headless prefab need not
		// carry, and FpsActorController.instance does not exist on a headless build at all.
		// Both are cosmetics, and this closes two of the section 3.6 NREs without changing what
		// a client or an offline build does.
		if (camera != null)
		{
			camera.enabled = false;
		}
		// V10 task 3 (A16): the mirror of Unholster's guard above.
		if (NetWeaponAuthority.CosmeticHalfRunsHere
			&& Ironfront.Net.Unity.NetPresenterGate.IsLocalActor(user))
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
	/// This step's aim demand, NORMALIZED so that a magnitude of 1 means "traverse at the
	/// turret's full rate". See <see cref="TankTurret.GetInput"/> for why the mouse and the bot
	/// paths convert by different constants -- one is a distance, the other a state.
	/// </summary>
	protected virtual Vector2 GetInput()
	{
		if (user == null)
		{
			return Vector2.zero;
		}
		if (user.aiControlled)
		{
			Vector3 vector = configuration.muzzle.worldToLocalMatrix.MultiplyVector(user.controller.FacingDirection());
			return new Vector2(vector.x * 5f, vector.y * 5f) / LEGACY_STEP_DEG;
		}
		Vector2 drained = _pendingMouseAim;
		_pendingMouseAim = Vector2.zero;
		float yawArc = aimLimits.YawRateDegPerSec * Time.fixedDeltaTime;
		float pitchArc = aimLimits.PitchRateDegPerSec * Time.fixedDeltaTime;
		return new Vector2((yawArc > 0f) ? (drained.x / yawArc) : 0f, (pitchArc > 0f) ? (drained.y / pitchArc) : 0f);
	}
}
