using Ironfront.Net.Replication.Combat;
using Ironfront.Net.Replication.Movement;
using Ironfront.Net.Unity;
using UnityEngine;

public class ThrowableWeapon : Weapon
{
	/// <summary>
	/// The aim the pending throw was ordered along, captured in <see cref="Fire"/>. V7-D7.
	/// </summary>
	/// <remarks>
	/// <b>A throw leaves the hand, not a barrel.</b> <c>configuration.muzzle</c> is the throw
	/// clip's release point, so its forward follows the throwing animation — and a headless
	/// server builds this prefab with no Animator at all, leaving that point at its bind pose.
	/// Neither is the direction the player aimed. Announcing the bind pose made every client
	/// draw the grenade arcing steeply upward and detonating in mid-air, with only its shadow
	/// still in view. The actor's own aim is the right direction, and <see cref="Fire"/> is the
	/// one moment it is handed to us: the release happens a <c>releaseDelay</c> later.
	/// </remarks>
	private Vector3 throwDirection;

	public override void Unholster()
	{
		base.Unholster();
		if (ammo == 0)
		{
			ReloadDone();
		}
	}

	public override void Fire(Vector3 direction, bool useMuzzleDirection)
	{
		if (CanFire())
		{
			lastFired = Time.time;

			// The actor's aim, not the weapon model's muzzle; tilted up by the pitch the original
			// authored on the throw point, ABOUT THE THROWER'S RIGHT AXIS so the tilt survives a
			// change of facing. See ThrowPitchDegrees for what world X cost.
			throwDirection = Quaternion.AngleAxis(ThrowPitchDegrees, ThrowerRight(direction))
				* direction;

			if (NetContext.IsServer)
			{
				// The replication authority owns the pending state and release tick. The engine
				// is invoked only through ReleaseApprovedByServer when that transaction matures.
			}
			else if (animator != null)
			{
				animator.SetTrigger("throw");
			}
			else
			{
				Shoot(throwDirection, false);
			}
		}
		holdingFire = true;
	}

	/// <summary>
	/// Called by the throw clip's animation event. V7-D7 made it cosmetic on a networked client.
	/// </summary>
	/// <remarks>
	/// <para>
	/// <b>On a client this spawns nothing.</b> The projectile arrives on
	/// <c>S_PROJECTILE_SPAWN</c>, whose <c>SpawnTick</c> puts it at the right moment regardless
	/// of when this animation happened to reach its event. Letting the event spawn as well would
	/// give the thrower two grenades -- one predicted, one authoritative -- and make a client
	/// the author of the release moment.
	/// </para>
	/// <para>
	/// The Animator still plays, and this method is still wired to it, because the ARM still has
	/// to move. Confirming each throwable prefab's Animator still fires this is Editor work the
	/// client track owns; the method deliberately remains public and non-empty so that a
	/// missing event shows up as a broken offline throw rather than as silence.
	/// </para>
	/// </remarks>
	public void SpawnThrowable()
	{
		// In a network match this callback is presentation only. Snapshot reconciliation is
		// the sole writer of loaded and reserve counts, and the server's explicit release is
		// the sole projectile creator. Offline keeps Ravenfield's original animation event.
		if (!NetContext.IsOffline) return;

		ReleaseThrowable();
	}

	/// <summary>Where a throw leaves the hand, expressed in the thrower's own aim frame.</summary>
	/// <remarks>
	/// <para>
	/// <b>The lateral numbers are the original's, read off <c>ThrowPoint</c> in
	/// <c>frag.prefab</c>: right <c>0.367</c>, forward <c>0.056</c>, both taken unchanged.</b> A
	/// hand is a third of a metre to the right of the camera and just in front of it, which is
	/// where the first-person model holds the grenade.
	/// </para>
	/// <para>
	/// <b>The vertical part is deliberately ZERO, and that is a decision rather than an
	/// omission.</b> <c>ThrowPoint</c> sits 0.259 m <i>above</i> the eye -- the top of an
	/// overhand throw -- and in the original that is invisible, because the client draws the
	/// view-model hand at exactly that point and the grenade appears in it. The port cannot do
	/// that: the server owns the spawn point and never sees the animation, so the number is read
	/// in world space and the grenade visibly leaves <b>above the player's head</b> (measured
	/// 2026-09-25, lane-B: spawn 1.85 m above the feet with the crown at 1.80 m). Eye level is
	/// where the view-model hand rests when the player is looking level, so that is what this
	/// places the release at. Raising it again is this one number.
	/// </para>
	/// </remarks>
	private static readonly Vector3 ThrowHandOffset = new Vector3(0.367f, 0f, 0.056f);

