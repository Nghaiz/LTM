using System.Collections.Generic;
using Ironfront.Net.Replication.Ai;
using UnityEngine;

public class Squad
{
	public enum State
	{
		Stationary = 0,
		Moving = 1,
		DigIn = 2,
		MovingThenDigIn = 3,
		EnterVehicle = 4
	}

	private const float GROUPED_UP_DISTANCE = 7f;

	private static int nextNumber = 1;

	private AiActorController leader;

	public List<AiActorController> members;

	public bool hasAssignedOrder;

	public State state;

	public Vehicle squadVehicle;

	public int number;

	public SpawnPoint targetSpawnPoint;

	private float readyTime;

	private bool groupedUp;

	private bool hasSquadVehicle;

	private int recentTakingFireEvents;

	// ---- the team commander's order (phase P28, BotCommander) ----

	/// <summary>What the commander has this squad doing; None leaves it to the original behaviour.</summary>
	public SquadRole commandRole;

	/// <summary>The flag the order names.</summary>
	public SpawnPoint commandTarget;

	/// <summary>The flag's index in the commander's list, -1 for none.</summary>
	public int commandFlag = -1;

	/// <summary>A flank's side approach, or the spot a defence digs in round.</summary>
	public Vector3 commandPoint;

	/// <summary>A flank has reached its side approach and turned in.</summary>
	public bool commandReachedPoint = true;

	/// <summary>Approaching quietly: nobody sprints until the waypoint.</summary>
	public bool sneaking;

	/// <summary>Whether the commander has given this squad a job.</summary>
	public bool HasCommand => commandRole != SquadRole.None && commandTarget != null;

	/// <summary>When the squad last dug in (phase P28): how long it has held its cover.</summary>
	private float digInTime;

	/// <summary>
	/// Whether a dug-in squad stays in its cover this tick: while any member still has an enemy
	/// in its sights, up to <see cref="CombatRules.HoldCoverSeconds"/> (phase P28).
	/// </summary>
	public bool HoldingCover()
	{
		return CombatRules.HoldCover(state == State.DigIn, GetTarget() != null, Time.time - digInTime);
	}

	/// <summary>
	/// Whether the squad takes <paramref name="vehicle"/> for the job it has (phase P28, part 3):
	/// <see cref="VehicleRules.ShouldBoard"/> over the trip to its objective and the walk to the
	/// vehicle. A squad with no objective keeps the original's "take what is near".
	/// </summary>
	public bool ShouldBoard(Vehicle vehicle)
	{
		AiActorController leader = Leader();
		if (leader == null || vehicle == null)
		{
			return false;
		}
		Vector3 at = leader.actor.Position();
		SpawnPoint objective = HasCommand ? commandTarget : targetSpawnPoint;
		float objectiveDistance = objective != null ? Vector3.Distance(at, objective.transform.position) : float.PositiveInfinity;
		float vehicleDistance = Vector3.Distance(at, vehicle.transform.position);
		return VehicleRules.ShouldBoard(KindOf(vehicle), HasCommand ? commandRole : SquadRole.None, objectiveDistance, vehicleDistance);
	}

	/// <summary>What <paramref name="vehicle"/> is for, to a squad deciding whether to take it.</summary>
	public static VehicleKind KindOf(Vehicle vehicle)
	{
		if (vehicle is Tank)
		{
			return VehicleKind.Armour;
		}
		if (vehicle is Helicopter)
		{
			return VehicleKind.Aircraft;
		}
		if (vehicle is Boat)
		{
			return VehicleKind.Boat;
		}
		return VehicleKind.Transport;
	}

	/// <summary>
	/// Splits off up to <paramref name="count"/> members on foot, never the leader, as a new squad
	/// -- a tank's crew (phase P28, part 3). Null when nobody can go.
	/// </summary>
	public Squad SplitCrew(int count)
	{
		var crew = new List<AiActorController>(count);
		for (int i = members.Count - 1; i >= 0 && crew.Count < count; i--)
		{
			AiActorController member = members[i];
			if (member != null && member != leader && !member.actor.IsSeated())
			{
				crew.Add(member);
			}
		}
		return crew.Count > 0 ? SplitSquad(crew) : null;
	}

