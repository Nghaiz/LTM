using System.Collections.Generic;
using System.Text;
using Ironfront.Net.Replication.Ai;
using Ironfront.Net.Replication.Movement;
using UnityEngine;

/// <summary>
/// Each side's commander: every couple of seconds it reads the flags and the side's squads, asks
/// <see cref="TeamPlanner"/> for a plan, and hands each squad its role. Phase P28, from the owner's
/// report of 2026-09-30.
/// </summary>
/// <remarks>
/// <para>
/// <b>What a side knows.</b> The flags and their owners, its own squads, and the enemies it is in
/// contact with: an enemy counts toward the threat at a flag only while some bot of the side is
/// within <see cref="ContactRadius"/> of it -- or while it stands on the flag itself, which every
/// player's map shows as contested. A commander that knew where every enemy stood would be a
/// cheat, and would play like one; one that did not know its own flag was being taken lost it
/// (part 4's simulator: the commander held a third fewer flags than the original squads).
/// </para>
/// <para>
/// <b>Where it runs.</b> Wherever the bots think: on the dedicated server, and offline. A networked
/// client's bots are proxies with no squads, so there it finds nothing to command.
/// </para>
/// <para>
/// <b>Cheap by construction.</b> One side is planned per call, the two alternating, one call every
/// <see cref="PlanPeriod"/> / 2 seconds, over arrays reused for the whole match.
/// </para>
/// </remarks>
public sealed class BotCommander : MonoBehaviour
{
	/// <summary>How often each side is re-planned, in seconds.</summary>
	public const float PlanPeriod = 2f;

	/// <summary>An enemy this close to a flag threatens it.</summary>
	public const float ThreatRadius = 60f;

	/// <summary>An enemy this close to one of the side's bots is known to the side.</summary>
	public const float ContactRadius = 90f;

	/// <summary>How often a side's plan is logged when nothing about it changed, in seconds.</summary>
	private const float QuietLogPeriod = 60f;

	public static BotCommander instance;

	/// <summary>The weights both sides plan with.</summary>
	public static TacticsProfile Profile = TacticsProfile.Default();

	private readonly TeamPlanner[] _planners = new TeamPlanner[2];
	private SpawnPoint[] _points = new SpawnPoint[0];
	private FlagInfo[] _flags = new FlagInfo[0];
	private int[] _adjacency = new int[0];

	private readonly List<Squad> _squads = new List<Squad>(TeamPlanner.MaxSquads);
	private readonly HashSet<Squad> _seen = new HashSet<Squad>();
	private readonly SquadInfo[] _squadInfo = new SquadInfo[TeamPlanner.MaxSquads];
	private readonly SquadOrder[] _orders = new SquadOrder[TeamPlanner.MaxSquads];
	private readonly SquadRegroup _regroup = new SquadRegroup();
	private readonly SquadMerge[] _merges = new SquadMerge[TeamPlanner.MaxSquads];
	private readonly string[] _lastSummary = new string[2];
	private readonly float[] _lastLogged = new float[2];
	private readonly StringBuilder _log = new StringBuilder(256);

	private float _nextPlan;
	private int _nextTeam;

	/// <summary>The commander for this match, on <paramref name="host"/>; made once, kept after.</summary>
	/// <remarks>
	/// Added in code, not authored: a logic component with no hierarchy, riding on ActorManager's
	/// object in every map scene the way the client's presenters add theirs at runtime.
	/// </remarks>
	public static BotCommander EnsureOn(GameObject host)
	{
		BotCommander commander = host.GetComponent<BotCommander>();
		if (commander == null)
		{
			commander = host.AddComponent<BotCommander>();
		}
		return commander;
	}

	private void Awake()
	{
		instance = this;
	}

