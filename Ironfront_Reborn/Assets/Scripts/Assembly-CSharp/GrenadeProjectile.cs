using Ironfront.Net.Unity;
using UnityEngine;

public partial class GrenadeProjectile : Projectile
{
	private const int LAYER_MASK = 4097;

	private const float CLEANUP_TIME = 10f;

	private const float ROTATION_SPEED_MAGNITUDE = 400f;

	public ExplodingProjectile.ExplosionConfiguration explosionConfiguration;

	public Renderer[] renderers;

	public float radius = 0.1f;

	public float bounciness = 0.5f;

	public float bounceDrag = 0.2f;

	private Vector3 rotationAxis;

	private float angularSpeed;

	/// <summary>
	/// The server tick this grenade was launched on, and the tick it detonates on. V7 task 4.
	/// </summary>
	/// <remarks>
	/// <c>Invoke("Explode", configuration.lifetime)</c> was a wall-clock, string-named timer:
	/// server and client each held their own float and fired on whichever frame crossed it. Both
	/// sides now count the same integer from the same launch tick, so they agree on the
	/// <b>tick</b> rather than the approximate second.
	/// </remarks>
	private uint detonationTick;

	private bool fuseArmed;

	private bool detonated;

	protected override void Start()
	{
		velocity = base.transform.forward * configuration.speed;
		rotationAxis = Random.insideUnitSphere.normalized;
		angularSpeed = 400f;

		// The tumble roll above stays client-local on purpose. V7-D4 governs GAMEPLAY-affecting
		// rolls; rotationAxis and angularSpeed drive only transform.Rotate, and the bounce reads
		// velocity and hitInfo.normal, never the rotation. Cosmetic, and exempt -- stated here so
		// it is a decision rather than an omission.

		if (NetContext.IsOffline)
		{
			// Offline has no tick clock to count against, so the authored wall-clock fuse stays
			// exactly as it was (V7-D11). nameof() rather than the string, so a grep finds it.
			Invoke(nameof(Explode), configuration.lifetime);
			return;
		}

		if (!fuseArmed) ArmFuse(NetContext.CurrentTick);
	}

	/// <summary>
	/// Sets the launch tick the fuse counts from. Called by the server authority at launch and
	/// by the client presenter on <c>S_PROJECTILE_SPAWN</c>, both with the same number.
	/// </summary>
	public void ArmFuse(uint spawnTick)
	{
		float tickDuration = 1f / Ironfront.Net.Protocol.ProtocolConstants.SIM_TICK_RATE;
		detonationTick = spawnTick + (uint)Mathf.Ceil(configuration.lifetime / tickDuration);
		fuseArmed = true;
	}

	protected override void Update()
	{
		if (fuseArmed && !detonated && NetContext.CurrentTick >= detonationTick)
		{
			Explode();
			return;
		}

		// V7 task 1: the half-acceleration term, for the reason spelled out in Projectile.Update
		// -- a bounce path that depends on framerate is a bounce path two peers disagree about.
		Vector3 vector = velocity * Time.deltaTime
			+ Physics.gravity * (0.5f * Time.deltaTime * Time.deltaTime);
		velocity += Physics.gravity * Time.deltaTime;
		Ray ray = new Ray(base.transform.position, vector);
		RaycastHit hitInfo;
		// V7-D5-local, the SphereCast half: sweep exactly the segment about to be traversed.
		// This was vector.magnitude * 2f.
		if (Physics.SphereCast(ray, radius, out hitInfo, vector.magnitude, 4097))
		{
			base.transform.position = hitInfo.point + hitInfo.normal * (radius + 0.01f);
			Vector3 vector2 = Vector3.Project(velocity, hitInfo.normal);
			velocity -= vector2 * (bounciness + 1f);
			Vector3 vector3 = velocity * bounceDrag;
			velocity -= vector3;
			rotationAxis = base.transform.worldToLocalMatrix.MultiplyVector((Vector3.Cross(vector3, Vector3.up) + rotationAxis).normalized);
			angularSpeed = (0f - vector3.magnitude) * 400f;
		}
		else
		{
			base.transform.position += vector;
		}
		base.transform.Rotate(rotationAxis, angularSpeed * Time.deltaTime);
	}

