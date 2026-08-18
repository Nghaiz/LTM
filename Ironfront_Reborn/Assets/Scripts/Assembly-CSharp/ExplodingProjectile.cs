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
		bool result = ActorManager.Explode(
			position, explosionConfiguration, source, Ironfront.Net.Protocol.ExplosionKind.Rocket);
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
		impactParticles.Play(true);
		audioSource.Stop();
		audioSource.pitch = UnityEngine.Random.Range(0.9f, 1.1f);
		audioSource.volume = 1f;
		audioSource.Play();
		Invoke("StopSmoke", smokeTime);
		return result;
	}

	private void StopSmoke()
	{
		impactParticles.Stop(true);
		Invoke("Cleanup", 10f);
	}

	private void Cleanup()
	{
		UnityEngine.Object.Destroy(base.gameObject);
	}
}
