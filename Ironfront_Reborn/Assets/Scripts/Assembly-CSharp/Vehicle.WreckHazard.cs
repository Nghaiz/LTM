using System.Collections.Generic;
using Ironfront.Net.Replication.Combat;
using Ironfront.Net.Unity;
using Ironfront.Net.Unity.Server;
using UnityEngine;

/// <summary>
/// What a vehicle does to the soldiers and vehicles around it when it goes up, and while it
/// burns: <see cref="WreckHazard"/>'s blast and fire, by distance.
/// </summary>
/// <remarks>
/// <para>
/// <b>Owner ruling 2026-10-06</b>: "explosions and fires must hurt what is around them, more the
/// closer it stands -- work out the real numbers and apply them". It replaces the 2026-09-27
/// ruling that a wreck's blast hurts nobody, which followed a flat 300 damage over 6 m that set
/// parked vehicles off one after another. A neighbouring vehicle therefore takes at most
/// <see cref="blastVulnerability"/> of its own health from one blast, so one wreck never
/// destroys a healthy vehicle beside it.
/// </para>
/// <para>
/// <b>Authoritative only.</b> Health is the server's (offline, the game's): a networked client
/// draws the blast and the flames from <see cref="Die"/> and the burn state, and learns the
/// damage from snapshots. Who the damage is credited to is whoever destroyed or set fire to
/// the vehicle (<see cref="LastDamagedBy"/>), so the killfeed names them.
/// </para>
/// </remarks>
public partial class Vehicle
{
	/// <summary>
	/// What goes up when this vehicle is destroyed -- fuel, ammunition -- in kg of TNT-equivalent.
	/// Authored per prefab: a quad bike 1, a jeep 3, a boat 2, a helicopter 10, a tank 25.
	/// </summary>
	[Header("Wreck hazard")]
	public float blastTntKg = 3f;

	/// <summary>
	/// The heat this vehicle's fire radiates while it burns, in megawatts: a car fire's peak heat
	/// release is a few megawatts, about a third of it radiated.
	/// </summary>
	public float fireRadiatedMegawatts = 1f;

	/// <summary>
	/// The share of this vehicle's own health a lethal blast beside it takes: 0.5 for a soft
	/// vehicle, near nothing for armour.
	/// </summary>
	public float blastVulnerability = 0.5f;

	/// <summary>Layers that shield a soldier from a blast or a fire: the world itself.</summary>
	private const int HazardShieldMask = 1;

	/// <summary>Seconds between applications of a fire's heat.</summary>
	private const float FireTickSeconds = 0.5f;

	private static readonly List<Actor> hazardVictims = new List<Actor>();

	private static readonly List<Vehicle> hazardVehicles = new List<Vehicle>();

	private static readonly System.Text.StringBuilder hazardReport = new System.Text.StringBuilder();

	/// <summary>Who destroyed this vehicle, read when it died: the blast is credited to them.</summary>
	private int destroyedBy = NoAttacker;

	private float nextFireTick;

	/// <summary>Where the blast and the fire come from: the hull's centre of mass.</summary>
	private Vector3 HazardCentre => rigidbody != null ? rigidbody.worldCenterOfMass : base.transform.position;

