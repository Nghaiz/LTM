using UnityEngine;

public class Hitbox : MonoBehaviour
{
	public const int LAYER = 8;

	public const int RAGDOLL_LAYER = 10;

	public const int SEATED_LAYER = 16;

	private const float RIGIDBODY_HIT_FORCE = 0.01f;

	public Hurtable parent;

	public float multiplier = 1f;

	public static bool IsHitboxLayer(int layer)
	{
		return layer == 8 || layer == 10 || layer == 16;
	}

	public bool ProjectileHit(Projectile p, Vector3 position)
	{
		Actor actor = parent as Actor;
		if (actor != null)
		{
			bool hurt = actor.DamageAttributed(
				p.Damage() * multiplier,
				p.BalanceDamage(),
				p.configuration.piercing,
				position,
				p.transform.forward,
				p.configuration.impactForce * p.transform.forward,
				p.source);
			// Achievements v2: a bullet that hurt a soldier is a hit for its shooter's accuracy.
			if (hurt && p.source != null)
			{
				Ironfront.Net.Unity.Server.ServerCombatEvents.ReportHit(p.source, actor, p.sourceWeaponId, p.shotSerial);
			}
			return hurt;
		}

		return parent.Damage(p.Damage() * multiplier, p.BalanceDamage(), p.configuration.piercing, position, p.transform.forward, p.configuration.impactForce * p.transform.forward);
	}

	/// <summary>
	/// A rigidbody driven into this hitbox: the ram check. <paramref name="attacker"/> is whoever
	/// was driving, or null, and a death this causes is credited to them (feature 2, 2026-09-29).
	/// </summary>
	public bool RigidbodyHit(Rigidbody r, Vector3 position, Actor attacker)
	{
		Actor actor = parent as Actor;
		if (actor != null)
		{
			return actor.DamageAttributed(20f, 200f, false, position, r.linearVelocity.normalized, r.linearVelocity * r.mass * 0.01f, attacker);
		}

		return parent.Damage(20f, 200f, false, position, r.linearVelocity.normalized, r.linearVelocity * r.mass * 0.01f);
	}
}
