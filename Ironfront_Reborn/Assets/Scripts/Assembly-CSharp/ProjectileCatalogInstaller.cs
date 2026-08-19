using Ironfront.Net.Replication.Movement;
using Ironfront.Net.Replication.Projectiles;
using Ironfront.Net.Unity;
using Ironfront.Net.Unity.Server;
using UnityEngine;

/// <summary>
/// Hands the running server the authored projectile prefabs, sampled into the engine-free
/// catalog it simulates from. Phase-V7 task 1.
/// </summary>
/// <remarks>
/// <para>
/// <b>This exists because of an assembly boundary, not because the server wanted a
/// MonoBehaviour.</b> <c>ServerTickLoop</c> lives in the <c>Ironfront.Net.Unity.Server</c>
/// asmdef, which compiles before <c>Assembly-CSharp</c> and can never reference it — so the one
/// assembly able to read a <c>Projectile.Configuration</c> off a prefab is the one the server
/// cannot call into. The server therefore exposes
/// <c>ServerTickLoop.InstallProjectileCatalog</c> and this component calls it, the same shape
/// the <c>Net/Server/Bindings/</c> interfaces use.
/// </para>
/// <para>
/// <b>Client-track item: the prefab array must be authored.</b> Until it is, the server steps
/// no projectiles and announces no launches — degraded, not broken, and visible as
/// <c>ServerProjectileBridge.LiveCount</c> staying at zero through a match with rockets in it.
/// </para>
/// </remarks>
[DefaultExecutionOrder(-900)]
[DisallowMultipleComponent]
public sealed class ProjectileCatalogInstaller : MonoBehaviour
{
	[Tooltip("Indexed by (byte)ProjectileKind: Shell=0, Rocket=1, GuidedMissile=2, Grenade=3, "
	         + "AmmoBag=4, Medipack=5, Bullet=6. Each entry is the projectile prefab whose "
	         + "Projectile.Configuration the server simulates from. An empty slot means that "
	         + "kind is not replicated.")]
	[SerializeField] private GameObject[] _prefabsByKind;

	private void Awake()
	{
		// Server only. A client builds its own catalog inside NetClientProjectilePresenter from
		// the same authored array, and offline needs neither.
		if (!NetContext.IsServer)
		{
			enabled = false;
			return;
		}

		ServerTickLoop loop = ServerTickLoop.Current;
		if (loop == null)
		{
			// Louder than a silent return: a server with no loop at this point is a bootstrap
			// ordering problem, and the symptom otherwise is projectiles quietly not existing.
			Debug.LogError(
				"[v7] ProjectileCatalogInstaller ran before the server tick loop was bound; "
				+ "no projectiles will replicate this session.");
			enabled = false;
			return;
		}

		loop.InstallProjectileCatalog(
			ProjectileCatalogBuilder.FromPrefabs(_prefabsByKind), new UnityProjectileWorldSweep());
	}
}

/// <summary>
/// The level-geometry half of a projectile's swept test. Phase-V7 task 2.
/// </summary>
/// <remarks>
/// <para>
/// Actor hitboxes are boxes the replication library owns and CI can grade; level geometry is
/// arbitrary collision mesh that exists only inside Unity. This is that seam, and it is
/// deliberately the whole of it.
/// </para>
/// <para>
/// <b>The segment comes from the caller and is used exactly as given</b> — V7-D5-local. The
/// original swept <c>delta.magnitude * 2f</c> and then advanced by <c>delta</c>, which made hit
/// registration a function of frame time; choosing a length here would put that decision back in
/// an engine call no test can read.
/// </para>
/// </remarks>
public sealed class UnityProjectileWorldSweep : IProjectileWorldSweep
{
	/// <summary>Everything but the ragdoll layer, matching <c>Projectile.HIT_MASK</c>.</summary>
	private const int HitMask = -2049;

	public bool Sweep(in Vec3 from, in Vec3 to, out Vec3 hitPoint)
	{
		var origin = new Vector3(from.X, from.Y, from.Z);
		var target = new Vector3(to.X, to.Y, to.Z);
		Vector3 delta = target - origin;

		float distance = delta.magnitude;
		if (distance <= 0f)
		{
			hitPoint = default;
			return false;
		}

		if (!Physics.Raycast(origin, delta / distance, out RaycastHit hit, distance, HitMask))
		{
			hitPoint = default;
			return false;
		}

		// Hitbox layers are the replication library's to resolve -- it already tested every
		// actor's boxes against this same segment, with lag compensation and self-exclusion that
		// a raw raycast has no way to apply. Reporting one here would end the flight early and
		// silently bypass both.
		if (Hitbox.IsHitboxLayer(hit.collider.gameObject.layer))
		{
			hitPoint = default;
			return false;
		}

		hitPoint = new Vec3(hit.point.x, hit.point.y, hit.point.z);
		return true;
	}
}