	/// <summary>
	/// The blast of this vehicle going up: every soldier and vehicle within
	/// <see cref="WreckHazard.BlastReach"/>, by its distance and whether a wall stands between.
	/// </summary>
	private void BlastSurroundings()
	{
		if (NetContext.IsClient || !(blastTntKg > 0f))
		{
			return;
		}
		Vector3 centre = HazardCentre;
		float reach = WreckHazard.BlastReach(blastTntKg);
		Actor attacker = ActorWithId(destroyedBy);
		ActorManager.ActorsInRange(centre, reach, hazardVictims);
		int hurt = 0;
		int killed = 0;
		hazardReport.Clear();
		for (int i = 0; i < hazardVictims.Count; i++)
		{
			Actor victim = hazardVictims[i];
			if (victim == null)
			{
				continue;
			}
			Vector3 at = victim.CenterPosition();
			Vector3 offset = at - centre;
			float distance = offset.magnitude;
			bool shielded = Shielded(centre, at);
			float damage = WreckHazard.BlastDamage(distance, blastTntKg, shielded);
			if (damage <= 0f)
			{
				continue;
			}
			Vector3 direction = distance > 0.01f ? offset / distance : Vector3.up;
			// The push that goes with it: enough to throw a soldier the blast badly hurts, whose
			// landing is then paid for as well (Actor.TrackRagdollFall).
			Vector3 push = (direction + Vector3.up * 0.5f).normalized * (damage * 6f);
			if (victim.dead)
			{
				victim.ApplyRigidbodyForce(push);
				continue;
			}
			using (DeathContext.Explosion())
			{
				victim.DamageAttributed(damage, damage * 3f, false, at, direction, push, attacker);
			}
			hurt++;
			if (victim.dead)
			{
				killed++;
			}
			if (hurt <= 8)
			{
				hazardReport.Append(hurt > 1 ? ", " : ": ").Append(victim.name).Append(' ')
					.Append(distance.ToString("0.0")).Append(" m").Append(shielded ? " behind cover" : string.Empty)
					.Append(' ').Append(damage.ToString("0")).Append(victim.dead ? " (killed)" : string.Empty);
			}
		}
		hazardVictims.Clear();

		int vehiclesHurt = 0;
		hazardVehicles.Clear();
		hazardVehicles.AddRange(ActorManager.instance.vehicles);
		for (int i = 0; i < hazardVehicles.Count; i++)
		{
			Vehicle other = hazardVehicles[i];
			if (other == null || other == this || other.dead)
			{
				continue;
			}
			Vector3 at = other.HazardCentre;
			float distance = Vector3.Distance(at, centre);
			if (distance > reach)
			{
				continue;
			}
			float share = WreckHazard.BlastDamage(distance, blastTntKg, Shielded(centre, at)) / WreckHazard.FullHealth;
			float amount = share * other.blastVulnerability * other.maxHealth;
			if (amount > 0f)
			{
				other.Damage(amount, destroyedBy);
				vehiclesHurt++;
			}
		}
		hazardVehicles.Clear();

		if (hurt > 0 || vehiclesHurt > 0)
		{
			Debug.Log($"[wreck] {base.gameObject.name} went up ({blastTntKg:0.#} kg TNT, lethal within {WreckHazard.LethalRadius(blastTntKg):0.0} m, "
				+ $"felt to {reach:0} m): {hurt} soldier(s) hurt, {killed} killed, {vehiclesHurt} vehicle(s) damaged{hazardReport}.");
		}
	}

	/// <summary>
	/// The heat of this vehicle's fire, every <see cref="FireTickSeconds"/> while it burns: every
	/// soldier within <see cref="WreckHazard.FireReach"/> with nothing in between, but not its own
	/// crew, who die with it if they stay aboard (<see cref="Die"/>).
	/// </summary>
	private void TickFire()
	{
		if (!burning || dead || NetContext.IsClient || !(fireRadiatedMegawatts > 0f) || Time.time < nextFireTick)
		{
			return;
		}
		nextFireTick = Time.time + FireTickSeconds;
		Vector3 centre = HazardCentre;
		Actor attacker = ActorWithId(LastDamagedBy);
		ActorManager.AliveActorsInRange(centre, WreckHazard.FireReach(fireRadiatedMegawatts), hazardVictims);
		for (int i = 0; i < hazardVictims.Count; i++)
		{
			Actor victim = hazardVictims[i];
			if (victim == null || victim.dead || (victim.IsSeated() && victim.seat.vehicle == this))
			{
				continue;
			}
			Vector3 at = victim.CenterPosition();
			if (Shielded(centre, at))
			{
				continue;
			}
			float damage = WreckHazard.FireDamagePerSecond(Vector3.Distance(centre, at), fireRadiatedMegawatts) * FireTickSeconds;
			if (damage <= 0f)
			{
				continue;
			}
			Vector3 away = (at - centre).normalized;
			using (DeathContext.Explosion())
			{
				victim.DamageAttributed(damage, 0f, false, at, away, Vector3.zero, attacker);
			}
		}
		hazardVictims.Clear();
	}

	/// <summary>Whether the world stands between the hazard and a soldier.</summary>
	private bool Shielded(Vector3 from, Vector3 to)
	{
		return Physics.Linecast(from, to, HazardShieldMask, QueryTriggerInteraction.Ignore);
	}

	/// <summary>The actor a network id names, or null: offline, or nobody in particular.</summary>
	private static Actor ActorWithId(int actorId)
	{
		if (actorId == NoAttacker || actorId < 0 || actorId > ushort.MaxValue)
		{
			return null;
		}
		if (!ServerActorRegistry.Instance.TryFind((ushort)actorId, out NetServerActor replicated) || replicated == null)
		{
			return null;
		}
		return replicated.GetComponent<Actor>();
	}
}
