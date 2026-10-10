using Ironfront.Net.Unity;
using UnityEngine;

public class ScopedWeapon : Weapon
{
	public GameObject scope;

	private Action blackoutAction = new Action(0.3f);

	private Texture2D blackoutTexture;

	private bool showingScope;

	protected override void Awake()
	{
		base.Awake();
		blackoutTexture = new Texture2D(8, 8);
	}

	public override void FindRenderers(bool thirdPerson)
	{
		base.FindRenderers(thirdPerson);
		Renderer[] componentsInChildren = scope.GetComponentsInChildren<Renderer>();
		foreach (Renderer item in componentsInChildren)
		{
			renderers.Remove(item);
		}
		SetAiming(false);
	}

	public override void Unholster()
	{
		base.Unholster();
		SetAiming(false);
	}

	public override void SetAiming(bool aiming)
	{
		base.SetAiming(aiming);
		if (!HasActiveAnimator())
		{
			return;
		}
		if (aiming)
		{
			EnsureBlackout();
			showingScope = false;
			blackoutAction.Start();
			return;
		}
		showingScope = false;
		blackoutAction.Stop();
		scope.SetActive(false);
		foreach (Renderer renderer in renderers)
		{
			renderer.enabled = true;
		}
	}

	protected override void Update()
	{
		base.Update();
		UpdateScopeControls();
		if (blackoutAction.TrueDone() || !(blackoutAction.Ratio() > 0.5f) || showingScope)
		{
			return;
		}
		showingScope = true;
		scope.SetActive(true);
		foreach (Renderer renderer in renderers)
		{
			renderer.enabled = false;
		}
	}

	/// <summary>How opaque the blackout is now: zero unless the scope is coming up in first person.</summary>
	internal float BlackoutAlpha()
	{
		if (HasActiveAnimator() && !blackoutAction.TrueDone() && showingScope)
		{
			return Mathf.Clamp01(4f - 4f * blackoutAction.Ratio());
		}
		return 0f;
	}

	internal Texture2D BlackoutTexture => blackoutTexture;

	// ------------------------------------------------------------------ the scope's own controls
	//
	// Owner's run of 2026-10-10 (phase P38): "the three scoped guns feel the same -- one should
	// zoom very far, for the long shots". ScopeProfiles says what each rifle's scope can do; this
	// is the player's hand on it: the wheel steps the power, Page Up / Page Down click the zero,
	// the Sprint key holds the breath, and a rangefinder reads the distance to whatever the
	// crosshair rests on. Only the local player's first-person rifle runs any of it.

	private const float BreathSeconds = 5f;
	private const float BreathRecoverySeconds = 3f;
	private const float HeldBreathSway = 0.1f;
	private const float GaspingSway = 1.8f;
	private const float RangefinderIntervalSeconds = 0.1f;
	private const float RangefinderReachMetres = 2000f;

	private ScopeProfile profile;
	private bool profileResolved;
	private int zoomIndex;
	private int zeroMetres;
	private float breath = 1f;
	private bool holdingBreath;
	private bool outOfBreath;
	private float swayFactor = 1f;
	private float swayClock;
	private Vector2 swayApplied;
	private float rangeMetres = -1f;
	private float nextRangeAt;
	private bool claimsWheel;

	private ScopeProfile Profile
	{
		get
		{
			if (!profileResolved && NetworkId != 0)
			{
				profileResolved = true;
				if (ScopeProfiles.TryGet(NetworkId, out profile))
				{
					zeroMetres = profile.ZeroMinMetres;
				}
			}
			return profile;
		}
	}

	/// <summary>Whether this rifle's scope has controls of its own (<see cref="ScopeProfiles"/>).</summary>
	public bool HasScopeProfile => Profile != null;

	/// <summary>The power the scope is set to.</summary>
	public float CurrentMagnification => Profile != null ? Profile.MagnificationAt(zoomIndex) : 1f;

	/// <summary>How much the mouse is slowed at this power (<see cref="ScopeProfiles.SensitivityScale"/>).</summary>
	public float SensitivityScale => ScopeProfiles.SensitivityScale(Profile, CurrentMagnification);

	/// <summary>The zero the player set; the round crosses the line of sight there.</summary>
	public override float SightZeroMetres => Profile != null && Profile.AdjustableZero ? zeroMetres : 0f;

	internal int ZeroMetres => zeroMetres;

	internal bool AdjustableZero => Profile != null && Profile.AdjustableZero;

	internal bool HasRangefinder => Profile != null && Profile.HasRangefinder;

	internal float RangeMetres => rangeMetres;

	internal float Breath => breath;

	internal bool HoldingBreath => holdingBreath;

	internal bool OutOfBreath => outOfBreath;

	/// <summary>The readout is drawn: the local player's scope is up and has controls.</summary>
	internal bool ShowsReadout => showingScope && Profile != null && IsLocalScope();

