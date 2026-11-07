using UnityEngine;

public class MountedCameraWeapon : MountedWeapon
{
	private Camera camera;

	public float maxPitch = 60f;

	public float minPitch = -15f;

	/// <summary>
	/// The local gunner looks through this gun's own camera for as long as they man it, not only
	/// while aiming.
	/// </summary>
	/// <remarks>
	/// For an open mount, where the seat's eye would sit inside the gun: the RHIB's bow gun stands
	/// a third of a metre in front of its gunner's face, so the seat view was the breech filling
	/// the lower half of the screen (owner report 2026-11-07, "the boat's machine gun cannot be
	/// used"). The helicopter's door gun keeps the cabin view and opens its camera to aim.
	/// </remarks>
	public bool viewThroughGun;

	/// <summary>Field of view while aiming through <see cref="viewThroughGun"/>: the zoom.</summary>
	public float aimedFieldOfView = 35f;

	private float restFieldOfView = 60f;

	private bool aimedNow;

	// True while this gun has the local player's seat camera switched off, so Holster gives it
	// back exactly once.
	private bool viewingThroughGun;

	protected override void Awake()
	{
		base.Awake();
		camera = GetComponentInChildren<Camera>();
		if (camera != null)
		{
			camera.enabled = false;
			camera.depth = 50f;
			restFieldOfView = camera.fieldOfView;
		}
	}

	protected override void Update()
	{
		base.Update();
		if (user != null)
		{
			// AimDirection, not controller.FacingDirection: on a dedicated server a player's body
			// is a suspended bot whose controller never turns, so the gun stayed pointed where the
			// body faced when it sat down while the player aimed elsewhere, and every round flew
			// along that frozen muzzle (2026-11-07). AimDirection reads the aim the player's input
			// frames carry, and falls back to the controller for bots and offline.
			base.transform.rotation = Quaternion.LookRotation(user.AimDirection(), Vector3.up);
			Vector3 localEulerAngles = base.transform.localEulerAngles;
			float value = Mathf.DeltaAngle(0f, localEulerAngles.x);
			localEulerAngles.x = Mathf.Clamp(value, minPitch, maxPitch);
			base.transform.localEulerAngles = localEulerAngles;
			base.transform.rotation = Quaternion.LookRotation(base.transform.forward, Vector3.up);
		}
		if (viewThroughGun && camera != null && camera.enabled)
		{
			float target = aimedNow ? aimedFieldOfView : restFieldOfView;
			camera.fieldOfView = Mathf.MoveTowards(camera.fieldOfView, target, 120f * Time.deltaTime);
		}
	}

	public override void Unholster()
	{
		base.Unholster();
		if (viewThroughGun && LocalGunner())
		{
			camera.enabled = true;
			// The seat camera would otherwise draw the whole scene again underneath a full-screen
			// gun view. MountedTurret does the same for a tank's gunner.
			if (FpsActorController.instance != null)
			{
				FpsActorController.instance.DisableCameras();
			}
			viewingThroughGun = true;
		}
	}

	public override void SetAiming(bool aiming)
	{
		base.SetAiming(aiming);
		aimedNow = aiming;
		if (LocalGunner())
		{
			camera.enabled = aiming || viewThroughGun;
		}
	}

	public override void Holster()
	{
		base.Holster();
		aimedNow = false;
		if (camera != null && user != null && !user.aiControlled)
		{
			camera.enabled = false;
			camera.fieldOfView = restFieldOfView;
		}
		if (viewingThroughGun)
		{
			viewingThroughGun = false;
			if (FpsActorController.instance != null)
			{
				FpsActorController.instance.EnableCameras();
			}
		}
	}

	/// <summary>The local player is the one at this gun, on a peer that draws: the camera is theirs.</summary>
	private bool LocalGunner()
	{
		return camera != null && user != null && !user.aiControlled
			&& Ironfront.Net.Unity.NetWeaponAuthority.CosmeticHalfRunsHere;
	}
}
