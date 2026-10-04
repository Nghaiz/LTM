using System.Collections.Generic;
using Ironfront.Net.Unity;
using UnityEngine;

/// <summary>
/// How bots use a vehicle's horn (owner request 2026-10-04): a bot at the wheel honks to call its
/// crew aboard, and friendly bots on foot nearby answer a call by getting in.
/// </summary>
/// <remarks>
/// <para>
/// <b>The warning honk is the original game's</b> and needs nothing here: a bot driving a car
/// "fires" while something blocks it (<c>AiActorController.AiVehicle</c>: <c>fire =
/// blockerAhead</c>), and a car's driver seat holds the horn. The server used to sound it and tell
/// nobody; now every client near it hears it (<see cref="CarHorn"/>).
/// </para>
/// <para>
/// <b>The call.</b> A crew sent back for a vehicle waits up to <c>Squad.CrewMusterSeconds</c> with
/// seats still empty. Its driver honks every <see cref="CallEverySeconds"/> while it waits.
/// </para>
/// <para>
/// <b>The answer</b>, to any horn on its own side, a player's included, while seats are free:
/// friendly bots on foot within <see cref="AnswerRadius"/> and not fighting join a crew already
/// boarding that vehicle (<c>Squad.JoinCrew</c>); for a player at the wheel, the nearest idle squad
/// small enough to fit climbs in (<c>Squad.EnterVehicle</c>), so a player can honk for a lift.
/// Offline and on the server only, where bots think.
/// </para>
/// </remarks>
public static class BotHorn
{
	/// <summary>Metres a call to come aboard carries for a bot.</summary>
	public const float AnswerRadius = 35f;

	/// <summary>Seconds between a mustering driver's calls.</summary>
	public const float CallEverySeconds = 4f;

	/// <summary>Seconds a vehicle's call is not answered again by a new squad.</summary>
	public const float SquadAnswerCooldown = 12f;

	private static readonly Dictionary<Vehicle, float> lastCall = new Dictionary<Vehicle, float>();

	private static readonly Dictionary<Vehicle, float> lastSquadAnswer = new Dictionary<Vehicle, float>();

	/// <summary>A mustering crew's driver honks for the rest of its crew, now and then.</summary>
	public static void CallCrew(Vehicle vehicle)
	{
		if (vehicle == null || vehicle.dead || NetContext.IsClient)
		{
			return;
		}
		CarHorn horn = HornOf(vehicle);
		if (horn == null || horn.Driver == null || !horn.Driver.aiControlled)
		{
			return;
		}
		if (lastCall.TryGetValue(vehicle, out float last) && Time.time - last < CallEverySeconds)
		{
			return;
		}
		if (horn.Honk())
		{
			lastCall[vehicle] = Time.time;
			Debug.Log("[horn] " + horn.Driver.name + " honks for the rest of its crew.");
		}
	}

	/// <summary>The horn fitted to a vehicle's driver seat, or null.</summary>
	public static CarHorn HornOf(Vehicle vehicle)
	{
		if (vehicle == null || vehicle.seats == null)
		{
			return null;
		}
		foreach (Seat seat in vehicle.seats)
		{
			if (seat != null && seat.weapon is CarHorn horn)
			{
				return horn;
			}
		}
		return null;
	}

	private static void OnHonk(CarHorn horn)
	{
		if (NetContext.IsClient || horn == null)
		{
			return;
		}
		Vehicle vehicle = horn.Vehicle;
		Actor driver = horn.Driver;
		if (vehicle == null || vehicle.dead || driver == null || !vehicle.HasUnclaimedSeats())
		{
			return;
		}
		Vector3 at = vehicle.transform.position;
		Squad crew = Squad.BoardingCrewFor(vehicle);
		List<Actor> friends = ActorManager.AliveActorsOnTeam(driver.team);
		Squad nearestIdle = null;
		float nearestIdleSqr = AnswerRadius * AnswerRadius;
		for (int i = 0; i < friends.Count; i++)
		{
			Actor friend = friends[i];
			AiActorController ai = friend != null ? friend.controller as AiActorController : null;
			if (ai == null || friend == driver || friend.IsSeated() || ai.HasTarget())
			{
				continue;
			}
			float d = (friend.Position() - at).sqrMagnitude;
			if (d > AnswerRadius * AnswerRadius)
			{
				continue;
			}
			if (crew != null)
			{
				if (ai.squad != crew && crew.JoinCrew(ai))
				{
					Debug.Log("[horn] a bot answers " + driver.name + "'s call and joins its crew.");
					if (!vehicle.HasUnclaimedSeats())
					{
						return;
					}
				}
				continue;
			}
			Squad squad = ai.squad;
			if (!driver.aiControlled && squad != null && squad.state != Squad.State.EnterVehicle
				&& squad.Leader() == ai && squad.members.Count < vehicle.seats.Length && d < nearestIdleSqr)
			{
				nearestIdle = squad;
				nearestIdleSqr = d;
			}
		}
		if (nearestIdle != null
			&& (!lastSquadAnswer.TryGetValue(vehicle, out float last) || Time.time - last >= SquadAnswerCooldown))
		{
			lastSquadAnswer[vehicle] = Time.time;
			nearestIdle.EnterVehicle(vehicle);
			Debug.Log("[horn] a squad of " + nearestIdle.members.Count + " answers " + driver.name + "'s horn and boards.");
		}
	}

	[RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
	private static void Install()
	{
		lastCall.Clear();
		lastSquadAnswer.Clear();
		CarHorn.Honked -= OnHonk;
		CarHorn.Honked += OnHonk;
	}
}
