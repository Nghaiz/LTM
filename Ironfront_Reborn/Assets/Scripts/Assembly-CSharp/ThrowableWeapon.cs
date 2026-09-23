using Ironfront.Net.Unity;
using UnityEngine;

public class ThrowableWeapon : Weapon
{
	/// <summary>
	/// The tick the pending throw releases on, or 0 when nothing is pending. V7-D7.
	/// </summary>
	/// <remarks>
	/// Scheduled from <c>configuration.releaseDelay</c>, which is authored PER WEAPON to match
	/// that weapon's own throw clip -- the event's clip time divided by the <c>Throw</c> state's
	/// speed multiplier. Both networked roles derive it from the same authored value, so the
	/// projectile leaves the hand on the same tick regardless of the thrower's framerate or
	/// animation state. Gate rule <b>A9</b> fails the build when the value and the clip diverge
	/// (ledger D-1); it was one shared <c>0.6f</c>, correct for neither clip, until phase 6.
	/// </remarks>
	private uint releaseTick;

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

			// The actor's aim, not the weapon model's muzzle. See throwDirection.
			// Tilted up by the pitch the original authored on the throw point -- see ThrowPitchDegrees.
			throwDirection = Quaternion.Euler(ThrowPitchDegrees, 0f, 0f) * direction;

			if (NetContext.IsServer)
			{
				// No Animator here, and none wanted. The release is a scheduled tick; Update
				// below performs it. Firing Shoot() now -- which is what a headless server did
				// before this change, because HasActiveAnimator() is false -- would throw
				// instantly while every client threw 0.6 s later.
				float tickDuration = 1f / Ironfront.Net.Protocol.ProtocolConstants.SIM_TICK_RATE;
				releaseTick = NetContext.CurrentTick
					+ (uint)Mathf.Ceil(configuration.releaseDelay / tickDuration);
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

	protected override void Update()
	{
		// base first: Weapon.Update drives the cooldown, the reload timer and the hold-fire
		// state this weapon's CanFire() reads. Declaring a new private Update here instead of
		// overriding would hide all of it and break the weapon silently.
		base.Update();

		if (releaseTick == 0 || NetContext.CurrentTick < releaseTick) return;

		releaseTick = 0;
		ReleaseThrowable();
	}

	/// <summary>
	/// Drops a scheduled release. V7-D7.
	/// </summary>
	/// <remarks>
	/// The release is a tick in a plain field, so <c>CancelInvoke()</c> — which is what
	/// <c>Weapon.Drop</c> and <c>Weapon.Holster</c> reach for — cannot see it. Without this a
	/// grenade ordered and then holstered inside the 0.6 s delay still leaves the hand, from a
	/// weapon the player has already put away.
	/// </remarks>
	protected override void CancelPendingActions()
	{
		base.CancelPendingActions();
		releaseTick = 0;
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
		if (NetContext.IsClient)
		{
			// The authoritative projectile is supplied by S_PROJECTILE_SPAWN, but the local
			// weapon still owns the immediately visible HUD prediction. Returning without
			// consuming a round left the grenade count unchanged forever.
			if (ammo != -1 && ammo > 0)
			{
				ammo--;
				AmmoChanged();
			}
			Reload();
			return;
		}

		ReleaseThrowable();
	}

	/// <summary>
	/// Where a throw leaves the hand, expressed in the thrower's own frame. V7-D7.
	/// </summary>
	/// <remarks>
	/// <para>
	/// <b>The default origin is the local view-model rig, and on a server that rig is inert.</b>
	/// <c>configuration.muzzle</c> is <c>ThrowPoint</c>, a child of the weapon root, and
	/// <c>Actor</c> parents that root to <c>controller.WeaponParent()</c>. For a human that parent
	/// is moved and pitched every frame by <c>PlayerFpParent</c> -- the rig that exists to put a
	/// weapon in front of the LOCAL player's eyes. A headless server runs no such rig, so the
	/// muzzle reported a pose from nowhere: the announced launch sat off the thrower's body and
	/// the grenade appeared to leave from behind them.
	/// </para>
	/// <para>
	/// <b>The offset is the original's own geometry, not a number invented here.</b> Reading the
	/// player prefab's chain: <c>FP Camera Parent</c> is at <c>(0, 0.63, 0)</c> -- eye height --
	/// and <c>Shoulder Parent</c> and <c>Weapon Parent</c> carry <c>(0.211, -0.206, 0.13)</c> and
	/// <c>(-0.211, 0.206, -0.13)</c>, which cancel exactly, so the weapon root sits at the eye.
	/// <c>ThrowPoint</c> is then <c>(0.367, 0.259, 0.056)</c> from that root. Summed and read in
	/// the thrower's frame: chest height, a hand's width to the right, just forward.
	/// </para>
	/// </remarks>
	protected override Vector3 ProjectileOrigin()
	{
		if (user == null) return base.ProjectileOrigin();

		return user.transform.position + user.transform.rotation * ThrowOriginOffset;
	}

	/// <summary>Eye height plus the throw point, in the thrower's frame. See ProjectileOrigin.</summary>
	private static readonly Vector3 ThrowOriginOffset = new Vector3(0.367f, 0.889f, 0.056f);

	/// <summary>
	/// How far above the aim a throw leaves, in degrees. V7-D7.
	/// </summary>
	/// <remarks>
	/// <b>The original's own number, read off the throw point.</b> <c>ThrowPoint</c> carries
	/// <c>m_LocalEulerAnglesHint: -15</c> in <c>frag.prefab</c> — a negative X euler pitches the
	/// forward vector UP — so in the original a grenade never left along the barrel, it left
	/// fifteen degrees above it. That tilt is why a throw arcs at all: without it, a level aim
	/// produces a level throw that skids into the ground.
	/// <para>
	/// The port kept the tilt for free while it took its direction from <c>muzzle.forward</c>,
	/// which includes the local rotation. Aiming the throw instead quietly dropped it, and the
	/// throw went flat. This puts the same authored angle back, in the frame the throw now uses.
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

	public override bool CanBeAimed()
	{
		return base.CanBeAimed() && HasLoadedAmmo();
	}
}
