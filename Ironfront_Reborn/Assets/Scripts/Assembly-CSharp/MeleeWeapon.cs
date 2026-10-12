using System.Collections;
using System.Collections.Generic;
using UnityEngine;

public class MeleeWeapon : Weapon
{
	private const int HIT_MASK = 66816;

	public float radius = 0.4f;

	public float range = 2f;

	public float swingTime = 0.15f;

	public float damage = 55f;

	public float balanceDamage = 150f;

	public float force = 100f;

	public AudioClip hitSound;

	protected override void Shoot(Vector3 direction, bool useMuzzleDirection)
	{
		base.Shoot(direction, useMuzzleDirection);
		StartCoroutine(Swing());
	}

	protected override Projectile SpawnProjectile(Vector3 direction)
	{
		return null;
	}

	private IEnumerator Swing()
	{
		yield return new WaitForSeconds(swingTime);
		ShootMeleeRay();
	}

	protected void ShootMeleeRay()
	{
		ShootMeleeRay(new Ray(configuration.muzzle.position, configuration.muzzle.forward));
	}

	/// <summary>
	/// Deals a swing the server has approved for a networked player (owner's run of 2026-10-12):
	/// at once, from the player's eye along the player's aim, which the server supplies because a
	/// server-side body's muzzle hangs in a bind pose. The client plays the swing itself. Before this
	/// the server never dealt a networked player's swing, so the wrench neither hurt nor repaired
	/// anything online.
	/// </summary>
	public void SwingApprovedByServer(Vector3 eye, Vector3 aim)
	{
		lastFired = Time.time;
		ShootMeleeRay(new Ray(eye, aim.sqrMagnitude > 0f ? aim.normalized : Vector3.forward));
	}

	private static readonly RaycastHit[] _swingHits = new RaycastHit[16];

	private static readonly IComparer<RaycastHit> ByDistance =
		Comparer<RaycastHit>.Create((a, b) => a.distance.CompareTo(b.distance));

	/// <summary>
	/// The first thing the swing meets on <paramref name="mask"/>, never the swinger's own body: a
	/// server-side body carries hitboxes on every bone, and an eye-level swing can start inside
	/// its own arm. Offline the player's body has none in reach, so this is the original's first hit.
	/// </summary>
	protected bool FirstHitInReach(Ray ray, int mask, out RaycastHit hitInfo)
	{
		int count = Physics.SphereCastNonAlloc(ray, radius, _swingHits, range, mask);
		System.Array.Sort(_swingHits, 0, count, ByDistance);
		for (int i = 0; i < count; i++)
		{
			Hitbox own = _swingHits[i].collider.GetComponent<Hitbox>();
			if (own != null && user != null && own.parent == user) continue;
			hitInfo = _swingHits[i];
			return true;
		}
		hitInfo = default;
		return false;
	}

	protected virtual void ShootMeleeRay(Ray ray)
	{
		RaycastHit hitInfo;
		if (FirstHitInReach(ray, 66816, out hitInfo))
		{
			Hitbox component = hitInfo.collider.GetComponent<Hitbox>();
			HitSomething(hitInfo.collider);
			audio.PlayOneShot(hitSound);
			// Null for a client's remote corpse: RemoteRagdoll builds its colliders on the Ragdoll
			// layer with no Hitbox behind them (see Projectile.Hit). The swing lands and has nothing
			// to damage.
			// Attributed to whoever swung, so a melee kill names its killer instead of "The world"
			// (feature 2, 2026-09-29). A prop takes the plain path: only an Actor keeps a killer.
			bool hurt;
			using (DeathContext.Melee(NetworkId))
			{
				Actor victim = component != null ? component.parent as Actor : null;
				hurt = victim != null
					? victim.DamageAttributed(damage, balanceDamage, false, hitInfo.point, ray.direction, ray.direction * force, user)
					: component != null
						&& component.parent.Damage(damage, balanceDamage, false, hitInfo.point, ray.direction, ray.direction * force);
			}
			if (hurt)
			{
				IngameUi.Hit();
			}
		}
	}

	protected virtual void HitSomething(Collider c)
	{
	}
}
