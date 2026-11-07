using UnityEngine;
using UnityEngine.UI;

public class Javelin : ScopedWeapon
{
	private const float MAX_DISTANCE = 1000f;

	private const int TARGET_LOS_MASK = 1;

	private const int TARGET_LAYER_MASK = 5377;

	private const float LOCK_ON_DOT = 0.99f;

	/// <summary>
	/// Metres above a soldier's feet the lock and the missile aim at: the chest, so a near miss
	/// still bursts inside the warhead's radius (<c>JavelinMissile.proximityFuse</c>).
	/// </summary>
	public const float ChestHeight = 1.1f;

	public Transform pointSampler;

	public RawImage lockImage;

	public RawImage targetImage;

	public Texture2D lockingTexture;

	public Texture2D lockedTexture;

	public Renderer crosshair;

	private bool hasManualTarget;

	private bool lockingOnManualTarget;

	/// <summary>
	/// What the lock is on: a <see cref="Vehicle"/> or, since 2026-10-06, an <see cref="Actor"/>.
	/// The original launcher locked vehicles only, so aimed at a soldier it fell back to marking a
	/// point -- and that ray (level geometry only) passed through the soldier and marked the
	/// ground behind him. The missile then flew to that patch of ground: the owner's "it hits
	/// well behind the target I aimed at" (v4.3.0 playtest).
	/// </summary>
	private Component target;

	private Vector3 manualTargetPoint;

	private Action lockOnAction = new Action(2f);

	private Action lockOnStayAction = new Action(1f);

	// Server only: where the networked body carrying this launcher looks, as its accepted input
	// frames say. pointSampler is a child of the weapon root, so CullFpsObjects destroys it on
	// every body that is not the local player -- and on a server that is every body. Until this
	// existed the lock-on read a destroyed transform: every pull of a networked player's trigger
	// threw out of ServerCombatBridge.StepCombat and no Javelin was ever launched online
	// (2026-09-27 playtest). Offline hasNetworkAim stays false and the shipped reads are used.
	private bool hasNetworkAim;

	private Vector3 networkEye;

	private Vector3 networkForward;

	public override void SteerByNetwork(Vector3 eye, Vector3 forward, bool aimHeld)
	{
		hasNetworkAim = true;
		networkEye = eye;
		networkForward = forward;
		// Through SetAiming, as Actor.UpdateWeapon does offline: starting to aim is what clears
		// a manual target, and the lock-on in LateUpdate only runs while aiming.
		if (aimHeld != aiming)
		{
			SetAiming(aimHeld);
		}
	}

	/// <remarks>
	/// On a networked client the launch is always the server's to make, so the client predicts
	/// none and shows the launch when the server's clip says it happened
	/// (<see cref="PresentServerLaunch"/>). A launch predicted on the client's own lock, while the
	/// server's lock was a moment behind, showed the tube's flash and smoke and no missile, and the
	/// round it spent never came back (owner report 2026-10-11).
	/// </remarks>
	public override bool WithholdsTrigger()
	{
		return ServerMakesTheLaunch() || !HasLock();
	}

	/// <summary>
	/// Seconds a server holds a pull that came while its lock was still running, and fires it the
	/// moment the lock completes.
	/// </summary>
	/// <remarks>
	/// The client draws its lock box from its own copy of the lock and the server decides from
	/// its copy, a few frames apart. A pull made the moment the box turned to "locked" reached a
	/// server whose lock was not quite done, and was ignored: only a trigger still held when the
	/// server's lock completed ever launched. Measured on the live Dustbowl server on 2026-10-11,
	/// a 0.1 s tap 2.2 s into the aim launched nothing while one at 2.6 s did.
	/// </remarks>
	public const float HeldPullSeconds = 1f;

	/// <summary>How long after a pull a launch the server reports is shown as this player's.</summary>
	public const float LaunchConfirmSeconds = HeldPullSeconds + 1.5f;

	private const float TrackingBeepSeconds = 0.18f;

	private const float LockToneVolume = 0.35f;

	private float heldPullUntil = float.NegativeInfinity;

	private float launchRequestedAt = float.NegativeInfinity;

	private int lastPullFrame = -2;

	private AudioSource lockTone;

	private AudioClip trackingClip;

	private AudioClip lockedClip;

