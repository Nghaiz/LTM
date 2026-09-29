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
/// Main thread only, as all of the game's damage is. Server only in effect: the one reader is
/// <c>ServerCombatEvents.ReportDeath</c>, which returns off the server, and opening a scope
/// offline costs a few field writes.
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
		return scope;
	}

	/// <summary>A blade or a wrench swung by hand.</summary>
	public static Scope Melee(byte weaponId)
	{
		Scope scope = Scope.Capture();
		WeaponId = weaponId;
		Detail |= DeathDetail.Melee;
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
		return scope;
	}

	/// <summary>The crew of <paramref name="vehicle"/> dying with it.</summary>
	public static Scope WentDownWith(GameObject vehicle)
	{
		Scope scope = Scope.Capture();
		Cause = CauseOfDeath.Vehicle;
		Vehicle = vehicle;
		Detail |= DeathDetail.WentDownWithVehicle;
		return scope;
	}

	/// <summary>Restores the context a scope was opened over. Dispose it with <c>using</c>.</summary>
	public readonly struct Scope : IDisposable
	{
		private readonly CauseOfDeath _cause;
		private readonly byte _weaponId;
		private readonly GameObject _vehicle;
		private readonly DeathDetail _detail;

		private Scope(CauseOfDeath cause, byte weaponId, GameObject vehicle, DeathDetail detail)
		{
			_cause = cause;
			_weaponId = weaponId;
			_vehicle = vehicle;
			_detail = detail;
		}

		internal static Scope Capture() => new Scope(Cause, WeaponId, Vehicle, Detail);

		public void Dispose()
		{
			Cause = _cause;
			WeaponId = _weaponId;
			Vehicle = _vehicle;
			Detail = _detail;
		}
	}

	[RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
	private static void ResetOnLoad()
	{
		Cause = CauseOfDeath.Bullet;
		WeaponId = WeaponIds.NONE;
		Vehicle = null;
		Detail = DeathDetail.None;
	}
}