	/// <summary>
	/// The bearing of the fire coming at the squad: the first member under fire's, else
	/// <paramref name="fallback"/>.
	/// </summary>
	public Vector3 TakingFireDirection(Vector3 fallback)
	{
		foreach (AiActorController member in members)
		{
			if (member.IsTakingFire())
			{
				return member.takingFireDirection;
			}
		}
		return fallback;
	}

	public Squad(List<AiActorController> members, float timeUntilReady)
	{
		number = nextNumber++;
		state = State.Stationary;
		this.members = members;
		leader = this.members[0];
		foreach (AiActorController member in this.members)
		{
			member.AssignedToSquad(this);
		}
		readyTime = Time.time + timeUntilReady;
	}

	public bool Ready()
	{
		return Time.time > readyTime;
	}

	public void DropMember(AiActorController a)
	{
		members.Remove(a);
		if (squadVehicle != null)
		{
			// Named, so the claim that is released is THIS bot's. The no-argument form takes
			// one off an anonymous pile, which is why two bots claiming and one leaving used to
			// leave the vehicle reporting itself full with a seat empty (V4-D10).
			squadVehicle.DropSeatClaim(a != null ? a.actor : null);
		}
		if (members.Count == 0)
		{
			Disband();
		}
		else if (leader == a)
		{
			leader = members[0];
		}
	}

	public AiActorController Leader()
	{
		return leader;
	}

	public Actor GetTarget()
	{
		foreach (AiActorController member in members)
		{
			if (member.HasTarget())
			{
				return member.target;
			}
		}
		return null;
	}

	public bool HasTargetSpawnPoint()
	{
		return targetSpawnPoint != null;
	}

	public bool ShouldGotoSpawnPoint(SpawnPoint spawnPoint)
	{
		return spawnPoint.owner != Leader().actor.team || !spawnPoint.IsSafe();
	}

	public SpawnPoint ClosestSpawnPoint()
	{
		return ActorManager.ClosestSpawnPoint(Leader().actor.Position());
	}

	public void NewAttackOrder()
	{
		Actor actor = Leader().actor;
		SpawnPoint spawnPoint = ClosestSpawnPoint();
		if (spawnPoint.owner != actor.team)
		{
			AttackSpawnPoint(spawnPoint);
			return;
		}
		List<SpawnPoint> list = new List<SpawnPoint>();
		foreach (SpawnPoint adjacentSpawnPoint in spawnPoint.adjacentSpawnPoints)
		{
			if (adjacentSpawnPoint.owner != actor.team)
			{
				list.Add(adjacentSpawnPoint);
				if (adjacentSpawnPoint.owner >= 0)
				{
					list.Add(adjacentSpawnPoint);
				}
			}
		}
		if (list.Count > 0)
		{
			AttackSpawnPoint(list[Random.Range(0, list.Count)]);
		}
		else
		{
			AttackSpawnPoint(ActorManager.RandomEnemySpawnPoint(actor.team));
		}
	}

	public void ReissueAttackOrder()
	{
		AttackSpawnPoint(targetSpawnPoint);
	}

	/// <summary>
	/// Takes the commander's order, and carries it out now when <paramref name="applyNow"/> --
	/// otherwise at the leader's next order tick, so a squad under fire finishes the fight first.
	/// </summary>
	public void Command(SquadRole role, SpawnPoint target, int flag, Vector3 point, bool sneak, bool applyNow)
	{
		commandRole = target != null ? role : SquadRole.None;
		commandTarget = target;
		commandFlag = flag;
		commandPoint = point;
		commandReachedPoint = role != SquadRole.Flank;
		sneaking = sneak && role == SquadRole.Flank;

		if (applyNow && HasCommand)
		{
			FollowCommand();
		}
	}

