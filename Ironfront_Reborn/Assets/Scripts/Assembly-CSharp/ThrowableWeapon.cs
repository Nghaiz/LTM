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
				Shoot(direction, useMuzzleDirection);
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
		if (NetContext.IsClient) return;

		ReleaseThrowable();
	}

	/// <summary>The gameplay half of a throw: the projectile leaves, the next one chambers.</summary>
	private void ReleaseThrowable()
	{
		Shoot(Vector3.zero, true);
		Reload();
	}

	public override bool CanBeAimed()
	{
		return base.CanBeAimed() && HasLoadedAmmo();
	}
}