	/// <summary>
	/// Plans by night or by day (phase P32 Night Mode): <see cref="TacticsProfile.Night"/> gathers
	/// short of a defended flag and draws lone bots into squads from further off. A change starts
	/// both sides' plans afresh; the same answer twice changes nothing.
	/// </summary>
	public void UseNightTactics(bool night)
	{
		if (night == usingNightTactics)
		{
			return;
		}
		usingNightTactics = night;
		Profile = night ? TacticsProfile.Night() : TacticsProfile.Default();
		_planners[0] = new TeamPlanner(Profile);
		_planners[1] = new TeamPlanner(Profile);
		Debug.Log("[commander] planning by " + (night ? "night: gather before an assault, regroup within " + Profile.RegroupRadius.ToString("0") + " m" : "day") + ".");
	}

	private bool usingNightTactics;

	/// <summary>Reads the map's flags and their links. Called when a match starts.</summary>
	public void StartGame()
	{
		_planners[0] = new TeamPlanner(Profile);
		_planners[1] = new TeamPlanner(Profile);

		_points = ActorManager.instance != null && ActorManager.instance.spawnPoints != null
			? ActorManager.instance.spawnPoints
			: new SpawnPoint[0];

		int count = Mathf.Min(_points.Length, TeamPlanner.MaxFlags);
		_flags = new FlagInfo[count];

		// Both ways round. The maps list neighbours one way only in places -- Island's Landing
		// names none, while Fort and Farm both name it -- and a flag that does not know its own
		// neighbours never counts as the front line.
		var links = new List<int>[count];
		for (int f = 0; f < count; f++)
		{
			links[f] = new List<int>();
		}
		for (int f = 0; f < count; f++)
		{
			SpawnPoint point = _points[f];
			if (point == null || point.adjacentSpawnPoints == null)
			{
				continue;
			}
			foreach (SpawnPoint neighbour in point.adjacentSpawnPoints)
			{
				int index = System.Array.IndexOf(_points, neighbour);
				if (index < 0 || index >= count || index == f)
				{
					continue;
				}
				if (!links[f].Contains(index)) links[f].Add(index);
				if (!links[index].Contains(f)) links[index].Add(f);
			}
		}

		var adjacency = new List<int>();
		for (int f = 0; f < count; f++)
		{
			_flags[f].AdjacencyStart = adjacency.Count;
			adjacency.AddRange(links[f]);
			_flags[f].AdjacencyCount = links[f].Count;
		}

		_adjacency = adjacency.ToArray();

		// The HQs: the flags a side holds as the match begins (phase P32). Read once, here, because
		// whoever holds an HQ later, it is still the place that side's vehicles stand.
		for (int f = 0; f < count; f++)
		{
			_flags[f].IsBase = _points[f] != null && _points[f].owner >= 0;
		}

		_nextPlan = Time.time + 3f;
		_lastSummary[0] = _lastSummary[1] = null;
		Squad.Census.Reset();
	}

	private void Update()
	{
		if (_flags.Length == 0 || Time.time < _nextPlan)
		{
			return;
		}

		_nextPlan = Time.time + PlanPeriod * 0.5f;
		int team = _nextTeam;
		_nextTeam = 1 - _nextTeam;

		PlanFor(team);
	}

	private void PlanFor(int team)
	{
		CollectSquads(team);
		if (_squads.Count == 0)
		{
			return;
		}

		if (Regroup())
		{
			CollectSquads(team);
		}

		ReadFlags(team);

		int squadCount = Mathf.Min(_squads.Count, TeamPlanner.MaxSquads);
		for (int i = 0; i < squadCount; i++)
		{
			_squadInfo[i] = Describe(_squads[i]);
		}

		TeamPlanner planner = _planners[team];
		// The networked match's score where there is one; offline, the offline scoreboard's.
		if (!Ironfront.Net.Unity.MatchScoreFeed.TryScore(team, out int score)
			|| !Ironfront.Net.Unity.MatchScoreFeed.TryScore(1 - team, out int enemyScore))
		{
			score = team == 0 ? MatchScoreboard.Current.BlueScore : MatchScoreboard.Current.RedScore;
			enemyScore = team == 0 ? MatchScoreboard.Current.RedScore : MatchScoreboard.Current.BlueScore;
		}

		int written = planner.Plan(
			team, _flags, _adjacency,
			new System.ReadOnlySpan<SquadInfo>(_squadInfo, 0, squadCount),
			score, enemyScore,
			new System.Span<SquadOrder>(_orders, 0, squadCount), Time.time);

		for (int i = 0; i < written; i++)
		{
			Apply(_squads[_orders[i].SquadIndex], _orders[i]);
		}

		Report(team, planner, written);
	}

