using Ironfront.Net.Unity;
using UnityEngine;

public class CarHorn : MountedWeapon
{
	/// <summary>
	/// Sounds the horn: reveals the occupant to AI, and makes a noise. V6 task 5.
	/// </summary>
	/// <remarks>
	/// <para>
	/// <b>Three lines, and each belongs to a different machine.</b> <c>user.Highlight()</c> is
	/// GAMEPLAY — it is what makes AI notice the vehicle (<c>Actor.cs</c>'s highlight action) —
	/// so it happens once, on the authority. <c>audio.Play()</c> is cosmetic and is skipped on a
	/// dedicated server, where <c>Weapon.Awake</c>'s <c>GetComponent&lt;AudioSource&gt;()</c>
	/// returns null on a stripped prefab.
	/// </para>
	/// <para>
	/// <b><c>lastFired</c> is neither, and is never replicated.</b> <c>Time.time</c> is seconds
	/// since THIS PROCESS started, so two peers that launched a minute apart hold values a minute
	/// apart for the same event — the field is meaningless off-machine. Its only legitimate use is
	/// the local <c>CoolingDown()</c> comparison, which is a difference against the same clock.
	/// The authoritative cooldown lives in the server's <c>WeaponRuntimeState.LastFiredTime</c> on
	/// the server clock, which is why the gate is in <c>MountedWeaponAuthority</c> rather than
	/// reading this field. The same reasoning holds for every <c>lastFired</c> in the hierarchy.
	/// </para>
	/// <para>
	/// <b>It spends no ammo and spawns no projectile</b> — this override skips <c>ammo--</c> and
	/// <c>AmmoChanged()</c> entirely, which is why <see cref="SpendsAmmoPerShot"/> says so and the
	/// server's clip of 1 stays a permanent 1.
	/// </para>
	/// </remarks>
	protected override void Shoot(Vector3 direction, bool useMuzzleDirection)
	{
		if (configuration.loud && NetWeaponAuthority.GameplayHalfRunsHere)
		{
			user.Highlight();
		}
		if (audio != null && NetWeaponAuthority.CosmeticHalfRunsHere)
		{
			audio.Play();
		}
		lastFired = Time.time;
	}

	/// <inheritdoc />
	protected override bool SpendsAmmoPerShot()
	{
		return false;
	}
}