	private AudioClip deniedClip;

	private float nextTrackingBeep;

	/// <summary>True on a networked client: the server flies the missile and decides when it leaves.</summary>
	private bool ServerMakesTheLaunch()
	{
		return Ironfront.Net.Unity.NetContext.IsClient && ServerDrawsProjectile();
	}

	/// <summary>
	/// Server: whether a pull held for the lock (<see cref="HeldPullSeconds"/>) is due now that the
	/// lock is done. Taken once.
	/// </summary>
	public override bool TakeHeldTrigger()
	{
		if (Time.time > heldPullUntil || !HasLock())
		{
			return false;
		}
		heldPullUntil = float.NegativeInfinity;
		return true;
	}

	/// <summary>
	/// Shows this player's launch once the server has made it: the tube's flash, smoke, report and
	/// kick, and the round leaving the clip. Called by the local rig when the server's clip drops.
	/// </summary>
	/// <returns>False when no pull of this player's is waiting for a launch.</returns>
	public bool PresentServerLaunch()
	{
		if (Time.time - launchRequestedAt > LaunchConfirmSeconds)
		{
			return false;
		}
		launchRequestedAt = float.NegativeInfinity;
		Shoot(SampleForward(), false);
		return true;
	}

	private Vector3 SampleOrigin()
	{
		return hasNetworkAim ? networkEye : pointSampler.position;
	}

	private Vector3 SampleForward()
	{
		return hasNetworkAim ? networkForward : pointSampler.forward;
	}

	// The shipped code measures bearings and distances from the weapon itself, which offline sits
	// at the player's eye. A server-side weapon hangs in a bind pose nobody is standing in, so the
	// eye the input frame stated stands in for it.
	private Vector3 SightPosition()
	{
		return hasNetworkAim ? networkEye : base.transform.position;
	}

	private Vector3 LineOfSightOrigin()
	{
		return hasNetworkAim ? networkEye : MuzzlePosition();
	}

	/// <remarks>
	/// The original loaded an empty launcher the moment it was drawn (<c>ReloadDone</c>, with no
	/// reload time at all). Owner report 2026-10-07: "after the shot it reloads by itself -- it
	/// should take R". Online it also loaded only the client's copy: the server reloads on the R it
	/// is sent and on nothing else, so the client then showed a round the server would refuse to
	/// fire. Drawn empty, it stays empty until the player reloads.
	/// </remarks>
	public override void Unholster()
	{
		base.Unholster();
		hasManualTarget = false;
		heldPullUntil = float.NegativeInfinity;
		launchRequestedAt = float.NegativeInfinity;
		SetReloadPose(false);
	}

	/// <summary>
	/// Seconds the reload's raise takes: the "Reload raise" state plays the unholster clip
	/// (1.29 s) at speed 1.6. Pinned against the controller by <c>JavelinReloadAnimationTests</c>.
	/// </summary>
	public const float ReloadRaiseSeconds = 0.81f;

	/// <summary>
	/// A reload you can see. On R the launcher comes down out of view, a fresh tube goes on, and it
	/// comes back up just as the reload completes.
	/// </summary>
	/// <remarks>
	/// <para>
	/// <b>Owner request 2026-10-06</b>: "the guided launcher has no reload animation". Nor did the
	/// original: its controller has no reload state, so after the shot the launcher sat in view
	/// for the whole reload and was simply loaded again when it ended. The original's
	/// <c>reloadTime</c> (2 s) is kept, and the animation is fitted inside it: down out of view
	/// ("reloading" set here, from the hip or straight out of the shot's kick), and the raise
	/// started <see cref="ReloadRaiseSeconds"/> before the reload ends, so the launcher is up as
	/// it is loaded.
	/// </para>
	/// <para>
	/// Presentation only: a bot's launcher and a server's have no animator.
	/// </para>
	/// </remarks>
	public override void Reload(bool overrideHolstered = false)
	{
		bool wasReloading = reloading;
		base.Reload(overrideHolstered);
		if (wasReloading || !reloading)
		{
			return;
		}
		SetReloadPose(true);
		CancelInvoke(nameof(RaiseAfterReload));
		Invoke(nameof(RaiseAfterReload), Mathf.Max(0f, configuration.reloadTime - ReloadRaiseSeconds));
	}

