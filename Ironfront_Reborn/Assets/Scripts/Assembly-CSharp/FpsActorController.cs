using System;
using Ironfront.Net.Unity;
using UnityEngine;
using UnityEngine.Audio;
using UnityEngine.Rendering;
using UnityStandardAssets.Characters.FirstPerson;
using UnityStandardAssets.ImageEffects;

[RequireComponent(typeof(FirstPersonController))]
public class FpsActorController : ActorController
{
	public const float BASE_SENSITIVITY = 4f;

	private const float DEATH_TO_LOADOUT_TIME = 2f;

	private const int USE_LAYER_MASK = 2048;

	private const float MAX_USE_DISTANCE = 3f;

	private const float SEAT_CAMERA_OFFSET_UP = 0.85f;

	private const float SEAT_CAMERA_OFFSET_FORWARD = 0.2f;

	private const float EXIT_VEHICLE_PAD_UP = 0.8f;

	public const float HELICOPTER_FOV = 75f;

	public const float HELICOPTER_ZOOM_FOV = 50f;

	public const float DEFAULT_FOV = 60f;

	public const float DEFAULT_ZOOM_FOV = 45f;

	private const float CAMERA_RETURN_SPEED = 400f;

	private const float FINE_AIM_FOV = 30f;

	private const float CROUCH_HEIGHT = 0.5f;

	private const float STAND_HEIGHT = 1.8f;

	private const float UNCROUCH_SPHERECAST_RADIUS = 0.3f;

	private const float UNCROUCH_SPHERECAST_DISTANCE = 2.1f;

	private const int UNCROUCH_SPHERECAST_MASK = 4097;

	private const int CAMERA_LAYER_MASK = 4097;

	public static FpsActorController instance;

	public static int playerTeam = -1;

	public Camera fpCamera;

	public Transform fpCameraParent;

	public Camera tpCamera;

	public PlayerFpParent fpParent;

	public Transform weaponParent;

	public SoundBank bulletFlybySoundbank;

	public AudioMixer mixer;

	public AudioMixerSnapshot defaultMix;

	public AudioMixerSnapshot deafMix;

	private NoiseAndGrain fpNoise;

	private NoiseAndGrain tpNoise;

	private CharacterController characterController;

	private FirstPersonController controller;

	private Renderer[] thirdpersonRenderers;

	private Vector3 fpCameraParentOffset;

	private Vector3 actorLocalOrigin;

	private bool inputEnabled = true;

	private bool aimToggle;

	[NonSerialized]
	public bool crouching;

	private bool mouseViewLocked;

	private Action cannotLeaveAction = new Action(1f);

	private Action hasNotBeenGroundedAction = new Action(1.5f);

	private Action sprintCannotFireAction = new Action(0.2f);

	private bool crouchInput;

	// Phase-00 task 3: every gameplay input below arrives through this, so a networked
	// controller can supply one. UI and debug keys keep reading Input directly -- criterion 6
	// permits it, and widening the seam to cover them buys nothing and risks the loadout screen.
	// See plans/unity-client/study/step-02-input-source.md and docs/codebase-map.md section 4.
	// Starts as the null object rather than null: MoveX, Lean and LookDelta* are plain property
	// reads with no extension-method guard behind them, and they sit on per-frame paths. A field
	// that can be null turns one ordering mistake into an exception every frame forever.
	private IInputSource inputSource = NullInputSource.Instance;

	/// <summary>The active input source. Local keyboard and mouse unless something replaced it.</summary>
	public IInputSource InputSource => inputSource;

	/// <summary>
	/// Replaces the input source. Call before Awake, or at any point afterwards; the controller
	/// re-reads it on every access rather than caching anything derived from it.
	/// </summary>
	public void SetInputSource(IInputSource source)
	{
		inputSource = source ?? NullInputSource.Instance;
	}