	protected virtual void Explode()
	{
		detonated = true;

		// V7-D1: the detonation replicates through S_EXPLOSION, which carries the authoritative
		// blast centre. A REMOTE actor's grenade therefore does not draw its own blast here --
		// the layer mask 4097 is level AND vehicles, and a grenade that bounced off a moving
		// truck is not deterministic across peers, so the predicted position is a guess. The
		// blast arrives at the right place from the server. The LOCAL player's own grenade DOES
		// draw immediately and its confirmation is swallowed, which is V10-D13's existing
		// prediction and the reason ExplosionSuppressor exists.
		bool drawsOwnBlast = !NetContext.IsClient
			|| Ironfront.Net.Unity.Client.NetClientPresenterGuard.IsLocalActor(source);

		if (drawsOwnBlast)
		{
			// V1 task 3. See ExplodingProjectile.Explode for why the two extra arguments exist.
			// ActorManager.Explode applies no damage on a client -- that guard is V1's, and it
			// is what makes this call safe to reach from the local prediction path.
			// debt-closure phase 2 task 2e. See ExplodingProjectile.Explode for why this asks
			// LibraryOwnsProjectileDamage rather than !EngineAppliesProjectileDamage -- the same
			// call carries the corpse impulse and the local player's predicted blast (V10 D13),
			// neither of which the cutover removes (ledger C-1).
			if (!Ironfront.Net.Unity.Server.NetProjectileAuthority.LibraryOwnsProjectileDamage
				&& ActorManager.Explode(
					base.transform.position, explosionConfiguration, source,
					Ironfront.Net.Protocol.ExplosionKind.Grenade)
				&& !source.aiControlled && NetContext.IsOffline)
			{
				// V7 task 3: the hitmarker is server-driven on a network, arriving as
				// S_HIT_CONFIRM to the thrower alone.
				IngameUi.Hit();
			}
		}

		base.transform.rotation = Quaternion.LookRotation(Vector3.up);
		base.enabled = false;
		Renderer[] array = renderers;
		foreach (Renderer renderer in array)
		{
			renderer.enabled = false;
		}

		if (!drawsOwnBlast || !ProjectileCleanupPolicy.PlaysCosmetics)
		{
			// Nothing of this grenade is going to be looked at: either the server is running it
			// or the authoritative blast is being drawn elsewhere. Go now rather than in ten
			// seconds -- V7 task 8.
			Invoke(nameof(Cleanup), 0f);
			return;
		}

		Ray ray = new Ray(base.transform.position, Vector3.down);
		RaycastHit hitInfo;
		if (Physics.Raycast(ray, out hitInfo, 1f, 1))
		{
			DecalManager.AddDecal(hitInfo.point, hitInfo.normal, Random.Range(1f, 2f), DecalManager.DecalType.Impact);
		}

		// V1 handed these to V0's headless list and V0 closed without absorbing them. A dedicated
		// server builds this prefab with neither component, and the blast above has already been
		// applied, so everything here is cosmetic.
		//
		// The pitch roll deliberately does NOT use UnityEngine.Random: that stream is shared with
		// gameplay, and advancing it for a sound means a headless server (which now skips the
		// roll) and a client (which does not) walk it at different rates. A cosmetic must not be
		// able to move a gameplay stream at all.
		ParticleSystem burst = GetComponent<ParticleSystem>();
		if (burst != null)
		{
			burst.Play(true);
		}
		AudioSource component = GetComponent<AudioSource>();
		if (component != null)
		{
			component.pitch = CosmeticRandom.Range(0.9f, 1.1f);
			component.Play();
		}
		Invoke(nameof(Cleanup), ProjectileCleanupPolicy.HoldSeconds(CLEANUP_TIME));
	}

	private void Cleanup()
	{
		Object.Destroy(base.gameObject);
	}
}