	/// <summary>
	/// Where this throw leaves the hand. V7-D7.
	/// </summary>
	/// <remarks>
	/// <para>
	/// <b>Anchored on the authority's own eye and expressed in the thrower's aim frame.</b> Both
	/// halves were wrong before this. The anchor was <c>user.transform</c>, which is the capsule
	/// CENTRE on the body the server actually throws from (<c>Actor</c> sits on the root of the AI
	/// prefab, next to the <c>CharacterController</c>) but the FEET on the client's player prefab
	/// -- so one expression meant two points 0.9 m apart, and the server's answer was a grenade
	/// leaving 1.85 m above the thrower's feet with the crown of his head at 1.80 m. The frame
	/// was the body's transform rotation, and nothing writes that on a server: it stays identity,
	/// so the offset's right and forward parts were applied along the WORLD axes. Facing +Z got it
	/// right by luck; facing -Z put the grenade seven tenths of a metre behind the hand; facing
	/// ±X put it a third of a metre off to the side.
	/// </para>
	/// <para>
	/// <b>Off the authority path this falls through to the muzzle, and that is the original
	/// behaviour rather than a leftover.</b> Offline there is no server to ask, the muzzle <i>is</i>
	/// the animated release point, and the local client is the one drawing the hand it belongs to.
	/// </para>
	/// </remarks>
	protected override Vector3 ProjectileOrigin(Vector3 direction)
	{
		// Human network throws receive the deterministic session eye from ServerCombatBridge.
		// Read that value before consulting the body component so this launch uses exactly the
		// origin the inventory transaction and projectile announcement were approved against.
		if (TryGetNetworkShotOrigin(out Vector3 suppliedEye))
			return suppliedEye + ThrowerFrame(direction) * ThrowHandOffset;

		if (!AuthoritativeEye(out Vector3 eye)) return base.ProjectileOrigin(direction);

		return eye + ThrowerFrame(direction) * ThrowHandOffset;
	}

	/// <summary>
	/// The thrower's authoritative eye, as of right now. False off the server, or on a body with
	/// no movement authority behind it.
	/// </summary>
	/// <remarks>
	/// <para>
	/// <b>Asked at the RELEASE, not at the trigger.</b> A throw leaves the hand a
	/// <c>releaseDelay</c> after the trigger -- 0.95 s on a frag -- and an origin captured at the
	/// trigger would leave the grenade where the player used to be; a walking player would watch
	/// it fall out of the air behind them. It is the same argument <c>ThrowableWeapon.Fire</c>
	/// makes for scheduling the release rather than shooting on the spot.
	/// </para>
	/// <para>
	/// <b>The authority's own formula, called rather than copied.</b>
	/// <c>ServerCombatAuthority.ShotOrigin</c> is where "the session position is the capsule
	/// centre, so the feet are half a capsule below it and the eye is EyeHeight above that" is
	/// written down, and the hitboxes are placed by the same numbers. A second transcription of
	/// it here would be free to drift the moment either constant moved.
	/// </para>
	/// <para>
	/// <b>Server only, and the client's copy of this component is not a substitute.</b> The local
	/// player's body carries a <c>NetMovementAgent</c> too, but on a client its state is the
	/// PREDICTION; and it does not matter anyway, because a client spawns no throwable at all --
	/// <c>Fire</c> takes the animator branch there and <c>SpawnThrowable</c> is presentation-only
	/// during a network match.
	/// </para>
	/// <para>
	/// No frame is available at a release, so the prone bit reads false and a prone thrower is
	/// placed at standing eye height. The movement simulation does not model prone either; the
	/// bit is a client-reported flag with no body behind it, and the standing eye is the honest
	/// approximation rather than a second thing to keep in step.
	/// </para>
	/// </remarks>
	private bool AuthoritativeEye(out Vector3 eye)
	{
		eye = default;

		if (!NetContext.IsServer || user == null) return false;

		NetMovementAgent agent = user.GetComponent<NetMovementAgent>();
		if (agent == null) return false;

		Vec3 origin = ServerCombatAuthority.ShotOrigin(in agent.State, default);

		eye = new Vector3(origin.X, origin.Y, origin.Z);
		return true;
	}