	/// <summary>
	/// Hands the netcode's tick loop this actor's fire/aim/reload bits and aim pitch.
	/// </summary>
	/// <remarks>
	/// <para>
	/// PUSHED FROM HERE, NOT PULLED FROM THERE. NetPredictionClock lives in the
	/// Ironfront.Net.Unity.Shared assembly, which declares no references and is the assembly the
	/// dedicated SERVER builds on; IInputSource lives in Assembly-CSharp, one layer up. Shared
	/// naming it would be a layering inversion the compiler refuses outright. So the layer that
	/// owns the seam installs a delegate into the layer that needs the value.
	/// </para>
	/// <para>
	/// Closures over the FIELD, not over its current value, so a later SetInputSource -- a
	/// scripted client, a network-driven actor -- is picked up with no re-install. That is also
	/// what makes debt-closure phase 3C's Lane B work without a second input path.
	/// </para>
	/// <para>
	/// Until this existed, ClientPredictionStage built its C_INPUT button mask from Jump, Sprint
	/// and Crouch alone and sent a hard-coded level pitch, so no networked player could fire,
	/// aim or reload at all and no shot could have been aimed -- debt-ledger row X-3.
	/// </para>
	/// </remarks>
	private void InstallNetworkCombatIntent()
	{
		Ironfront.Net.Unity.NetPredictionClock clock =
			GetComponent<Ironfront.Net.Unity.NetPredictionClock>();
		if (clock == null) return;

		clock.CombatButtonSource = () => (Ironfront.Net.Protocol.InputButtons)inputSource.Buttons;
		clock.AimPitchSource = () => inputSource.Pitch;
	}

	private void Awake()
	{
		instance = this;
		playerTeam = actor.team;
		controller = GetComponent<FirstPersonController>();
		characterController = GetComponent<CharacterController>();
		thirdpersonRenderers = actor.ragdoll.AnimatedRenderers();
		fpCameraParent = fpCamera.transform.parent;
		fpCameraParentOffset = fpCameraParent.transform.localPosition;
		fpNoise = fpCamera.GetComponent<NoiseAndGrain>();
		tpNoise = tpCamera.GetComponent<NoiseAndGrain>();
		if (inputSource == NullInputSource.Instance && !Ironfront.Net.Unity.NetContext.IsServer)
		{
			// Default to local input, so single-player runs exactly as it did before any
			// networking exists to override it. Anything that called SetInputSource before
			// Awake keeps what it set.
			//
			// NOT at server role (V5-D9). LocalInputSource reads OptionsUi.GetOptions() for the
			// helicopter axes -- per-user sensitivity and four invert flags that are a client's
			// business and that a headless process has no PlayerPrefs for. Reaching them from
			// the authority would be both an authority hole and an NRE waiting for the first
			// networked helicopter; the null object is the honest answer, and
			// ServerVehicleInputBridge replaces it with a NetInputSource the moment somebody
			// actually drives.
			// Aiming() folds in toggleAim and a latch LocalInputSource cannot see, so it is
			// handed over as a live delegate rather than duplicated there.
			inputSource = new LocalInputSource(fpCamera.transform, Aiming);
			// Temporary, and deliberately unconditional: the harness that says whether the
			// substitution above was correct. Delete both this line and InputShadowCompare.cs
			// once a playtest has come back quiet.
			InputShadowCompare.Install(base.gameObject, inputSource);
		}
		InstallNetworkCombatIntent();
		ForceEndCrouch();
	}

	private void Start()
	{
		SceneryCamera.instance.camera.enabled = true;
		actorLocalOrigin = actor.transform.localPosition;
		DisableInput();
		defaultMix.TransitionTo(0f);
	}

	public override bool Fire()
	{
		if (IngameMenuUi.IsOpen() || IsSprinting() || !sprintCannotFireAction.TrueDone())
		{
			return false;
		}
		return inputSource.Fire();
	}

	public override bool Aiming()
	{
		if (OptionsUi.GetOptions().toggleAim)
		{
			return aimToggle && !LoadoutUi.IsOpen();
		}
		return inputSource.Aim();
	}

	public override bool Reload()
	{
		return inputSource.Reload();
	}

	public override bool OnGround()
	{
		return controller.OnGround();
	}

	public override bool ProjectToGround()
	{
		return false;
	}

	public override Vector3 Velocity()
	{
		return controller.Velocity();
	}

	public override Vector3 SwimInput()
	{
		// The basis stays the third-person camera. The phase-00 mapping table proposed a
		// yaw/pitch basis here; that is a handling change to swimming, not a refactor, and
		// step 02 is a refactor. Only the two axis reads move.
		return tpCamera.transform.forward * inputSource.MoveZ + tpCamera.transform.right * inputSource.MoveX;
	}