	private bool IsLocalScope()
	{
		return HasActiveAnimator() && user != null && !user.aiControlled
			&& FpsActorController.instance != null && FpsActorController.instance.actor == user;
	}

	private void UpdateScopeControls()
	{
		bool scoped = showingScope && Profile != null && IsLocalScope();
		ringActive = scoped;
		ClaimWheel(scoped && Profile.VariableZoom);
		if (!scoped)
		{
			ReleaseSway();
			UpdateBreath(false, Time.deltaTime);
			return;
		}
		bool keys = !LocalTextEntry.OwnsKeyboard;
		if (keys)
		{
			float wheel = Input.mouseScrollDelta.y;
			if (wheel > 0f)
			{
				StepZoom(1);
			}
			else if (wheel < 0f)
			{
				StepZoom(-1);
			}
			if (Profile.AdjustableZero && GameKeys.Down(GameAction.ZeroUp))
			{
				zeroMetres = Profile.StepZero(zeroMetres, 1);
			}
			if (Profile.AdjustableZero && GameKeys.Down(GameAction.ZeroDown))
			{
				zeroMetres = Profile.StepZero(zeroMetres, -1);
			}
		}
		UpdateBreath(keys && GameKeys.Held(GameAction.Sprint), Time.deltaTime);
		ApplySway(Time.deltaTime);
		if (Profile.HasRangefinder && Time.time >= nextRangeAt)
		{
			nextRangeAt = Time.time + RangefinderIntervalSeconds;
			rangeMetres = MeasureRange();
		}
	}

	private void StepZoom(int direction)
	{
		int next = Mathf.Clamp(zoomIndex + direction, 0, Profile.Magnifications.Count - 1);
		if (next == zoomIndex)
		{
			return;
		}
		zoomIndex = next;
		if (PlayerFpParent.instance != null)
		{
			PlayerFpParent.instance.SetAimMagnification(CurrentMagnification);
		}
	}

	/// <summary>
	/// Five seconds of held breath steady the rifle to a tenth of its drift; run out and it gasps,
	/// drifting harder until the lungs refill.
	/// </summary>
	private void UpdateBreath(bool hold, float dt)
	{
		if (outOfBreath)
		{
			holdingBreath = false;
			breath = Mathf.MoveTowards(breath, 1f, dt / BreathRecoverySeconds);
			outOfBreath = breath < 1f;
		}
		else if (hold)
		{
			holdingBreath = true;
			breath = Mathf.MoveTowards(breath, 0f, dt / BreathSeconds);
			if (breath <= 0f)
			{
				outOfBreath = true;
				holdingBreath = false;
			}
		}
		else
		{
			holdingBreath = false;
			breath = Mathf.MoveTowards(breath, 1f, dt / BreathRecoverySeconds);
		}
		float target = holdingBreath ? HeldBreathSway : outOfBreath ? GaspingSway : 1f;
		swayFactor = Mathf.MoveTowards(swayFactor, target, dt * 4f);
	}

	/// <summary>
	/// Moves the aim itself -- the camera's parent, the aim the server is sent and the round flies
	/// along -- by this frame's change in the drift, so the crosshair and the round never part.
	/// </summary>
	private void ApplySway(float dt)
	{
		Transform look = FpsActorController.instance.fpCameraParent;
		if (look == null)
		{
			return;
		}
		swayClock += dt;
		float amplitude = Profile.SwayDegrees * swayFactor;
		float t = swayClock;
		const float Tau = 2f * Mathf.PI;
		var drift = new Vector2(
			amplitude * 0.8f * (0.7f * Mathf.Sin(Tau * 0.17f * t + 0.6f) + 0.3f * Mathf.Sin(Tau * 0.61f * t)),
			amplitude * (0.75f * Mathf.Sin(Tau * 0.21f * t) + 0.25f * Mathf.Sin(Tau * 0.53f * t + 1.1f)));
		Vector2 change = drift - swayApplied;
		swayApplied = drift;
		Vector3 euler = look.localEulerAngles;
		euler.x += change.x;
		euler.y += change.y;
		look.localEulerAngles = euler;
	}

	private void ReleaseSway()
	{
		if (swayApplied == Vector2.zero)
		{
			return;
		}
		Transform look = FpsActorController.instance != null ? FpsActorController.instance.fpCameraParent : null;
		if (look != null)
		{
			Vector3 euler = look.localEulerAngles;
			euler.x -= swayApplied.x;
			euler.y -= swayApplied.y;
			look.localEulerAngles = euler;
		}
		swayApplied = Vector2.zero;
	}

	private bool ringActive;
	private bool ringBaseKnown;
	private Vector3 ringBaseScale;
	private Vector3 ringBasePosition;
	private Quaternion ringBaseRotation;
	private Vector3 ringWrittenScale;
	private Vector3 ringWrittenPosition;
	private Quaternion ringWrittenRotation;

