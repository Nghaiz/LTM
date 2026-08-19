using Ironfront.Net.Unity;
using UnityEngine;

public class MountedWeapon : Weapon
{
	private int spareAmmo;

	/// <summary>
	/// The replicated id of the vehicle this weapon is bolted to, or 0. V6 task 2.
	/// </summary>
	/// <remarks>
	/// Cached because resolving it walks <c>user.seat.vehicle</c> and then a dictionary, and the
	/// aim path runs on every fixed step. Invalidated by <see cref="ResolveNetSeat"/> the moment
	/// the occupant changes, which is the only thing that can move a mounted weapon between seats.
	/// </remarks>
	protected ushort netVehicleId;

	/// <summary>This weapon's seat index on that vehicle. Meaningless while <see cref="netVehicleId"/> is 0.</summary>
	protected byte netSeatIndex;

	/// <summary>True when the local player is the one sitting here. Always false on a server.</summary>
	protected bool netLocallyOccupied;

	private Actor resolvedFor;

	protected override void Awake()
	{
		base.Awake();
		spareAmmo = configuration.spareAmmo;
	}

	public override void Fire(Vector3 direction, bool useMuzzleDirection)
	{
		base.Fire(direction, true);
	}

	public override void Show()
	{
	}

	public override void Hide()
	{
	}

	protected override int RemoveSpareAmmo(int count)
	{
		if (HasInfiniteSpareAmmo())
		{
			return count;
		}
		int num = Mathf.Max(0, spareAmmo - count);
		int result = spareAmmo - num;
		spareAmmo = num;
		return result;
	}

	public override int GetSpareAmmo()
	{
		return spareAmmo;
	}

	public override void Holster()
	{
		unholstered = false;
		reloading = false;
		CancelInvoke();
	}

	public override void Unholster()
	{
		base.Unholster();
		if (!HasLoadedAmmo() && configuration.forceAutoReload)
		{
			Reload(true);
		}
	}

	/// <summary>
	/// Refreshes <see cref="netVehicleId"/> / <see cref="netSeatIndex"/> and announces this
	/// weapon to whichever side is authoritative. V6 tasks 2 and 3.
	/// </summary>
	/// <remarks>
	/// <para>
	/// <b>Resolved from the occupant, not from prefab authoring.</b> A mounted weapon reaches its
	/// vehicle through <c>user.seat.vehicle</c>, so an unoccupied one has no id and needs none —
	/// nobody is aiming it and nothing is firing it. That is also why this costs nothing offline:
	/// <c>NetTurretAim.VehicleIdOf</c> answers 0 with no resolver installed and every branch
	/// below falls through to the shipped path.
	/// </para>
	/// <para>
	/// <b>The declaration is idempotent and cheap, so it is repeated rather than sequenced.</b>
	/// Announcing once from a lifecycle hook would mean keeping that hook in step with seat
	/// entry, vehicle spawn and the id pool — three things with three different orders — and the
	/// failure mode of getting it wrong is a turret the server does not know about, which reads
	/// as "the gun does not work" with nothing in any log.
	/// </para>
	/// </remarks>
	protected void ResolveNetSeat()
	{
		if (user == null || user.seat == null)
		{
			netVehicleId = 0;
			netSeatIndex = 0;
			netLocallyOccupied = false;
			resolvedFor = null;
			return;
		}

		if (!ReferenceEquals(resolvedFor, user))
		{
			resolvedFor = user;
			Vehicle vehicle = user.seat.vehicle;
			int seatIndex = vehicle != null ? vehicle.SeatIndexOf(user.seat) : -1;

			netVehicleId = seatIndex >= 0
				? NetTurretAim.VehicleIdOf(vehicle.gameObject)
				: (ushort)0;
			netSeatIndex = seatIndex >= 0 ? (byte)seatIndex : (byte)0;
			netLocallyOccupied = Ironfront.Net.Unity.Client.NetClientPresenterGuard.IsLocalActor(user);
		}

		if (netVehicleId == 0)
		{
			return;
		}

		NetWeaponAuthority.Declare(netVehicleId, netSeatIndex, BuildNetDeclaration());
	}

	/// <summary>
	/// This weapon's numbers, as its own prefab authored them.
	/// </summary>
	/// <remarks>
	/// <c>configuration.ammo</c> is an <c>int</c> that carries <c>-1</c> for "no magazine"; the
	/// server's clip is a <c>byte</c>, so the sentinel becomes 0 — which
	/// <c>MountedWeaponAuthority.CheckCanFire</c> reads as "this weapon cannot run out" rather
	/// than as "this weapon is empty". Clamping the other way would jam every unlimited weapon.
	/// </remarks>
	protected virtual MountedWeaponDeclaration BuildNetDeclaration()
	{
		int ammo = configuration.ammo;
		byte clipSize = ammo > 0 ? (byte)Mathf.Min(ammo, 255) : (byte)0;

		int spare = configuration.spareAmmo;
		short spareOnWire = spare < short.MinValue || spare > short.MaxValue
			? (short)0
			: (short)spare;

		return new MountedWeaponDeclaration(
			NetworkId, clipSize, spareOnWire, configuration.cooldown, SpendsAmmoPerShot());
	}

	/// <summary>
	/// False for a weapon whose <c>Shoot</c> override never reaches <c>ammo--</c>. V6 task 5.
	/// </summary>
	/// <remarks>
	/// Virtual rather than a check on the type, so the next weapon like <c>CarHorn</c> declares
	/// the fact about itself instead of being special-cased inside the authority.
	/// </remarks>
	protected virtual bool SpendsAmmoPerShot()
	{
		return true;
	}

	/// <summary>
	/// Announces this weapon to the replication layer. Called from <c>Seat.SetOccupant</c>.
	/// </summary>
	/// <remarks>
	/// The public face of <see cref="ResolveNetSeat"/>, so the one place that knows an occupant
	/// just arrived is the one place that triggers registration — rather than every path that
	/// might, someday, have happened to call <c>CanFire</c> first.
	/// </remarks>
	public void DeclareToNet()
	{
		ResolveNetSeat();
	}

	/// <summary>
	/// The server's verdict on whether this weapon may fire, or true when nobody is asking.
	/// </summary>
	public override bool CanFire()
	{
		ResolveNetSeat();

		if (!NetWeaponAuthority.MayFire(
				netVehicleId, netSeatIndex, netLocallyOccupied,
				user != null && !user.dead))
		{
			return false;
		}

		return base.CanFire();
	}
}