	public override Vector3 FacingDirection()
	{
		return fpCamera.transform.forward;
	}

	private Camera ActiveCamera()
	{
		if (actor.fallenOver)
		{
			return tpCamera;
		}
		return fpCamera;
	}

	public override Vector2 BoatInput()
	{
		return CarInput();
	}

	public override Vector2 CarInput()
	{
		return new Vector2(inputSource.MoveX, inputSource.MoveZ);
	}

	public override Vector4 HelicopterInput()
	{
		// V5-D8: assembled from the four IInputSource members rather than computed here, so a
		// networked helicopter is expressible at all. LookDeltaX/Y is a per-frame mouse delta and
		// C_INPUT carries an absolute angle -- an absolute-angle protocol cannot express a delta,
		// so NetInputSource returns 0 for it and always will. The helicopterType == 2 branch was
		// worse still: it read UnityEngine.Input directly, past the seam entirely, booked as
		// accepted debt by a comment that lived here. Both now live in LocalInputSource, which is
		// where reading a keyboard is allowed, and this method is component order and nothing else.
		//
		// The component order is Helicopter.cs's contract and is pinned by HelicopterAxes:
		//   x = yaw, y = collective, z = roll (the vehicle negates it), w = pitch.
		return new Vector4(
			inputSource.HeliYaw,
			inputSource.HeliCollective,
			inputSource.HeliRoll,
			inputSource.HeliPitch);
	}

	public override bool UseMuzzleDirection()
	{
		return true;
	}

	public override void ReceivedDamage(float damage, float balanceDamage, Vector3 point, Vector3 direction, Vector3 force)
	{
		if (balanceDamage > 5f)
		{
			fpParent.ApplyScreenshake(balanceDamage / 6f, Mathf.CeilToInt(balanceDamage / 20f));
		}
		if (damage > 5f)
		{
			fpParent.KickCamera(new Vector3(UnityEngine.Random.Range(5f, 10f), UnityEngine.Random.Range(-10f, 10f), UnityEngine.Random.Range(-5f, 5f)));
		}
		if (balanceDamage > 50f)
		{
			Deafen();
		}
		Vector3 vector = ActiveCamera().transform.worldToLocalMatrix.MultiplyVector(-direction);
		float angle = Mathf.Atan2(vector.z, vector.x) * 57.29578f - 90f;
		IngameUi.instance.ShowDamageIndicator(angle, damage < 2f && balanceDamage > damage);
	}

	public void Deafen()
	{
		deafMix.TransitionTo(0.7f);
		CancelInvoke("Undeafen");
		Invoke("Undeafen", 5f);
	}

	private void Undeafen()
	{
		defaultMix.TransitionTo(8f);
	}

	public override void DisableInput()
	{
		characterController.enabled = false;
		controller.inputEnabled = false;
		inputEnabled = false;
	}

	public override void EnableInput()
	{
		characterController.enabled = true;
		controller.inputEnabled = true;
		inputEnabled = true;
	}

	public override void StartSeated(Seat seat)
	{
		controller.DisableCharacterController();
		controller.SetMouseEnabled(seat.type != Seat.Type.Pilot);
		mouseViewLocked = seat.type == Seat.Type.Pilot;
		fpCameraParent.parent = seat.transform;
		fpCameraParent.localPosition = Vector3.up * 0.85f + Vector3.forward * 0.2f;
		fpCameraParent.localRotation = Quaternion.identity;
		if (!seat.CanUseCarriedWeapon())
		{
			if (seat.vehicle.GetType() == typeof(Helicopter))
			{
				fpParent.SetFov(75f, 50f);
			}
			else
			{
				fpParent.SetAimFov(45f);
			}
		}
		if (!seat.CanUseCarriedWeapon())
		{
			HideFpModel();
		}
		IngameUi.instance.ShowVehicleBar(seat.vehicle.GetHealthRatio());
	}