	// After the animator: the LRR's scope-in animation writes the overlay's scale every frame.
	private void LateUpdate()
	{
		if (ringActive)
		{
			KeepRing();
		}
	}

	/// <summary>
	/// Keeps the scope's ring and reticle the size on screen they were drawn for, with the
	/// reticle's aim mark on the aim. The overlay is a model just in front of the camera, so its
	/// apparent size follows the field of view: at 25x it would fill the screen and at 6x shrink.
	/// Scaling its two axes across the view (not the one along it) by the change in the view's
	/// half-angle tangent keeps every part of it, at any depth, where it was drawn to appear at the
	/// prefab's own aim field of view. A reticle whose aim mark is drawn off the overlay's centre
	/// (<see cref="ScopeProfile.AimMarkBelowCentre"/>) is turned up about the eye until the mark
	/// sits on the round's line: the screen's centre, where the camera aims. Turned, not moved: the
	/// overlay is a tube whose reticle sits deep inside it, and a turn about the eye shifts every
	/// depth by the same angle.
	/// </summary>
	private void KeepRing()
	{
		FpsActorController fps = FpsActorController.instance;
		Camera camera = fps != null ? fps.fpCamera : null;
		if (scope == null || camera == null || configuration.aimFov <= 0f || Profile == null)
		{
			return;
		}
		Transform overlay = scope.transform;
		if (!ringBaseKnown)
		{
			ringBaseKnown = true;
			ringBaseScale = overlay.localScale;
			ringBasePosition = overlay.localPosition;
			ringBaseRotation = overlay.localRotation;
		}
		// This class's own write from last frame, unless the animator has written over it since.
		Vector3 baseScale = overlay.localScale == ringWrittenScale ? ringBaseScale : overlay.localScale;
		Vector3 basePosition = overlay.localPosition == ringWrittenPosition ? ringBasePosition : overlay.localPosition;
		Quaternion baseRotation = overlay.localRotation == ringWrittenRotation ? ringBaseRotation : overlay.localRotation;
		overlay.localPosition = basePosition;
		overlay.localRotation = baseRotation;

		float halfTan = Mathf.Tan(camera.fieldOfView * 0.5f * Mathf.Deg2Rad);
		float s = halfTan / Mathf.Tan(configuration.aimFov * 0.5f * Mathf.Deg2Rad);
		Vector3 view = camera.transform.forward;
		float x = Mathf.Abs(Vector3.Dot(overlay.right, view));
		float y = Mathf.Abs(Vector3.Dot(overlay.up, view));
		float z = Mathf.Abs(Vector3.Dot(overlay.forward, view));
		var across = new Vector3(
			x >= y && x >= z ? 1f : s,
			y > x && y >= z ? 1f : s,
			z > x && z > y ? 1f : s);
		ringWrittenScale = Vector3.Scale(baseScale, across);
		overlay.localScale = ringWrittenScale;

		float drop = Profile.AimMarkBelowCentre;
		if (drop != 0f)
		{
			float angle = Mathf.Atan(2f * drop * halfTan) * Mathf.Rad2Deg;
			Quaternion lift = Quaternion.AngleAxis(-angle, camera.transform.right);
			Vector3 eye = camera.transform.position;
			overlay.SetPositionAndRotation(eye + lift * (overlay.position - eye), lift * overlay.rotation);
		}
		ringWrittenPosition = overlay.localPosition;
		ringWrittenRotation = overlay.localRotation;
	}

	private float MeasureRange()
	{
		FpsActorController fps = FpsActorController.instance;
		if (fps == null || !fps.TryGetAimRay(out Ray aim))
		{
			return -1f;
		}
		float distance = DistanceAlongAim(user, aim, RangefinderReachMetres, out bool found);
		return found ? distance : -1f;
	}

	private void ClaimWheel(bool claim)
	{
		if (claim == claimsWheel)
		{
			return;
		}
		claimsWheel = claim;
		HudInputClaims.ScopeOwnsWheel = claim;
	}

	private void OnDisable()
	{
		ClaimWheel(false);
		ReleaseSway();
	}

	// The blackout's IMGUI draw lives on a component of its own, added here, on the first-person
	// weapon, the first time it aims: an OnGUI on this class ran on every bot's rifle (ScopeBlackout).
	// Not compiled into the dedicated server: IMGUI is stripped there, and Unity logs
	// 'OnGUI function detected ... not called' for every instance -- once per bot, 402 lines in
	// one 100-bot match (B4, 2026-09-30).
	private void EnsureBlackout()
	{
#if !UNITY_SERVER
		if (blackout == null)
		{
			blackout = base.gameObject.AddComponent<ScopeBlackout>();
			blackout.weapon = this;
		}
		if (readout == null && Profile != null)
		{
			readout = base.gameObject.AddComponent<ScopeReadout>();
			readout.weapon = this;
		}
#endif
	}

#if !UNITY_SERVER
	private ScopeBlackout blackout;

	private ScopeReadout readout;
#endif
}
