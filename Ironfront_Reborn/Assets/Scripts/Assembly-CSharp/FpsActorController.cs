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

	/// <summary>
	/// The team the human at this keyboard is fighting for, or <see cref="UNKNOWN_TEAM"/> when
	/// there is no local body to ask.
	/// </summary>
	/// <remarks>
	/// <para>
	/// <b>A property over the body, not a field latched at <c>Awake</c> — P12 D-1.</b> This was
	/// <c>public static int playerTeam = -1;</c> assigned once from <c>actor.team</c> in
	/// <c>Awake</c>. On a networked client the body's team arrives with the first snapshot,
	/// which is always AFTER <c>Awake</c>, so the latch held the prefab's authored value for the
	/// whole session: every reader below answered for the wrong side and nothing errored.
	/// </para>
	/// <para>
	/// Reading through to the body is the fix rather than a second write, because a second write
	/// only moves the question to "did that one run late enough". There is exactly one answer to
	/// "what team is the local player on" and it lives on the local player. The three readers —
	/// <c>ActorBlip.LateUpdate</c>, <c>AiActorController</c> twice — are unchanged and now
	/// cannot observe a stale value at all.
	/// </para>
	/// <para>
	/// <b><see cref="UNKNOWN_TEAM"/> is still <c>-1</c>, deliberately.</b> That is the value the
	/// shipped code used and the value <c>AiActorController</c>'s own comment names; the wire's
	/// <c>TeamId.None</c> (255) is a different sentinel for a different layer and
	/// <c>MinimapUi</c> is where the two meet. What matters to every reader here is only that it
	/// is neither 0 nor 1 — a sentinel of 0 is exactly the bug P12 closes.
	/// </para>
	/// </remarks>
	public static int playerTeam
	{
		get
		{
			FpsActorController local = instance;
			return local != null && local.actor != null ? local.actor.team : UNKNOWN_TEAM;
		}
	}

	/// <summary>The team value meaning "no local body, or its team has not arrived yet".</summary>
	public const int UNKNOWN_TEAM = -1;

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

	/// <summary>Where the offline player's current fall started (<see cref="TrackOfflineFall"/>).</summary>
	private readonly Ironfront.Net.Replication.Movement.FallTracker offlineFall = new Ironfront.Net.Replication.Movement.FallTracker();

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

	// Unity key/mouse edges last for one rendered frame, while C_INPUT is sampled by a separate
	// 30 Hz clock. Keep these edges until that clock has actually included them in a frame. A
	// scripted test held both values for seconds and therefore could not expose this race.
	private int pendingNetworkWeaponSlot = -1;

	// A slot pressed while a shot is still waiting for its C_INPUT frame. The server applies a
	// frame's slot before its trigger, so a tap and a swap inside one 33 ms tick reached it as
	// "switch, then fire the new gun": the shot the player saw leave the launcher was never
	// resolved and never spent (owner report 2026-10-04). Held back one frame, the server reads
	// them in the order they were pressed.
	private int deferredNetworkWeaponSlot = -1;

	private bool pendingNetworkFire;

	/// <summary>
	/// Set when the trigger goes up, and carried to the server by its own frame.
	/// </summary>
	/// <remarks>
	/// <b>A release is not a formality on the accepted-input path.</b> That path re-arms a
	/// semi-automatic's trigger edge only on a frame that ARRIVES with <c>Fire</c> clear -- see
	/// <c>SemiAutoTriggerEdgeTests.ReleasingAndPressingAgainArmsTheEdgeForASecondRound</c>. A
	/// client that sends a frame only when it has something pending never sends that one, so
	/// after the first press the edge stayed spent and every later press was ignored until
	/// something else -- a slot change, a sprint, a reload -- happened to push a frame out.
	/// For a throwable the visible half of that is a throw whose animation played, whose ammo
	/// was spent, and whose grenade never left: the server had no edge to fire on.
	/// </remarks>
	private bool pendingNetworkFireRelease;

	/// <summary>Last render frame's trigger state, so the release is read as an edge.</summary>
	private bool wasFireHeld;

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

		clock.AimPitchSource = () => inputSource.Pitch;
		clock.SimulationEnabled = () => inputEnabled && actor != null && !actor.dead && !actor.IsSeated();
		clock.KeepButtonsWhileSuspended = () => inputEnabled && actor != null && !actor.dead && actor.IsSeated();
		clock.CombatButtonSource = SampleNetworkCombatButtons;

		// Crouch() and not Input.GetButton("Crouch"): with the toggle-crouch option on the state
		// is a latch, so a player who taps once and releases is crouched while the button reads
		// false. Everything downstream reads the state -- Actor.Update calls StartCrouch() from
		// Crouch() -- so the wire and NetMovementAgent.ApplyStanceHeight have to as well, or the
		// capsule has two writers that disagree every tick and the server stands the body up
		// behind cover the player believes they are behind.
		clock.CrouchSource = () => Crouch();

		// The composite, not the key, and this one is the difference between a shot and no shot:
		// a sprinting body's weapon is holstered on both sides of the wire. Holding Shift while
		// aiming is NOT sprinting -- the game fires and spends the round -- so sending the raw key
		// made the client's prediction and the server refuse a shot the game had already taken,
		// and the next snapshot wrote the round back. The magazine never emptied.
		clock.SprintSource = () => IsSprinting();

		clock.OnTickSimulated += OnNetworkTickSimulated;
	}

	private Ironfront.Net.Protocol.InputButtons SampleNetworkCombatButtons()
	{
		Ironfront.Net.Protocol.InputButtons buttons =
			(Ironfront.Net.Protocol.InputButtons)inputSource.Buttons;
		if (pendingNetworkFire)
		{
			buttons |= Ironfront.Net.Protocol.InputButtons.Fire;
		}
		// The original game starts auto-reload inside Weapon.AmmoChanged(), not from an input button.
		// Mirror that already-started reload onto C_INPUT so the server fills the authoritative
		// clip too; otherwise the local animation spends reserve ammo and the next snapshot puts
		// the clip straight back to zero. This also covers grenades and launchers.
		if (actor != null && actor.activeWeapon != null && actor.activeWeapon.reloading)
		{
			buttons |= Ironfront.Net.Protocol.InputButtons.Reload;
		}
		buttons |= Ironfront.Net.Protocol.InputFrame.SlotBit(pendingNetworkWeaponSlot);
		return buttons;
	}

	private void OnNetworkTickSimulated(
		uint tick, Ironfront.Net.Replication.Movement.MoveInput input)
	{
		// The shot has gone (or was dropped): a slot held behind it goes on the next frame.
		if (deferredNetworkWeaponSlot >= 0 && !pendingNetworkFire)
		{
			pendingNetworkWeaponSlot = deferredNetworkWeaponSlot;
			deferredNetworkWeaponSlot = -1;
		}
		if (pendingNetworkWeaponSlot < 0 && !pendingNetworkFire && !pendingNetworkFireRelease) return;

		bool sentFire = pendingNetworkFire && input.Fire;
		bool sentRelease = pendingNetworkFireRelease && !input.Fire;
		bool sentSlot = pendingNetworkWeaponSlot >= 0
			&& input.WeaponSlot == pendingNetworkWeaponSlot;
		if (!sentFire && !sentRelease && !sentSlot) return;

		Debug.Log($"[input] C_INPUT tick {tick} buffered fire={sentFire} release={sentRelease} slot="
			+ $"{(sentSlot ? input.WeaponSlot : -1)}");
		if (sentFire)
		{
			pendingNetworkFire = false;
			if (deferredNetworkWeaponSlot >= 0)
			{
				pendingNetworkWeaponSlot = deferredNetworkWeaponSlot;
				deferredNetworkWeaponSlot = -1;
			}
		}
		if (sentRelease) pendingNetworkFireRelease = false;
		if (sentSlot) pendingNetworkWeaponSlot = -1;
	}

	private void Awake()
	{
		NetClientBindings.OfflineSeatCandidate = ProbeOfflineSeat;
		instance = this;

		// P12 D-1. The prefab used to author `team: 0` on this body, and that literal was the
		// ONLY thing that ever set the local player's team: GameManager.StartGame instantiates
		// the rig and nothing calls SetTeam on it (ActorManager.CreateAIActor does that for
		// bots, IronfrontNetBindings.CreatePlayerBody for server-side bodies). On a networked
		// client the literal was simply wrong — a team-1 player believed it was team 0 — so the
		// prefab now authors UNKNOWN_TEAM and the answer comes from whoever knows it.
		//
		// Offline, that is here, and the literal 0 is the same one MinimapUi.UpdateSpawnPointButtons
		// already carries for the same reason (V10 D16): the human is always team 0 in
		// single-player, so this keeps offline byte-for-byte what it was. Networked, the answer
		// comes from the snapshot via NetClientLocalCombatDriver — deliberately not from here,
		// because it has not arrived yet at Awake and that is the whole defect.
		//
		// SetTeam rather than a bare field write: it also recolours the two skinned renderers,
		// which is what the prefab's authored literal never did. Done in Start, not here: the
		// Actor sits on a child ("Actor Parent") whose Awake runs after this one, so its
		// renderers were still null and SetTeam threw -- which aborted this Awake before
		// `controller` was set, and every offline frame after that threw from Velocity() and
		// the loadout never opened.

		controller = GetComponent<FirstPersonController>();
		controller.externalMovementAuthority = NetContext.IsClient;
		// The netcode moves this capsule from Update, so CharacterController.velocity reads the
		// frame rate as much as the walk. Footsteps, the weapon bob and the body's walk blend take
		// the tick's own displacement instead: see NetMovementAgent.TickVelocity.
		Ironfront.Net.Unity.NetMovementAgent movementAgent =
			GetComponent<Ironfront.Net.Unity.NetMovementAgent>();
		if (controller.externalMovementAuthority && movementAgent != null)
		{
			controller.externalVelocitySource = () => movementAgent.TickVelocity;
		}
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
			// handed over as a live delegate rather than duplicated there. The sprint bit is
			// handed over for the same reason and one more: it is the COMPOSITE the trigger rule
			// on both sides of the wire is built on, and sending the raw Sprint key in its place
			// made a held Shift while aiming refuse every shot the game had already fired.
			//
			// It takes the three keys rather than reading them back, and that is what keeps this
			// from recursing. The gate is written in terms of Crouch() and Aiming(), which read
			// through inputSource -- so a no-argument delegate asked inputSource for the crouch
			// bit while inputSource was still working out the sprint bit, and that recomputed the
			// sprint bit. Infinite, and it overflowed the stack on the first frame after a map
			// loaded. CrouchFrom/AimFrom are the same two rules taking the key as an argument, so
			// there is still exactly one definition of each and no route back into the source.
			inputSource = new LocalInputSource(
				fpCamera.transform, Aiming, SampleWeaponSlotIntent,
				(crouchKey, aimKey, sprintKey) =>
					!CrouchFrom(crouchKey) && !AimFrom(aimKey) && !IsReloading()
					&& sprintKey && !actor.IsSeated());
			// InputShadowCompare, the temporary harness that checked this substitution against the
			// expressions it replaced, was removed after the v3.1.1 playtests of 2026-10-01: nine of
			// its ten sites never diverged over 311,480 frames in six sessions, and the tenth,
			// Sprint, diverged only because the sprint bit was deliberately changed to mean "is
			// sprinting" (see LocalInputSource's sprinting parameter) after it was written.
		}
		InstallNetworkCombatIntent();
		ForceEndCrouch();
	}

	private void Start()
	{
		// See Awake's remark (P12 D-1): offline, the human is always team 0.
		if (NetContext.IsOffline && actor != null && actor.team == UNKNOWN_TEAM)
		{
			actor.SetTeam(0);
		}
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
		return AimFrom(inputSource.Aim());
	}

	/// <summary>
	/// <see cref="Aiming"/>'s rule, given the key rather than fetching it from the input source.
	/// </summary>
	/// <remarks>Same reason as <see cref="CrouchFrom"/>.</remarks>
	private bool AimFrom(bool key)
	{
		if (OptionsUi.GetOptions().toggleAim)
		{
			return aimToggle && !LoadoutUi.IsOpen();
		}
		return key;
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

	/// <summary>
	/// Whether this controller is currently reading the player's input. Read-only.
	/// </summary>
	/// <remarks>
	/// Exists for the lane-B artifact and nothing else. Check 13 is "death -> input disable ->
	/// respawn screen", and the harness could record the death and the respawn window and NOT
	/// the term in the middle: <c>inputEnabled</c> is private, and the obvious proxy is a trap,
	/// because <c>DisableInput</c> also clears <c>characterController.enabled</c> while X-19's
	/// fix has <c>ClientPredictionStage</c> RE-ASSERTING that capsule every tick. So the capsule
	/// says nothing about input.
	///
	/// Observation only -- no setter, no behaviour. Phase-3d section 6 permits read-only
	/// accessors by a decision recorded in that file.
	/// </remarks>
	public bool IsInputEnabled => inputEnabled;

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

	/// <summary>
	/// The death camera for a body the server killed: third person, kept on the corpse until the
	/// next return to first person.
	/// </summary>
	/// <remarks>
	/// <para>
	/// Playtest 2026-09-28, bug 1. <see cref="Die"/> places the third-person camera behind the body
	/// once, with the sweep that keeps it out of terrain and vehicle hulls; a networked death never
	/// ran it, and marks the actor dead in the same call, so <see cref="UpdateThirdPersonCamera"/>
	/// never moved the camera either. On foot that went unnoticed -- the camera's rest pose is
	/// already behind the body. A pilot killed with his helicopter is set down at the seat's exit,
	/// often in the air, and the corpse fell out of a camera left hanging there.
	/// </para>
	/// <para>
	/// Following rather than placing once is what <c>NetClientLocalCombatDriver</c> documents for
	/// the death camera ("the body falls and the camera follows it"), and it keeps a corpse that
	/// falls fifty metres in frame.
	/// </para>
	/// </remarks>
	public void FollowCorpse()
	{
		followingCorpse = true;
		ThirdPersonCamera();
		UpdateThirdPersonCamera(true);
	}

	public override void Die()
	{
		// Cleared here so the deploy screen can come back for the next life. This is the one
		// place that must undo it: a corpse is exactly the state the menu view is FOR.
		deployedView = false;
		ThirdPersonCamera();
		UpdateThirdPersonCamera(true);
		Invoke("OpenLoadoutWhileDead", 2f);
	}

	public void OpenLoadoutWhileDead()
	{
		// Ledger X-48. GameManager schedules this by name through Invoke("OpenPlayerLoadout", 1f),
		// so it lands a full second after StartGame -- and on a networked client the server's
		// S_SPAWN_ACTOR can arrive inside that second. Without this guard the deploy screen we
		// just dismissed reopens on a timer nobody can see, which is worse than never dismissing
		// it: it looks intermittent.
		//
		// Guarded on deployedView rather than on actor.dead because the two are not the same
		// question on this path. actor.dead is the CLIENT's copy of a flag the server owns, and
		// nothing on the client's spawn path clears it -- ServerCombatBridge.PlaceAtSpawn writes
		// IsAlive on the SERVER's actor, one process over.
		if (deployedView)
		{
			return;
		}
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

	/// <summary>
	/// Opens the stock loadout UI for a network player's first life without pretending the
	/// player died and without granting a spawn locally.  The server still places the body only
	/// after the UI's Deploy button is consumed by NetClientLocalCombatDriver.
	/// </summary>
	public void OpenInitialNetworkLoadout()
	{
		if (deployedView || LoadoutUi.IsOpen())
		{
			return;
		}

		DisableInput();
		OpenLoadout();
	}

	/// <summary>
	/// The networked counterpart of <see cref="OpenLoadoutWhileDead"/>, for a body the server
	/// killed. <see cref="Die"/> is what clears <see cref="deployedView"/> offline and a networked
	/// body never runs it, so the guard in <see cref="OpenLoadoutWhileDead"/> would refuse; this
	/// clears it first, then opens the same screen.
	/// </summary>
	public void OpenLoadoutAfterNetworkDeath()
	{
		deployedView = false;
		if (!LoadoutUi.IsOpen())
		{
			OpenLoadout();
		}
	}

	public void CloseLoadout()
	{
		LoadoutUi.Hide();
		controller.SetMouseEnabled(true);
	}

	/// <summary>
	/// Set by <see cref="DeployFromLoadout"/> and cleared by
	/// <see cref="ConsumeLoadoutDeployPressed"/>. An edge, not a level.
	/// </summary>
	private bool loadoutDeployPressed;

	/// <summary>
	/// The loadout screen's Deploy button, as distinct from every other reason the loadout
	/// closes.
	/// </summary>
	/// <remarks>
	/// <para>
	/// <b>Why this is not simply <see cref="CloseLoadout"/>.</b> <see cref="EnterDeployedView"/>
	/// calls <c>CloseLoadout</c> too, and it runs when the SERVER confirms a spawn — so latching
	/// the edge inside <c>CloseLoadout</c> would post a deploy request in response to the
	/// server's answer to the previous one. The two callers want different things and only one
	/// of them is the player asking.
	/// </para>
	/// <para>
	/// <b>Why the edge exists at all.</b> Offline, closing the loadout IS the deploy: a spawn
	/// wave puts the body down and nothing has to be asked for. On a client the body is placed
	/// by the server and only <c>C_SPAWN_REQUEST</c> starts that, so this screen — the one the
	/// player is actually looking at on their first spawn — had no way to send it. The deploy
	/// screen's own button could not stand in: that panel is authored as the DEATH screen,
	/// titled in the scene, and showing it before anyone has died is what put "YOU WERE KILLED /
	/// Killed by actor 0" in front of every player on their first spawn.
	/// </para>
	/// <para>
	/// The request carries the loadout, so reading the edge after <c>LoadoutUi</c> has finalized
	/// the selection is also what makes the five slots the player just chose the ones that go on
	/// the wire.
	/// </para>
	/// </remarks>
	public void DeployFromLoadout()
	{
		loadoutDeployPressed = true;
		CloseLoadout();
	}

	/// <summary>Reads and clears the loadout Deploy edge. See <see cref="DeployFromLoadout"/>.</summary>
	public bool ConsumeLoadoutDeployPressed()
	{
		if (!loadoutDeployPressed)
		{
			return false;
		}

		loadoutDeployPressed = false;
		return true;
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
		deployedView = true;
	}

	/// <summary>
	/// Whether this controller has been switched from the pre-deploy menu view to the in-world
	/// view. Set by <see cref="SpawnAt"/> and by <see cref="EnterDeployedView"/>, cleared on
	/// death.
	/// </summary>
	private bool deployedView;

	// Set by FollowCorpse, cleared by every return to first person. See FollowCorpse.
	private bool followingCorpse;

	/// <summary>
	/// The presentation half of <see cref="SpawnAt"/>, with no write to the body's transform.
	/// Ledger <b>X-48</b>.
	/// </summary>
	/// <remarks>
	/// <para>
	/// <b>A networked client rendered the deploy menu for the whole match, on every run ever
	/// captured.</b> <c>SpawnAt</c> is the only code in the project that turns the menu backdrop
	/// off, gives the player their controls and switches to the first-person camera — and a
	/// networked body deliberately never runs it (<c>Actor.EquipLoadout</c>, "not SpawnAt, and
	/// not controller.EnableInput()"), because it would teleport a body the server owns.
	/// <c>Start</c> above turns the backdrop ON and calls <c>DisableInput</c>, so on a networked
	/// client both stayed that way forever. Measured across 90 checkpoint records of five runs:
	/// <c>Scenery Camera</c> enabled at depth 100 in every one, <c>localInputEnabled</c> false in
	/// every one — and <c>SceneryCamera</c> clears to skybox on a full culling mask, so at the
	/// highest depth in the scene it repaints over the live FP camera rather than blending with
	/// it. That is why the frames are truthful and still show no game.
	/// </para>
	/// <para>
	/// <b>What is deliberately NOT here.</b> <c>controller.transform.position</c> and
	/// <c>ResetVelocity()</c>. The server places a claimed body through
	/// <c>ServerCombatBridge.MoveToSpawnPoint</c> and owns it thereafter; writing the transform
	/// from the client's presentation path would make two writers for one position, which is the
	/// authority split AD-1 exists to prevent. Everything else <c>SpawnAt</c> does is
	/// presentation, and presentation is this client's to run.
	/// </para>
	/// <para>
	/// <b>Idempotent, so a repeated spawn message costs a few bool writes and nothing else.</b>
	/// Each call below already no-ops when it is already in the requested state.
	/// </para>
	/// </remarks>
	public void EnterDeployedView()
	{
		// CloseLoadout also does controller.SetMouseEnabled(true), which is SpawnAt's line.
		CloseLoadout();

		// Actor.Awake parks every body as dead.  The offline SpawnAt path normally clears that
		// flag, but the network path intentionally cannot call SpawnAt because it would overwrite
		// the server-owned transform.  Clear the gameplay half explicitly before arming the
		// loadout: SwitchToFirstAvailableWeapon itself refuses to run on a dead actor.
		actor.EnterNetworkDeployedState();

		// X-11's other half. SpawnAt arms a body through SpawnLoadoutWeapons (Actor.cs:266);
		// this path is the one SpawnAt never runs for a networked body (see this method's own
		// remark above), and nothing else on the client ever called EquipLoadout either -- its
		// only production caller was ServerCombatBridge.PlaceAtSpawn, one process over. So the
		// client's own rendering of its weapon never armed: the deploy screen closed, the body
		// stood there, and activeWeapon stayed null. EquipLoadout is SpawnLoadoutWeapons() and
		// nothing else (Actor.cs:313-316), so it writes no transform and does not reopen the
		// authority split this method's own remark protects.
		actor.EquipLoadout();

		// SpawnAt normally owns these three HUD writes.  A network deploy deliberately skips
		// SpawnAt because the transform is server-owned, so reproduce only its local presentation
		// here after the chosen loadout has created an active weapon.
		if (IngameUi.instance != null)
		{
			IngameUi.instance.Show();
			IngameUi.instance.SetHealth(Mathf.Max(0f, actor.health));
			actor.UpdateAmmoUi();
		}

		// Null-guarded where SpawnAt is not. SpawnAt runs from a spawn wave, which cannot happen
		// before the scene's singletons exist; this runs off a network message, which can arrive
		// during a scene change. GameManager.cs makes the same argument for its own read.
		SceneryCamera scenery = SceneryCamera.instance;
		if (scenery != null && scenery.camera != null)
		{
			scenery.camera.enabled = false;
		}

		EnableInput();

		// The prefab deliberately ships its network clock disabled so an offline game and the
		// parked pre-deploy body do not start producing C_INPUT.  Lane B used to be the only
		// caller that enabled it, which made the automated clients move and throw grenades while
		// the real menu/deploy flow never sent a single input frame.  The visible symptom was a
		// live first-person weapon that could play its local muzzle flash, but WASD left the body
		// fixed and a locally thrown grenade never received the server explosion that owns its
		// detonation.  Deployment is the authority boundary at which this body becomes playable,
		// so start the clock here and leave it running through death: SimulationEnabled above
		// turns dead/seated input into neutral frames while keeping acknowledgements current.
		Ironfront.Net.Unity.NetPredictionClock networkClock =
			GetComponent<Ironfront.Net.Unity.NetPredictionClock>();
		if (networkClock != null)
		{
			networkClock.enabled = true;
		}

		FirstPersonCamera();
		ForceEndCrouch();
		deployedView = true;
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

	// Whether this networked player is swimming, as last presented.
	private bool networkSwimming;

	// The body's head bone, which a swimmer is placed by (SwimPresentation.RootHeight).
	private Transform swimHead;

	/// <summary>Whether this networked player is in water, swimming: drawn third-person at the surface.</summary>
	public bool IsNetworkSwimming => networkSwimming;

	/// <summary>
	/// Swims a networked player the way the original swims one: third person, the body at the
	/// surface in the game's own swim animation, and back to first person on land.
	/// </summary>
	/// <remarks>
	/// <para>
	/// <b>Owner ruling 2026-09-29.</b> The original fells a body in water and swims it as a ragdoll,
	/// with the camera behind it (<see cref="StartRagdoll"/>, <see cref="SwimInput"/> steering by the
	/// third-person camera). A networked body is never felled for water -- its movement is
	/// <c>MovementCore</c>'s, which the server replays, and a ragdoll here would be a second writer
	/// (PR #281) -- so the camera and the pose are given without the physics:
	/// <see cref="SwimPresentation"/> plays the original's swim clips on the animated body, and
	/// <see cref="LateUpdate"/> draws it at the surface.
	/// </para>
	/// <para>
	/// <b>In water is the capsule's own test</b> (<see cref="CapsuleInWaterNow"/>, which
	/// <c>Actor.Update</c> stores in <c>actor.inWater</c> for this body) -- the one its movement swims
	/// by and its breath drains on, taken where the body is this frame. Offline play
	/// is untouched: <c>NetContext.IsClient</c> is false there, and the body swims by ragdoll as it
	/// always has.
	/// </para>
	/// <para>
	/// <b>A death in water leaves the camera to the death</b>: the swim ends without returning to
	/// first person, and <see cref="FollowCorpse"/> takes the camera from there.
	/// </para>
	/// </remarks>
	private void UpdateNetworkSwim()
	{
		bool swim = NetContext.IsClient && actor != null && SwimPresentation.Swims(!actor.dead, CapsuleInWaterNow(), actor.IsSeated());
		if (swim || networkSwimming)
		{
			bool moving = swim && (Mathf.Abs(inputSource.MoveX) > 0.01f || Mathf.Abs(inputSource.MoveZ) > 0.01f);
			actor.PresentNetworkSwim(swim, moving);
		}
		if (swim == networkSwimming)
		{
			return;
		}
		networkSwimming = swim;
		if (swim)
		{
			if (swimHead == null && actor.animator.isHuman)
			{
				swimHead = actor.animator.GetBoneTransform(HumanBodyBones.Head);
			}
			ThirdPersonCamera();
			return;
		}
		actor.transform.localPosition = actorLocalOrigin;
		if (!actor.dead)
		{
			FirstPersonCamera();
		}
	}

	/// <summary>
	/// The capsule's own water test, where the body is now: the test <c>Actor.Update</c> stores in
	/// <c>actor.inWater</c> for this body. Taken here rather than read from that field, which may be a
	/// frame old -- from before a respawn moved the body out of the water.
	/// </summary>
	private bool CapsuleInWaterNow()
	{
		Vector3 capsule = base.transform.position;
		return Ironfront.Net.Replication.Movement.MovementCore.IsInWater(capsule.x, capsule.y, capsule.z);
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
		followingCorpse = false;
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
		// A network client does not own the body's grounded/ragdoll state. Its CharacterController
		// is deliberately decoupled from the server transform while prediction/reconciliation is
		// running, so isGrounded can remain false on perfectly valid authoritative terrain. Letting
		// the offline 1.5-second airborne detector run here made every movement correction call
		// Actor.FallOver(): the player stood up, moved, fell over, and repeated forever at 100 HP.
		// Server death/respawn messages already own the client-side ragdoll lifecycle.
		if (NetContext.IsClient)
		{
			hasNotBeenGroundedAction.Start();
			return;
		}

		if (!characterController.enabled || characterController.isGrounded || actor.fallenOver || actor.dead || actor.IsSeated())
		{
			hasNotBeenGroundedAction.Start();
		}
		if (hasNotBeenGroundedAction.TrueDone() && !actor.fallenOver)
		{
			actor.FallOver();
		}
	}

	/// <summary>
	/// The offline player's fall damage: the speed of the height it fell, from the last ground it
	/// stood on or the seat it left (<see cref="Ironfront.Net.Replication.Movement.FallTracker"/>),
	/// paid by <see cref="Ironfront.Net.Replication.Combat.FallDamage"/>.
	/// </summary>
	/// <remarks>
	/// Owner request 2026-10-06: leaving a helicopter high up must hurt on landing, and a high fall
	/// must kill. A networked player's landing is the server's (<c>ServerPlayer.ApplyLanding</c>,
	/// the same rule); this is the practice game's, where this body's CharacterController falls.
	/// Measured on the actor's own transform, which rides the seat while seated and the capsule
	/// on foot, so a bail-out is measured from the seat. A ragdoll's fall is
	/// <c>Actor.TrackRagdollFall</c>'s.
	/// </remarks>
	private void TrackOfflineFall()
	{
		if (!NetContext.IsOffline)
		{
			return;
		}
		if (actor.dead || actor.fallenOver)
		{
			offlineFall.Forget();
			return;
		}
		float y = actor.transform.position.y;
		if (actor.IsSeated() || !characterController.enabled)
		{
			offlineFall.Rebase(y);
			return;
		}
		float landedAt = offlineFall.Observe(controller.OnGround(), y, characterController.velocity.y);
		float damage = Ironfront.Net.Replication.Combat.FallDamage.ForImpact(landedAt);
		if (damage <= 0f)
		{
			return;
		}
		using (DeathContext.Fall())
		{
			actor.Damage(damage, 0f, true, actor.Position(), Vector3.down, Vector3.zero);
		}
	}

	private void Update()
	{
		UpdateNetworkSwim();
		TrackOfflineFall();

		// Capture the edge every render frame. NetPredictionClock may or may not simulate a tick
		// in this frame; OnNetworkTickSimulated clears it only after it reached C_INPUT.
		bool fireHeldNow = Input.GetButton("Fire1") || Input.GetMouseButton(0);
		if (NetContext.IsClient && inputEnabled && !LocalTextEntry.Composing
			&& !LoadoutUi.IsOpen())
		{
			if (Input.GetButtonDown("Fire1") || Input.GetMouseButtonDown(0))
			{
				pendingNetworkFire = true;
			}
			else if (!fireHeldNow && wasFireHeld)
			{
				pendingNetworkFireRelease = true;
			}
		}
		wasFireHeld = fireHeldNow;

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
		// Offline only. The "Loadout" axis is bound to return with enter as its alternate
		// (ProjectSettings/InputManager.asset), and in a networked match Enter is the chat box's
		// key: it opens the box and sends the line. Reading it here as well opened the deploy
		// screen on the same press (playtest 2026-09-28) -- and a composing guard alone could not
		// stop it, because the chat box runs first and clears that flag before this
		// runs, on the very frame the send happens. A networked deploy screen opens on death
		// (OpenLoadoutAfterNetworkDeath) and on the first deploy, never from a key.
		if (!NetContext.IsClient && Input.GetButtonDown("Loadout") && !LocalTextEntry.OwnsKeyboard)
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
		// The original game's developer keys -- K kills you, O draws the AI debug labels, Caps Lock
		// or B toggles slow motion -- are offline only. In a networked match none of them can do
		// what it says, and each one breaks the player instead (playtest 2026-09-28): the server
		// owns health, so K only ragdolled the local body while the server kept it standing, and
		// the prediction then fought the ragdoll -- the body thrown into the air, the camera
		// shaking, input gone, "blood and a fall but no death". Slow motion slowed this client's
		// clock alone against a server that does not slow down.
		bool developerKeys = NetContext.IsOffline && !LocalTextEntry.OwnsKeyboard;
		if (developerKeys && Input.GetKeyDown(KeyCode.K))
		{
			actor.Damage(200f, 200f, true, actor.CenterPosition(), Vector3.forward, Vector3.zero);
		}
		if (developerKeys && Input.GetKeyDown(KeyCode.O))
		{
			ActorManager.instance.debug = !ActorManager.instance.debug;
		}
		if (developerKeys && Input.GetButtonDown("Slowmotion") && !IngameMenuUi.IsOpen())
		{
			// PhysicsRate, not a second Time.fixedDeltaTime = Time.timeScale / 60f here. That
			// literal made this component an unwitting authority on the project's physics rate:
			// a peer that never constructed it -- a dedicated server build -- kept the 50 Hz
			// project setting while this one forced 60, and rigidbody integration is not
			// step-independent. Issue #123.
			PhysicsRate.SetTimeScale(Time.timeScale < 1f ? 1f : 0.2f);
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
		// SEAT AUTHORITY IS THE SERVER'S AT CLIENT ROLE (design D2, ledger X-30).
		//
		// Everything below decides, locally and immediately, that this player is now in a seat
		// -- SampleUseRay -> actor.EnterSeat, and the else-branch's actor.LeaveSeat. That is
		// correct offline and is exactly the local decision the netcode forbids: the client would
		// seat itself in a vehicle the SeatArbiter may refuse (occupied, destroyed, out of reach,
		// still inside the re-entry lockout) and nothing would ever put it back on its feet,
		// because the refusal it ignored was the only message that could have.
		//
		// Before ClientSeatRequester existed this was harmless in the way an unreachable bug is
		// harmless: no client sent C_SEAT_REQUEST at all, so a networked player pressing Use next
		// to a car simply got a seat nobody else could see. It stops being harmless the moment
		// one press produces BOTH a local entry here and a server request there.
		//
		// Guarded rather than deleted: offline and the original single-player game still run this
		// path, and NetContext.Role is Offline until something calls SetRole.
		if (NetContext.IsClient)
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

	// Everything below is edge-triggered -- GetKeyDown, GetButtonDown, mouseScrollDelta.
	// Weapon selection is predicted here for the original game's responsiveness and independently sampled
	// as an absolute slot by SampleWeaponSlotIntent for the authoritative C_INPUT stream. Seat
	// selection remains on its dedicated network seam.
	private void UpdateInput()
	{
		// One guard for the whole method rather than eleven. Every read below is a bare key --
		// the digits especially -- so typing "1st squad" into the chat line would otherwise
		// switch weapon three times on the way through the sentence. OwnsKeyboard rather than
		// Composing, so the frame the line closes on is covered too.
		if (LocalTextEntry.OwnsKeyboard)
		{
			return;
		}
		if (Input.GetKeyDown(KeyCode.Alpha1))
		{
			QueueWeaponSwitch(0);
		}
		if (Input.GetKeyDown(KeyCode.Alpha2))
		{
			QueueWeaponSwitch(1);
		}
		if (Input.GetKeyDown(KeyCode.Alpha3))
		{
			QueueWeaponSwitch(2);
		}
		if (Input.GetKeyDown(KeyCode.Alpha4))
		{
			QueueWeaponSwitch(3);
		}
		if (Input.GetKeyDown(KeyCode.Alpha5))
		{
			QueueWeaponSwitch(4);
		}
		// F1-F8 move the body between the seats of its vehicle -- LeaveSeat and EnterSeat, both
		// local. Seat authority is the server's at client role (design D2, ledger X-30, and the
		// Use key's own guard below): a networked client switching here moved its camera to
		// another seat while the server kept it driving, and the protocol has no switch to ask
		// for. Offline they are the original game's keys, unchanged. Found auditing bug 4 of the
		// 2026-09-28 playtest, the keys nobody knew were live.
		bool offlineSeatKeys = !NetContext.IsClient;
		if (offlineSeatKeys && Input.GetKeyDown(KeyCode.F1))
		{
			actor.SwitchSeat(0);
		}
		if (offlineSeatKeys && Input.GetKeyDown(KeyCode.F2))
		{
			actor.SwitchSeat(1);
		}
		if (offlineSeatKeys && Input.GetKeyDown(KeyCode.F3))
		{
			actor.SwitchSeat(2);
		}
		if (offlineSeatKeys && Input.GetKeyDown(KeyCode.F4))
		{
			actor.SwitchSeat(3);
		}
		if (offlineSeatKeys && Input.GetKeyDown(KeyCode.F5))
		{
			actor.SwitchSeat(4);
		}
		if (offlineSeatKeys && Input.GetKeyDown(KeyCode.F6))
		{
			actor.SwitchSeat(5);
		}
		if (offlineSeatKeys && Input.GetKeyDown(KeyCode.F7))
		{
			actor.SwitchSeat(6);
		}
		if (offlineSeatKeys && Input.GetKeyDown(KeyCode.F8))
		{
			actor.SwitchSeat(7);
		}
		if (OptionsUi.GetOptions().toggleCrouch && Input.GetButtonDown("Crouch"))
		{
			crouchInput = !crouchInput;
		}
		// While the map is held open the wheel zooms it (MinimapUi); switching weapons with the
		// same notch would change the gun in the player's hands every time they zoom.
		float wheel = MinimapUi.OwnsScrollWheel ? 0f : Input.mouseScrollDelta.y;
		if (wheel < 0f)
		{
			QueueWeaponSwitch(actor.FindWeaponSlot(1, skipToggleable: true));
		}
		else if (wheel > 0f)
		{
			QueueWeaponSwitch(actor.FindWeaponSlot(-1, skipToggleable: false));
		}
	}

	private void QueueWeaponSwitch(int slot)
	{
		if (slot < 0) return;
		actor.SwitchWeapon(slot);
		if (!NetContext.IsClient) return;

		if (pendingNetworkFire)
		{
			deferredNetworkWeaponSlot = slot;
			Debug.Log($"[input] weapon slot {slot} waits one frame behind an unsent shot");
			return;
		}
		pendingNetworkWeaponSlot = slot;
		Debug.Log($"[input] queued weapon slot {slot} for C_INPUT");
	}

	/// <summary>
	/// Returns the base game's number-key or wheel selection until the 30 Hz network clock has
	/// actually carried it. <see cref="UpdateInput"/> owns the edge and immediate presentation;
	/// <see cref="OnNetworkTickSimulated"/> owns clearing it after transmission.
	/// </summary>
	private int SampleWeaponSlotIntent()
	{
		return LocalTextEntry.Composing ? -1 : pendingNetworkWeaponSlot;
	}

	/// <summary>
	/// The seat <see cref="SampleUseRay"/> would enter if the key went down now, for the "F" prompt
	/// in the offline game. Same ray, same reach, same conditions.
	/// </summary>
	private bool ProbeOfflineSeat(out Transform vehicle, out Vector3 seat, out Ironfront.Net.Protocol.VehicleKind kind,
		out int crew, out int seats, out bool enemyCrew)
	{
		vehicle = null;
		seat = Vector3.zero;
		kind = Ironfront.Net.Protocol.VehicleKind.Car;
		crew = 0;
		seats = 0;
		enemyCrew = false;
		if (NetContext.IsClient || actor == null || actor.dead || actor.IsSeated() || !actor.CanEnterSeat())
		{
			return false;
		}
		Ray ray = ((!actor.fallenOver) ? new Ray(fpCamera.transform.position, fpCamera.transform.forward) : new Ray(actor.CenterPosition(), tpCamera.transform.forward + tpCamera.transform.up * 0.2f));
		RaycastHit hitInfo;
		if (!Physics.Raycast(ray, out hitInfo, 3f, 2048) || hitInfo.collider.gameObject.layer != 11)
		{
			return false;
		}
		Seat target = hitInfo.collider.GetComponent<Seat>();
		if (target == null || target.vehicle == null)
		{
			return false;
		}
		Vehicle body = target.vehicle;
		vehicle = body.transform;
		seat = target.transform.position;
		kind = body is Tank ? Ironfront.Net.Protocol.VehicleKind.Tank : body is Helicopter ? Ironfront.Net.Protocol.VehicleKind.Helicopter : body is Boat ? Ironfront.Net.Protocol.VehicleKind.Boat : Ironfront.Net.Protocol.VehicleKind.Car;
		if (body.seats != null)
		{
			seats = body.seats.Length;
			foreach (Seat other in body.seats)
			{
				if (other != null && other.occupant != null && !other.occupant.dead)
				{
					crew++;
					enemyCrew |= other.occupant.team != actor.team;
				}
			}
		}
		return true;
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
		if (networkSwimming)
		{
			// At the surface by its head, in the pose the animator drew this frame; the camera below
			// then frames it there.
			Transform body = actor.transform;
			float headAboveRoot = swimHead != null
				? swimHead.position.y - body.position.y
				: SwimPresentation.IdleHeadAboveRoot;
			Vector3 at = body.position;
			float surface = Ironfront.Net.Replication.Movement.MovementCore.SurfaceAt(at.x, at.z);
			// No water over the body at all: it was moved out of the water after this frame's swim
			// was decided, and "the surface" there is negative infinity. v3.1.1 on Forest Lake,
			// 2026-10-01: a player who drowned in the lake respawned on the hill, and this assigned
			// (772, -Infinity, 1467). The next UpdateNetworkSwim ends the swim.
			if (!float.IsNegativeInfinity(surface))
			{
				body.position = new Vector3(at.x, SwimPresentation.RootHeight(surface, headAboveRoot), at.z);
			}
		}
		if (tpCamera.enabled)
		{
			UpdateThirdPersonCamera(followingCorpse);
			if (networkSwimming)
			{
				KeepSwimCameraAboveTheSurface();
			}
		}
	}

	// How far over the surface the camera behind a swimmer is held.
	private const float SwimCameraAboveSurface = 0.6f;

	/// <summary>
	/// Keeps the camera behind a swimmer out of the water. It hangs off the spine, which a swimmer
	/// carries under the surface, so a level or upward look put it under water, facing the seabed
	/// (live test 2026-09-30).
	/// </summary>
	private void KeepSwimCameraAboveTheSurface()
	{
		Vector3 at = tpCamera.transform.position;
		float lowest = Ironfront.Net.Replication.Movement.MovementCore.SurfaceAt(at.x, at.z) + SwimCameraAboveSurface;
		if (at.y < lowest)
		{
			tpCamera.transform.position = new Vector3(at.x, lowest, at.z);
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
		WeaponManager.LoadoutSet chosen = LoadoutUi.instance.loadout;

		// Ledger X-27, second half. With no pin installed -- every configuration that ships, and
		// every ordinary Play session -- this returns the loadout screen's own selection and the
		// behaviour is what it was before the seam existed. The same shape, and the same
		// argument, as AiActorController.PinnedOr on the server side.
		//
		// THIS is the seam and not NetClientLocalCombatDriver.RequestRespawn, deliberately: that
		// one rewrites only the ids the spawn request carries, which would arm the SERVER body
		// with the pinned weapon and leave this client rendering and predicting the drawn one --
		// X-11's disagreement, reintroduced for exactly the runs being measured. Everything that
		// asks what this player chose comes through here, so one override keeps both sides
		// holding the same gun.
		ClientLoadoutPin pin = ClientLoadoutPin.Active;
		if (pin == null || chosen == null)
		{
			return chosen;
		}

		// A COPY. LoadoutUi.instance.loadout is the screen's own object and is handed out by
		// reference; overwriting its fields would make the pin outlive the harness that set it
		// and silently rewrite what the player sees selected.
		WeaponManager.LoadoutSet pinned = new WeaponManager.LoadoutSet
		{
			primary = pin.PinnedOr(chosen.primary, ClientLoadoutSlot.Primary, EntryNamedOrNull),
			secondary = pin.PinnedOr(chosen.secondary, ClientLoadoutSlot.Secondary, EntryNamedOrNull),
			gear1 = pin.PinnedOr(chosen.gear1, ClientLoadoutSlot.Gear1, EntryNamedOrNull),

			// Untouched: the pin covers the three slots PinnedLoadoutDirectory covers, so the
			// two halves of X-27 pin the same set and a run cannot be half-pinned depending on
			// which body was armed.
			gear2 = chosen.gear2,
			gear3 = chosen.gear3
		};

		// ONCE per pin, on the respawn path. LogError rather than LogWarning: an unmatched name
		// means the run is not the experiment it was asked for, and the whole reason this row
		// was reopened is that the old pin reported success while pinning nothing.
		if (pin.TryTakeReport(out string report))
		{
			if (pin.HasUnresolved)
			{
				Debug.LogError(report);
			}
			else
			{
				Debug.Log(report);
			}
		}

		return pinned;
	}

	// WeaponManager.EntryNamed dereferences `instance` without a guard, so a scene that has not
	// built the catalogue yet would take an NRE on a path that is meant to degrade to the draw.
	private static WeaponManager.WeaponEntry EntryNamedOrNull(string name)
	{
		return WeaponManager.instance == null ? null : WeaponManager.EntryNamed(name);
	}

	public override bool Crouch()
	{
		return CrouchFrom(inputSource.Crouch());
	}

	/// <summary>
	/// <see cref="Crouch"/>'s rule, given the key rather than fetching it from the input source.
	/// </summary>
	/// <remarks>
	/// The two-argument form exists so the sprint gate can apply the same rule without reading
	/// back through <c>inputSource</c> while <c>inputSource</c> is still computing. See the
	/// delegate passed to <see cref="LocalInputSource"/> for what that cost when it did.
	/// </remarks>
	private bool CrouchFrom(bool key)
	{
		if (OptionsUi.GetOptions().toggleCrouch)
		{
			return crouchInput;
		}
		return key;
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
