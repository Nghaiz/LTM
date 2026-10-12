using System;
using Ironfront.Net.Protocol;
using UnityEngine;

/// <summary>
/// How the damage being dealt right now is being dealt: the circumstances of a death, for the
/// killfeed. Playtest 2026-09-28, feature 2.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why a scope and not more parameters.</b> Every death in the game funnels through
/// <c>Actor.DamageAttributed</c>, and its callers -- a projectile, a blast, a wreck, a blade --
/// are the only code that knows what killed. Threading a weapon and a vehicle through
/// <c>Hitbox.ProjectileHit</c>, <c>Actor.Damage</c> and every override between them would touch
/// every damage path in the game to carry two bytes to one line. A caller opens a scope around
/// the damage it deals; the death branch reads it; the scope closes and restores what was there,
/// so a blast inside a projectile's hit keeps the projectile's weapon.
/// </para>
/// <para>
/// Main thread only, as all of the game's damage is. Two readers: <c>ServerCombatEvents.ReportDeath</c>
/// on a server, and the practice report in <c>Actor.DamageAttributed</c> offline, which feeds the
/// practice killfeed (owner's run of 2026-10-12). Opening a scope costs a few field writes.
/// </para>
/// </remarks>
public static class DeathContext
{
	/// <summary>What the damage is: a round unless a scope says otherwise.</summary>
	public static CauseOfDeath Cause { get; private set; } = CauseOfDeath.Bullet;

	/// <summary>The weapon dealing it (<see cref="WeaponIds"/>), <c>NONE</c> when unknown.</summary>
	public static byte WeaponId { get; private set; } = WeaponIds.NONE;

	/// <summary>The vehicle whose destruction is killing its crew, or null.</summary>
	public static GameObject Vehicle { get; private set; }

	/// <summary>Detail bits the killfeed shows.</summary>
	public static DeathDetail Detail { get; private set; }

	/// <summary>
	/// Where the blow landed: the head when a projectile struck a head hitbox, the body for
	/// everything else, a blast and a fall included.
	/// </summary>
	public static HitboxType HitboxHit { get; private set; } = HitboxType.Body;

	/// <summary>A projectile striking a hitbox (<c>Hitbox.ProjectileHit</c>): the head, or not.</summary>
	public static Scope Struck(bool head)
	{
		Scope scope = Scope.Capture();
		HitboxHit = head ? HitboxType.Head : HitboxType.Body;
		return scope;
	}

	/// <summary>A projectile's hit: <paramref name="weaponId"/> fired it.</summary>
	public static Scope Weapon(byte weaponId)
	{
		Scope scope = Scope.Capture();
		if (weaponId != WeaponIds.NONE)
		{
			WeaponId = weaponId;
		}
		return scope;
	}

	/// <summary>A blast. Keeps the weapon an outer scope named: the grenade, the rocket.</summary>
	public static Scope Explosion()
	{
		Scope scope = Scope.Capture();
		Cause = CauseOfDeath.Explosion;
		// A blast has no hitbox: the soldiers it catches are not headshots because the round that
		// set it off struck a head.
		HitboxHit = HitboxType.Body;
		return scope;
	}

	/// <summary>A blade or a wrench swung by hand.</summary>
	public static Scope Melee(byte weaponId)
	{
		Scope scope = Scope.Capture();
		WeaponId = weaponId;
		Detail |= DeathDetail.Melee;
		HitboxHit = HitboxType.Body;
		return scope;
	}

	/// <summary>
	/// <paramref name="vehicle"/> driving into somebody: the ram check. A ram is not a weapon, so
	/// an outer scope's weapon is cleared rather than inherited.
	/// </summary>
	public static Scope RunOver(GameObject vehicle)
	{
		Scope scope = Scope.Capture();
		Cause = CauseOfDeath.Vehicle;
		WeaponId = WeaponIds.NONE;
		Vehicle = vehicle;
		HitboxHit = HitboxType.Body;
		return scope;
	}

	/// <summary>A body landing too hard (<c>FallDamage</c>): nobody's weapon, nobody's vehicle.</summary>
	public static Scope Fall()
	{
		Scope scope = Scope.Capture();
		Cause = CauseOfDeath.Fall;
		WeaponId = WeaponIds.NONE;
		Vehicle = null;
		HitboxHit = HitboxType.Body;
		return scope;
	}

	/// <summary>The crew of <paramref name="vehicle"/> dying with it.</summary>
	public static Scope WentDownWith(GameObject vehicle)
	{
		Scope scope = Scope.Capture();
		Cause = CauseOfDeath.Vehicle;
		Vehicle = vehicle;
		Detail |= DeathDetail.WentDownWithVehicle;
		HitboxHit = HitboxType.Body;
		return scope;
	}

	/// <summary>Restores the context a scope was opened over. Dispose it with <c>using</c>.</summary>
	public readonly struct Scope : IDisposable
	{
		private readonly CauseOfDeath _cause;
		private readonly byte _weaponId;
		private readonly GameObject _vehicle;
		private readonly DeathDetail _detail;
		private readonly HitboxType _hitbox;

		private Scope(CauseOfDeath cause, byte weaponId, GameObject vehicle, DeathDetail detail, HitboxType hitbox)
		{
			_cause = cause;
			_weaponId = weaponId;
			_vehicle = vehicle;
			_detail = detail;
			_hitbox = hitbox;
		}

		internal static Scope Capture() => new Scope(Cause, WeaponId, Vehicle, Detail, HitboxHit);

		public void Dispose()
		{
			Cause = _cause;
			WeaponId = _weaponId;
			Vehicle = _vehicle;
			Detail = _detail;
			HitboxHit = _hitbox;
		}
	}

	[RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
	private static void ResetOnLoad()
	{
		Cause = CauseOfDeath.Bullet;
		WeaponId = WeaponIds.NONE;
		Vehicle = null;
		Detail = DeathDetail.None;
		HitboxHit = HitboxType.Body;
	}
}
