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

	/// <summary>A gathering squad dug in this close to its rally point stays put instead of moving again.</summary>
	private const float RallyHoldDistance = TeamPlanner.RallyRadius;

	/// <summary>Why a squad split: what <see cref="Census"/> counts it under.</summary>
	public enum SplitReason
	{
		/// <summary>A member with no path to its order went its own way (<c>AiActorController.CreateRougeSquad</c>).</summary>
		Rogue = 0,

		/// <summary>Part of the squad split off to crew a tank (<see cref="SplitCrew"/>).</summary>
		Crew = 1,

		/// <summary>The squad's vehicle filled up and the members still on foot split off.</summary>
		VehicleFull = 2
	}

	/// <summary>
	/// Where each side's squads come from and how they break up, since the match started (phase
	/// P29): the commander's log line reports it, so a server log says why a side has as many
	/// squads as bots instead of leaving it to be guessed.
	/// </summary>
	/// <remarks>
	/// Nothing in the original ever merged two squads -- they only ever split, or shrank as members
	/// died -- so every one of these counts only goes up until <see cref="Merged"/> takes squads back.
	/// </remarks>
	public static class Census
	{
		/// <summary>Squads a spawn wave formed.</summary>
		public static readonly int[] Formed = new int[2];

		/// <summary>Of those, the ones a wave left with a single bot.</summary>
		public static readonly int[] FormedAlone = new int[2];

		/// <summary>Splits, by <see cref="SplitReason"/>, per side.</summary>
		public static readonly int[][] Splits = { new int[3], new int[3] };

		/// <summary>Deaths that left one bot on its own in its squad.</summary>
		public static readonly int[] LeftAlone = new int[2];

		/// <summary>Squads folded into another one.</summary>
		public static readonly int[] Merged = new int[2];

		/// <summary>Bots a spawn wave sent to reinforce a squad instead of starting one of their own.</summary>
		public static readonly int[] Reinforced = new int[2];

		/// <summary>Bots respawned at a flag because an empty vehicle of the side's stood there (phase P32).</summary>
		public static readonly int[] SpawnedForVehicle = new int[2];

		public static void Reset()
		{
			for (int team = 0; team < 2; team++)
			{
				Formed[team] = FormedAlone[team] = LeftAlone[team] = Merged[team] = Reinforced[team] = SpawnedForVehicle[team] = 0;
				System.Array.Clear(Splits[team], 0, Splits[team].Length);
			}
		}

		public static void NoteSpawnedForVehicle(int team)
		{
			if ((uint)team < 2u)
			{
				SpawnedForVehicle[team]++;
			}
		}

		public static void NoteFormed(int team, int size)
		{
			if ((uint)team >= 2u)
			{
				return;
			}
			Formed[team]++;
			if (size == 1)
			{
				FormedAlone[team]++;
			}
		}

		public static void NoteSplit(int team, SplitReason reason)
		{
			if ((uint)team < 2u)
			{
				Splits[team][(int)reason]++;
			}
		}

		public static void NoteLeftAlone(int team)
		{
			if ((uint)team < 2u)
			{
				LeftAlone[team]++;
			}
		}

		public static void NoteMerged(int team)
		{
			if ((uint)team < 2u)
			{
				Merged[team]++;
			}
		}

		public static void NoteReinforced(int team)
		{
			if ((uint)team < 2u)
			{
				Reinforced[team]++;
			}
		}
	}

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

	/// <summary>When the squad set out to board <see cref="squadVehicle"/>.</summary>
	private float enterVehicleTime;

	/// <summary>
	/// How long a squad may take to get into a vehicle before it gives up on it.
	/// </summary>
	/// <remarks>
	/// Measured with no limit (bot soak, ten minutes on each map, 50 bots): 69 squads got in, in
	/// 5 s to 10 s as a rule and 46 s at the longest (Island); the ones that never would waited up
	/// to 107 s. A minute keeps every boarding that was going to happen.
	/// </remarks>
	private const float BoardingTimeoutSeconds = 60f;

	/// <summary>
	/// How long a crew formed for a vehicle (phase P32) waits in it, seated, for more bots
	/// respawning for the same vehicle before it sets off with the seats it has.
	/// </summary>
	public const float CrewMusterSeconds = 10f;

	/// <summary>Formed from bots sent back for <see cref="squadVehicle"/> (phase P32).</summary>
	private bool isCrew;

	/// <summary>Marks this squad as a crew sent back for its vehicle: see <see cref="CrewMusterSeconds"/>.</summary>
	public void MarkCrew()
	{
		isCrew = true;
	}

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
		// A map with no boat graph gives a boat's driver nowhere to path (phase P32: Forest Lake).
		if (vehicle is Boat && !ActorManager.BoatsNavigable)
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
		return crew.Count > 0 ? SplitSquad(crew, SplitReason.Crew) : null;
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
			TakeOffOtherRoster(member);
			member.AssignedToSquad(this);
		}
		readyTime = Time.time + timeUntilReady;
	}

	/// <summary>
	/// Takes <paramref name="member"/> off the roster of the squad it names, if that is another
	/// squad (phase P29): a bot is on exactly one roster, the one its <c>squad</c> field names.
	/// </summary>
	/// <remarks>
	/// Nothing kept that true. A roster a bot was never taken off counted it twice -- 56 bots a
	/// side against 50 in the capacity bench -- and when it came back and was asked to join that
	/// squad, <see cref="Join"/> found it already listed and left without giving it the squad:
	/// the squad went on ordering a bot with no squad of its own, which threw every frame. The
	/// cause was a death path that never left the squad (see <c>ActorGameplaySource.IsDead</c>);
	/// this makes every way onto a roster a move, so the next such path cannot repeat it.
	/// </remarks>
	private void TakeOffOtherRoster(AiActorController member)
	{
		Squad previous = member.squad;
		if (previous != null && previous != this && previous.members.Contains(member))
		{
			previous.DropMember(member);
		}
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

	/// <summary>Takes <paramref name="member"/> into the squad and sends it after the others (phase P29).</summary>
	/// <remarks>
	/// A move, not an add: off any other roster first, and given this squad even when this roster
	/// already lists it, which is the case that used to return early and leave it squadless.
	/// </remarks>
	/// <summary>
	/// The squad on its way to board <paramref name="vehicle"/> with a seat still unclaimed, or null
	/// (phase P32: a bot sent back for that vehicle joins its crew).
	/// </summary>
	public static Squad BoardingCrewFor(Vehicle vehicle)
	{
		if (vehicle == null || vehicle.dead || vehicle.ownerTeam < 0)
		{
			return null;
		}
		List<Actor> alive = ActorManager.AliveActorsOnTeam(vehicle.ownerTeam);
		for (int i = 0; i < alive.Count; i++)
		{
			AiActorController ai = alive[i] != null ? alive[i].controller as AiActorController : null;
			Squad squad = ai != null ? ai.squad : null;
			if (squad != null && squad.state == State.EnterVehicle && squad.squadVehicle == vehicle && vehicle.HasUnclaimedSeats())
			{
				return squad;
			}
		}
		return null;
	}

	/// <summary>
	/// Takes <paramref name="member"/> into this boarding squad and sends it to a seat of the squad's
	/// vehicle. False, and nothing changed, once the squad is not boarding or the seats are claimed.
	/// </summary>
	public bool JoinCrew(AiActorController member)
	{
		if (member == null || state != State.EnterVehicle || squadVehicle == null || squadVehicle.dead
			|| !squadVehicle.HasUnclaimedSeats() || members.Count >= squadVehicle.seats.Length)
		{
			return false;
		}
		Join(member);
		member.GotoAndEnterVehicle(squadVehicle);
		squadVehicle.ClaimSeat(member.actor);
		return true;
	}

	public void Join(AiActorController member)
	{
		if (member == null)
		{
			return;
		}
		TakeOffOtherRoster(member);
		if (!members.Contains(member))
		{
			members.Add(member);
		}
		member.AssignedToSquad(this);
		member.JoinedSquad(this);
	}

	/// <summary>
	/// Takes every member of <paramref name="other"/> into this squad and leaves it empty: the
	/// commander folding a lone bot back in (phase P29, <see cref="SquadRegroup"/>).
	/// </summary>
	public void Absorb(Squad other)
	{
		if (other == null || other == this || other.members.Count == 0)
		{
			return;
		}
		AiActorController first = other.members[0];
		int team = first != null && first.actor != null ? first.actor.team : -1;
		List<AiActorController> joining = new List<AiActorController>(other.members);
		foreach (AiActorController member in joining)
		{
			other.DropMember(member);
			Join(member);
		}
		Census.NoteMerged(team);
	}

	/// <summary>
	/// The squad of <paramref name="team"/> on foot whose leader stands nearest
	/// <paramref name="point"/>, within <paramref name="radius"/>, with room for
	/// <paramref name="count"/> more; null when there is none (phase P29).
	/// </summary>
	public static Squad NearestWithRoom(int team, Vector3 point, float radius, int count)
	{
		Squad best = null;
		float bestDistance = radius;
		List<Actor> alive = ActorManager.AliveActorsOnTeam(team);
		for (int i = 0; i < alive.Count; i++)
		{
			Actor actor = alive[i];
			AiActorController ai = actor != null ? actor.controller as AiActorController : null;
			Squad squad = ai != null ? ai.squad : null;
			if (squad == null || squad == best || squad.members.Count == 0 || squad.members.Count + count > SquadRegroup.MaxSize)
			{
				continue;
			}
			if (squad.HasVehicle() || squad.state == State.EnterVehicle)
			{
				continue;
			}
			AiActorController squadLeader = squad.Leader();
			if (squadLeader == null || squadLeader.actor == null)
			{
				continue;
			}
			float distance = Vector3.Distance(squadLeader.actor.Position(), point);
			if (distance <= bestDistance)
			{
				best = squad;
				bestDistance = distance;
			}
		}
		return best;
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
		case SquadRole.Assemble:
			// Gathering short of a defended flag (phase P29): into cover at the rally point, facing
			// the flag, until the commander sends the whole assault in.
			if (HasVehicle())
			{
				AttackSpawnPoint(commandTarget);
				return;
			}
			if (state == State.DigIn && leader != null && Vector3.Distance(leader.actor.Position(), commandPoint) < RallyHoldDistance)
			{
				return;
			}
			targetSpawnPoint = commandTarget;
			MoveToAndDigIn(commandPoint);
			return;
		}

		// The flag has fallen to this side: hold it until the commander hands out the next one.
		if (commandTarget.owner == team && commandTarget.IsSafe())
		{
			// A squad aboard, or boarding, holds from where it is. DigIn cannot put a crew in cover
			// and said so on every order tick ("Squad dig in while in vehicle, ignore."); the
			// Defend and Assemble cases above already keep a vehicle squad out of it.
			if (HasVehicle())
			{
				hasAssignedOrder = true;
				return;
			}
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
	/// leader, the way the original always did. With no order, yes. An attack takes one within
	/// <see cref="TacticsProfile.AttackDivertRange"/> of its capture range -- the original's reflex,
	/// kept within a reach part 4 trained -- a defence only its own flag, and a flank nothing until
	/// it has turned in.
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
			return leader != null && Vector3.Distance(leader.actor.Position(), spawnPoint.transform.position) < spawnPoint.GotoRadius() + BotCommander.Profile.AttackDivertRange;
		}
		case SquadRole.Flank:
		case SquadRole.Assemble:
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
			// Closer together in the dark (phase P32 NightTactics).
			float spread = NightTactics.IsNight ? NightTactics.SquadSpreadMetres : NightTactics.DaySquadSpreadMetres;
			member.Goto(point + Vector3.Scale(Random.insideUnitSphere, new Vector3(spread, 0f, spread)));
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
		enterVehicleTime = Time.time;
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

	/// <summary>
	/// Whether the vehicle this squad set out to board is no longer one it can board: wrecked, taken
	/// by someone outside the squad, or not boarded in <see cref="BoardingTimeoutSeconds"/>.
	/// </summary>
	/// <remarks>
	/// The original gave up on a WRECKED vehicle only (<c>AiActorController.AiOrders</c>), and no
	/// other order reaches a squad in <see cref="State.EnterVehicle"/>. So a squad whose vehicle
	/// another squad drove off, or left two hundred metres away, stood on the spot where it had
	/// been until it died. The bot soak found such squads waiting more than a minute 1, 5 and 3
	/// times in ten minutes on Dustbowl, Island and Forest Lake: a jeep 648 m away with another
	/// squad at the wheel, a helicopter 363 m away, quadbikes 198 and 231 m away with no driver.
	/// </remarks>
	public bool BoardingFailed()
	{
		if (state != State.EnterVehicle)
		{
			return false;
		}
		if (squadVehicle == null || squadVehicle.dead)
		{
			return true;
		}
		Actor driver = squadVehicle.HasDriver() ? squadVehicle.Driver() : null;
		if (driver != null && !IsMember(driver))
		{
			return true;
		}
		return Time.time - enterVehicleTime > BoardingTimeoutSeconds;
	}

	/// <summary>
	/// Gives up on boarding. Nobody aboard: the squad stands down where it is. Some aboard: they
	/// keep their seats and the rest go on foot as a squad of their own, the way a full vehicle
	/// already splits a squad (<see cref="UpdateVehicleStatus"/>).
	/// </summary>
	public void GiveUpBoarding()
	{
		List<AiActorController> onFoot = new List<AiActorController>();
		int aboard = 0;
		foreach (AiActorController member in members)
		{
			if (member == null)
			{
				continue;
			}
			Actor body = member.actor;
			if (body != null && body.IsSeated() && body.seat.vehicle == squadVehicle)
			{
				aboard++;
			}
			else
			{
				onFoot.Add(member);
			}
		}
		if (aboard == 0)
		{
			ExitVehicle();
			return;
		}
		foreach (AiActorController member in onFoot)
		{
			member.LeaveVehicle();
		}
		state = State.Stationary;
		if (onFoot.Count > 0)
		{
			SplitSquad(onFoot, SplitReason.VehicleFull);
		}
	}

	private bool IsMember(Actor actor)
	{
		foreach (AiActorController member in members)
		{
			if (member != null && member.actor == actor)
			{
				return true;
			}
		}
		return false;
	}

	public void ExitVehicle()
	{
		foreach (AiActorController member in members)
		{
			member.LeaveVehicle();
			// By name, as DropMember does: a squad walking away from a vehicle holds no seat in it.
			if (squadVehicle != null)
			{
				squadVehicle.DropSeatClaim(member != null ? member.actor : null);
			}
		}
		state = State.Stationary;
		hasSquadVehicle = false;
		// Out, so it no longer has one. See HasVehicle.
		squadVehicle = null;
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

	/// <summary>Whether the squad is in its vehicle, or on its way into it.</summary>
	/// <remarks>
	/// <para>
	/// <b>The original answered "was this squad ever given a vehicle".</b> <see cref="squadVehicle"/>
	/// is set by <see cref="EnterVehicle"/> and <see cref="SetAlreadyInVehicle"/> and nothing cleared
	/// it; <see cref="ExitVehicle"/> cleared only <c>hasSquadVehicle</c>, a flag nothing reads. So a
	/// squad that got out -- shot at, a stuck boat, a burning car, or one that never got in -- went on
	/// as a mounted squad on foot: it would not dig in at a flag it held ("Squad dig in while in
	/// vehicle, ignore." on every order tick), did not turn to cover when shot at, never boarded
	/// another vehicle or merged into a nearby squad, and the commander planned for it as driving.
	/// </para>
	/// <para>
	/// Now: boarding, or with a member in that vehicle's seat, however the others left it.
	/// </para>
	/// </remarks>
	public bool HasVehicle()
	{
		if (squadVehicle == null)
		{
			return false;
		}
		if (state == State.EnterVehicle)
		{
			return true;
		}
		foreach (AiActorController member in members)
		{
			Actor body = member != null ? member.actor : null;
			if (body != null && body.IsSeated() && body.seat.vehicle == squadVehicle)
			{
				return true;
			}
		}
		return false;
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
			// A crew sent back for this vehicle waits a little for the next bots respawning for
			// it, so one bot does not drive off alone in a jeep the next three were sent to.
			if (isCrew && !squadVehicle.IsFull() && Time.time - enterVehicleTime < CrewMusterSeconds)
			{
				return;
			}
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
			SplitSquad(list, SplitReason.VehicleFull);
			state = State.Stationary;
		}
	}

	public Squad SplitSquad(List<AiActorController> leavingMembers, SplitReason reason)
	{
		AiActorController first = leavingMembers.Count > 0 ? leavingMembers[0] : null;
		Census.NoteSplit(first != null && first.actor != null ? first.actor.team : -1, reason);
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

	/// <summary>How far a squad on foot will walk out of its way to a supply cache its side holds.</summary>
	public const float SupplyDetourMetres = 60f;

	/// <summary>The longest a squad spends on one trip to a cache, walking there included.</summary>
	public const float SupplyDetourSeconds = 25f;

	/// <summary>How long after a trip before the squad may take another.</summary>
	public const float SupplyDetourCooldownSeconds = 45f;

	private float supplyDetourEnds = -1f;

	private float nextSupplyDetour;

	/// <summary>The cache or field crate (phase P32) the squad is on its way to.</summary>
	private Component supplyTarget;

	/// <summary>
	/// Whether a member is down to half the spare rounds a weapon holds. Not
	/// <c>Actor.needsResupply</c>: that flag stays set for a weapon whose spare ceiling is zero,
	/// which no cache can ever fill, and a squad reading it would never leave the crates.
	/// </summary>
	private bool MemberShortOfAmmo()
	{
		foreach (AiActorController member in members)
		{
			Actor soldier = member.actor;
			for (int i = 0; i < soldier.weapons.Length; i++)
			{
				Weapon weapon = soldier.weapons[i];
				if (weapon != null && weapon.configuration.spareAmmo > 0 && soldier.spareAmmo[i] <= weapon.configuration.spareAmmo / 2)
				{
					return true;
				}
			}
		}
		return false;
	}

	/// <summary>
	/// Sends a squad that is short of ammunition, or has a hurt member, to the nearest supply cache
	/// its side holds, and keeps it there until the need is met. False when there is no need, no
	/// cache in reach, or the squad rides a vehicle; the caller then gives its usual order.
	/// </summary>
	/// <remarks>
	/// Owner request 2026-10-03: the caches at every flag are for bots as well as players. Called
	/// only from the leader's quiet branch (no fire taken, not holding cover), so a squad never
	/// breaks off a fight for a crate. A trip is capped at <see cref="SupplyDetourSeconds"/> and
	/// followed by <see cref="SupplyDetourCooldownSeconds"/>, so a need the crates cannot meet (a
	/// straggler who never arrives) costs the squad one trip, not the round.
	/// </remarks>
	/// <param name="leaderMoving">Whether the leader still has a path to walk: an order is
	/// re-issued only to a squad that has stopped short, never every half second.</param>
	public bool TryGoResupply(bool leaderMoving)
	{
		if (HasVehicle())
		{
			supplyDetourEnds = -1f;
			return false;
		}
		AiActorController leader = Leader();
		if (leader == null || leader.actor == null)
		{
			return false;
		}
		bool ammo = MemberShortOfAmmo();
		bool health = MemberNeedsHealth();
		float now = Time.time;
		if (!ammo && !health)
		{
			if (supplyDetourEnds >= 0f)
			{
				supplyDetourEnds = -1f;
				nextSupplyDetour = now + SupplyDetourCooldownSeconds;
			}
			return false;
		}
		if (supplyDetourEnds >= 0f && now > supplyDetourEnds)
		{
			supplyDetourEnds = -1f;
			nextSupplyDetour = now + SupplyDetourCooldownSeconds;
			return false;
		}
		if (supplyDetourEnds < 0f && now < nextSupplyDetour)
		{
			return false;
		}
		Vector3 from = leader.actor.Position();
		SupplyCache cache = SupplyCache.Nearest(from, leader.actor.team, ammo, health, SupplyDetourMetres);
		// Or a crate left in the field (phase P32), whichever is nearer: it serves both sides.
		FieldCrate crate = FieldCrate.Nearest(from, ammo, health, SupplyDetourMetres);
		Component target = cache;
		float range = cache != null ? cache.range : 0f;
		if (crate != null && (cache == null || (crate.transform.position - from).sqrMagnitude < (cache.transform.position - from).sqrMagnitude))
		{
			target = crate;
			range = FieldCrate.Range;
		}
		if (target == null)
		{
			supplyDetourEnds = -1f;
			return false;
		}
		bool starting = supplyDetourEnds < 0f || target != supplyTarget;
		if (supplyDetourEnds < 0f)
		{
			supplyDetourEnds = now + SupplyDetourSeconds;
		}
		supplyTarget = target;
		// Walk there once; again only if the squad stopped short. At it: hold for the pulses.
		if ((starting || !leaderMoving) && Vector3.Distance(from, target.transform.position) > range * 0.6f)
		{
			MoveTo(target.transform.position);
		}
		return true;
	}

	public void MakeLeader(AiActorController member)
	{
		leader = member;
	}
}