	public override void EndSeated(Vector3 exitPosition, Quaternion flatFacing)
	{
		controller.EnableCharacterController();
		controller.SetMouseEnabled(true);
		mouseViewLocked = false;
		base.transform.position = exitPosition + 0.8f * Vector3.up;
		base.transform.rotation = flatFacing;
		fpCameraParent.parent = base.transform;
		fpCameraParent.localPosition = fpCameraParentOffset;
		fpCameraParent.localRotation = Quaternion.identity;
		SetupWeaponFov(actor.activeWeapon);
		ShowFpModel();
		actor.transform.position = exitPosition;
		IngameUi.instance.HideVehicleBar();
	}

	public override void StartRagdoll()
	{
		ThirdPersonCamera();
	}

	public override void GettingUp()
	{
		base.transform.position = actor.ragdoll.Position() + Vector3.up * characterController.height / 2f;
		actor.transform.localPosition = actorLocalOrigin;
		Debug.DrawRay(base.transform.position, Vector3.up * 100f, Color.green, 100f);
	}

	public override void EndRagdoll()
	{
		FirstPersonCamera();
	}

	public override void Die()
	{
		ThirdPersonCamera();
		UpdateThirdPersonCamera(true);
		Invoke("OpenLoadoutWhileDead", 2f);
	}

	public void OpenLoadoutWhileDead()
	{
		if (actor.dead)
		{
			OpenLoadout();
		}
	}

	public void OpenLoadout()
	{
		LoadoutUi.Show();
		controller.SetMouseEnabled(false);
	}

	public void CloseLoadout()
	{
		LoadoutUi.Hide();
		controller.SetMouseEnabled(true);
	}

	public override void SpawnAt(Vector3 position)
	{
		SceneryCamera.instance.camera.enabled = false;
		EnableInput();
		controller.transform.position = position + Vector3.up * (characterController.height / 2f);
		controller.ResetVelocity();
		controller.SetMouseEnabled(true);
		FirstPersonCamera();
		ForceEndCrouch();
	}

	public override void ApplyRecoil(Vector3 impulse)
	{
		fpParent.ApplyRecoil(impulse);
		Weapon activeWeapon = actor.activeWeapon;
		fpParent.ApplyWeaponSnap(activeWeapon.configuration.snapMagnitude, activeWeapon.configuration.snapDuration, activeWeapon.configuration.snapFrequency);
	}

	public override float Lean()
	{
		if (IsSprinting())
		{
			return 0f;
		}
		return inputSource.Lean;
	}

	private void HideFpModel()
	{
		if (actor.HasUnholsteredWeapon())
		{
			actor.activeWeapon.Hide();
		}
	}

	private void ShowFpModel()
	{
		if (actor.HasUnholsteredWeapon())
		{
			actor.activeWeapon.Show();
		}
	}

	private void ThirdPersonCamera()
	{
		fpCamera.enabled = false;
		tpCamera.enabled = true;
		Renderer[] array = thirdpersonRenderers;
		foreach (Renderer renderer in array)
		{
			renderer.shadowCastingMode = ShadowCastingMode.On;
		}
	}

	private void FirstPersonCamera()
	{
		fpCamera.enabled = true;
		tpCamera.enabled = false;
		Renderer[] array = thirdpersonRenderers;
		foreach (Renderer renderer in array)
		{
			renderer.shadowCastingMode = ShadowCastingMode.ShadowsOnly;
		}
	}

	private void FixedUpdate()
	{
		if (!characterController.enabled || characterController.isGrounded || actor.fallenOver || actor.dead || actor.IsSeated())
		{
			hasNotBeenGroundedAction.Start();
		}
		if (hasNotBeenGroundedAction.TrueDone() && !actor.fallenOver)
		{
			actor.FallOver();
		}
	}