	/// <summary>
	/// Does what the commander ordered: straight at the flag, dig in round it, or round the side
	/// through the waypoint first. With no order it is the original "attack the nearest".
	/// </summary>
	public void FollowCommand()
	{
		if (!HasCommand)
		{
			NewAttackOrder();
			return;
		}

		AiActorController leader = Leader();
		int team = leader != null && leader.actor != null ? leader.actor.team : -1;

		switch (commandRole)
		{
		case SquadRole.Defend:
			if (HasVehicle())
			{
				AttackSpawnPoint(commandTarget);
				return;
			}
			// Already dug in round the flag: hold, rather than get up and lie down again.
			if (state == State.DigIn && leader != null && Vector3.Distance(leader.actor.Position(), commandPoint) < 20f)
			{
				return;
			}
			targetSpawnPoint = commandTarget;
			MoveToAndDigIn(commandPoint);
			return;
		case SquadRole.Flank:
			if (!commandReachedPoint)
			{
				targetSpawnPoint = commandTarget;
				MoveTo(commandPoint);
				return;
			}
			break;
		}

		// The flag has fallen to this side: hold it until the commander hands out the next one.
		if (commandTarget.owner == team && commandTarget.IsSafe())
		{
			DigIn();
			return;
		}

		AttackSpawnPoint(commandTarget);
	}

	/// <summary>
	/// Called on the leader's order tick: a flanking squad that has reached its side approach
	/// stops sneaking and turns in on the flag.
	/// </summary>
	public void UpdateCommandProgress()
	{
		if (commandRole != SquadRole.Flank || commandReachedPoint || !HasCommand)
		{
			return;
		}
		AiActorController leader = Leader();
		if (leader == null || leader.actor == null)
		{
			return;
		}
		if (Vector3.Distance(leader.actor.Position(), commandPoint) < 12f)
		{
			commandReachedPoint = true;
			sneaking = false;
			AttackSpawnPoint(commandTarget);
		}
	}

	/// <summary>
	/// Whether the squad may break off for <paramref name="spawnPoint"/>, the flag nearest its
	/// leader, the way the original always did. With no order, yes. An attack takes a flag it is
	/// standing on, a defence only its own flag, and a flank nothing until it has turned in.
	/// </summary>
	public bool MayDivertTo(SpawnPoint spawnPoint)
	{
		if (!HasCommand || spawnPoint == commandTarget)
		{
			return true;
		}
		switch (commandRole)
		{
		case SquadRole.Attack:
		{
			AiActorController leader = Leader();
			return leader != null && Vector3.Distance(leader.actor.Position(), spawnPoint.transform.position) < spawnPoint.GotoRadius() + 10f;
		}
		case SquadRole.Flank:
			return false;
		default:
			return false;
		}
	}

	public void AttackSpawnPoint(SpawnPoint spawnPoint)
	{
		targetSpawnPoint = spawnPoint;
		if (spawnPoint != null)
		{
			Vector3 vector = Random.insideUnitSphere.ToGround() * spawnPoint.GotoRadius();
			MoveTo(spawnPoint.transform.position + vector);
		}
	}

	public void MoveTo(Vector3 point)
	{
		hasAssignedOrder = true;
		LeaveAnyCover();
		state = State.Moving;
		foreach (AiActorController member in members)
		{
			// A hurt member keeps to the cover it fell back to, and catches up after (phase P28).
			if (member.IsFallingBack())
			{
				continue;
			}
			member.Goto(point + Vector3.Scale(Random.insideUnitSphere, new Vector3(3f, 0f, 3f)));
			if (member.squadLeader)
			{
				member.EmoteMoveOrder(point);
			}
		}
	}

	public void MoveToAndDigIn(Vector3 point)
	{
		hasAssignedOrder = true;
		if (HasVehicle())
		{
			Debug.LogWarning("Squad dig in while in vehicle, ignore.");
			return;
		}
		state = State.DigIn;
		digInTime = Time.time;
		foreach (AiActorController member in members)
		{
			member.FindCoverAtPoint(point);
			member.EmoteHailPlayer();
		}
	}

	public void DigIn()
	{
		hasAssignedOrder = true;
		if (HasVehicle())
		{
			Debug.LogWarning("Squad dig in while in vehicle, ignore.");
		}
		else
		{
			if (state == State.DigIn)
			{
				return;
			}
			state = State.DigIn;
			digInTime = Time.time;
			foreach (AiActorController member in members)
			{
				member.FindCover();
				if (member.squadLeader)
				{
					member.EmoteHalt();
				}
			}
		}
	}

