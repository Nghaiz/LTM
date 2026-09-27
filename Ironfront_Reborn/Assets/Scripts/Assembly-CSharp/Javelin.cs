using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;

public class Javelin : ScopedWeapon
{
	private const float MAX_DISTANCE = 1000f;

	private const int TARGET_LOS_MASK = 1;

	private const int TARGET_LAYER_MASK = 5377;

	private const float LOCK_ON_DOT = 0.99f;

	public Transform pointSampler;

	public RawImage lockImage;

	public RawImage targetImage;

	public Texture2D lockingTexture;

	public Texture2D lockedTexture;

	public Renderer crosshair;

	private bool hasManualTarget;

	private bool lockingOnManualTarget;

	private Vehicle target;

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

	public override bool WithholdsTrigger()
	{
		return !HasLock();
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

	public override void Unholster()
	{
		base.Unholster();
		hasManualTarget = false;
		if (ammo == 0)
		{
			ReloadDone();
		}
	}

	protected override Projectile SpawnProjectile(Vector3 direction)
	{
		Projectile projectile = base.SpawnProjectile(direction);
		JavelinMissile javelinMissile = (JavelinMissile)projectile;
		if (hasManualTarget)
		{
			javelinMissile.targetPoint = manualTargetPoint;
		}
		else
		{
			javelinMissile.target = target.transform;
			if (target.HasDriver() && target.directJavelinPath)
			{
				javelinMissile.ForceDirectMode();
			}
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
			Vehicle vehicle = null;
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
		if (!(user != null) || user.aiControlled)
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
				lockImage.rectTransform.position = Camera.main.WorldToScreenPoint(target.transform.position);
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
	}

	public override void Fire(Vector3 direction, bool useMuzzleDirection)
	{
		if (HasLock())
		{
			base.Fire(direction, useMuzzleDirection);
			Reload();
		}
		else if (!hasManualTarget)
		{
			Ray ray = new Ray(SampleOrigin(), SampleForward());
			RaycastHit hitInfo;
			if (Physics.Raycast(ray, out hitInfo, 1000f, 1))
			{
				manualTargetPoint = hitInfo.point;
				hasManualTarget = true;
			}
		}
	}

	private bool IsLocking()
	{
		return target != null || lockingOnManualTarget;
	}

	private bool HasLock()
	{
		return IsLocking() && lockOnAction.Done();
	}

	private Vehicle FindTarget()
	{
		List<Vehicle> sortedTargets = GetSortedTargets();
		foreach (Vehicle item in sortedTargets)
		{
			Vector3 direction = item.transform.position - SightPosition();
			if (IsInFov(item.transform.position))
			{
				Ray ray = new Ray(LineOfSightOrigin(), direction);
				if (!Physics.Raycast(ray, direction.magnitude, 1))
				{
					return item;
				}
			}
		}
		return null;
	}

	private bool IsInFov(Vector3 point)
	{
		return Vector3.Dot((point - SightPosition()).normalized, SampleForward()) > 0.99f;
	}

	private List<Vehicle> GetSortedTargets()
	{
		List<Vehicle> list = new List<Vehicle>(ActorManager.instance.vehicles);
		if (user.IsSeated())
		{
			list.Remove(user.seat.vehicle);
		}
		Dictionary<Vehicle, bool> isEnemy = new Dictionary<Vehicle, bool>();
		foreach (Vehicle item in list)
		{
			if (item.HasDriver())
			{
				isEnemy.Add(item, item.Driver().team != user.team);
			}
			else
			{
				isEnemy.Add(item, false);
			}
		}
		Vector3 sight = SightPosition();
		list.Sort((Vehicle x, Vehicle y) => (isEnemy[x] != isEnemy[y]) ? isEnemy[y].CompareTo(isEnemy[x]) : Vector3.Distance(sight, x.transform.position).CompareTo(Vector3.Distance(sight, y.transform.position)));
		return list;
	}

	public override bool CanBeAimed()
	{
		return base.CanBeAimed() && HasLoadedAmmo();
	}
}