	/// <summary>
	/// Folds the side's lone bots into squads near them before the plan is made (phase P29,
	/// <see cref="SquadRegroup"/>); true when any squad changed.
	/// </summary>
	private bool Regroup()
	{
		int count = Mathf.Min(_squads.Count, TeamPlanner.MaxSquads);
		for (int i = 0; i < count; i++)
		{
			_squadInfo[i] = Describe(_squads[i]);
		}

		int merges = _regroup.Plan(new System.ReadOnlySpan<SquadInfo>(_squadInfo, 0, count), _merges, Profile.RegroupRadius);
		for (int m = 0; m < merges; m++)
		{
			_squads[_merges[m].Into].Absorb(_squads[_merges[m].From]);
		}
		return merges > 0;
	}

	/// <summary>The side's squads: every squad with a live bot in it, once.</summary>
	private void CollectSquads(int team)
	{
		_squads.Clear();
		_seen.Clear();

		List<Actor> alive = ActorManager.AliveActorsOnTeam(team);
		for (int i = 0; i < alive.Count && _squads.Count < TeamPlanner.MaxSquads; i++)
		{
			Actor actor = alive[i];
			if (actor == null || !actor.aiControlled)
			{
				continue;
			}
			AiActorController ai = actor.controller as AiActorController;
			if (ai == null || !ai.enabled || ai.squad == null || ai.squad.members.Count == 0)
			{
				continue;
			}
			if (_seen.Add(ai.squad))
			{
				_squads.Add(ai.squad);
			}
		}
	}

	/// <summary>Who holds each flag, and how many enemies the side is in contact with round it.</summary>
	private void ReadFlags(int team)
	{
		List<Actor> ours = ActorManager.AliveActorsOnTeam(team);
		List<Actor> theirs = ActorManager.AliveActorsOnTeam(1 - team);

		for (int f = 0; f < _flags.Length; f++)
		{
			SpawnPoint point = _points[f];
			_flags[f].Owner = point != null ? point.owner : -1;
			_flags[f].Capturable = point is CapturePoint capture && capture.canBeCaptured;
			_flags[f].Position = ToCore(point != null ? point.transform.position : Vector3.zero);
			_flags[f].EnemiesInContact = 0;
			// Contested as every player's map shows it (phase P29): an enemy stood on it lately.
			// A base is never safe by SpawnPoint's own default and can never be taken, so only a
			// capture point can be contested.
			_flags[f].Contested = point is CapturePoint contested && !contested.IsSafe();
		}

		for (int e = 0; e < theirs.Count; e++)
		{
			Actor enemy = theirs[e];
			if (enemy == null)
			{
				continue;
			}
			Vector3 at = enemy.Position();
			bool known = InContact(at, ours);
			for (int f = 0; f < _flags.Length; f++)
			{
				SpawnPoint point = _points[f];
				if (point == null)
				{
					continue;
				}
				float distance = (point.transform.position - at).sqrMagnitude;
				float onFlag = point is CapturePoint capture ? capture.captureRange : 0f;
				if ((known && distance < ThreatRadius * ThreatRadius) || distance < onFlag * onFlag)
				{
					_flags[f].EnemiesInContact++;
				}
			}
		}
	}

	private static bool InContact(Vector3 enemy, List<Actor> ours)
	{
		for (int i = 0; i < ours.Count; i++)
		{
			if (ours[i] != null && (ours[i].Position() - enemy).sqrMagnitude < ContactRadius * ContactRadius)
			{
				return true;
			}
		}
		return false;
	}

