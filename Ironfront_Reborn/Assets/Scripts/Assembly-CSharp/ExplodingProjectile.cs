using System;
using UnityEngine;

public class ExplodingProjectile : Projectile
{
	[Serializable]
	public class ExplosionConfiguration
	{
		public float damage = 300f;

		public float balanceDamage = 300f;

		public float force = 500f;

		public float damageRange = 6f;

		public AnimationCurve damageFalloff;

		public float balanceRange = 9f;

		public AnimationCurve balanceFalloff;
	}

	private const float CLEANUP_TIME = 10f;

	public ExplosionConfiguration explosionConfiguration;

	public float smokeTime = 8f;

	public Renderer[] renderers;

	public ParticleSystem trailParticles;

	public ParticleSystem impactParticles;

	protected AudioSource audioSource;

	protected virtual void Awake()
	{
		audioSource = GetComponent<AudioSource>();
	}

	protected override bool Hit(Ray ray, RaycastHit hitInfo)
	{
		base.transform.position = hitInfo.point;
		bool flag = false;
		if (hitInfo.collider.gameObject.layer == 12)
		{
			Vehicle componentInParent = hitInfo.collider.gameObject.GetComponentInParent<Vehicle>();
			if (source.IsSeated() && componentInParent == source.seat.vehicle)
			{
				return false;
			}
			flag = !componentInParent.dead;
			componentInParent.Damage(Damage());
		}
		if ((Explode(hitInfo.point, hitInfo.normal) || flag) && !source.aiControlled)
		{
			IngameUi.Hit();
		}
		return true;
	}

	protected virtual bool Explode(Vector3 position, Vector3 up)
	{
		// V1 task 3: Explode now needs to know WHO set it off and WHAT KIND it is, so the
		// server can attribute the S_EXPLOSION and a client can recognise its own blast.
		// Rocket covers every ExplodingProjectile in scope -- rockets and tank shells alike.
		// debt-closure phase 2 task 2e. LibraryOwnsProjectileDamage, NOT the negation of
		// EngineAppliesProjectileDamage: this call also applies the corpse ragdoll impulse, which
		// is kept at every role because corpses are never replicated (AD-4), so gating it on
		// "does the engine apply damage" would switch corpses off on every client. The narrower
		// question is false offline and on a client, so both are byte-for-byte unchanged; it goes
		// true only on a server that has handed flight to the library stepper (ledger C-1).
		bool result = !Ironfront.Net.Unity.Server.NetProjectileAuthority.LibraryOwnsProjectileDamage
			&& ActorManager.Explode(
				position, explosionConfiguration, source,
				Ironfront.Net.Protocol.ExplosionKind.Rocket);
		base.transform.rotation = Quaternion.LookRotation(up);
		base.enabled = false;
		Renderer[] array = renderers;
		foreach (Renderer renderer in array)
		{
			renderer.enabled = false;
		}
		if (trailParticles != null)
		{
			trailParticles.Stop(true);
		}
		// V1 handed these two sites to V0's headless list and V0 closed without absorbing them,
		// so they stayed unguarded a phase longer than the guard three lines above. A dedicated
		// server builds these prefabs with no particle system and no audio source; the blast
		// itself is gameplay and already happened in ActorManager.Explode above, so everything
		// from here down is cosmetic and skipping it changes nothing a client can observe.
		if (impactParticles != null)
		{
			impactParticles.Play(true);
		}
		if (audioSource != null)
		{
			audioSource.Stop();
			// Not UnityEngine.Random -- see CosmeticRandom. A sound must not be able to advance
			// a stream gameplay reads, least of all at a different rate on server and client.
			audioSource.pitch = CosmeticRandom.Range(0.9f, 1.1f);
			audioSource.volume = 1f;
			audioSource.Play();
		}
		// V7 task 8: on a server this chain existed only to hold a dead GameObject alive for
		// eighteen seconds so particles nobody can see could finish. Both timers now go through
		// one policy, and nameof() makes them greppable -- the string forms were two names no
		// search could find.
		if (ProjectileCleanupPolicy.PlaysCosmetics)
		{
			Invoke(nameof(StopSmoke), ProjectileCleanupPolicy.HoldSeconds(smokeTime));
		}
		else
		{
			Invoke(nameof(Cleanup), 0f);
		}
		return result;
	}

	private void StopSmoke()
	{
		// Reached on a client and offline only -- a server takes the direct Cleanup branch above.
		if (impactParticles != null)
		{
			impactParticles.Stop(true);
		}
		Invoke(nameof(Cleanup), ProjectileCleanupPolicy.HoldSeconds(CLEANUP_TIME));
	}

	private void Cleanup()
	{
		UnityEngine.Object.Destroy(base.gameObject);
	}
}
