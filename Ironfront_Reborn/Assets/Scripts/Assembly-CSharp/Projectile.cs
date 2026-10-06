using System;
using Ironfront.Net.Unity;
using UnityEngine;

public partial class Projectile : MonoBehaviour, Ironfront.Net.Unity.IProjectileBody
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

		/// <summary>
		/// Metres flown dead straight before gravity takes hold. 0, the default, is the original
		/// game: everything drops from the muzzle.
		/// </summary>
		/// <remarks>
		/// Authored on the BEU-AW1's rocket (owner, v4.3.0 playtest: "it must fly dead straight to
		/// show how powerful it is, and only drop once it has flown very far"). A motor that keeps
		/// burning, not a lighter projectile: past this distance the arc is the ordinary one. The
		/// server's engine flight and every client's drawing run this same Update, so both sides
		/// fly the same line. Bots aim with it too (<c>AiActorController.WeaponLead</c>).
		/// </remarks>
		public float straightDistance;

		/// <summary>
		/// A plain round's air drag: the quadratic drag constant k, per metre, its speed falling by
		/// e^(-k*x) over x metres (<c>RoundBallistics.DragPerMetre</c>).
		/// </summary>
		/// <remarks>
		/// With <see cref="speed"/> (the muzzle velocity) and <see cref="zeroMetres"/> it is the
		/// round's whole flight, the same numbers the server sweeps a player's shot along
		/// (<c>WeaponCatalog.Rounds</c>); <c>RoundBallisticsPrefabTests</c> fails if the two drift.
		/// </remarks>
		public float dragPerMetre;

		/// <summary>
		/// The range, metres, a plain round's sights are zeroed at: it leaves the muzzle tilted up
		/// just enough to fall back onto the line of sight there (<c>RoundBallistics.ZeroMetres</c>).
		/// </summary>
		public float zeroMetres;
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
	/// The weapon that fired this, as its <c>NetworkId</c>, for the killfeed. Set beside
	/// <see cref="source"/> in <c>Weapon.SpawnProjectile</c>; 0 (<c>WeaponIds.NONE</c>) otherwise.
	/// </summary>
	[NonSerialized]
	public byte sourceWeaponId;

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

	/// <summary>Where a plain round left the muzzle, the start of its <see cref="Round"/> flight.</summary>
	private Vector3 flightOrigin;

	/// <summary>The direction a plain round left the muzzle: the aim tilted up to the sights' zero.</summary>
	private Vector3 flightLaunch;

	/// <summary>Seconds a plain round has flown.</summary>
	private float flightTime;

	/// <summary>
	/// The flight of a plain round (<see cref="IsHitscanRound"/>) as its prefab authors it: the
	/// muzzle velocity, the drag and the zero.
	/// </summary>
	public Ironfront.Net.Replication.Combat.RoundBallistics Round
		=> new Ironfront.Net.Replication.Combat.RoundBallistics(configuration.speed, configuration.dragPerMetre, configuration.zeroMetres);

	/// <summary>
	/// A plain round: this class itself, no warhead, guidance or deployable behind it -- a rifle,
	/// pistol, shotgun or sniper bullet.
	/// </summary>
	/// <remarks>
	/// <para>
	/// <b>It flies its gun's own ballistic arc</b> (<see cref="Round"/>, owner request
	/// 2026-10-06): its muzzle velocity, the air's drag and gravity, out of a barrel tilted to the
	/// sights' zero. The server sweeps a player's shot along the same arc
	/// (<c>LagCompensator.ResolveBallistic</c>), a bot's round flies it here, and every client
	/// draws it here, all from one closed-form flight, so the line drawn is the line judged at
	/// any frame rate. Until then these rounds flew dead straight (#552), because the server
	/// judged a straight ray.
	/// </para>
	/// <para>
	/// Weapons whose rounds the server flies itself -- rockets, shells, the gatling's exploding
	/// rounds -- keep <see cref="FlightStep"/>.
	/// </para>
	/// </remarks>
	public static bool IsHitscanRound(Projectile projectile)
	{
		return projectile != null && projectile.GetType() == typeof(Projectile);
	}

	protected virtual void Start()
	{
		velocity = base.transform.forward * configuration.speed;
		if (IsHitscanRound(this))
		{
			Vector3 aim = base.transform.forward;
			Ironfront.Net.Replication.Movement.Vec3 launch = Round.LaunchDirection(new Ironfront.Net.Replication.Movement.Vec3(aim.x, aim.y, aim.z));
			flightLaunch = new Vector3(launch.X, launch.Y, launch.Z);
			flightOrigin = base.transform.position;
			flightTime = 0f;
			velocity = flightLaunch * configuration.speed;
		}
		expireTime = Time.time + configuration.lifetime;
		if (warnsEnemyAi)
		{
			ActorManager.RegisterProjectile(this);
		}
	}

	/// <summary>
	/// Re-seats this projectile's velocity from an authoritative re-parameterization.
	/// V7-D6 and V7-D8.
	/// </summary>
	/// <remarks>
	/// <c>velocity</c> is <c>protected</c>, so a presenter outside the hierarchy cannot write it
	/// — and a re-seat that moved only the transform would leave the projectile coasting on its
	/// launch vector between corrections. Deployables carry a Rigidbody instead, and their
	/// velocity lives there.
	/// </remarks>
	public void ApplyNetVelocity(Vector3 netVelocity)
	{
		velocity = netVelocity;

		Rigidbody body = GetComponent<Rigidbody>();
		if (body != null) body.linearVelocity = netVelocity;

		OnNetVelocity(netVelocity);
	}

	/// <summary>
	/// Called with every authoritative velocity a presented copy is handed, so a projectile that
	/// steers can carry its turn on between re-seats (<c>JavelinMissile</c>).
	/// </summary>
	protected virtual void OnNetVelocity(Vector3 announced)
	{
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
		Vector3 delta = IsHitscanRound(this)
			? BallisticStep(Time.deltaTime)
			: FlightStep(ref velocity, travelDistance, configuration.straightDistance, Time.deltaTime);
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

	/// <summary>
	/// One step of a free flight: the displacement over <paramref name="dt"/>, with
	/// <paramref name="velocity"/> advanced to the step's end. Gravity acts only once
	/// <paramref name="travelled"/> is past <paramref name="straightDistance"/>
	/// (<see cref="Configuration.straightDistance"/>).
	/// </summary>
	public static Vector3 FlightStep(ref Vector3 velocity, float travelled, float straightDistance, float dt)
	{
		Vector3 gravity = travelled > straightDistance ? Physics.gravity : Vector3.zero;
		Vector3 delta = velocity * dt + gravity * (0.5f * dt * dt);
		velocity += gravity * dt;
		return delta;
	}

	/// <summary>
	/// One frame of a plain round's flight: the displacement to where its closed-form arc
	/// (<see cref="Round"/>) puts it after <paramref name="dt"/> more seconds, with
	/// <see cref="velocity"/> set to the arc's own. Computed from the launch, not accumulated, so
	/// a slow frame and a fast one put the round at the same point at the same time.
	/// </summary>
	private Vector3 BallisticStep(float dt)
	{
		flightTime += dt;
		Ironfront.Net.Replication.Combat.RoundBallistics round = Round;
		var origin = new Ironfront.Net.Replication.Movement.Vec3(flightOrigin.x, flightOrigin.y, flightOrigin.z);
		var launch = new Ironfront.Net.Replication.Movement.Vec3(flightLaunch.x, flightLaunch.y, flightLaunch.z);
		Ironfront.Net.Replication.Movement.Vec3 at = round.PositionAt(in origin, in launch, flightTime);
		Ironfront.Net.Replication.Movement.Vec3 v = round.VelocityAt(in launch, flightTime);
		velocity = new Vector3(v.X, v.Y, v.Z);
		return new Vector3(at.X, at.Y, at.Z) - base.transform.position;
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
		// to offline behaviour under brainstorm D8. ASweptSegmentIsNotDoubleCounted pins the
		// LIBRARY's equivalent; this line is Unity's own copy and no CI test can reach it.
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
		// A collider on a hitbox layer with NO Hitbox is geometry, not a body part. The stock rig
		// puts a Hitbox on every bone, so this read could not miss offline -- but a client's
		// REMOTE corpse is built by RemoteRagdoll at runtime on the Ragdoll layer (10), with
		// colliders and nothing to damage behind them. Read as a Hitbox, every round that struck
		// a remote body lying on the ground threw a NullReferenceException here (both playtest
		// clients, 2026-09-27) and died inside its own Update, never reaching the impulse and the
		// Destroy below. Falling through is the answer: the round stops, and the impulse knocks
		// the limb it struck.
		Hitbox component = Hitbox.IsHitboxLayer(hitInfo.collider.gameObject.layer)
			? hitInfo.collider.GetComponent<Hitbox>()
			: null;
		// Names the weapon for a death this hit causes (feature 2, 2026-09-29): the hit reaches
		// Actor.DamageAttributed through Hitbox.ProjectileHit, which carries no weapon.
		using (DeathContext.Weapon(sourceWeaponId))
		if (component != null)
		{
			if (component.parent == source)
			{
				base.transform.position = hitInfo.point + velocity.normalized * 0.2f;
			}
			// V7-D3: damage is the server's, computed from the server's own distance
			// accumulator. Two peers with different frame times accumulate different distances,
			// so a client-computed number is a different number -- and a modified client's is
			// whatever it likes. A networked client's projectile is a thing you watch.
			// debt-closure phase 2 task 2e: NetProjectileAuthority.EngineAppliesProjectileDamage
			// subsumes the !NetContext.IsClient this line carried -- a client already applied no
			// damage here, and now a SERVER running the library stepper does not either. Without
			// it, flipping AuthoritativeFlight would apply this hit twice (ledger C-1).
			else if (Ironfront.Net.Unity.Server.NetProjectileAuthority.EngineAppliesProjectileDamage
				&& component.ProjectileHit(this, hitInfo.point)
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
		// debt-closure phase 2 task 2f (ledger C-11): a shot fuel drum goes off. Behind the same
		// ownership question as the hitbox damage above -- a client neither decides that a prop
		// was destroyed nor applies the resulting blast; the server does, and announces it as
		// S_EXPLOSION with ExplosionKind.Environment.
		if (Ironfront.Net.Unity.Server.NetProjectileAuthority.EngineAppliesProjectileDamage)
		{
			ExplosiveProp prop = hitInfo.collider.gameObject.GetComponentInParent<ExplosiveProp>();
			if (prop != null)
			{
				prop.Damage(Damage());
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
