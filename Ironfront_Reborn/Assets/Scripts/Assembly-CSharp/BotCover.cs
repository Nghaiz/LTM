using System.Collections.Generic;
using Ironfront.Net.Replication.Ai;
using Ironfront.Net.Unity;
using UnityEngine;

/// <summary>
/// Where a bot takes cover: the level's authored points and the stationary vehicles round it,
/// each judged against the enemy it faces by <see cref="CoverProbe"/>. Phase P28, part 2.
/// </summary>
/// <remarks>
/// <para>
/// <b>What it replaced.</b> The original sent a bot under fire to the nearest authored point
/// turned within 30 degrees of the shots, up to 50 m off, whether or not that point hid it from
/// the shooter -- and where none faced the right way the bot walked on in the open. Vehicles were
/// never cover at all: no authored point can know where a tank will be parked.
/// </para>
/// <para>
/// <b>Cost.</b> One search judges at most <see cref="CombatRules.MaxCoverCandidates"/> points and
/// <see cref="CombatRules.MaxVehicleCandidates"/> vehicles, nearest first, stopping early; a
/// judgement is two to four short rays. Searches happen when a bot is shot at, told to dig in,
/// or falls back hurt -- never per frame.
/// </para>
/// </remarks>
public static class BotCover
{
	/// <summary>A chosen spot: an authored point, or (<see cref="Authored"/> null) ground behind a vehicle.</summary>
	public struct Pick
	{
		public CoverPoint Authored;

		public Vector3 Spot;

		public CoverFit Fit;
	}

	/// <summary>Two bots' vehicle spots closer than this, in metres, are one spot.</summary>
	private const float SpotSpacing = 1.2f;

	private static readonly List<CoverPoint> Nearby = new List<CoverPoint>(CombatRules.MaxCoverCandidates);

	private static readonly Vector3[] Spots = new Vector3[CoverProbe.MaxCandidates];

	private static readonly CoverPoint[] Owners = new CoverPoint[CoverProbe.MaxCandidates];

	private static readonly Vehicle[] NearVehicles = new Vehicle[CombatRules.MaxVehicleCandidates];

	private static readonly float[] NearVehicleDistances = new float[CombatRules.MaxVehicleCandidates];

	/// <summary>Every bot's own spot marker, so two bots do not pick the same patch behind one tank.</summary>
	private static readonly List<CoverPoint> BotSpots = new List<CoverPoint>();

	/// <summary>
	/// The best cover within <paramref name="radius"/> of <paramref name="origin"/> against an
	/// enemy whose eye is at <paramref name="threatEye"/>; false when nothing there hides a bot.
	/// </summary>
	public static bool Find(Vector3 origin, Vector3 threatEye, float radius, bool fallingBack, out Pick pick)
	{
		pick = default(Pick);
		int count = 0;

		if (CoverManager.instance != null)
		{
			CoverManager.instance.NearestVacant(origin, radius, Nearby, CombatRules.MaxCoverCandidates);
			for (int i = 0; i < Nearby.Count && count < Spots.Length; i++)
			{
				Spots[count] = Nearby[i].transform.position;
				Owners[count] = Nearby[i];
				count++;
			}
		}
		count = AddVehicleSpots(origin, threatEye, radius, count);

		int best = CoverProbe.Choose(Spots, count, origin, threatEye, fallingBack, out CoverFit fit);
		if (best >= 0)
		{
			pick.Authored = Owners[best];
			pick.Spot = Spots[best];
			pick.Fit = fit;
		}

		// Nothing here may outlive the call: a destroyed point must not sit in a static array.
		System.Array.Clear(Owners, 0, count);
		Nearby.Clear();
		return best >= 0;
	}

	/// <summary>The authored type a bot in cover acts on: lean one way, lean the other, or crouch.</summary>
	public static CoverPoint.Type TypeFor(CoverFit fit)
	{
		switch (fit)
		{
		case CoverFit.LeanLeft:
			return CoverPoint.Type.LeanLeft;
		case CoverFit.LeanRight:
			return CoverPoint.Type.LeanRight;
		default:
			return CoverPoint.Type.Crouch;
		}
	}