	/// <summary>
	/// The thrower's own basis for a throw along <paramref name="direction"/>: local +X is the
	/// thrower's right, +Y is up, +Z is the aim.
	/// </summary>
	/// <remarks>
	/// <para>
	/// <b>Built from the aim rather than read off the body, because the body does not turn on a
	/// server.</b> <c>Actor.Update</c> returns before <c>UpdateFacing</c> for a body whose AI
	/// controller is suspended, which is every networked player, so the transform's rotation is
	/// identity for the whole match and anything multiplied by it is applied along the world axes.
	/// </para>
	/// <para>
	/// The right axis comes out of <see cref="ThrowerRight"/> rather than out of the rotation
	/// itself, so the hand offset and the throw's pitch cannot disagree about which way the player
	/// is facing -- and so the degenerate-aim guard lives in one place.
	/// </para>
	/// </remarks>
	private static Quaternion ThrowerFrame(Vector3 direction)
	{
		Vector3 forward = direction.normalized;
		Vector3 right = ThrowerRight(direction);

		return Quaternion.LookRotation(forward, Vector3.Cross(forward, right));
	}

	/// <summary>
	/// The thrower's right axis for a throw along <paramref name="direction"/>, in the horizontal
	/// plane. See <see cref="ThrowerFrame"/>.
	/// </summary>
	/// <remarks>
	/// Yaw only, deliberately: the pitch of the aim must not roll this axis, or the fifteen-degree
	/// throw tilt below would stop meaning "up from the aim" the moment a player looked above the
	/// horizon. Looking straight up or down flattens to nothing and the cross product with it is
	/// undefined -- world right is the honest answer there, since the yaw is not observable and a
	/// collapsed axis would put the grenade inside the player's face.
	/// </remarks>
	private static Vector3 ThrowerRight(Vector3 direction)
	{
		Vector3 flat = new Vector3(direction.x, 0f, direction.z);

		return flat.sqrMagnitude > 1e-6f
			? Vector3.Cross(Vector3.up, flat.normalized)
			: Vector3.right;
	}

	/// <summary>
	/// How far above the aim a throw leaves, in degrees. V7-D7.
	/// </summary>
	/// <remarks>
	/// <para>
	/// <b>The original's own number, read off the throw point.</b> <c>ThrowPoint</c> carries
	/// <c>m_LocalEulerAnglesHint: -15</c> in <c>frag.prefab</c> — a negative X euler pitches the
	/// forward vector UP — so in the original a grenade never left along the barrel, it left
	/// fifteen degrees above it. That tilt is why a throw arcs at all: without it, a level aim
	/// produces a level throw that skids into the ground.
	/// </para>
	/// <para>
	/// <b>It has to be applied about the thrower's RIGHT axis, not the world's X.</b> The original
	/// kept the tilt in the view-model's local rotation, so it turned with the player. Applied
	/// about world X it is correct only while facing ±Z: facing ±X it becomes a roll about the
	/// throw's own axis and the throw goes out FLAT, and facing -Z it tilts fifteen degrees
	/// <i>down</i>, into the ground. Measured 2026-09-25 against the reported "flies forward
	/// instead of being thrown".
	/// </para>
	/// </remarks>
	private const float ThrowPitchDegrees = -15f;

	/// <summary>The gameplay half of a throw: the projectile leaves along the ordered aim, the next one
	/// chambers.</summary>
	/// <remarks>
	/// <c>useMuzzleDirection</c> is false on purpose. The muzzle here is the throw clip's
	/// release point, not a barrel: on a weapon carried in the hand its forward is the
	/// animation's, and the server has no animation at all. <see cref="throwDirection"/> is the
	/// aim the thrower actually ordered.
	/// </remarks>
	private void ReleaseThrowable()
	{
		Shoot(throwDirection, false);
		Reload();
	}

	/// <summary>
	/// Creates exactly one throwable for a release already committed by the server authority.
	/// It deliberately bypasses Shoot, Reload and CanFire: those would spend or refill a second
	/// inventory and would introduce a second opinion about the release tick.
	/// </summary>
	public bool ReleaseApprovedByServer(Vector3 direction)
	{
		if (!NetContext.IsServer || user == null || configuration.projectilePrefab == null
			|| configuration.projectilePrefab.GetComponent<Projectile>() == null)
		{
			ClearNetworkShotOrigin();
			return false;
		}

		throwDirection = Quaternion.AngleAxis(ThrowPitchDegrees, ThrowerRight(direction))
			* direction;

		try
		{
			return SpawnProjectile(throwDirection) != null;
		}
		finally
		{
			ClearNetworkShotOrigin();
		}
	}

	public override bool CanBeAimed()
	{
		return base.CanBeAimed() && HasLoadedAmmo();
	}
}