	private void RaiseAfterReload()
	{
		SetReloadPose(false);
	}

	private void SetReloadPose(bool down)
	{
		if (HasActiveAnimator())
		{
			animator.SetBool("reloading", down);
		}
	}

	protected override Projectile SpawnProjectile(Vector3 direction)
	{
		Projectile projectile = base.SpawnProjectile(direction);
		// Null on a client: the missile is the server's to fly and to draw
		// (ProjectileNetAnnouncer.IsServerDrawn). The shooter's own guided copy used to fly to the
		// client's lock while the server's flew to the server's, so the shooter watched a hit that
		// did no damage whenever the two disagreed.
		if (projectile == null)
		{
			return null;
		}
		JavelinMissile javelinMissile = (JavelinMissile)projectile;
		if (hasManualTarget)
		{
			javelinMissile.targetPoint = manualTargetPoint;
		}
		else
		{
			javelinMissile.Lock(target);
		}
		return projectile;
	}

	public override void SetAiming(bool aiming)
	{
		base.SetAiming(aiming);
		if (aiming)
		{
			hasManualTarget = false;
		}
	}

	private void LateUpdate()
	{
		if (aiming)
		{
			bool flag = lockingOnManualTarget;
			lockingOnManualTarget = hasManualTarget && (IsInFov(manualTargetPoint) || !lockOnStayAction.TrueDone());
			Component vehicle = null;
			bool flag2;
			if (lockingOnManualTarget)
			{
				flag2 = !flag && lockingOnManualTarget;
				if (flag2)
				{
					lockOnAction.Start();
				}
				target = null;
			}
			else
			{
				vehicle = FindTarget();
				flag2 = vehicle != target;
			}
			if (hasManualTarget)
			{
				if (IsInFov(manualTargetPoint))
				{
					lockOnStayAction.Start();
				}
			}
			else if (!flag2 && IsLocking() && HasLock())
			{
				lockOnStayAction.Start();
			}
			if (flag2 && lockOnStayAction.TrueDone())
			{
				if (!lockingOnManualTarget)
				{
					target = vehicle;
				}
				if (IsLocking())
				{
					lockOnAction.Start();
				}
				else
				{
					lockOnAction.Stop();
				}
			}
		}
		else
		{
			target = null;
			lockingOnManualTarget = false;
			lockOnAction.Stop();
		}
		// The lock box and crosshair are the local player's HUD. A server runs this for every
		// networked body's launcher, with no camera and the HUD culled.
		if (!(user != null) || user.aiControlled || !Ironfront.Net.Unity.NetPresenterGate.IsLocalActor(user))
		{
			return;
		}
		if (IsLocking())
		{
			lockImage.enabled = true;
			if (hasManualTarget)
			{
				lockImage.rectTransform.position = Camera.main.WorldToScreenPoint(manualTargetPoint);
			}
			else
			{
				lockImage.rectTransform.position = Camera.main.WorldToScreenPoint(AimPointOf(target));
			}
		}
		else
		{
			lockImage.enabled = false;
		}
		crosshair.enabled = !HasLock() && !hasManualTarget;
		if (aiming && hasManualTarget)
		{
			targetImage.enabled = true;
			targetImage.rectTransform.position = Camera.main.WorldToScreenPoint(manualTargetPoint);
		}
		else
		{
			targetImage.enabled = false;
		}
		if (HasLock())
		{
			lockImage.texture = lockedTexture;
		}
		else
		{
			lockImage.texture = lockingTexture;
		}
		UpdateLockTone();
	}

