using System;
using Ironfront.Net.Unity;
using UnityEngine;

public class Projectile : MonoBehaviour
{
	[Serializable]
	public class Configuration
	{
		public float speed = 300f;

		public float impactForce = 200f;

		public float lifetime = 2f;

		public float damage = 70f;

		public float balanceDamage = 60f;

		public float impactDecalSize = 0.2f;

		public bool piercing;

		public bool makesFlybySound;

		public float flybyPitch = 1f;

		public float dropoffEnd = 300f;

		public AnimationCurve damageDropOff;
	}

	private const float PASS_PLAYER_MAX_SOUND_DISTANCE = 15f;

	private const int LEVEL_LAYER = 0;

	private const int RAGDOLL_LAYER = 10;

	private const int HIT_MASK = -2049;

	private const float PIERCING_RANGE = 2f;

	public Configuration configuration;

	protected Vector3 velocity = Vector3.zero;

	protected float expireTime;

	[NonSerialized]
	public Actor source;

	/// <summary>
	/// Whether this projectile warns enemy AI that fire is incoming. V7 task 3.
	/// </summary>
	/// <remarks>
	/// <c>ActorManager.RegisterProjectile</c> raycasts 9999 m and walks every alive enemy, and
	/// the base <c>Start</c> called it for everything that inherits from this class -- so a
	/// thrown <b>Medipack</b> made the enemy team duck. Subclasses that are not weapons clear
	/// this; see <c>Ammobox</c> and <c>Medipack</c>.
	/// </remarks>
	protected bool warnsEnemyAi = true;

	/// <summary>
	/// The projectile id the server assigned, or 0 when this is an offline or purely cosmetic
	/// instance. V7 task 3: what lets a re-announce find this object instead of spawning a
	/// second one.
	/// </summary>
	[NonSerialized]
	public ushort netProjectileId;

	private bool travellingTowardsPlayer;

	private float travelDistance;

	protected virtual void Start()
	{
		velocity = base.transform.forward * configuration.speed;
		expireTime = Time.time + configuration.lifetime;
		if (warnsEnemyAi)
		{
			ActorManager.RegisterProjectile(this);
		}
	}

	protected virtual void Update()
	{
		if (Time.time > expireTime)
		{
			UnityEngine.Object.Destroy(base.gameObject);
			return;
		}
		Vector3 position = base.transform.position;
		travelDistance += configuration.speed * Time.deltaTime;
		// V7 task 1: the half-acceleration term makes the arc exact for constant gravity and so
		// identical at any framerate. Without it the drop carries a +0.5*g*dt*T error, which is
		// about 33 cm over a two-second flight at 30 Hz against 6 cm of position quantization --
		// so server and client disagreed about where a bullet was purely from frame timing.
		// Recorded in Ballistics.Step as the third deliberate change to offline behaviour.
		Vector3 delta = velocity * Time.deltaTime
			+ Physics.gravity * (0.5f * Time.deltaTime * Time.deltaTime);
		velocity += Physics.gravity * Time.deltaTime;
		Travel(delta);
		if (!configuration.makesFlybySound)
		{
			return;
		}
		// A flyby is a sound played near the local player's ears. On a dedicated server there
		// is no player and this whole block is measurement for nobody -- and every bot's every
		// bullet used to run it. V7 task 3 makes the role explicit rather than relying on the
		// player happening to be null: a headless server that ever gains an ActorManager.Player
		// would otherwise start doing this work again silently.
		if (NetContext.IsServer)
		{
			return;
		}
		Actor player = ActorManager.Player;
		if (player == null || FpsActorController.instance == null)
		{
			return;
		}
		Vector3 vector = player.Position();
		Vector3 lhs = base.transform.position - vector;
		bool flag = travellingTowardsPlayer;
		travellingTowardsPlayer = Vector3.Dot(lhs, velocity) < 0f;
		if (!travellingTowardsPlayer && flag)
		{
			Vector3 vector2 = SMath.LineVsPointClosest(position, base.transform.position, vector);
			if (Vector3.Distance(vector2, vector) < 15f)
			{
				FpsActorController.instance.BulletFlyby(vector2, UnityEngine.Random.Range(configuration.flybyPitch, 0.9f * configuration.flybyPitch));
			}
		}
	}