	private void Update()
	{
		controller.sprinting = IsSprinting();
		if (IsSprinting())
		{
			sprintCannotFireAction.Start();
		}
		fpParent.lean = Lean();
		if (Input.GetButtonDown("Fire2"))
		{
			aimToggle = !aimToggle;
		}
		bool flag = actor.IsAiming();
		if (flag && actor.HasUnholsteredWeapon() && actor.activeWeapon.configuration.aimFov < 30f)
		{
			controller.SetMouseSensitivityMultiplier(OptionsUi.GetOptions().sniperMultiplier * OptionsUi.GetOptions().mouseSensitivity, OptionsUi.GetOptions().mouseInvert);
		}
		else
		{
			controller.SetMouseSensitivityMultiplier(OptionsUi.GetOptions().mouseSensitivity, OptionsUi.GetOptions().mouseInvert);
		}
		if (flag)
		{
			fpParent.Aim();
		}
		else
		{
			fpParent.StopAim();
		}
		if (mouseViewLocked)
		{
			controller.SetMouseEnabled(flag);
			if (!flag)
			{
				fpCameraParent.transform.localRotation = Quaternion.RotateTowards(fpCameraParent.transform.localRotation, Quaternion.identity, Time.deltaTime * 400f);
			}
		}
		if (Input.GetButtonDown("Loadout"))
		{
			if (LoadoutUi.IsOpen())
			{
				CloseLoadout();
			}
			else
			{
				OpenLoadout();
			}
		}
		if (Input.GetKeyDown(KeyCode.K))
		{
			actor.Damage(200f, 200f, true, actor.CenterPosition(), Vector3.forward, Vector3.zero);
		}
		if (Input.GetKeyDown(KeyCode.O))
		{
			ActorManager.instance.debug = !ActorManager.instance.debug;
		}
		if (Input.GetButtonDown("Slowmotion") && !IngameMenuUi.IsOpen())
		{
			if (Time.timeScale < 1f)
			{
				Time.timeScale = 1f;
			}
			else
			{
				Time.timeScale = 0.2f;
			}
			Time.fixedDeltaTime = Time.timeScale / 60f;
			mixer.SetFloat("pitch", Time.timeScale);
		}
		if (inputEnabled)
		{
			UpdateInput();
		}
		if (!Input.GetButtonDown("Use"))
		{
			return;
		}
		if (!actor.IsSeated())
		{
			if (actor.CanEnterSeat())
			{
				SampleUseRay();
			}
		}
		else if (cannotLeaveAction.TrueDone())
		{
			actor.LeaveSeat();
		}
	}

	// Everything below is edge-triggered -- GetKeyDown, GetButtonDown, mouseScrollDelta --
	// and IInputSource reports levels, not edges. Weapon and seat selection do affect gameplay
	// and phase-00 section 5 books them as debt to be paid in phase 02, when the C_INPUT
	// weapon-switch bits (11..14) get a consumer. Routing an edge through a level channel now
	// would either drop presses or fire them twice.
	private void UpdateInput()
	{
		if (Input.GetKeyDown(KeyCode.Alpha1))
		{
			actor.SwitchWeapon(0);
		}
		if (Input.GetKeyDown(KeyCode.Alpha2))
		{
			actor.SwitchWeapon(1);
		}
		if (Input.GetKeyDown(KeyCode.Alpha3))
		{
			actor.SwitchWeapon(2);
		}
		if (Input.GetKeyDown(KeyCode.Alpha4))
		{
			actor.SwitchWeapon(3);
		}
		if (Input.GetKeyDown(KeyCode.Alpha5))
		{
			actor.SwitchWeapon(4);
		}
		if (Input.GetKeyDown(KeyCode.F1))
		{
			actor.SwitchSeat(0);
		}
		if (Input.GetKeyDown(KeyCode.F2))
		{
			actor.SwitchSeat(1);
		}
		if (Input.GetKeyDown(KeyCode.F3))
		{
			actor.SwitchSeat(2);
		}
		if (Input.GetKeyDown(KeyCode.F4))
		{
			actor.SwitchSeat(3);
		}
		if (Input.GetKeyDown(KeyCode.F5))
		{
			actor.SwitchSeat(4);
		}
		if (Input.GetKeyDown(KeyCode.F6))
		{
			actor.SwitchSeat(5);
		}
		if (Input.GetKeyDown(KeyCode.F7))
		{
			actor.SwitchSeat(6);
		}
		if (Input.GetKeyDown(KeyCode.F8))
		{
			actor.SwitchSeat(7);
		}
		if (OptionsUi.GetOptions().toggleCrouch && Input.GetButtonDown("Crouch"))
		{
			crouchInput = !crouchInput;
		}
		if (Input.mouseScrollDelta.y < 0f)
		{
			actor.NextWeapon();
		}
		else if (Input.mouseScrollDelta.y > 0f)
		{
			actor.PreviousWeapon();
		}
	}