	private static SquadInfo Describe(Squad squad)
	{
		AiActorController leader = squad.Leader();
		Vector3 at = leader != null && leader.actor != null ? leader.actor.Position() : Vector3.zero;

		return new SquadInfo
		{
			Id = squad.number,
			Position = ToCore(at),
			Size = squad.members.Count,
			InVehicle = squad.HasVehicle(),
			Engaged = squad.GetTarget() != null || squad.IsTakingFire(),
			Role = squad.commandRole,
			Flag = squad.commandFlag,
		};
	}

	/// <summary>Hands one squad its order, if it is a new one.</summary>
	private void Apply(Squad squad, in SquadOrder order)
	{
		if (!order.Changed)
		{
			return;
		}

		SpawnPoint target = order.Flag >= 0 && order.Flag < _points.Length ? _points[order.Flag] : null;
		Vector3 point = order.HasWaypoint ? ToUnity(order.Waypoint) : (target != null ? target.transform.position : Vector3.zero);

		// A squad in a fight finishes it: its leader's order tick picks the new order up after.
		bool applyNow = squad.Ready() && !squad.IsTakingFire() && squad.state != Squad.State.EnterVehicle;
		squad.Command(order.Role, target, order.Flag, point, order.Sneak, applyNow);
	}

	/// <summary>
	/// One log line per side when its plan changes shape, and once a minute regardless, so a server
	/// log shows what the bots were told to do.
	/// </summary>
	private void Report(int team, TeamPlanner planner, int squadCount)
	{
		int attack = 0, defend = 0, flank = 0, gather = 0, bots = 0;
		for (int i = 0; i < squadCount; i++)
		{
			bots += _squadInfo[i].Size;
			switch (_orders[i].Role)
			{
			case SquadRole.Attack: attack++; break;
			case SquadRole.Defend: defend++; break;
			case SquadRole.Flank: flank++; break;
			case SquadRole.Assemble: gather++; break;
			}
		}

		_log.Clear();
		_log.Append("[bots] team ").Append(team).Append(": ").Append(bots).Append(" bots in ")
			.Append(squadCount).Append(" squads, ").Append(planner.LastPosture)
			.Append(", ").Append(planner.LastObjectives).Append(" objective(s); squads attack ")
			.Append(attack).Append(", gather ").Append(gather).Append(", flank ").Append(flank).Append(", defend ").Append(defend)
			.Append(" (").Append(planner.LastDefenders).Append(" bots)");
		string summary = _log.ToString();

		// Where the squads came from, which changes on nearly every plan: kept out of the summary
		// the change test compares, so the line still only prints when the plan changes shape.
		int[] splits = Squad.Census.Splits[team];
		_log.Append("; squads formed ").Append(Squad.Census.Formed[team])
			.Append(" (").Append(Squad.Census.FormedAlone[team]).Append(" alone), split rogue ")
			.Append(splits[(int)Squad.SplitReason.Rogue]).Append(" crew ").Append(splits[(int)Squad.SplitReason.Crew])
			.Append(" full ").Append(splits[(int)Squad.SplitReason.VehicleFull]).Append(", left alone by a death ")
			.Append(Squad.Census.LeftAlone[team]).Append(", merged ").Append(Squad.Census.Merged[team])
			.Append(", reinforced by a spawn ").Append(Squad.Census.Reinforced[team])
			.Append(", respawned for a vehicle ").Append(Squad.Census.SpawnedForVehicle[team]);

		bool changed = summary != _lastSummary[team];
		if (!changed && Time.time - _lastLogged[team] < QuietLogPeriod)
		{
			return;
		}

		_lastSummary[team] = summary;
		_lastLogged[team] = Time.time;
		Debug.Log(_log.ToString());
	}

	private static Vec3 ToCore(Vector3 v) => new Vec3(v.x, v.y, v.z);

	private static Vector3 ToUnity(Vec3 v) => new Vector3(v.X, v.Y, v.Z);
}