	protected virtual void Travel(Vector3 delta)
	{
		Ray ray = new Ray(base.transform.position, delta.normalized);
		bool flag = true;
		RaycastHit hitInfo;
		// V7-D5-local: sweep EXACTLY the segment about to be traversed. This was
		// `delta.magnitude * 2f`, which swept twice as far as the projectile then advanced -- so
		// whether a thin collider registered depended on frame time (a 144 Hz client swept ~7 mm
		// per step, a 30 Hz one ~33 mm, and each swept double). Accepted as a deliberate change
		// to offline behaviour under brainstorm D8; ASweptSegmentIsNotDoubleCounted pins it.
		if (Physics.Raycast(ray, out hitInfo, delta.magnitude, -2049) && Hit(ray, hitInfo))
		{
			flag = false;
			if (hitInfo.collider.gameObject.layer == 0)
			{
				SpawnDecal(hitInfo);
			}
		}
		if (flag)
		{
			base.transform.position += delta;
		}
	}

	protected virtual bool Hit(Ray ray, RaycastHit hitInfo)
	{
		if (hitInfo.collider.CompareTag("Piercable"))
		{
			Collider collider = hitInfo.collider;
			collider.enabled = false;
			Ray ray2 = new Ray(hitInfo.point, ray.direction);
			RaycastHit hitInfo2;
			if (Physics.Raycast(ray2, out hitInfo2, 2f, -2049))
			{
				hitInfo = hitInfo2;
			}
			collider.enabled = true;
		}
		if (Hitbox.IsHitboxLayer(hitInfo.collider.gameObject.layer))
		{
			Hitbox component = hitInfo.collider.GetComponent<Hitbox>();
			if (component.parent == source)
			{
				base.transform.position = hitInfo.point + velocity.normalized * 0.2f;
			}
			// V7-D3: damage is the server's, computed from the server's own distance
			// accumulator. Two peers with different frame times accumulate different distances,
			// so a client-computed number is a different number -- and a modified client's is
			// whatever it likes. A networked client's projectile is a thing you watch.
			else if (!NetContext.IsClient && component.ProjectileHit(this, hitInfo.point)
				&& !source.aiControlled)
			{
				// V7 task 3: offline only. On a server the hitmarker travels to the shooter as
				// S_HIT_CONFIRM, which phase-05 already emits; a locally-predicted marker for a
				// shot the server missed is a worse lie than one that arrives 60 ms late.
				if (NetContext.IsOffline)
				{
					IngameUi.Hit();
				}
			}
		}
		Rigidbody attachedRigidbody = hitInfo.collider.attachedRigidbody;
		if (attachedRigidbody != null)
		{
			// Prop and ragdoll motion. Cosmetic on a client, authoritative on the server, and
			// harmless on both -- so this one is deliberately NOT role-gated.
			attachedRigidbody.AddForceAtPosition(velocity.normalized * configuration.impactForce, hitInfo.point, ForceMode.Impulse);
		}
		if (configuration.makesFlybySound && travellingTowardsPlayer && !NetContext.IsServer
			&& ActorManager.Player != null
			&& FpsActorController.instance != null
			&& Vector3.Distance(hitInfo.point, ActorManager.Player.Position()) < 15f)
		{
			FpsActorController.instance.BulletFlyby(hitInfo.point, configuration.flybyPitch);
		}
		UnityEngine.Object.Destroy(base.gameObject);
		return true;
	}

	protected virtual void SpawnDecal(RaycastHit hitInfo)
	{
		DecalManager.AddDecal(hitInfo.point, hitInfo.normal, configuration.impactDecalSize, DecalManager.DecalType.Impact);
	}

	public virtual float Damage()
	{
		return DamageDropOff() * configuration.damage;
	}

	public virtual float BalanceDamage()
	{
		return DamageDropOff() * configuration.balanceDamage;
	}

	private float DamageDropOff()
	{
		return configuration.damageDropOff.Evaluate(travelDistance / configuration.dropoffEnd);
	}
}
