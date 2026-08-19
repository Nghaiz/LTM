using Ironfront.Net.Protocol;
using Ironfront.Net.Replication.Movement;
using Ironfront.Net.Unity;
using Ironfront.Net.Unity.Server;
using UnityEngine;

/// <summary>
/// Keeps the server's replication record of an engine-simulated projectile in step with the
/// GameObject that is actually simulating it. Phase-V7 tasks 6 and 7.
/// </summary>
/// <remarks>
/// <para>
/// <b>The missing half of a re-announce.</b> V7-D6 sends a guided missile's current parameters
/// at 5 Hz and V7-D8 sends a tumbling deployable's at 10 Hz — but the numbers being sent live in
/// a Rigidbody or in <c>JavelinMissile</c>'s guidance, neither of which the replication library
/// can see. Without something pushing them, a deployable's recorded velocity stays at its throw
/// value forever, so it never counts as "at rest" and re-announces for its whole life; and a
/// missile is announced once at launch and never corrected.
/// </para>
/// <para>
/// <b>Server only, and it neither simulates nor decides anything.</b> It reads a transform and a
/// Rigidbody and hands the numbers over. On a client and offline it disables itself in
/// <c>Awake</c>.
/// </para>
/// </remarks>
[DisallowMultipleComponent]
public sealed class ProjectileNetSync : MonoBehaviour
{
	/// <summary>
	/// Ticks between guided-missile re-parameterizations. Read from the replication library
	/// rather than declared here, so the bandwidth test grades the rate this driver actually
	/// uses. V7-D6.
	/// </summary>
	public const int GuidedReAnnounceTicks =
		Ironfront.Net.Replication.Projectiles.ServerDeployableAuthority.GuidedReAnnounceTicks;

	private Projectile _projectile;
	private Rigidbody _body;
	private ProjectileKind _kind;
	private uint _lastAnnouncedTick;
	private Vector3 _lastPosition;

	private void Awake()
	{
		if (!NetContext.IsServer)
		{
			enabled = false;
			return;
		}

		_projectile = GetComponent<Projectile>();
		if (_projectile == null)
		{
			enabled = false;
			return;
		}

		_body = GetComponent<Rigidbody>();
		_kind = ProjectileNetAnnouncer.KindOf(_projectile);
		_lastPosition = transform.position;
		_lastAnnouncedTick = NetContext.CurrentTick;
	}

	private void LateUpdate()
	{
		if (_projectile == null || _projectile.netProjectileId == 0) return;

		ServerProjectileBridge bridge = ServerTickLoop.Current?.Projectiles;
		if (bridge == null) return;

		Vector3 position = transform.position;

		// A Rigidbody knows its own velocity; anything else is differenced from the last frame,
		// which is the only honest answer for a transform-driven projectile.
		Vector3 velocity = _body != null
			? _body.linearVelocity
			: (Time.deltaTime > 0f ? (position - _lastPosition) / Time.deltaTime : Vector3.zero);

		_lastPosition = position;

		if (_kind == ProjectileKind.AmmoBag || _kind == ProjectileKind.Medipack)
		{
			// The deployable authority decides FROM this whether the bag has settled and
			// whether a re-announce is owed; this call is only the measurement.
			bridge.Deployables.UpdatePose(
				_projectile.netProjectileId,
				new Vec3(position.x, position.y, position.z),
				new Vec3(velocity.x, velocity.y, velocity.z));
			return;
		}

		if (_kind != ProjectileKind.GuidedMissile) return;

		uint now = NetContext.CurrentTick;
		if (now - _lastAnnouncedTick < GuidedReAnnounceTicks) return;

		_lastAnnouncedTick = now;

		bridge.ReAnnounce(
			_projectile.netProjectileId, _kind, OwnerActorIdOf(_projectile),
			new Vec3(position.x, position.y, position.z),
			new Vec3(velocity.x, velocity.y, velocity.z),
			RemainingLifetimeSeconds());
	}

	private void OnDestroy()
	{
		if (_projectile == null || _projectile.netProjectileId == 0) return;

		// The id goes back whether this ended in a blast, an expiry or a scene teardown. Leaving
		// it out is a leak of exactly one id per projectile, which brainstorm criterion 13's
		// five-back-to-back-matches check is what would eventually find.
		ServerTickLoop.Current?.Projectiles?.ReleaseEngineSimulated(_projectile.netProjectileId);
		_projectile.netProjectileId = 0;
	}

	private float RemainingLifetimeSeconds()
		=> _projectile.configuration != null ? _projectile.configuration.lifetime : 0f;

	private static ushort OwnerActorIdOf(Projectile projectile)
	{
		if (projectile.source == null) return 0;

		return projectile.source.TryGetComponent(out NetServerActor actor) ? actor.ActorId : (ushort)0;
	}
}
