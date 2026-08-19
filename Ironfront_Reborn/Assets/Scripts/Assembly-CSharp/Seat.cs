using System;
using UnityEngine;

public class Seat : MonoBehaviour
{
	public enum SitAnimation
	{
		Chair = 0,
		Quad = 1
	}

	public enum Type
	{
		Driver = 0,
		Pilot = 1,
		Gunner = 2,
		Passenger = 3
	}

	public const int LAYER = 11;

	public Vehicle vehicle;

	public Type type = Type.Passenger;

	public SitAnimation animation;

	public bool enclosed;

	public Vector3 exitOffset = Vector3.zero;

	public MountedWeapon weapon;

	[NonSerialized]
	public Actor occupant;

	public GameObject hud;

	public float maxOccupantBalance = 200f;

	public bool IsOccupied()
	{
		return occupant != null;
	}

	public void SetOccupant(Actor actor)
	{
		occupant = actor;
		if (HasMountedWeapon())
		{
			weapon.user = occupant;
			// V6 task 3. THE registration trigger, and it has to be here rather than lazily from
			// CanFire(): on a dedicated server nothing drives a networked gunner's controller, so
			// CanFire is never called and the weapon would never announce itself -- leaving an
			// authority that exists, compiles and grades nothing. Idempotent, so a player getting
			// in and out does not re-arm a half-empty gun.
			weapon.DeclareToNet();
		}
		if (!occupant.aiControlled && hud != null)
		{
			hud.SetActive(true);
		}
		vehicle.OccupantEntered(this);
	}

	public void OccupantLeft()
	{
		Actor leaver = occupant;
		occupant = null;
		if (HasMountedWeapon())
		{
			weapon.StopFire();
			weapon.user = null;
		}
		if (hud != null)
		{
			hud.SetActive(false);
		}
		vehicle.OccupantLeft(this, leaver);
	}

	private void Update()
	{
		if (IsOccupied())
		{
			occupant.balance = Mathf.Min(occupant.balance, maxOccupantBalance);
		}
	}

	/// <summary>
	/// Whether this seat's occupant may use their OWN carried weapon while seated. V6-D7.
	/// </summary>
	/// <remarks>
	/// <para>
	/// <b>A Gunner returns false and that is correct, not a bug.</b> Read the nine call sites
	/// together and the predicate is "may use their own carried weapon while seated": true for a
	/// Passenger leaning out of a window, false for a Driver, a Pilot and a Gunner, all three of
	/// whom have their hands on something else. A Gunner still fires — through the separate
	/// <see cref="HasMountedWeapon"/> clause at <c>Actor.cs</c>'s fire gate — and
	/// <c>Actor.ControllingVehicle()</c> is literally DEFINED as the negation of this.
	/// </para>
	/// <para>
	/// <b>Renamed from <c>CanUseWeapon</c>, behaviour unchanged.</b> The old name was the trap:
	/// the next person to read <c>CanUseWeapon() == false</c> on a Gunner seat would "fix" it, and
	/// that fix silently re-arms a seated player's rifle and un-holsters it inside a tank. The
	/// rename is the whole change — one declaration, eight call sites, zero behavioural
	/// difference, and a test that pins both halves so it cannot drift into one.
	/// </para>
	/// </remarks>
	public bool CanUseCarriedWeapon()
	{
		return type == Type.Passenger;
	}

	public bool HasMountedWeapon()
	{
		return weapon != null;
	}
}