	/// <summary>A bot's own spot marker: inactive, so <see cref="CoverManager"/>'s scan never lists it.</summary>
	/// <remarks>
	/// Built in code, not from a prefab: an invisible logic marker with no hierarchy, one per bot,
	/// that exists only because the cover code the bots already run works on a
	/// <see cref="CoverPoint"/> -- the same reason <see cref="BotCommander"/> is added in code.
	/// </remarks>
	public static CoverPoint NewBotSpot()
	{
		var marker = new GameObject("Bot Cover Spot");
		marker.SetActive(false);
		CoverPoint point = marker.AddComponent<CoverPoint>();
		BotSpots.Add(point);
		return point;
	}

	/// <summary>Drops a bot's spot marker when the bot goes.</summary>
	public static void ForgetBotSpot(CoverPoint point)
	{
		if (point == null)
		{
			return;
		}
		BotSpots.Remove(point);
		Object.Destroy(point.gameObject);
	}

	private static int AddVehicleSpots(Vector3 origin, Vector3 threatEye, float radius, int count)
	{
		if (ActorManager.instance == null || ActorManager.instance.vehicles == null)
		{
			return count;
		}

		int near = 0;
		float limit = radius * radius;
		List<Vehicle> vehicles = ActorManager.instance.vehicles;
		for (int i = 0; i < vehicles.Count; i++)
		{
			Vehicle vehicle = vehicles[i];
			if (vehicle == null || vehicle.burning || vehicle.colliders == null || !vehicle.IsStill())
			{
				continue;
			}
			float distance = (vehicle.transform.position - origin).sqrMagnitude;
			if (distance > limit)
			{
				continue;
			}
			near = InsertNearest(vehicle, distance, near);
		}

		for (int i = 0; i < near && count < Spots.Length; i++)
		{
			if (TryBounds(NearVehicles[i], out Bounds bounds)
				&& CoverProbe.SpotBehind(bounds, threatEye, out Vector3 spot)
				&& !Taken(spot))
			{
				Spots[count] = spot;
				Owners[count] = null;
				count++;
			}
			NearVehicles[i] = null;
		}
		return count;
	}

	private static int InsertNearest(Vehicle vehicle, float distance, int near)
	{
		int capacity = NearVehicles.Length;
		if (near == capacity && distance >= NearVehicleDistances[capacity - 1])
		{
			return near;
		}
		int at = near < capacity ? near : capacity - 1;
		while (at > 0 && NearVehicleDistances[at - 1] > distance)
		{
			NearVehicles[at] = NearVehicles[at - 1];
			NearVehicleDistances[at] = NearVehicleDistances[at - 1];
			at--;
		}
		NearVehicles[at] = vehicle;
		NearVehicleDistances[at] = distance;
		return near < capacity ? near + 1 : near;
	}

	/// <summary>The vehicle's solid body as one box: every enabled, non-trigger collider on it.</summary>
	private static bool TryBounds(Vehicle vehicle, out Bounds bounds)
	{
		bounds = default(Bounds);
		bool any = false;
		Collider[] colliders = vehicle.colliders;
		for (int i = 0; i < colliders.Length; i++)
		{
			Collider collider = colliders[i];
			if (collider == null || !collider.enabled || collider.isTrigger)
			{
				continue;
			}
			if (!any)
			{
				bounds = collider.bounds;
				any = true;
			}
			else
			{
				bounds.Encapsulate(collider.bounds);
			}
		}
		return any;
	}

	private static bool Taken(Vector3 spot)
	{
		float spacing = SpotSpacing * SpotSpacing;
		for (int i = 0; i < BotSpots.Count; i++)
		{
			CoverPoint other = BotSpots[i];
			if (other != null && other.taken && (other.transform.position - spot).sqrMagnitude < spacing)
			{
				return true;
			}
		}
		return false;
	}
}