	public override void Fire(Vector3 direction, bool useMuzzleDirection)
	{
		bool freshPull = Time.frameCount - lastPullFrame > 1;
		lastPullFrame = Time.frameCount;
		if (HasLock())
		{
			heldPullUntil = float.NegativeInfinity;
			if (ServerMakesTheLaunch())
			{
				// The pull has gone to the server in this frame's input; the launch is shown when
				// the server's clip says it happened (PresentServerLaunch).
				launchRequestedAt = Time.time;
				return;
			}
			// No Reload() after the shot, as the original had: the next missile goes on when the
			// player presses R, as every other gun's magazine does (owner report 2026-10-07; see
			// Unholster). A bot reloads through its controller whenever its weapon is empty.
			base.Fire(direction, useMuzzleDirection);
		}
		// Marking a point while a lock on a soldier or a vehicle is still running would throw that
		// lock away for a patch of ground: the pull that came a moment too early is ignored instead.
		else if (!hasManualTarget && target == null)
		{
			Ray ray = new Ray(SampleOrigin(), SampleForward());
			RaycastHit hitInfo;
			if (Physics.Raycast(ray, out hitInfo, 1000f, 1))
			{
				manualTargetPoint = hitInfo.point;
				hasManualTarget = true;
			}
		}
		else if (hasNetworkAim && IsLocking())
		{
			// Server: the lock is still running. The pull is held for it (HeldPullSeconds) and
			// handed back through TakeHeldTrigger the frame the lock completes.
			heldPullUntil = Time.time + HeldPullSeconds;
		}
		// From the hip, empty or mid-reload the launcher cannot lock, so a pull does nothing: say
		// so with the denied tone, as a lock-on launcher in any modern shooter does.
		if (freshPull && !aiming && IsLocalHuman())
		{
			PlayLockSound(deniedClip);
		}
	}

	private bool IsLocalHuman()
	{
		return user != null && !user.aiControlled && Ironfront.Net.Unity.NetPresenterGate.IsLocalActor(user);
	}

	private AudioSource LockTone()
	{
		if (lockTone == null)
		{
			// A child, not a second AudioSource on the weapon root: Weapon reads its report's
			// source off the root. Switched off with the weapon, so a holstered tone is silent.
			GameObject tone = new GameObject("Lock tone");
			tone.transform.SetParent(base.transform, false);
			lockTone = tone.AddComponent<AudioSource>();
			lockTone.playOnAwake = false;
			lockTone.spatialBlend = 0f;
			lockTone.volume = LockToneVolume;
			trackingClip = Resources.Load<AudioClip>("IronfrontUi/lock-tracking");
			lockedClip = Resources.Load<AudioClip>("IronfrontUi/lock-locked");
			deniedClip = Resources.Load<AudioClip>("IronfrontUi/lock-denied");
		}
		return lockTone;
	}

	private void PlayLockSound(AudioClip clip)
	{
		AudioSource source = LockTone();
		if (clip != null)
		{
			source.PlayOneShot(clip);
		}
	}

	/// <summary>
	/// The seeker's tones, the local player's only: a quick beep while the lock is being taken and
	/// a steady tone once it holds, the way the lock-on launchers of the big shooters talk to a
	/// player who cannot watch the box and the target at once. The original launcher was silent.
	/// </summary>
	private void UpdateLockTone()
	{
		bool locked = aiming && HasLock();
		bool tracking = aiming && IsLocking() && !locked;
		if (!locked && !tracking && lockTone == null)
		{
			return;
		}
		AudioSource source = LockTone();
		if (locked)
		{
			if (!source.isPlaying || source.clip != lockedClip)
			{
				source.clip = lockedClip;
				source.loop = true;
				source.Play();
			}
			return;
		}
		if (source.loop)
		{
			source.Stop();
			source.loop = false;
			source.clip = null;
		}
		if (tracking && Time.time >= nextTrackingBeep && trackingClip != null)
		{
			source.PlayOneShot(trackingClip);
			nextTrackingBeep = Time.time + TrackingBeepSeconds;
		}
	}

	private bool IsLocking()
	{
		return target != null || lockingOnManualTarget;
	}

	public override string DescribeFireGates()
	{
		return base.DescribeFireGates() + $" lock={HasLock()} locking={IsLocking()} aiming={aiming} networkAim={hasNetworkAim}";
	}

	/// <remarks>
	/// <para>
	/// <b>TrueDone, never Done.</b> <c>Action.Done</c> answers false ONCE when its timer
	/// expires and true from the next call on, and this is asked from seven places, so which one
	/// asked first decided whether the lock existed. On a server the two that collide are the
	/// withhold check and the pull: <see cref="WithholdsTrigger"/> took the false, kept the
	/// trigger from the combat authority, and <see cref="Fire"/> got the true and launched
	/// through <c>Weapon.Fire</c> on its own -- a missile no round paid for, and
	/// <c>holdingFire</c> latched with nothing on a server to clear it. Every launch the
	/// authority approved after that was refused by the engine: 78 "NOTHING WAS LAUNCHED"
	/// lines over 2026-10-12..14 on Island and Forest Lake, all BIL SCALPEL, all players.
	/// </para>
	/// <para>
	/// The lock completes on the frame its two seconds run out instead of one question later,
	/// which no player can see; offline nothing else changes.
	/// </para>
	/// </remarks>
	private bool HasLock()
	{
		return IsLocking() && lockOnAction.TrueDone();
	}