	private void SampleUseRay()
	{
		Ray ray = ((!actor.fallenOver) ? new Ray(fpCamera.transform.position, fpCamera.transform.forward) : new Ray(actor.CenterPosition(), tpCamera.transform.forward + tpCamera.transform.up * 0.2f));
		RaycastHit hitInfo;
		if (Physics.Raycast(ray, out hitInfo, 3f, 2048) && hitInfo.collider.gameObject.layer == 11)
		{
			Seat component = hitInfo.collider.GetComponent<Seat>();
			actor.EnterSeat(component);
			cannotLeaveAction.Start();
		}
	}

	private void LateUpdate()
	{
		if (tpCamera.enabled)
		{
			UpdateThirdPersonCamera();
		}
	}

	private void UpdateThirdPersonCamera(bool forceUseActorPosition = false)
	{
		tpCamera.transform.rotation = fpCamera.transform.rotation;
		if (!actor.dead || forceUseActorPosition)
		{
			Vector3 vector = -tpCamera.transform.forward * 3f;
			Ray ray = new Ray(actor.CenterPosition() + Vector3.up * 0.5f, vector);
			RaycastHit hitInfo;
			if (Physics.SphereCast(ray, 0.3f, out hitInfo, vector.magnitude, 4097))
			{
				tpCamera.transform.position = hitInfo.point + hitInfo.normal * 0.15f;
			}
			else
			{
				tpCamera.transform.position = ray.origin + vector;
			}
		}
	}

	public override SpawnPoint SelectedSpawnPoint()
	{
		if (GameManager.instance.spectating || !LoadoutUi.HasBeenOpen())
		{
			return null;
		}
		SpawnPoint spawnPoint = MinimapUi.SelectedSpawnPoint();
		if (spawnPoint == null || spawnPoint.owner != actor.team)
		{
			return null;
		}
		return spawnPoint;
	}

	public override Transform WeaponParent()
	{
		return weaponParent;
	}

	public override void SwitchedToWeapon(Weapon weapon)
	{
		SetupWeaponFov(weapon);
	}

	private void SetupWeaponFov(Weapon weapon)
	{
		if (weapon != null)
		{
			fpParent.SetAimFov(weapon.configuration.aimFov);
		}
		else
		{
			fpParent.SetAimFov(45f);
		}
	}

	public override WeaponManager.LoadoutSet GetLoadout()
	{
		return LoadoutUi.instance.loadout;
	}

	public override bool Crouch()
	{
		if (OptionsUi.GetOptions().toggleCrouch)
		{
			return crouchInput;
		}
		return inputSource.Crouch();
	}

	public override void StartCrouch()
	{
		characterController.height = 0.5f;
		crouching = true;
	}

	public override bool EndCrouch()
	{
		Ray ray = new Ray(actor.Position(), Vector3.up);
		bool flag = Physics.SphereCast(ray, 0.3f, 2.1f, 4097);
		if (!flag)
		{
			crouching = false;
			ForceEndCrouch();
		}
		return !flag;
	}

	private void ForceEndCrouch()
	{
		characterController.height = 1.8f;
		characterController.transform.position = characterController.transform.position + Vector3.up * 1.3f / 2f;
		crouchInput = false;
	}

	public override bool IsGroupedUp()
	{
		return false;
	}

	private bool IsReloading()
	{
		return actor.HasUnholsteredWeapon() && actor.activeWeapon.reloading;
	}

	public override bool IsSprinting()
	{
		return !Crouch() && !Aiming() && !IsReloading() && inputSource.Sprint() && !actor.IsSeated();
	}

	public void DisableCameras()
	{
		fpCamera.enabled = false;
		tpCamera.enabled = false;
	}

	public void DisableAudioListener()
	{
		fpCamera.GetComponent<AudioListener>().enabled = false;
	}

	public void EnableCameras()
	{
		FirstPersonCamera();
	}

	public void EnableNoise()
	{
		fpNoise.enabled = true;
		tpNoise.enabled = true;
	}

	public void DisableNoise()
	{
		fpNoise.enabled = false;
		tpNoise.enabled = false;
	}

	public void BulletFlyby(Vector3 position, float pitch)
	{
		bulletFlybySoundbank.transform.position = position;
		bulletFlybySoundbank.audioSource.pitch = pitch;
		bulletFlybySoundbank.PlayRandom();
	}
}