	public void DigInTowards(Vector3 direction)
	{
		hasAssignedOrder = true;
		if (state == State.DigIn)
		{
			return;
		}
		state = State.DigIn;
		digInTime = Time.time;
		foreach (AiActorController member in members)
		{
			member.FindCoverTowards(direction);
			if (member.squadLeader)
			{
				member.EmoteHalt();
			}
		}
	}

	public void SetAlreadyInVehicle(Vehicle vehicle)
	{
		squadVehicle = vehicle;
		AiActorController leader = Leader();
		squadVehicle.ClaimSeat(leader != null ? leader.actor : null);
	}

	public void EnterVehicle(Vehicle vehicle)
	{
		hasAssignedOrder = true;
		if (state == State.EnterVehicle)
		{
			return;
		}
		state = State.EnterVehicle;
		hasSquadVehicle = true;
		squadVehicle = vehicle;
		vehicle.ownerTeam = Leader().actor.team;
		for (int i = 0; i < members.Count; i++)
		{
			members[i].GotoAndEnterVehicle(vehicle);
			vehicle.ClaimSeat(members[i] != null ? members[i].actor : null);
			if (members[i].squadLeader)
			{
				members[i].EmoteMoveOrder(vehicle.transform.position);
			}
		}
	}

	public void ExitVehicle()
	{
		foreach (AiActorController member in members)
		{
			member.LeaveVehicle();
		}
		state = State.Stationary;
		hasSquadVehicle = false;
	}

	public bool IsTakingFire()
	{
		foreach (AiActorController member in members)
		{
			if (member.IsTakingFire())
			{
				return true;
			}
		}
		return false;
	}

	public bool AllSeated()
	{
		foreach (AiActorController member in members)
		{
			if (!member.actor.IsSeated())
			{
				return false;
			}
		}
		return true;
	}

	public bool HasVehicle()
	{
		return squadVehicle != null;
	}

	private void LeaveAnyCover()
	{
		if (state != State.DigIn)
		{
			return;
		}
		foreach (AiActorController member in members)
		{
			member.LeaveCover();
		}
	}

	private void Disband()
	{
	}

	public bool IsGroupedUp()
	{
		return groupedUp;
	}

	public void Update()
	{
		UpdateGroupedUpFlag();
		UpdateVehicleStatus();
	}

	private void UpdateGroupedUpFlag()
	{
		if (members.Count < 2)
		{
			groupedUp = false;
			return;
		}
		Vector3 zero = Vector3.zero;
		foreach (AiActorController member in members)
		{
			zero += member.transform.position;
		}
		zero /= (float)members.Count;
		int num = 0;
		foreach (AiActorController member2 in members)
		{
			if (Vector3.Distance(member2.transform.position, zero) < 7f)
			{
				num++;
			}
		}
		groupedUp = num >= 2;
	}

	private void UpdateVehicleStatus()
	{
		if (state != State.EnterVehicle)
		{
			return;
		}
		if (AllSeated())
		{
			state = State.Stationary;
		}
		else
		{
			if (!squadVehicle.IsFull())
			{
				return;
			}
			List<AiActorController> list = new List<AiActorController>();
			foreach (AiActorController member in members)
			{
				if (!member.actor.IsSeated())
				{
					list.Add(member);
				}
			}
			SplitSquad(list);
			state = State.Stationary;
		}
	}

	public Squad SplitSquad(List<AiActorController> leavingMembers)
	{
		foreach (AiActorController leavingMember in leavingMembers)
		{
			DropMember(leavingMember);
		}
		Squad squad = new Squad(leavingMembers, 0.5f);
		foreach (AiActorController leavingMember2 in leavingMembers)
		{
			leavingMember2.squad = squad;
			if (leavingMember2.actor.IsSeated())
			{
				leavingMember2.squad.SetAlreadyInVehicle(leavingMember2.actor.seat.vehicle);
			}
		}
		return squad;
	}

	public bool MemberNeedsResupply()
	{
		foreach (AiActorController member in members)
		{
			if (member.actor.needsResupply)
			{
				return true;
			}
		}
		return false;
	}

	public bool MemberNeedsHealth()
	{
		foreach (AiActorController member in members)
		{
			if (member.actor.health < 80f)
			{
				return true;
			}
		}
		return false;
	}

	public void MakeLeader(AiActorController member)
	{
		leader = member;
	}
}