	/// <summary>
	/// The enemy soldier or vehicle nearest the crosshair inside the lock cone that the launcher can
	/// see; failing an enemy, any vehicle so (an empty one has no side, as before). Null when none.
	/// </summary>
	private Component FindTarget()
	{
		Vector3 sight = SightPosition();
		Vector3 forward = SampleForward();
		// Sticky: a soldier moves, and re-ranking every frame would hand the lock back and forth
		// between two men near the crosshair and never let the two-second lock finish.
		if (target != null && IsStillLockable(target, sight, forward))
		{
			return target;
		}
		Component best = null;
		bool bestIsEnemy = false;
		float bestDot = LOCK_ON_DOT;
		foreach (Vehicle vehicle in ActorManager.instance.vehicles)
		{
			if (vehicle == null || vehicle.dead || (user.IsSeated() && user.seat.vehicle == vehicle))
			{
				continue;
			}
			bool enemy = vehicle.HasDriver() && vehicle.Driver().team != user.team;
			Consider(vehicle, vehicle.transform.position, enemy, sight, forward, ref best, ref bestIsEnemy, ref bestDot);
		}
		foreach (Actor actor in ActorManager.instance.actors)
		{
			// A soldier in a seat is locked through his vehicle; a parked or dead body is no one.
			if (actor == null || actor == user || actor.dead || actor.team == user.team || actor.IsSeated()
				|| !actor.gameObject.activeInHierarchy)
			{
				continue;
			}
			Consider(actor, AimPointOf(actor), enemy: true, sight, forward, ref best, ref bestIsEnemy, ref bestDot);
		}
		return best;
	}

	private void Consider(Component candidate, Vector3 point, bool enemy, Vector3 sight, Vector3 forward,
		ref Component best, ref bool bestIsEnemy, ref float bestDot)
	{
		Vector3 direction = point - sight;
		float distance = direction.magnitude;
		if (distance <= 0f || distance > MAX_DISTANCE)
		{
			return;
		}
		float dot = Vector3.Dot(direction / distance, forward);
		if (dot <= LOCK_ON_DOT || (bestIsEnemy && !enemy) || (bestIsEnemy == enemy && dot <= bestDot))
		{
			return;
		}
		if (!HasLineOfSight(point))
		{
			return;
		}
		best = candidate;
		bestIsEnemy = enemy;
		bestDot = dot;
	}

	private bool IsStillLockable(Component locked, Vector3 sight, Vector3 forward)
	{
		if (locked is Actor actor ? actor.dead || actor.IsSeated() || !actor.gameObject.activeInHierarchy : ((Vehicle)locked).dead)
		{
			return false;
		}
		Vector3 point = AimPointOf(locked);
		Vector3 direction = point - sight;
		float distance = direction.magnitude;
		return distance > 0f && distance <= MAX_DISTANCE
			&& Vector3.Dot(direction / distance, forward) > LOCK_ON_DOT
			&& HasLineOfSight(point);
	}

	private bool HasLineOfSight(Vector3 point)
	{
		Vector3 origin = LineOfSightOrigin();
		Vector3 toPoint = point - origin;
		return !Physics.Raycast(new Ray(origin, toPoint), toPoint.magnitude, TARGET_LOS_MASK);
	}

	/// <summary>Where a lock on <paramref name="locked"/> points: a soldier's chest, a vehicle's origin.</summary>
	private static Vector3 AimPointOf(Component locked)
	{
		return locked is Actor actor ? actor.Position() + Vector3.up * ChestHeight : locked.transform.position;
	}

	private bool IsInFov(Vector3 point)
	{
		return Vector3.Dot((point - SightPosition()).normalized, SampleForward()) > 0.99f;
	}

	public override bool CanBeAimed()
	{
		return base.CanBeAimed() && HasLoadedAmmo();
	}
}
