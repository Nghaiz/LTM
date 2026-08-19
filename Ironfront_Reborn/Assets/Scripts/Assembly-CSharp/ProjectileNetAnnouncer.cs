using Ironfront.Net.Protocol;
using Ironfront.Net.Replication.Movement;
using Ironfront.Net.Unity;
using Ironfront.Net.Unity.Server;
using UnityEngine;

/// <summary>
/// The one place a spawned projectile becomes a replicated one. Phase-V7 tasks 2 and 3.
/// </summary>
/// <remarks>
/// <para>
/// <b>Hooked at <c>Weapon.SpawnProjectile</c> because that is the single instantiation point.</b>
/// Every weapon in the game reaches the wire through it — rifles, launchers, tank guns, the
/// throwables via <c>ThrowableWeapon.Shoot</c>, and the deployables — so announcing here means
/// there is no second route that quietly skips replication. It is also where the spread roll
/// already happens (<c>Weapon.cs:497</c>), which is V7-D4's server roll resolved exactly once,
/// on the server, before the direction is announced.
/// </para>
/// <para>
/// <b>The kind is read off the prefab's component type, not from authored data.</b> A
/// <c>weaponId → ProjectileKind</c> table would be a second source of truth for something the
/// prefab already states unambiguously: a prefab carrying a <c>JavelinMissile</c> IS a guided
/// missile. It also means nothing has to be assigned in the Editor for replication to work, so
/// this cannot silently do nothing because a table row was missed.
/// </para>
/// </remarks>
public static class ProjectileNetAnnouncer
{
	/// <summary>
	/// Registers and announces a freshly spawned projectile, and stamps the id the server
	/// assigned onto it. A no-op off the server.
	/// </summary>
	public static void AnnounceLaunch(Projectile projectile, Vector3 origin, Vector3 direction, Actor source)
	{
		if (projectile == null) return;
		if (!NetContext.IsServer) return;

		ServerProjectileBridge bridge = ServerTickLoop.Current?.Projectiles;
		if (bridge == null) return;

		ushort actorId = ActorIdOf(source);
		ProjectileKind kind = KindOf(projectile);

		// The server learns each kind's numbers from the first prefab of that kind it fires,
		// rather than from an array somebody has to remember to fill in. See
		// ServerProjectileBridge.EnsureConfig for why an unregistered kind would fail silently.
		bridge.EnsureConfig(kind, ProjectileCatalogBuilder.FromConfiguration(projectile.configuration));

		var netOrigin = new Vec3(origin.x, origin.y, origin.z);

		if (kind == ProjectileKind.AmmoBag || kind == ProjectileKind.Medipack)
		{
			// A deployable leaves the hand with a Rigidbody velocity rather than a muzzle
			// direction, and it is not ballistically stepped -- Ammobox.Awake and Medipack.Awake
			// hand the Rigidbody its initial velocity and Unity owns the tumble from there.
			Vector3 velocity = direction.normalized * projectile.configuration.speed;
			projectile.netProjectileId = bridge.Deploy(
				kind, actorId, in netOrigin,
				new Vec3(velocity.x, velocity.y, velocity.z),
				projectile.configuration.lifetime);

			// Without this the bag's recorded velocity never changes, so it never reads as at
			// rest and re-announces at 10 Hz for its entire life -- the opposite of V7-D8.
			if (projectile.netProjectileId != 0) AttachSync(projectile);
			return;
		}

		var netDirection = new Vec3(direction.x, direction.y, direction.z);
		projectile.netProjectileId = bridge.Launch(kind, in netOrigin, in netDirection, actorId);

		// A hitscan-resolved bullet is deliberately not announced -- its tracer already rides
		// S_WEAPON_FIRE -- so it has no id and needs no sync component.
		if (projectile.netProjectileId != 0) AttachSync(projectile);

		// A grenade's fuse counts from the launch tick on both sides. Arming it from the same
		// number the message carries is what makes "the same tick" true rather than approximate.
		if (projectile is GrenadeProjectile grenade)
		{
			grenade.ArmFuse(NetContext.CurrentTick);
		}
	}

	/// <summary>
	/// Attaches the component that keeps the server's record in step with the GameObject, and
	/// that returns the id when the projectile dies.
	/// </summary>
	/// <remarks>
	/// Added at runtime rather than authored on every projectile prefab, because it is
	/// server-only bookkeeping that would otherwise have to be remembered on each of the
	/// game's projectile prefabs — and a prefab that was missed would leak an id per shot and
	/// re-announce forever, silently.
	/// </remarks>
	private static void AttachSync(Projectile projectile)
	{
		if (projectile.GetComponent<ProjectileNetSync>() == null)
		{
			projectile.gameObject.AddComponent<ProjectileNetSync>();
		}
	}

	/// <summary>
	/// Which <see cref="ProjectileKind"/> this prefab is, by its component type.
	/// </summary>
	/// <remarks>
	/// Ordered most-derived first, because the hierarchy is
	/// <c>JavelinMissile : Rocket : ExplodingProjectile : Projectile</c> and
	/// <c>Ammobox</c>/<c>Medipack</c>/<c>GrenadeProjectile</c> all derive from
	/// <c>Projectile</c> — testing the base first would report every projectile in the game as
	/// a bullet.
	/// </remarks>
	public static ProjectileKind KindOf(Projectile projectile)
	{
		if (projectile is JavelinMissile) return ProjectileKind.GuidedMissile;
		if (projectile is Ammobox) return ProjectileKind.AmmoBag;
		if (projectile is Medipack) return ProjectileKind.Medipack;
		if (projectile is GrenadeProjectile) return ProjectileKind.Grenade;

		// Rocket and every other ExplodingProjectile -- rockets and tank shells alike, which is
		// the same grouping ExplodingProjectile.Explode already uses when it passes
		// ExplosionKind.Rocket for both (V1 task 3).
		if (projectile is ExplodingProjectile) return ProjectileKind.Rocket;

		return ProjectileKind.Bullet;
	}

	/// <summary>
	/// The replicated id for an actor, or 0 when it has none. A bot spawned outside the server
	/// registry legitimately has none, and its projectiles simply are not replicated.
	/// </summary>
	private static ushort ActorIdOf(Actor actor)
	{
		if (actor == null) return 0;

		return actor.TryGetComponent(out NetServerActor netActor) ? netActor.ActorId : (ushort)0;
	}
}
