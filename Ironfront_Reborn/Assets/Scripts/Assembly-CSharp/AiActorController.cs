using System;
using System.Collections;
using System.Collections.Generic;
using Ironfront.Net.Replication.Ai;
using Ironfront.Net.Unity;
using Ironfront.Net.Unity.Bindings;
using Ironfront.Net.Unity.Server;
using Pathfinding;
using UnityEngine;

public class AiActorController : ActorController
{
	public struct AiParameters
	{
		public float LEAD_SWAY_MAGNITUDE;

		public float LEAD_NOISE_MAGNITUDE;

		public float SWAY_MAGNITUDE;

		public float ACQUIRE_TARGET_OFFSET_PER_METER;

		public float ACQUIRE_TARGET_DEPTH_EXTRA_OFFSET_PER_METER;

		public float ACQUIRE_TARGET_DURATION_BASE;

		public float ACQUIRE_TARGET_DURATION_PER_METER;

		public float AIM_BASE_SWAY;

		public float AIM_MAX_SWAY;

		public float VISIBILITY_MULTIPLIER;

		public float AI_FIRE_RECTANGLE_BOUND;

		public float TAKING_FIRE_REACTION_TIME;
	}

	private const float AI_TICK_PERIOD = 0.2f;

	private const float AI_KEEP_TARGET_TIME = 0.5f;

	private const float AI_ORDER_PERIOD = 0.5f;

	private const float AI_VEHICLE_PERIOD = 0.5f;

	private const float MAX_VEHICLE_DISTANCE = 150f;

	private const float SPRINT_DURATION_MIN = 3f;

	private const float SPRINT_DURATION_MAX = 6f;

	private const float SPRINT_COOLDOWN_MIN = 5f;

	private const float SPRINT_COOLDOWN_MAX = 11f;

	private const float NORMAL_WALK_SPEED = 3.2f;

	private const float SPRINT_SPEED = 5.5f;

	private const float HAS_TARGET_WALK_SPEED = 2f;

	private const float VEHICLE_STUCK_DISTANCE = 0.4f;

	private const float VEHICLE_STUCK_TIME = 1.5f;

	private const float VEHICLE_STUCK_RECOVER_TIME = 1f;

	private const float MAX_RECENT_ANTI_STUCK_EVENTS = 2f;

	private const float ANTI_STUCK_EVENT_LIFETIME = 30f;

	private const int CAR_UNEVEN_SURFACE_PENALTY = 100000;

	private const float CAR_TARGET_MAX_SPEED = 15f;

	private const float CAR_REVERSE_SPEED = 7f;

	private const int GRAPH_MASK_ON_FOOT = 1;

	private const int GRAPH_MASK_BOAT = 2;

	private const int GRAPH_MASK_CAR = 4;

	private const float HELICOPTER_TARGET_FLIGHT_HEIGHT_MIN = 30f;

	private const float HELICOPTER_TARGET_FLIGHT_HEIGHT_MAX = 60f;

	private const float HELICOPTER_HEIGHT_EXTRAPOLATION_TIME = 3f;

	private const float HELICOPTER_MAX_PITCH = 25f;

	private const float HELICOPTER_MAX_ROLL = 25f;

	private const float HELICOPTER_ATTACK_RANGE = 200f;

	private const float TANK_PROJECTED_DRIVING_MIN_DISTANCE = 3f;

	private const float TANK_PROJECTED_DRIVING_SPEED_GAIN = 1f;

	private const float CAR_PROJECTED_DRIVING_MIN_DISTANCE = 4f;

	private const float CAR_PROJECTED_DRIVING_SPEED_GAIN = 0.5f;

	private const float CAR_PREDICTION_PROJECTED_DRIVING_MIN_DISTANCE = 4f;

	private const float CAR_PREDICTION_PROJECTED_DRIVING_SPEED_GAIN = 3f;

	private const float CAR_TURN_MULTIPLIER = 5f;

	private const float TRANSPORT_EXIT_AND_WALK_MAX_DISTANCE = 40f;

	private const float AI_WEAPON_FAST_TICK_PERIOD = 0.05f;

	private const float AI_WEAPON_SLOW_TICK_PERIOD = 0.5f;

	private const float AI_WEAPON_SLOW_TICK_DISTANCE = 40f;

	public const float TAKING_FIRE_MAX_DISTANCE = 5f;

	private const float FOOT_BLOCK_SPHERECAST_RADIUS = 0.5f;

	private const float FOOT_CHECK_BLOCKER_AHEAD_RANGE = 2f;

	private const int FOOT_BLOCK_MASK = 4096;

	private const float VEHICLE_BLOCK_AHEAD_TIME = 1f;

	private const float VEHICLE_BLOCK_AVOID_MULTIPLIER = 0.3f;

	private const int VEHICLE_BLOCK_MASK = 256;

	private const float CAR_TURNING_SPEED_MULTIPLIER = 0.5f;

	private const float CAR_DRIVING_FORWARD_TURN_MULTIPLIER = 0.5f;

	private const float AI_MIN_SCAN_TIME = 0.8f;

	private const float AI_MAX_SCAN_TIME = 3f;

	private const float LOOK_FORWARD_CHANCE = 0.8f;

	private const float AI_FACE_HIGHLIGHTED_DISTANCE = 30f;

	private const float AI_FACE_HIGHLIGHTED_CHANCE = 0.2f;

	private const float AI_CHASE_EXTRAPOLATION_TIME = 2f;

	private const float AI_INVESTIGATE_MIN_TIME = 3f;

	private const float AI_UPDATE_CLOSE_ACTORS_TIME = 1f;

	private const float CLOSE_ACTORS_RANGE = 10f;

	private const float LOCAL_AVOIDANCE_MIN_DISTANCE = 1.5f;

	private const float LOCAL_AVOIDANCE_SPEED = 2f;

	private const int FRIENDLY_LAYER_MASK = 5376;

	private const int GROUND_LAYER_MASK = 1;

	private const float FATIGUE_GAIN = 0.04f;

	private const float FATIGUE_DRAIN = 0.4f;

	private const float AIM_SLERP_SPEED = 6f;

	private const float AIM_CONSTANT_SPEED = 5f;

	private const float MIN_GOTO_DELTA = 2f;

	private const int CAN_SEE_RAYCAST_SAMPLES = 3;

	private const float FOV_MIN_DOT = 0.1f;

	private const float WAYPOINT_COMPLETE_DISTANCE = 0.2f;

	private const float WAYPOINT_COMPLETE_DISTANCE_LQ = 1f;

	private const float WAYPOINT_COMPLETE_DISTANCE_VEHICLE = 2.5f;

	private const float WAYPOINT_COMPLETE_DISTANCE_VEHICLE_AQUATIC = 4f;

	private const float LEAN_SPEED = 2f;

	private const float EYE_HEIGHT = 0.2f;

	private const float MAX_ENTER_SEAT_DISTANCE = 4f;

	private const float SELECT_NON_FRONTLINE_SPAWN_CHANCE = 0.3f;

	private const float PLAYER_APPROACHING_DOT = 0.7f;

	private const float PLAYER_APPROACHING_LOOK_DOT = 0.9f;

	private const float PLAYER_APPROACHING_MAX_RANGE = 30f;

	private const float GET_IN_PLAYER_VEHICLE_RANGE = 8f;

	private static string[] primaryWeaponNames = new string[6] { "RK-44", "RK-44", "76 EAGLE", "SL-DEFENDER", "SIGNAL DMR", "RECON LRR" };

	private static string[] secondaryWeaponNames = new string[1] { "S-IND7" };

	private static string[] gearNames = new string[10] { "BEU AW1", "BEU AW1", "FRAG", "FRAG", "FRAG", "SPEARHEAD", "AMMO BAG", "AMMO BAG", "MEDIPACK", "MEDIPACK" };

	private static AiParameters PARAMETERS_EASY;

	private static AiParameters PARAMETERS_NORMAL;

	public Transform eyeTransform;

	public Transform weaponParent;

	private Quaternion facingDirection = Quaternion.identity;

	private Quaternion targetFacingDirection = Quaternion.identity;

	[NonSerialized]
	public Actor target;

	private bool hasPath;

	/// <summary>
	/// Walked the whole of its last path and is standing where it was sent (phase P29): waiting
	/// for the squad's next order, which is not the same thing as having lost its way.
	/// </summary>
	/// <remarks>
	/// The original told the two apart by nothing but a 3 s timeout, so every member that reached
	/// its spot more than three seconds before its leader reached his was split off as a squad of
	/// its own (<see cref="CreateRougeSquad"/>). Measured offline on Dustbowl, 16 bots a side:
	/// 162 such splits in fourteen minutes, and fifteen squads for fifteen bots.
	/// </remarks>
	private bool arrivedAtGoto;

	/// <summary>
	/// Said once already that this body is walking a path with no squad (<see cref="Velocity"/>);
	/// cleared when a squad takes it.
	/// </summary>
	private bool reportedPathWithoutSquad;

	private bool calculatingPath;

	private Seeker seeker;

	private Path path;

	private int waypoint;

	private Vector3 lastSeenTargetPosition = Vector3.zero;

	private Vector3 lastSeenTargetVelocity = Vector3.zero;

	private bool skipNextScan;

	private RadiusModifier radiusModifier;

	private AlternativePath alternatePathModifier;

	private bool fire;

	private float randomTimeOffset;

	private float fatigue;

	private Vector3 acquireTargetOffset;

	private Action acquireTargetAction = new Action(1f);

	private List<Actor> closeActors;

	private CoverPoint cover;

	private bool inCover;

	private Action stayInCoverAction = new Action(3f);

	private float lean;

	private Action takingFireAction = new Action(3f);

	// The goggles this bot wears at night (phase P32 Night Mode), made the first night it fights.
	private BotNightVision nightVision;

	/// <summary>Whether this bot has its night-vision goggles on (phase P32).</summary>
	public bool IsWearingNightVision => nightVision != null && nightVision.IsOn;

	/// <summary>How near its objective a bot puts its goggles on at night, in metres.</summary>
	private const float NightObjectiveMetres = 70f;

	[NonSerialized]
	public Vector3 takingFireDirection;

	private Vehicle targetVehicle;

	private bool forceAntiStuckReverse;

	private bool waitForPlayer;

	private int recentAntiStuckEvents;

	private bool canTurnCarTowardsWaypoint = true;

	private List<Vehicle> avoidedVehicles = new List<Vehicle>();

	private bool aquatic;

	private bool flying;

	private bool hasFlightTarget;

	private float helicopterTargetFlightHeight;

	private Vector3 flightTargetPosition;

	private Action helicopterAttackAction = new Action(4f);

	private Action helicopterAttackCooldownAction = new Action(8f);

	private Action helicopterTakeoffAction = new Action(2f);

	private Action helicopterNewOrderAction = new Action(10f);

	private Action sprintAction = new Action(1f);

	private Action sprintCooldownAction = new Action(4f);

	private Action ragdollAutokillAction = new Action(60f);

	private Action moveTimeoutAction = new Action(3f);

	private float smoothNoisePhase;

	[NonSerialized]
	public Squad squad;

	// Set when SpawnAt asked for the AI coroutines before this bot had a squad. See
	// StartAiCoroutines.
	private bool aiCoroutinesAwaitSquad;

	[NonSerialized]
	public bool squadLeader;

	private Vector3 lastWaypoint;

	private Vector3 lastGotoPoint;

	private bool blockerAhead;

	private Vector3 blockerPosition;

	// ---- combat tactics (phase P28, part 2; CombatRules holds the numbers) ----

	/// <summary>The enemy that last shot at this bot, and when: the first it answers.</summary>
	private Actor threatActor;

	private float threatActorTime = -100f;

	/// <summary>This bot's marker for cover found behind a vehicle, made on first use.</summary>
	private CoverPoint ownCoverSpot;

	/// <summary>
	/// Which way the bot faces in its cover and how it uses it -- judged against the enemy it hid
	/// from, where the original read the authored point's own facing and type.
	/// </summary>
	private Vector3 coverFacing;

	private CoverPoint.Type coverType;

	/// <summary>Metres to the current target, refreshed by AiTrack; infinite with none.</summary>
	private float targetDistance = float.PositiveInfinity;

	private Vector3 sideStepDirection;

	private Action sideStepAction = new Action(1f);

	private Action sideStepPauseAction = new Action(2f);

	/// <summary>Until when a hurt bot keeps to the cover it fell back to.</summary>
	private float fallingBackUntil;

	private Action fallBackCooldownAction = new Action(CombatRules.FallBackCooldownSeconds);

	public static AiParameters PARAMETERS
	{
		get
		{
			if (OptionsUi.GetOptions().difficulty == 0)
			{
				return PARAMETERS_EASY;
			}
			return PARAMETERS_NORMAL;
		}
	}

	public static void SetupParameters()
	{
		PARAMETERS_EASY = default(AiParameters);
		PARAMETERS_EASY.LEAD_SWAY_MAGNITUDE = 0.3f;
		PARAMETERS_EASY.LEAD_NOISE_MAGNITUDE = 0.1f;
		PARAMETERS_EASY.SWAY_MAGNITUDE = 1.5f;
		PARAMETERS_EASY.ACQUIRE_TARGET_OFFSET_PER_METER = 0.2f;
		PARAMETERS_EASY.ACQUIRE_TARGET_DEPTH_EXTRA_OFFSET_PER_METER = 1f;
		PARAMETERS_EASY.ACQUIRE_TARGET_DURATION_BASE = 2f;
		PARAMETERS_EASY.ACQUIRE_TARGET_DURATION_PER_METER = 0.02f;
		PARAMETERS_EASY.AIM_BASE_SWAY = 0.01f;
		PARAMETERS_EASY.AIM_MAX_SWAY = 0.1f;
		PARAMETERS_EASY.AI_FIRE_RECTANGLE_BOUND = 2.5f;
		PARAMETERS_EASY.VISIBILITY_MULTIPLIER = 1f;
		PARAMETERS_EASY.TAKING_FIRE_REACTION_TIME = 0.5f;
		PARAMETERS_NORMAL = default(AiParameters);
		PARAMETERS_NORMAL.LEAD_SWAY_MAGNITUDE = 0.1f;
		PARAMETERS_NORMAL.LEAD_NOISE_MAGNITUDE = 0.05f;
		PARAMETERS_NORMAL.SWAY_MAGNITUDE = 0.5f;
		PARAMETERS_NORMAL.ACQUIRE_TARGET_OFFSET_PER_METER = 0.1f;
		PARAMETERS_NORMAL.ACQUIRE_TARGET_DEPTH_EXTRA_OFFSET_PER_METER = 0.5f;
		PARAMETERS_EASY.ACQUIRE_TARGET_DURATION_BASE = 1f;
		PARAMETERS_NORMAL.ACQUIRE_TARGET_DURATION_PER_METER = 0.01f;
		PARAMETERS_NORMAL.AIM_BASE_SWAY = 0.002f;
		PARAMETERS_NORMAL.AIM_MAX_SWAY = 0.05f;
		PARAMETERS_NORMAL.AI_FIRE_RECTANGLE_BOUND = 1f;
		PARAMETERS_NORMAL.VISIBILITY_MULTIPLIER = 2f;
		PARAMETERS_NORMAL.TAKING_FIRE_REACTION_TIME = 0.15f;
	}

	// Bot level-of-detail. Null on every bot that has no BotLodGate attached, which is all of
	// them unless a measurement run adds one -- see AiWorkAllowed below.
	private BotLodGate lodGate;

	// The one question the LOD gate answers, asked at the head of Update and of all eight AI
	// coroutines. No gate means no gating: a bot without the component behaves exactly as it
	// did before this seam existed, which is what makes the nine call sites safe to land ahead
	// of any measurement. Unity's overloaded == also makes a destroyed gate read as null here,
	// so a bot outliving its gate keeps thinking rather than freezing.
	/// <summary>
	/// Whether this brain should think this tick. The LOD gate, and — ledger <b>X-57</b> — whether
	/// this controller is steering this body at all.
	/// </summary>
	/// <remarks>
	/// <para>
	/// <b><c>enabled</c> is checked here because Unity does not stop a coroutine when a
	/// MonoBehaviour is disabled.</b> <c>IAiDriver.Suspend</c> sets <c>enabled = false</c> when a
	/// networked player claims this body, which stops <c>Update</c> and nothing else: all eight AI
	/// coroutines kept running on a body the server was driving. The suspension was half a
	/// suspension, and the half that was missing is the one that carries state.
	/// </para>
	/// <para>
	/// <b>What that cost.</b> <c>AiVehicle</c> reached <c>PushAntiStuckEvent</c>, which dereferences
	/// <c>squad.squadVehicle</c> — and a player-slot body has never had a squad. That is X-45's
	/// defect at a site X-45 did not reach, and it only became reachable once X-46 let a networked
	/// player actually drive: <c>artifacts/lane-a/o6/o6-combat-01</c>, 5 throws in 150 s, the only
	/// null-reference site left after O6's first two fixes.
	/// </para>
	/// <para>
	/// <b>Here rather than at <c>PushAntiStuckEvent</c>.</b> The same coroutine calls
	/// <c>squad.ExitVehicle()</c> and <c>squad.MoveTo()</c> two branches away, and seven other
	/// coroutines are running on the same suspended brain. Guarding the one site that happened to
	/// throw would leave the rest, which is the difference between fixing a suspension and muting a
	/// stack trace.
	/// </para>
	/// </remarks>
	private bool AiWorkAllowed()
	{
		if (!base.enabled)
		{
			return false;
		}
		return lodGate == null || lodGate.AllowAiWork;
	}

	// What a gated-off coroutine parks on. One shared instance because WaitForSeconds is
	// immutable and stateless once constructed -- allocating one per skipped iteration, at
	// eight coroutines x 40 bots, is the allocation this whole seam exists to avoid.
	//
	// 0.05f, not `yield return null`, and not the 0.1f the client track suggested comparing against.
	// Round 9 measured the skip path re-polling every frame at 0.404 ms/frame across 40 bots
	// doing no work -- 326 AI marker calls per frame with everything skipped against 103 with
	// everything working, so a skipped bot was entered three times as often as a busy one.
	// That polling ate roughly half the saving: 38.6 % of bot-ticks skipped bought only an
	// 18.5 % drop in AI cost. At 60 fps this cuts coroutine re-entry by ~2/3.
	//
	// The cost is resume latency, and 0.05f is where it stops mattering: worst case ~1.5 ticks
	// at 30 Hz before a bot re-entering interest thinks again. 0.1f is cheaper still but is
	// three full ticks, which is long enough to read as a bot standing frozen when a player
	// comes around a corner -- and a bot is only ever gated off while no human can see it, so
	// the frame it becomes visible is exactly the frame the latency is on show.
	private static readonly WaitForSeconds LodSkipWait = new WaitForSeconds(0.05f);

	private void Awake()
	{
		lodGate = GetComponent<BotLodGate>();
		seeker = GetComponent<Seeker>();
		Seeker obj = seeker;
		obj.pathCallback = (OnPathDelegate)Delegate.Combine(obj.pathCallback, new OnPathDelegate(OnPathComplete));
		randomTimeOffset = UnityEngine.Random.Range(0f, 10f);
		radiusModifier = GetComponent<RadiusModifier>();
		alternatePathModifier = GetComponent<AlternativePath>();
		smoothNoisePhase = UnityEngine.Random.Range(0f, (float)Math.PI * 2f);
		helicopterTargetFlightHeight = UnityEngine.Random.Range(30f, 60f);
	}

	/// <summary>
	/// The enemies worth a look this round, most urgent first: nearest, less the head start
	/// <see cref="CombatRules.TargetScore"/> gives one that shot at this bot or stands on its
	/// squad's flag (phase P28), at most <see cref="CombatRules.MaxTargetCandidates"/>. The
	/// original sorted every enemy by distance alone.
	/// </summary>
	/// <remarks>
	/// A new list each call, as the original's was: AiTarget walks it across yields, and a shared
	/// buffer would be rewritten under a second walker.
	/// </remarks>
	private List<Actor> FindPotentialTargets()
	{
		int team = ((actor.team == 0) ? 1 : 0);
		List<Actor> enemies = ActorManager.AliveActorsOnTeam(team);
		List<Actor> list = new List<Actor>(CombatRules.MaxTargetCandidates);
		Span<float> scores = stackalloc float[CombatRules.MaxTargetCandidates];
		Vector3 position = actor.Position();
		Actor shooter = RecentShooter();
		bool hasObjective = squad != null && squad.commandTarget != null;
		Vector3 objective = hasObjective ? squad.commandTarget.transform.position : Vector3.zero;
		float objectiveRadius = CombatRules.ObjectiveRadius * CombatRules.ObjectiveRadius;
		for (int i = 0; i < enemies.Count; i++)
		{
			Actor item = enemies[i];
			if (item == null || !HasEffectiveWeaponAgainst(item) || (item.IsSeated() && item.seat.vehicle.burning))
			{
				continue;
			}
			Vector3 at = item.Position();
			bool onObjective = hasObjective && (at - objective).sqrMagnitude < objectiveRadius;
			float score = CombatRules.TargetScore(Vector3.Distance(at, position), item.fallenOver, item == shooter, onObjective);
			InsertByScore(list, scores, item, score);
		}
		return list;
	}

	private static void InsertByScore(List<Actor> list, Span<float> scores, Actor item, float score)
	{
		int capacity = scores.Length;
		int count = list.Count;
		if (count == capacity)
		{
			if (score >= scores[capacity - 1])
			{
				return;
			}
			list.RemoveAt(capacity - 1);
			count--;
		}
		int at = count;
		while (at > 0 && scores[at - 1] > score)
		{
			scores[at] = scores[at - 1];
			at--;
		}
		scores[at] = score;
		list.Insert(at, item);
	}

	/// <summary>The enemy that shot at this bot within <see cref="CombatRules.ShooterMemorySeconds"/>, if still alive.</summary>
	private Actor RecentShooter()
	{
		if (threatActor == null || threatActor.dead || Time.time - threatActorTime > CombatRules.ShooterMemorySeconds)
		{
			return null;
		}
		return threatActor;
	}

	/// <summary>
	/// Remembers who shot at this bot: a hit (from <c>Actor.DamageAttributed</c>) or a near miss
	/// (from <c>ActorManager</c>'s incoming-fire warning). A teammate's stray round is not a threat.
	/// </summary>
	public void NoteAttacker(Actor attacker)
	{
		if (attacker == null || attacker == actor || attacker.dead || (actor != null && attacker.team == actor.team))
		{
			return;
		}
		threatActor = attacker;
		threatActorTime = Time.time;
	}

	/// <summary>
	/// Where to judge cover against: the eye of the enemy that shot at this bot, else its target,
	/// else a point 40 m along <paramref name="direction"/> -- the incoming fire's bearing.
	/// </summary>
	private Vector3 ThreatEye(Vector3 direction)
	{
		Actor shooter = RecentShooter();
		if (shooter != null)
		{
			return shooter.CenterPosition() + Vector3.up * 0.5f;
		}
		if (HasTarget())
		{
			return target.CenterPosition() + Vector3.up * 0.5f;
		}
		Vector3 flat = new Vector3(direction.x, 0f, direction.z);
		if (flat.sqrMagnitude < 0.01f)
		{
			flat = FacingDirection();
			flat.y = 0f;
		}
		return actor.CenterPosition() + flat.normalized * 40f + Vector3.up * 0.5f;
	}

	/// <summary>
	/// Where the enemy will come from, seen from <paramref name="point"/>: a known threat, or the
	/// nearest flag the other side holds. False when neither is known.
	/// </summary>
	private bool ThreatEyeAround(Vector3 point, out Vector3 eye)
	{
		if (RecentShooter() != null || HasTarget() || IsTakingFire())
		{
			eye = ThreatEye(takingFireDirection);
			return true;
		}
		eye = Vector3.zero;
		SpawnPoint[] points = ActorManager.instance != null ? ActorManager.instance.spawnPoints : null;
		if (points == null)
		{
			return false;
		}
		float best = float.PositiveInfinity;
		foreach (SpawnPoint spawnPoint in points)
		{
			if (spawnPoint == null || spawnPoint.owner < 0 || spawnPoint.owner == actor.team)
			{
				continue;
			}
			float distance = (spawnPoint.transform.position - point).sqrMagnitude;
			if (distance < best)
			{
				best = distance;
				eye = spawnPoint.transform.position + Vector3.up * 1.5f;
			}
		}
		return best < float.PositiveInfinity;
	}

	/// <summary>
	/// Takes the best cover round <paramref name="origin"/> against <paramref name="threatEye"/>,
	/// claiming it; false leaves this bot's cover state as it was.
	/// </summary>
	private bool TakeLevelCover(Vector3 origin, Vector3 threatEye, float radius, bool fallingBack)
	{
		if (!BotCover.Find(origin, threatEye, radius, fallingBack, out BotCover.Pick pick))
		{
			return false;
		}
		CoverPoint point = pick.Authored;
		if (point == null)
		{
			if (ownCoverSpot == null)
			{
				ownCoverSpot = BotCover.NewBotSpot();
			}
			point = ownCoverSpot;
			point.transform.position = pick.Spot;
		}
		Vector3 facing = threatEye - pick.Spot;
		facing.y = 0f;
		ClaimCover(point, facing.sqrMagnitude > 0.01f ? facing.normalized : point.transform.forward, BotCover.TypeFor(pick.Fit));
		return true;
	}

	private void ClaimCover(CoverPoint point, Vector3 facing, CoverPoint.Type type)
	{
		if (cover != null && cover != point)
		{
			cover.taken = false;
		}
		cover = point;
		cover.taken = true;
		coverFacing = facing;
		coverType = type;
	}

	/// <summary>Whether this bot is hurt and keeping to the cover it fell back to.</summary>
	public bool IsFallingBack()
	{
		return Time.time < fallingBackUntil && !actor.dead;
	}

	/// <summary>
	/// A badly hurt bot in a fight breaks off to the nearest spot that hides it, preferring ground
	/// away from the enemy, and stays there a while (phase P28). The original fought on in the open
	/// until it died.
	/// </summary>
	private void FallBackIfHurt()
	{
		if (IsFallingBack() || !fallBackCooldownAction.TrueDone())
		{
			return;
		}
		bool onFoot = !actor.IsSeated() && !actor.fallenOver && !actor.inWater;
		if (!CombatRules.ShouldFallBack(actor.health, HasTarget() || IsTakingFire(), InCover(), onFoot))
		{
			return;
		}
		fallBackCooldownAction.Start();
		if (!TakeLevelCover(actor.Position(), ThreatEye(takingFireDirection), CombatRules.FallBackSearchRadius, true))
		{
			return;
		}
		inCover = false;
		fallingBackUntil = Time.time + CombatRules.FallBackHoldSeconds;
		CancelPath();
		Goto(cover.transform.position);
		// A sprint whether or not the squad is sneaking: this is a bot running for its life.
		sprintAction.StartLifetime(UnityEngine.Random.Range(3f, 6f));
	}

	/// <summary>
	/// What a bot standing in the open does each look: crouch for a far shot, or step sideways
	/// when shot at or close in (phase P28). The original stood still.
	/// </summary>
	private void UpdateOpenGround()
	{
		if (!sideStepAction.TrueDone())
		{
			return;
		}
		bool onFoot = !actor.IsSeated() && !actor.fallenOver && !actor.inWater;
		if (!CombatRules.InTheOpen(onFoot, hasPath, HasCover()))
		{
			return;
		}
		if (CombatRules.InTheOpenMove(HasTarget(), targetDistance, IsTakingFire()) != OpenGroundMove.SideStep
			|| !sideStepPauseAction.TrueDone())
		{
			return;
		}
		Vector3 toThreat = HasTarget() ? target.Position() - actor.Position() : takingFireDirection;
		toThreat.y = 0f;
		if (toThreat.sqrMagnitude < 0.01f)
		{
			return;
		}
		Vector3 side = Vector3.Cross(Vector3.up, toThreat.normalized);
		if (UnityEngine.Random.Range(0, 2) == 0)
		{
			side = -side;
		}
		if (!SideStepClear(side))
		{
			side = -side;
			if (!SideStepClear(side))
			{
				sideStepPauseAction.StartLifetime(CombatRules.SideStepPauseMinSeconds);
				return;
			}
		}
		float step = UnityEngine.Random.Range(CombatRules.SideStepMinSeconds, CombatRules.SideStepMaxSeconds);
		sideStepDirection = side;
		sideStepAction.StartLifetime(step);
		sideStepPauseAction.StartLifetime(step + UnityEngine.Random.Range(CombatRules.SideStepPauseMinSeconds, CombatRules.SideStepPauseMaxSeconds));
	}

	/// <summary>Whether a step along <paramref name="side"/> meets no wall, no drop and no water.</summary>
	private bool SideStepClear(Vector3 side)
	{
		Vector3 feet = actor.Position();
		float reach = CombatRules.SideStepClearance;
		if (Physics.SphereCast(feet + Vector3.up * 0.9f, 0.3f, side, out RaycastHit _, reach, CoverProbe.ObstacleMask, QueryTriggerInteraction.Ignore))
		{
			return false;
		}
		return CoverProbe.Ground(feet + side * reach, feet.y, out Vector3 _);
	}


	/// <summary>
	/// Starts the eight AI coroutines, or defers them until <see cref="AssignedToSquad"/> when
	/// this bot has no squad yet.
	/// </summary>
	/// <remarks>
	/// <para>
	/// <b>The coroutines assume a squad</b> -- <c>squad.GetTarget()</c> in AiTarget,
	/// <c>squad.MemberNeedsResupply()</c> in AiWeapon and dozens more -- and in the original
	/// that always held: <c>ActorManager.SpawnActorList</c> placed every body and formed every
	/// squad in the same frame, and each coroutine yields before its first read. This project
	/// spreads a wave's placements over frames (<c>SPAWN_WORK_BUDGET_SECONDS</c>) and still forms
	/// the squads at the end, so a bot placed in an early frame ran with <c>squad == null</c> for
	/// a few frames. On the server every bot release threw one NullReferenceException from
	/// whichever coroutine got there first, and that coroutine was dead for the bot's whole
	/// life: a bot whose AiTarget died never picked a target again until it respawned.
	/// </para>
	/// <para>
	/// Deferring restores the original order -- squad first, then the coroutines -- without
	/// touching the time slicing. The spawn wave assigns a squad to every bot it placed that is
	/// still alive, so nothing waits indefinitely; a bot that dies while waiting is skipped by
	/// that wave and deferred again by the next one.
	/// </para>
	/// </remarks>
	private void StartAiCoroutines()
	{
		if (squad == null)
		{
			aiCoroutinesAwaitSquad = true;
			return;
		}
		aiCoroutinesAwaitSquad = false;
		StartCoroutine(AiBlocked());
		StartCoroutine(AiVehicle());
		StartCoroutine(AiOrders());
		StartCoroutine(AiTarget());
		StartCoroutine(AiWeapon());
		StartCoroutine(AiTrack());
		StartCoroutine(AiScan());
		StartCoroutine(AiTrackClosestActors());
	}

	private IEnumerator AiBlocked()
	{
		yield return new WaitForSeconds(UnityEngine.Random.Range(0.2f, 0.4f));
		Collider[] colliders = new Collider[128];
		while (true)
		{
			// LOD gate, 1 of 8. Parks on LodSkipWait rather than `yield return null`: see the
			// field for why a per-frame re-poll cost about half of what the gate saved.
			if (!AiWorkAllowed())
			{
				yield return LodSkipWait;
				continue;
			}
			if (hasPath)
			{
				Ray ray = new Ray(base.actor.CenterPosition(), GetWaypointDelta());
				blockerAhead = false;
				if (base.actor.IsSeated())
				{
					Vehicle vehicle = base.actor.seat.vehicle;
					if (vehicle.HasBlockSensor())
					{
						int nHits = vehicle.BlockTest(colliders, 1f, 256);
						for (int i = 0; i < nHits; i++)
						{
							Collider collider = colliders[i];
							Hurtable hurtable = collider.GetComponent<Hitbox>().parent;
							blockerAhead = hurtable.team == base.actor.team;
							if (blockerAhead)
							{
								Actor actor = hurtable as Actor;
								if (actor != null)
								{
									blockerPosition = actor.Position();
								}
								break;
							}
						}
					}
				}
			}
			yield return new WaitForSeconds(0.2f);
		}
	}

	/// <summary>
	/// Whether a living teammate-player is short of ammo and close enough to hand a box to.
	/// </summary>
	/// <remarks>
	/// This and <see cref="PlayerWantsHealthNearby"/> replace four copies of the same compound
	/// expression, each of which dereferenced <c>ActorManager.instance.player</c> three or four
	/// times. There is no player on a dedicated server, so every one of those copies threw --
	/// and the outer condition reaches them whenever the squad itself does not need resupply,
	/// which is most of the time.
	/// </remarks>
	private bool PlayerWantsAmmoNearby()
	{
		Actor player = ActorManager.Player;
		return player != null && !player.dead && actor.team == player.team && player.needsResupply
			&& Vector3.Distance(player.transform.position, base.transform.position) < 10f;
	}

	/// <summary>Whether a living teammate-player is hurt and close enough to heal.</summary>
	private bool PlayerWantsHealthNearby()
	{
		Actor player = ActorManager.Player;
		return player != null && !player.dead && actor.team == player.team && player.health < 80f
			&& Vector3.Distance(player.transform.position, base.transform.position) < 10f;
	}

	private bool PlayerIsApproaching()
	{
		// No player, nobody approaching. Today this is unreachable on a server because the
		// only caller first compares against FpsActorController.playerTeam, which stays -1
		// there -- an accidental guard, and not one to depend on.
		if (FpsActorController.instance == null)
		{
			return false;
		}
		Actor actor = FpsActorController.instance.actor;
		if (!actor.fallenOver && !actor.IsSeated())
		{
			Vector3 vector = actor.Position();
			Vector3 normalized = (base.actor.Position() - vector).normalized;
			return Vector3.Dot(normalized, actor.controller.FacingDirection()) > 0.9f && Vector3.Dot(normalized, actor.Velocity().normalized) > 0.7f && Vector3.Distance(vector, base.actor.Position()) < 30f;
		}
		return false;
	}

	private IEnumerator AiVehicle()
	{
		yield return new WaitForSeconds(UnityEngine.Random.Range(0.5f, 1f));
		Vector3 lastSampledVehiclePosition = Vector3.zero;
		float lastSampleTime = 0f;
		while (true)
		{
			// LOD gate, 2 of 8.
			if (!AiWorkAllowed())
			{
				yield return LodSkipWait;
				continue;
			}
			if (actor.IsSeated() && actor.seat.vehicle != null && actor.IsDriver())
			{
				Type vehicleType = actor.seat.vehicle.GetType();
				if (IsSquadLeader() && vehicleType != typeof(Boat) && WaterLevel.InWater(actor.seat.vehicle.transform.position))
				{
					actor.seat.vehicle.stuck = true;
					squad.ExitVehicle();
					if (hasPath)
					{
						squad.MoveTo(lastGotoPoint);
					}
				}
				else if (hasPath)
				{
					forceAntiStuckReverse = false;
					bool inNonAirVehicle = vehicleType == typeof(Car) || vehicleType == typeof(Boat) || vehicleType == typeof(Tank);
					waitForPlayer = inNonAirVehicle && !actor.seat.vehicle.IsFull() && actor.team == FpsActorController.playerTeam && PlayerIsApproaching();
					if (vehicleType == typeof(Car))
					{
						Vector3 waypointDelta = GetWaypointDelta();
						Car car = (Car)actor.seat.vehicle;
						canTurnCarTowardsWaypoint = car.CanTurnTowards(waypointDelta);
					}
					if (!LastWaypoint() && inNonAirVehicle)
					{
						Vector3 upcomingDeltaWaypointFlat = GetUpcomingBetweenWaypointsDelta().ToGround();
						Vector3 deltaWaypointFlat = GetWaypointDelta().ToGround();
						if (upcomingDeltaWaypointFlat == Vector3.zero)
						{
							waypoint++;
						}
					}
					Vector3 newPosition = actor.seat.vehicle.transform.position;
					// A tank holding its standoff is stopped on purpose, not stuck (phase P28).
					bool holdingStandoff = vehicleType == typeof(Tank) && VehicleRules.HoldStandoff(VehicleKind.Armour, HasTarget(), targetDistance);
					if (holdingStandoff || Vector3.Distance(newPosition, lastSampledVehiclePosition) > 0.4f)
					{
						lastSampledVehiclePosition = newPosition;
						lastSampleTime = Time.time;
					}
					else if (Time.time > lastSampleTime + 1.5f)
					{
						if (vehicleType == typeof(Boat))
						{
							actor.seat.vehicle.stuck = true;
							squad.ExitVehicle();
							squad.MoveTo(lastGotoPoint);
						}
						else if (vehicleType == typeof(Car) || vehicleType == typeof(Tank))
						{
							PushAntiStuckEvent();
							forceAntiStuckReverse = true;
							yield return new WaitForSeconds(1f);
							forceAntiStuckReverse = false;
							yield return new WaitForSeconds(1f);
							if (!actor.IsSeated())
							{
								continue;
							}
							RecalculatePath();
							lastSampleTime = Time.time;
						}
					}
				}
				if (vehicleType == typeof(Helicopter))
				{
					if (!squad.AllSeated())
					{
						helicopterTakeoffAction.Start();
					}
					if (HasTarget() && Vector3.Dot(actor.seat.vehicle.transform.forward, target.CenterPosition() - actor.seat.vehicle.transform.position) > 0f && helicopterAttackAction.TrueDone() && helicopterAttackCooldownAction.TrueDone() && Vector3.Distance(base.transform.position, target.transform.position) < 200f)
					{
						helicopterAttackAction.Start();
						helicopterAttackCooldownAction.Start();
					}
				}
			}
			if (actor.CanEnterSeat() && (!actor.fallenOver || actor.inWater) && HasTargetVehicle() && Vector3.Distance(actor.CenterPosition(), targetVehicle.transform.position) < 4f)
			{
				CancelPath();
				if (!targetVehicle.IsFull())
				{
					actor.EnterSeat(targetVehicle.GetEmptySeat());
				}
			}
			else if (HasTargetVehicle() && !actor.IsSeated() && !hasPath && !calculatingPath)
			{
				// Arrived where the vehicle was, and it has moved since -- driven a few metres by
				// the squad's own driver, rolled, pushed. Go to where it is now. The member used to
				// stand on the old spot for good; Squad.BoardingFailed decides when to stop chasing.
				Goto(targetVehicle.transform.position);
			}
			yield return new WaitForSeconds(0.5f);
		}
	}

	/// <summary>
	/// Marks the vehicle this body is driving as stuck and walks the squad out of it. Ledger
	/// <b>X-60</b>.
	/// </summary>
	/// <remarks>
	/// <para>
	/// <b>X-60's filed cause was wrong, and the branch above says so.</b> It was filed as "a
	/// squadless body with an enabled controller reaches here and dereferences a null squad",
	/// with the candidate fix of gating <c>AiWorkAllowed()</c> on having a squad. But this
	/// method has one caller — the Car/Tank arm of <c>AiVehicle</c> — and that arm is entered
	/// only after <c>IsSquadLeader()</c>, which is <c>squad.Leader() == this</c> with no null
	/// guard. A null <c>squad</c> throws THERE and never arrives here. <c>AiOrders</c> would
	/// have thrown on <c>squad.Update()</c> twice a second besides, and no artifact carries
	/// either site. <c>squad</c> is not the null.
	/// </para>
	/// <para>
	/// <b><c>squad.squadVehicle</c> is.</b> It is written only by <c>Squad.EnterVehicle</c> and
	/// <c>Squad.SetAlreadyInVehicle</c>, so a squad whose member boarded on its own has none —
	/// and <c>AiVehicle</c>'s own tail boards exactly that way,
	/// <c>actor.EnterSeat(targetVehicle.GetEmptySeat())</c>, with no squad order behind it. That
	/// member can then be driving, get stuck three times, and dereference a vehicle its squad
	/// never took. It needs a lone boarder AND a stuck vehicle, which is the intermittency the
	/// counts show: 5, 3, 2 and five zeroes across the eight Combat runs on record.
	/// </para>
	/// <para>
	/// <b>So this is a wrong reference corrected, not a null check added.</b> The vehicle that
	/// is stuck is the one this body is sitting in, which is what the Boat arm of the same
	/// coroutine already marks. The squad is still ordered out of it, because the squad is not
	/// the thing that was missing.
	/// </para>
	/// </remarks>
	private void PushAntiStuckEvent()
	{
		if ((float)recentAntiStuckEvents > 2f)
		{
			if (actor.IsSeated() && actor.seat.vehicle != null)
			{
				actor.seat.vehicle.stuck = true;
			}
			else
			{
				// Unreachable through the one caller, which enters only for a seated driver.
				// Reported rather than skipped: a body pushing an anti-stuck event from outside
				// a vehicle is a state nothing has explained, and X-59 is what a quiet fallback
				// buys.
				Debug.LogError(
					$"[ai] '{base.name}' pushed an anti-stuck event without driving anything, "
					+ "so the caller's seated-driver precondition no longer holds. Nothing was "
					+ "marked stuck; the squad is still being walked out.");
			}

			squad.ExitVehicle();
			squad.MoveTo(lastGotoPoint);
			recentAntiStuckEvents = 0;
			CancelInvoke("PopAntiStuckEvent");
		}
		recentAntiStuckEvents++;
		Invoke("PopAntiStuckEvent", 30f);
	}

	private void PopAntiStuckEvent()
	{
		recentAntiStuckEvents--;
	}

	/// <summary>
	/// Whether a bot standing without a path is lost, and gets split off as a squad of its own.
	/// </summary>
	/// <remarks>
	/// Not while its squad is dug in (phase P28): holding is the order then, cover or no cover. A
	/// squad now holds its ground while it is still fighting and defenders hold a flag for minutes,
	/// and every member that found no cover point stood pathless and was split off three seconds
	/// later -- a live match on Dustbowl ended with sixteen bots in sixteen squads.
	/// </remarks>
	private bool ShouldHavePath()
	{
		return (!actor.IsSeated() || actor.IsDriver()) && !inCover && squad.hasAssignedOrder && squad.state != Squad.State.DigIn;
	}

	private void CreateRougeSquad()
	{
		List<AiActorController> list = new List<AiActorController>(1);
		list.Add(this);
		squad.SplitSquad(list, Squad.SplitReason.Rogue);
		moveTimeoutAction.Start();
	}

	private IEnumerator AiOrders()
	{
		yield return new WaitForSeconds(UnityEngine.Random.Range(0.5f, 1f));
		while (true)
		{
			// LOD gate, 3 of 8.
			if (!AiWorkAllowed())
			{
				yield return LodSkipWait;
				continue;
			}
			// A member with no path goes its own way only when it never got where it was sent; one
			// that arrived waits for its squad (phase P29). A bot alone in its squad has nowhere to
			// split to: "splitting" it made a new squad of the same one bot, which dropped the
			// commander's order every few seconds while its search kept failing.
			if (!hasPath && !arrivedAtGoto && ShouldHavePath() && moveTimeoutAction.TrueDone() && squad.members.Count > 1)
			{
				CreateRougeSquad();
			}
			FallBackIfHurt();
			if (IsSquadLeader())
			{
				squad.Update();
				squad.UpdateCommandProgress();
				if (actor.IsSeated() && flying && helicopterNewOrderAction.Done())
				{
					squad.NewAttackOrder();
					helicopterNewOrderAction.Start();
				}
				if (actor.IsSeated() && actor.seat.vehicle.exitWhenTakingFire && squad.IsTakingFire() && Vector3.Distance(actor.Position(), lastGotoPoint) < 40f)
				{
					if (squad.squadVehicle != null)
					{
						squad.squadVehicle.MarkTakingFire();
					}
					squad.ExitVehicle();
				}
			}
			if (IsSquadLeader() && squad.Ready())
			{
				if (squad.BoardingFailed())
				{
					squad.GiveUpBoarding();
					squad.NewAttackOrder();
				}
				if (!squad.HasVehicle() && squad.state == Squad.State.Moving && FpsActorController.instance != null)
				{
					Actor playerActor = FpsActorController.instance.actor;
					if (playerActor.IsSeated() && !playerActor.seat.vehicle.IsFull() && playerActor.seat.vehicle.HasUnclaimedSeats() && Vector3.Distance(playerActor.Position(), base.transform.position) < 8f)
					{
						squad.EnterVehicle(playerActor.seat.vehicle);
					}
				}
				if (squad.HasVehicle() && squad.squadVehicle.burning)
				{
					squad.ExitVehicle();
				}
				if (!actor.fallenOver && squad.HasVehicle() && squad.AllSeated() && !squad.squadVehicle.HasDriver())
				{
					actor.SwitchSeat(0);
				}
				if (!squad.HasVehicle() && squad.state != Squad.State.DigIn && squad.IsTakingFire() && !actor.inWater)
				{
					// The bearing of the member actually under fire (phase P28): the original used
					// the leader's own, stale or zero whenever the shots were aimed at someone else.
					//
					// Not while the leader swims: a swimmer has put its gun away, the water has no
					// cover, and a squad that dug in there stayed under fire with nothing to shoot
					// back with (bot soak, Forest Lake, 9 of 16 long swims). It swims on to land.
					squad.DigInTowards(squad.TakingFireDirection(takingFireDirection));
				}
				else if (!squad.IsTakingFire() && !squad.HoldingCover() && !actor.IsPassenger() && squad.TryGoResupply(hasPath))
				{
					// Short of ammunition or hurt, with a cache of this side's in reach: on the way
					// there, or standing at it (owner request 2026-10-03). The order resumes once the
					// need is met or the trip runs out of time.
				}
				else if (!squad.IsTakingFire() && !squad.HoldingCover())
				{
					// A squad firing from cover finishes the exchange before it moves (phase P28):
					// the original walked out into the open three seconds after the last shot came
					// near it, mid-fight.
					//
					// The team commander's order comes first (phase P28): a squad sent round the side
					// or told to hold a flag does not break off for every flag it passes, the way
					// the original's "take whatever is nearest" did.
					SpawnPoint closestSpawnPoint = squad.ClosestSpawnPoint();
					if ((!squad.HasTargetSpawnPoint() || squad.targetSpawnPoint != closestSpawnPoint) && squad.ShouldGotoSpawnPoint(closestSpawnPoint) && squad.MayDivertTo(closestSpawnPoint))
					{
						squad.AttackSpawnPoint(closestSpawnPoint);
					}
					else if (squad.HasTargetSpawnPoint() && squad.targetSpawnPoint == closestSpawnPoint && !squad.ShouldGotoSpawnPoint(closestSpawnPoint))
					{
						squad.FollowCommand();
					}
					else if (!hasPath && !hasFlightTarget && squad.state != Squad.State.EnterVehicle)
					{
						bool enteringVehicle = false;
						if (!squad.HasVehicle())
						{
							List<Vehicle> nearbyVehicles = NearbyNonFullVehicles();
							foreach (Vehicle vehicle in nearbyVehicles)
							{
								// Taken with a purpose (phase P28, part 3): not by a squad holding a flag or
								// sneaking, not for a short walk, not at the cost of a long detour.
								if (!squad.ShouldBoard(vehicle))
								{
									continue;
								}
								int emptySeats = vehicle.EmptySeats();
								if (vehicle.claimedByPlayer)
								{
									if (actor.team != FpsActorController.playerTeam)
									{
										break;
									}
									emptySeats++;
								}
								if (emptySeats >= squad.members.Count)
								{
									squad.EnterVehicle(vehicle);
									enteringVehicle = true;
									break;
								}
								// A tank is crewed by part of the squad instead of standing empty (phase P28):
								// the original wanted a seat for every member, so a squad of four never took a
								// tank with fewer. The rest carry on with the squad's order on foot. Since P32 a
								// helicopter is crewed the same way, and a car by two or more: a squad of five
								// beside a four-seat jeep used to walk past it.
								if (!vehicle.claimedByPlayer && CrewSplitFits(Squad.KindOf(vehicle), emptySeats))
								{
									Squad crew = squad.SplitCrew(emptySeats);
									if (crew != null)
									{
										crew.EnterVehicle(vehicle);
										break;
									}
								}
							}
						}
						if (!enteringVehicle && !actor.IsPassenger())
						{
							if (squad.HasCommand)
							{
								squad.FollowCommand();
							}
							else if (!squad.HasTargetSpawnPoint() || !squad.ShouldGotoSpawnPoint(squad.targetSpawnPoint))
							{
								squad.NewAttackOrder();
							}
							else if (squad.HasTargetSpawnPoint() && squad.ShouldGotoSpawnPoint(squad.targetSpawnPoint))
							{
								squad.ReissueAttackOrder();
							}
						}
					}
				}
			}
			yield return new WaitForSeconds(0.5f);
		}
	}

	private IEnumerator AiTarget()
	{
		yield return new WaitForSeconds(UnityEngine.Random.Range(0f, 0.2f));
		Action investigateAction = new Action(3f);
		while (true)
		{
			// LOD gate, 4 of 8. The most expensive of the eight -- FindPotentialTargets walks
			// the actor list -- and therefore the one the LOD saving mostly comes from.
			if (!AiWorkAllowed())
			{
				yield return LodSkipWait;
				continue;
			}
			List<Actor> potentialTargets = FindPotentialTargets();
			Actor closestHighlighted = null;
			foreach (Actor a in potentialTargets)
			{
				// Ledger X-49, the half the registry fix cannot reach. potentialTargets is a
				// SNAPSHOT taken above, and this loop yields 0.2s between elements -- so on a
				// long list it walks for seconds while the world moves on, and any entry can be
				// destroyed mid-walk. Deregistering on destroy does not help a private copy that
				// was already taken.
				//
				// The check has to be `== null` and not `!a.dead`. Unity's overloaded == is the
				// only operator that reports a destroyed object as null; `dead` is an ordinary
				// managed field, which a destroyed Actor answers perfectly happily -- so the
				// existing `!a.dead` waves the corpse through and HasEffectiveWeaponAgainst then
				// reaches Actor.Position() -> Component.get_transform() and throws. That was
				// measured: 50 NullReferenceExceptions per combat run still came through here
				// after the registry leak was closed, all from this one coroutine.
				if (a == null) continue;

				if (!a.dead && HasEffectiveWeaponAgainst(a) && CanSeeActor(a, true))
				{
					SetTarget(a);
					break;
				}
				if (!a.dead && a.IsHighlighted() && Vector3.Distance(a.Position(), actor.Position()) < GunfireAttentionMetres() && UnityEngine.Random.Range(0f, 1f) < 0.2f)
				{
					LookAt(a.Position());
					skipNextScan = true;
					if (closestHighlighted == null)
					{
						closestHighlighted = a;
					}
				}
				yield return new WaitForSeconds(0.2f);
			}
			if (!HasTarget() && !actor.fallenOver)
			{
				Actor squadTarget = squad.GetTarget();
				if (squadTarget != null && HasEffectiveWeaponAgainst(squadTarget))
				{
					SetTarget(squadTarget);
				}
				else if (IsSquadLeader() && closestHighlighted != null && !closestHighlighted.IsSeated() && !HasTargetVehicle() && !actor.IsSeated() && investigateAction.TrueDone() && !HasCover() && stayInCoverAction.TrueDone())
				{
					squad.MoveTo(closestHighlighted.Position());
					investigateAction.Start();
				}
				if (!HasTarget() && hasPath && sprintCooldownAction.TrueDone())
				{
					StartSprint();
				}
			}
			else if (inCover)
			{
				stayInCoverAction.Start();
			}
			yield return new WaitForSeconds(0.5f);
		}
	}

	// Gunfire is heard and its flash seen from further off in the dark (NightTactics).
	private static float GunfireAttentionMetres()
		=> NightTactics.IsNight ? NightTactics.GunfireAttentionMetres : NightTactics.DayGunfireAttentionMetres;

	private void StartSprint()
	{
		// A squad sneaking round the side walks (phase P28): a sprint is heard and seen.
		if (squad != null && squad.sneaking)
		{
			return;
		}
		sprintAction.StartLifetime(UnityEngine.Random.Range(3f, 6f));
		sprintCooldownAction.StartLifetime(UnityEngine.Random.Range(5f, 11f));
	}

	private void StopSprint()
	{
		sprintAction.Stop();
	}

	private void SetTarget(Actor target)
	{
		if (target != this.target)
		{
			this.target = target;
			SwitchToEffectiveWeapon(target);
			Vector3 vector = target.Position() - actor.Position();
			float magnitude = vector.magnitude;
			acquireTargetOffset = UnityEngine.Random.insideUnitSphere.normalized * PARAMETERS.ACQUIRE_TARGET_OFFSET_PER_METER * magnitude + UnityEngine.Random.Range(-1f, 1f) * vector * PARAMETERS.ACQUIRE_TARGET_DEPTH_EXTRA_OFFSET_PER_METER;
			acquireTargetAction.StartLifetime(PARAMETERS.ACQUIRE_TARGET_DURATION_BASE + PARAMETERS.ACQUIRE_TARGET_DURATION_PER_METER * magnitude);
			StopSprint();
		}
	}

	private void DropTarget()
	{
		target = null;
		if (!actor.fallenOver)
		{
			SwitchToPrimaryWeapon();
		}
	}

	private IEnumerator AiWeapon()
	{
		yield return new WaitForSeconds(UnityEngine.Random.Range(0f, 0.5f));
		while (true)
		{
			// LOD gate, 5 of 8. Note `fire` keeps whatever value it had while gated rather than
			// being cleared: releasing the gate must not double as a cease-fire order.
			if (!AiWorkAllowed())
			{
				yield return LodSkipWait;
				continue;
			}
			fire = false;
			if (actor.HasUnholsteredWeapon() && !actor.activeWeapon.HasAnyAmmo())
			{
				if (target != null)
				{
					if (HasEffectiveWeaponAgainst(target))
					{
						SwitchToEffectiveWeapon(target);
					}
					else
					{
						DropTarget();
						SwitchToPrimaryWeapon();
					}
				}
				else
				{
					SwitchToPrimaryWeapon();
				}
			}
			if (actor.IsSeated() && actor.seat.vehicle.GetType() == typeof(Car) && actor.IsDriver())
			{
				fire = blockerAhead;
				yield return new WaitForSeconds(0.2f);
				continue;
			}
			if (!HasTarget() && actor.hasAmmoBox && actor.weapons[actor.ammoBoxSlot].AmmoFull() && (squad.MemberNeedsResupply() || PlayerWantsAmmoNearby()))
			{
				if (actor.activeWeapon == actor.weapons[actor.ammoBoxSlot])
				{
					if (PlayerWantsAmmoNearby())
					{
						LookAt(ActorManager.Player.transform.position);
					}
					fire = !IsMovingToCover() && UnityEngine.Random.Range(0, 2) == 0;
				}
				else
				{
					actor.SwitchWeapon(actor.ammoBoxSlot);
				}
				yield return new WaitForSeconds(0.2f);
				continue;
			}
			if (!HasTarget() && actor.hasMedipack && actor.weapons[actor.medipackSlot].AmmoFull() && (squad.MemberNeedsHealth() || PlayerWantsHealthNearby()))
			{
				if (actor.activeWeapon == actor.weapons[actor.medipackSlot])
				{
					if (PlayerWantsHealthNearby())
					{
						LookAt(ActorManager.Player.transform.position);
					}
					fire = !IsMovingToCover() && UnityEngine.Random.Range(0, 2) == 0;
				}
				else
				{
					actor.SwitchWeapon(actor.medipackSlot);
				}
				yield return new WaitForSeconds(0.2f);
				continue;
			}
			if (!HasTarget() || !actor.HasUnholsteredWeapon())
			{
				fire = false;
				yield return new WaitForSeconds(0.2f);
				continue;
			}
			if (HasTarget() && actor.activeWeapon.IsEmpty())
			{
				SwitchToEffectiveWeapon(target);
				yield return new WaitForSeconds(0.2f);
				continue;
			}
			if (!actor.activeWeapon.configuration.auto && fire)
			{
				fire = false;
				yield return new WaitForSeconds(0.05f);
				continue;
			}
			Vector3 muzzlePosition = actor.WeaponMuzzlePosition();
			Vector3 deltaTarget = GetTargetAcquiredPosition() - muzzlePosition + WeaponLead();
			float distance = deltaTarget.magnitude;
			Vector3 forward = ((!actor.IsSeated() || !actor.seat.HasMountedWeapon()) ? FacingDirection() : actor.activeWeapon.configuration.muzzle.forward);
			Vector3 orth1 = Vector3.Cross(forward, Vector3.up).normalized;
			Vector3 orth2 = Vector3.Cross(forward, orth1);
			float a = Vector3.Dot(deltaTarget, orth1);
			float b = Vector3.Dot(deltaTarget, orth2);
			float allowedAimSpread = actor.activeWeapon.configuration.aiAllowedAimSpread;
			bool insideAimCube = Vector3.Dot(deltaTarget, forward) > 0f && Mathf.Abs(a) < PARAMETERS.AI_FIRE_RECTANGLE_BOUND * allowedAimSpread && Mathf.Abs(b) < PARAMETERS.AI_FIRE_RECTANGLE_BOUND * allowedAimSpread;
			if (actor.activeWeapon.CanFire() && insideAimCube && !CombatRules.HoldFire(squad != null && squad.sneaking, IsTakingFire(), distance) && CanSeeActor(target))
			{
				Ray friendlyRay = new Ray(muzzlePosition + 0.3f * forward, forward);
				RaycastHit hitInfo;
				if (distance > 5f && Physics.Raycast(friendlyRay, out hitInfo, distance - 5f, 5376))
				{
					if (hitInfo.collider.gameObject.layer == 8)
					{
						fire = hitInfo.collider.GetComponent<Hitbox>().parent.team != actor.team;
					}
					else
					{
						fire = true;
					}
				}
				else
				{
					fire = true;
				}
			}
			yield return new WaitForSeconds(Mathf.Lerp(0.05f, 0.5f, distance / 40f));
		}
	}

	private IEnumerator AiTrack()
	{
		yield return new WaitForSeconds(UnityEngine.Random.Range(0f, 0.2f));
		while (true)
		{
			// LOD gate, 6 of 8.
			if (!AiWorkAllowed())
			{
				yield return LodSkipWait;
				continue;
			}
			if (HasTarget())
			{
				if (target.dead)
				{
					DropTarget();
				}
				else if (CanSeeActor(target))
				{
					lastSeenTargetPosition = target.Position();
					lastSeenTargetVelocity = target.Velocity();
				}
				else
				{
					DropTarget();
				}
			}
			targetDistance = HasTarget() ? Vector3.Distance(target.Position(), actor.Position()) : float.PositiveInfinity;
			UpdateOpenGround();
			yield return new WaitForSeconds(0.2f);
		}
	}

	private IEnumerator AiScan()
	{
		yield return new WaitForSeconds(UnityEngine.Random.Range(0f, 0.2f));
		while (true)
		{
			// LOD gate, 7 of 8. Ahead of the wait rather than after it, so a gated bot never
			// holds a scan timer that fires the instant it comes back.
			if (!AiWorkAllowed())
			{
				yield return LodSkipWait;
				continue;
			}
			yield return new WaitForSeconds(UnityEngine.Random.Range(0.8f, 3f));
			if (!skipNextScan && !HasTarget())
			{
				Vector3 facingDirection;
				if (IsTakingFire())
				{
					facingDirection = takingFireDirection;
				}
				if (InCover())
				{
					facingDirection = coverFacing + UnityEngine.Random.insideUnitSphere * 0.1f;
				}
				else
				{
					bool lookForward = IsSprinting() || UnityEngine.Random.Range(0f, 1f) < 0.8f;
					facingDirection = FacingDirection() * 0.5f + UnityEngine.Random.insideUnitSphere;
					if (IsSprinting())
					{
						facingDirection = Vector3.zero;
					}
					if (hasPath && lookForward)
					{
						facingDirection += Velocity().normalized * 1.5f;
					}
					if (!lookForward)
					{
						facingDirection += 0.4f * SquadFacingBias();
					}
					else
					{
						facingDirection += 0.1f * SquadFacingBias();
					}
					facingDirection.Normalize();
					if (IsSprinting())
					{
						facingDirection.y = 0f;
					}
					else
					{
						facingDirection.y *= UnityEngine.Random.Range(0.1f, 1f);
						if (facingDirection.y < 0f)
						{
							facingDirection.y *= 0.2f;
						}
					}
				}
				if (facingDirection != Vector3.zero)
				{
					targetFacingDirection = Quaternion.LookRotation(facingDirection, Vector3.up);
				}
			}
			skipNextScan = false;
		}
	}

	private IEnumerator AiTrackClosestActors()
	{
		closeActors = new List<Actor>();
		yield return new WaitForSeconds(UnityEngine.Random.Range(0f, 1f));
		while (true)
		{
			// LOD gate, 8 of 8.
			if (!AiWorkAllowed())
			{
				yield return LodSkipWait;
				continue;
			}
			closeActors = ActorManager.AliveActorsInRange(base.transform.position, 10f);
			yield return new WaitForSeconds(1f);
		}
	}

	private Vector3 SquadFacingBias()
	{
		Vector3 zero = Vector3.zero;
		int num = 0;
		foreach (AiActorController member in squad.members)
		{
			if (member != this)
			{
				zero -= member.transform.position - base.transform.position;
				num++;
			}
		}
		if (num == 0)
		{
			return Vector3.zero;
		}
		return zero / num;
	}

	private bool HasEffectiveWeaponAgainst(Actor targetActor)
	{
		float range = Vector3.Distance(targetActor.Position(), actor.Position());
		Actor.TargetType targetType = targetActor.GetTargetType();
		Weapon[] weapons = actor.weapons;
		foreach (Weapon weapon in weapons)
		{
			if (weapon != null && weapon.HasAnyAmmo() && weapon.EffectivenessAgainst(targetType) != 0 && weapon.EffectiveAtRange(range))
			{
				return true;
			}
		}
		return false;
	}

	private void LookAt(Vector3 position)
	{
		LookDirection(position - actor.Position());
	}

	private void LookDirection(Vector3 direction)
	{
		// A bot asked to face the point it stands on keeps the facing it has. Bots released
		// together are placed on a capture point's authored spawn children, picked at random, so
		// two of them can stand on the same one; a member hailing its leader there (#433 closed
		// only the leader hailing itself) turned to world north and Unity logged "Look rotation
		// viewing vector is zero" -- 22 times on the three v3.1.1 servers on 2026-10-01, nearly
		// every one within seconds of a bot release. Reproduced offline on Forest Lake from
		// EmoteHailLeader.
		if (direction.sqrMagnitude < 1e-8f)
		{
			return;
		}
		targetFacingDirection = Quaternion.LookRotation(direction);
	}

	private void OnPathComplete(Path p)
	{
		if (!p.error)
		{
			strandedFailures = 0;
			calculatingPath = false;
			hasPath = true;
			path = p;
			waypoint = 0;
			avoidedVehicles.Clear();
			if (!inCover && HasCover())
			{
				path.vectorPath.Add(cover.transform.position);
			}
		}
		else
		{
			// Free to ask again (phase P29). The original left calculatingPath set, and Goto
			// refuses to start a path while one is being calculated, so a bot whose search failed
			// -- "Couldn't find a close node to the start point" -- never walked again until it
			// died.
			calculatingPath = false;
			moveTimeoutAction.Start();

			// No node near where the bot stands: it is off the graph it searches, and every
			// retry fails the same way until it dies (phase P32 soaks: one quadbike rider asked
			// for ~150 such paths in ten minutes). See RescueStranded.
			if (p.errorLog != null && p.errorLog.Contains(NoStartNodeError))
			{
				strandedFailures++;
				if (strandedFailures >= StrandedFailuresBeforeRescue)
				{
					RescueStranded();
				}
			}
			else
			{
				strandedFailures = 0;
			}

			// Not logged here: AstarPath counts every failed search and reports them once a
			// minute (PathFailureSummary). Printing each again as an error put dozens of lines in
			// a match log for searches the bot simply retries, cancellations included.
		}
	}

	/// <summary>The search error a bot off its graph meets: A*'s own wording.</summary>
	private const string NoStartNodeError = "close node to the start point";

	/// <summary>How many such failures in a row before the bot is helped back onto its graph.</summary>
	private const int StrandedFailuresBeforeRescue = 3;

	/// <summary>
	/// The furthest a stranded bot on foot is moved to the walkable ground nearest it. Far, because
	/// Forest Lake's rock fields are wide: the night soak's quadbike rider stood 169 m from the nearest
	/// walkable node. A jump of that size is seen only by somebody up on the rocks with it.
	/// </summary>
	private const float StrandedRescueMetres = 250f;

	private int strandedFailures;

	/// <summary>
	/// Gets a bot back onto a graph it can search (phase P32). A driver whose vehicle has left the
	/// road graph -- a quadbike up the rocks, a car in the ford -- gets out and walks. On foot, the
	/// bot is put on the walkable ground nearest it, if that is within
	/// <see cref="StrandedRescueMetres"/>: the alternative is a soldier standing on a cliff asking
	/// for paths until somebody shoots him.
	/// </summary>
	private void RescueStranded()
	{
		strandedFailures = 0;
		Vector3 from = actor.Position();
		if (actor.IsSeated())
		{
			if (actor.IsDriver())
			{
				Debug.Log("[bots] " + base.name + " drove off the road graph at " + from.ToString("F0") + ": getting out to walk.");
				actor.LeaveSeat();
			}
			return;
		}
		if (AstarPath.active == null || !actor.autoMoveActor)
		{
			return;
		}
		NNConstraint walkable = new NNConstraint
		{
			graphMask = 1,
			constrainDistance = false
		};
		NNInfo nearest = AstarPath.active.GetNearest(from, walkable);
		if (nearest.node == null)
		{
			return;
		}
		Vector3 to = nearest.clampedPosition;
		float distance = Vector3.Distance(from, to);
		if (distance > StrandedRescueMetres)
		{
			return;
		}
		actor.transform.position = TerrainSurface.AtOrAbove(to);
		Debug.Log("[bots] " + base.name + " was off the walkable graph at " + from.ToString("F0") + ": moved " + distance.ToString("F0") + " m back onto it.");
	}

	private void RecalculatePath()
	{
		if (hasPath)
		{
			Vector3 targetPoint = lastGotoPoint;
			CancelPath();
			Goto(targetPoint);
		}
	}

	public void Goto(Vector3 targetPoint)
	{
		if (flying && actor.IsDriver())
		{
			flightTargetPosition = targetPoint;
			hasFlightTarget = true;
		}
		else if (!calculatingPath && (!actor.IsSeated() || actor.IsDriver()) && (!hasPath || Vector3.Distance(path.vectorPath[path.vectorPath.Count - 1], targetPoint) > 2f))
		{
			calculatingPath = true;
			int graphMask = 1;
			if (actor.IsDriver())
			{
				graphMask = ((!aquatic) ? 4 : 2);
			}
			lastGotoPoint = targetPoint;
			arrivedAtGoto = false;
			WaterPathTags.Apply(seeker, steeringBoat: aquatic && actor.IsDriver());
			if (aquatic && actor.IsDriver())
			{
				// A boat's goal is usually ashore: a flag the squad finishes on foot. The search for
				// the goal's node gave up beyond maxNearestNodeDistance (100 m) of water, so a flag
				// further inland failed outright -- "Couldn't find a close node to the end point",
				// the bot soak's Island boat sent to Farm -- and the boat never moved. Without the
				// limit the path ends at the water nearest the goal; StartSeated's exact end point
				// carries it on to the goal itself, so the boat runs aground there and AiVehicle's
				// stuck-boat branch puts the squad ashore to walk the rest.
				ABPath boatPath = ABPath.Construct(actor.Position(), targetPoint, null);
				boatPath.nnConstraint.constrainDistance = false;
				seeker.StartPath(boatPath, null, graphMask);
			}
			else
			{
				seeker.StartPath(actor.Position(), targetPoint, null, graphMask);
			}
			lastWaypoint = base.transform.position;
		}
	}

	public void CancelPath()
	{
		// The search still running is dropped too. Left alone it was delivered when it finished,
		// so a bot that cancelled its order -- to board a vehicle, or on dying -- got that order
		// back as a fresh path, and a bot that cancelled and asked again (falling back to cover,
		// a stuck car re-planning) made the seeker cancel it noisily: "Canceled path because a
		// new one was requested" and "Path Failed" in every long server log.
		seeker.CancelCurrentPathRequest();
		calculatingPath = false;
		path = null;
		hasPath = false;
		arrivedAtGoto = false;
		moveTimeoutAction.Start();
	}

	private Vector3 GetTargetAcquireOffset()
	{
		float f = acquireTargetAction.Ratio();
		return acquireTargetOffset * (1f - Mathf.Pow(f, 2f));
	}

	/// <summary>
	/// How far below the terrain surface a ragdoll must be before it has fallen through rather
	/// than clipped it. A limb dipping under a slope for a frame is ordinary and comes back up on
	/// its own; the pelvis three metres under the surface does not.
	/// </summary>
	private const float FallenThroughTerrainDepth = 3f;

	/// <summary>
	/// Whether this bot's ragdoll has gone through the terrain, where no amount of waiting lets it
	/// settle. False where there is no terrain or it has a hole (<c>TerrainSurface</c>).
	/// </summary>
	private bool HasFallenThroughTerrain()
	{
		return Ironfront.Net.Unity.TerrainSurface.IsUnder(actor.Position(), FallenThroughTerrainDepth);
	}

	// The goggles at night (phase P32): on for a fight or the last stretch to the objective.
	private void TickNightVision()
	{
		if (!NightTactics.IsNight)
		{
			if (nightVision != null && nightVision.IsOn)
			{
				nightVision.Battery.SwitchOff();
			}
			return;
		}
		if (nightVision == null)
		{
			nightVision = new BotNightVision(NightTactics.BatterySeconds);
		}
		bool nearObjective = squad != null && squad.commandTarget != null
			&& (squad.commandTarget.transform.position - actor.Position()).sqrMagnitude < NightObjectiveMetres * NightObjectiveMetres;
		nightVision.Tick(Time.deltaTime, HasTarget() || IsTakingFire(), nearObjective);
	}

	private void Update()
	{
		// Ahead of the dead check, not after it: a skipped tick must cost nothing at all.
		if (!AiWorkAllowed())
		{
			return;
		}
		if (actor.dead)
		{
			// A new life brings a full battery.
			if (nightVision != null)
			{
				nightVision.Refill();
			}
			return;
		}
		TickNightVision();
		if (!actor.fallenOver)
		{
			ragdollAutokillAction.Start();
		}
		else if (HasFallenThroughTerrain())
		{
			// A ragdoll under the terrain never settles, so waiting out the 60 s below only
			// keeps the bot out of the match -- falling, on a server, or dead for nothing
			// offline, with a point to the enemy. It gets up on the terrain where it fell over.
			actor.RecoverFromFallThroughTerrain();
			ragdollAutokillAction.Start();
		}
		else if (ragdollAutokillAction.TrueDone())
		{
			// Headless multiplayer cannot use an animation/physics timeout as a damage source.
			// It used to kill otherwise healthy bots with a null attacker after 60 seconds,
			// producing the repeated "The world -> actor" feed and continuously recycling bots.
			// Recover the stuck rig in-place; retain the original game's offline behaviour.
			if (Ironfront.Net.Unity.NetContext.IsServer)
			{
				actor.RecoverFromStuckRagdoll();
				ragdollAutokillAction.Start();
			}
			else
			{
				actor.Damage(100f, 0f, true, actor.Position(), Vector3.zero, Vector3.zero);
			}
		}
		if (!InCover() || IsReloading() || CoolingDown())
		{
			lean = Mathf.MoveTowards(lean, 0f, 2f * Time.deltaTime);
		}
		else if (InCover() && coverType == CoverPoint.Type.LeanLeft)
		{
			lean = Mathf.MoveTowards(lean, -1f, 2f * Time.deltaTime);
		}
		else if (InCover() && coverType == CoverPoint.Type.LeanRight)
		{
			lean = Mathf.MoveTowards(lean, 1f, 2f * Time.deltaTime);
		}
		else
		{
			lean = Mathf.MoveTowards(lean, 0f, 2f * Time.deltaTime);
		}
		if (HasTarget())
		{
			if (actor.HasUnholsteredWeapon())
			{
				targetFacingDirection = Quaternion.LookRotation(GetTargetAcquiredPosition() - actor.WeaponMuzzlePosition() + WeaponLead(), Vector3.up);
			}
			else
			{
				targetFacingDirection = Quaternion.LookRotation(target.CenterPosition() - actor.CenterPosition(), Vector3.up);
			}
		}
		facingDirection = Quaternion.Slerp(facingDirection, targetFacingDirection, 6f * Time.deltaTime);
		facingDirection = Quaternion.RotateTowards(facingDirection, targetFacingDirection, 5f * Time.deltaTime);
		fatigue = Mathf.Clamp01(fatigue - 0.4f * Time.deltaTime);
	}

	private Vector3 GetTargetAcquiredPosition()
	{
		return target.CenterPosition() + GetTargetAcquireOffset();
	}

	private bool IsReloading()
	{
		return actor.HasUnholsteredWeapon() && actor.activeWeapon.reloading;
	}

	private bool CoolingDown()
	{
		return actor.HasUnholsteredWeapon() && actor.activeWeapon.configuration.cooldown > 0.3f && actor.activeWeapon.CoolingDown();
	}

	private Vector3 WeaponLead()
	{
		Vector3 vector = target.Position() - actor.Position();
		float num = vector.magnitude / actor.activeWeapon.projectileSpeed;
		Vector3 normalized = vector.normalized;
		Vector3 vector2 = vector;
		float num2 = num;
		for (int i = 0; i < 1; i++)
		{
			Vector3 vector3 = Physics.gravity * Mathf.Pow(num2, 2f) / 2f;
			num2 = num / Vector3.Dot((vector - vector3).normalized, normalized);
		}
		Vector3 vector4 = SmoothNoise(0.2f);
		Vector3 vector5 = SmoothNoise(0.2333f);
		Vector3 vector6 = target.Velocity();
		Vector3 vector7 = FacingDirection();
		Vector3 vector8 = vector6 - Vector3.Dot(vector6, vector7) * vector7;
		Vector3 vector9 = target.Velocity() + vector4 * vector8.magnitude * 0.3f;
		float num3 = num2 * (1f + PARAMETERS.LEAD_SWAY_MAGNITUDE * (vector4.x + vector4.z) + UnityEngine.Random.Range(0f - PARAMETERS.LEAD_NOISE_MAGNITUDE, PARAMETERS.LEAD_NOISE_MAGNITUDE));
		Vector3 vector10 = vector9 * num3 - Physics.gravity * Mathf.Pow(num3, 2f) / 2f;
		return vector10 + vector5 * PARAMETERS.SWAY_MAGNITUDE;
	}

	private Vector3 SmoothNoise(float frequency)
	{
		float num = frequency * Time.time;
		return new Vector3(Mathf.Sin(num * 7.9f + smoothNoisePhase), Mathf.Sin(num * 8.3f + smoothNoisePhase), Mathf.Sin(num * 8.9f + smoothNoisePhase));
	}

	public override float Lean()
	{
		return lean;
	}

	public override bool Fire()
	{
		return fire;
	}

	public override bool Aiming()
	{
		return false;
	}

	public override bool Reload()
	{
		return actor.HasUnholsteredWeapon() && actor.activeWeapon.IsEmpty();
	}

	public override bool OnGround()
	{
		return true;
	}

	public override bool ProjectToGround()
	{
		return true;
	}

	private Vector3 GetWaypointDeltaBlockable()
	{
		if (blockerAhead)
		{
			return Vector3.zero;
		}
		return GetWaypointDelta();
	}

	private Vector3 GetWaypointDelta()
	{
		Vector3 vector = ((!actor.IsDriver()) ? actor.Position() : actor.seat.vehicle.transform.position);
		Vector3 vector2 = path.vectorPath[waypoint] - vector;
		Vector3 vector3 = vector2;
		vector3.y = 0f;
		float num = (actor.IsSeated() ? ((!aquatic) ? 2.5f : 4f) : ((!actor.IsLowQuality()) ? 0.2f : 1f));
		if (vector3.magnitude < num)
		{
			NextWaypoint();
		}
		return vector2;
	}

	private void NextWaypoint()
	{
		lastWaypoint = path.vectorPath[waypoint];
		if (!LastWaypoint())
		{
			waypoint++;
			NewWaypoint(lastWaypoint, path.vectorPath[waypoint]);
		}
		else
		{
			PathDone();
			hasPath = false;
		}
	}

	private void NewWaypoint(Vector3 origin, Vector3 target)
	{
		if (!HasTarget() && UnityEngine.Random.Range(0f, 1f) < 0.5f)
		{
			LookAt(target);
		}
		float num = ((!(targetVehicle != null)) ? 0f : targetVehicle.pathingRadius);
		Vehicle vehicle = null;
		float num2 = 9999999f;
		foreach (Vehicle vehicle2 in ActorManager.instance.vehicles)
		{
			if (!(vehicle2 == targetVehicle) && !avoidedVehicles.Contains(vehicle2) && vehicle2.ShouldBeAvoided() && vehicle2.CoarseLineOverlap(origin, target, num))
			{
				float num3 = Vector3.Distance(vehicle2.transform.position, actor.Position());
				if (num3 < num2)
				{
					num2 = num3;
					vehicle = vehicle2;
				}
			}
		}
		if (vehicle != null)
		{
			AvoidVehicle(vehicle, origin, target, num);
			avoidedVehicles.Add(vehicle);
		}
	}

	private void AvoidVehicle(Vehicle vehicle, Vector3 origin, Vector3 target, float pathingRadius)
	{
		int num = waypoint;
		bool flag = false;
		for (int i = waypoint; i < path.vectorPath.Count; i++)
		{
			if (!vehicle.IsCoarseOverlapping(path.vectorPath[i], pathingRadius))
			{
				num = i;
				flag = true;
				break;
			}
		}
		if (!flag)
		{
			return;
		}
		Vector3 vector = vehicle.transform.forward * (vehicle.avoidanceSize.y + pathingRadius);
		Vector3 vector2 = vehicle.transform.right * (vehicle.avoidanceSize.x + pathingRadius);
		Debug.DrawRay(path.vectorPath[num], Vector3.up, Color.green, 5f);
		float origin2 = (origin - vehicle.transform.position).ToVector2XZ().AtanAngle();
		float num2 = (path.vectorPath[num] - vehicle.transform.position).ToVector2XZ().AtanAngle();
		float num3 = SMath.V2D.RadiansFromTo(origin2, num2);
		bool shortLtNext = num3 < (float)Math.PI;
		Vector3[] array = new Vector3[4];
		float[] cornerFromOriginAngles = new float[4];
		array[0] = vector + vector2;
		array[1] = vector - vector2;
		array[2] = -vector - vector2;
		array[3] = -vector + vector2;
		List<int> list = new List<int>(4);
		List<int> list2 = new List<int>(4);
		for (int j = 0; j < 4; j++)
		{
			Vector2 v = array[j].ToVector2XZ();
			float num4 = SMath.V2D.RadiansFromTo(origin2, v.AtanAngle());
			cornerFromOriginAngles[j] = num4;
			if (shortLtNext ^ (num4 < num3))
			{
				list2.Add(j);
				Debug.DrawRay(vehicle.transform.position, array[j], Color.red, 5f);
			}
			else
			{
				list.Add(j);
				Debug.DrawRay(vehicle.transform.position, array[j], Color.blue, 5f);
			}
		}
		list.Sort(delegate (int x, int y)
		{
			int num6 = cornerFromOriginAngles[x].CompareTo(cornerFromOriginAngles[y]);
			return shortLtNext ? num6 : (-num6);
		});
		List<Vector3> list3 = new List<Vector3>(5);
		foreach (int item2 in list)
		{
			list3.Add(vehicle.transform.position + array[item2]);
		}
		List<Vector3> list4 = new List<Vector3>(list3);
		list4.Insert(0, origin);
		Vector3 vector3 = path.vectorPath[num];
		Vector3 vector4 = vector3 - list4[list4.Count - 1];
		list4.Add(list4[list4.Count - 1] + Vector3.ClampMagnitude(vector4, 5f));
		if (!RayPathClear(list4, 1))
		{
			list2.Sort(delegate (int x, int y)
			{
				int num5 = cornerFromOriginAngles[x].CompareTo(cornerFromOriginAngles[y]);
				return shortLtNext ? (-num5) : num5;
			});
			Vector3 item = vehicle.transform.position + array[list2[list2.Count - 1]];
			list3.Add(item);
			list4.Insert(list4.Count - 1, item);
			if (!RayPathClear(list4, 1))
			{
				list3 = new List<Vector3>(5);
				foreach (int item3 in list2)
				{
					list3.Add(vehicle.transform.position + array[item3]);
				}
				list4 = new List<Vector3>(list3);
				list4.Insert(0, origin);
				vector3 = path.vectorPath[num];
				vector4 = vector3 - list4[list4.Count - 1];
				list4.Add(list4[list4.Count - 1] + Vector3.ClampMagnitude(vector4, 5f));
				if (!RayPathClear(list4, 1))
				{
					return;
				}
			}
		}
		path.vectorPath.RemoveRange(waypoint, num - waypoint);
		path.vectorPath.InsertRange(waypoint, list3);
	}

	private bool RayPathClear(List<Vector3> points, int mask)
	{
		for (int i = 0; i < points.Count - 1; i++)
		{
			if (Physics.Linecast(points[i], points[i + 1], mask) || !Physics.Raycast(points[i], Vector3.down, 3f, mask))
			{
				return false;
			}
		}
		return true;
	}

	private Vector3 GetUpcomingBetweenWaypointsDelta()
	{
		return path.vectorPath[waypoint + 1] - path.vectorPath[waypoint];
	}

	private Vector3 GetNextWaypointDelta()
	{
		Vector3 vector = ((!actor.IsDriver()) ? actor.Position() : actor.seat.vehicle.transform.position);
		return path.vectorPath[waypoint + 1] - vector;
	}

	private bool LastWaypoint()
	{
		return path.vectorPath.Count <= waypoint + 1;
	}

	private void PathDone()
	{
		arrivedAtGoto = true;
		if (HasCover())
		{
			LookDirection(coverFacing);
			inCover = true;
			stayInCoverAction.Start();
			StopSprint();
		}
		moveTimeoutAction.Start();
	}

	/// <summary>
	/// The on-foot stick. Ledger <b>X-69</b> and <b>X-71</b>: a SUSPENDED controller steers
	/// nothing, because a claimed body's only writer is <c>ServerPlayer</c>.
	/// </summary>
	/// <remarks>
	/// <para>
	/// <b>One missing guard, two ledger rows.</b> <c>IAiDriver.Suspend</c> sets
	/// <c>enabled = false</c> when a connection claims this body, and Unity's flag gates the
	/// engine's own callbacks only -- an override another component CALLS runs regardless. That
	/// is why <see cref="CarInput"/>, <see cref="BoatInput"/>, <see cref="HelicopterInput"/>,
	/// <see cref="StartSeated"/>, <see cref="EndSeated"/> and <c>AiWorkAllowed</c> each open with
	/// this check. This one did not, so on a claimed body it kept returning a real walk vector:
	/// the server walked the body 518 m across the map while its owner sent no movement input
	/// (<b>X-71</b>), and the same call reached <c>LocalAvoidanceVelocity</c>, which enumerates
	/// <c>squad.members</c> on a slot that is squadless by design -- 10,126 NREs in one 600 s
	/// soak (<b>X-69</b>).
	/// </para>
	/// <para>
	/// <b>Zero, not the relayed axes.</b> <see cref="CarInput"/> returns what the server accepted
	/// because a vehicle's physics needs a stick either way. On foot there is no equivalent:
	/// <c>NetMovementAgent</c> applies the accepted input to the <c>CharacterController</c>
	/// itself, so anything returned here would be a SECOND writer to one position -- which is the
	/// condition <c>IAiDriver</c> exists to prevent.
	/// </para>
	/// <para>
	/// Above the <c>hasPath</c> branch rather than inside it, for the reason <see cref="CarInput"/>
	/// states: a claimed body that happens to be pathing is the only state either defect was ever
	/// observed in.
	/// </para>
	/// </remarks>
	public override Vector3 Velocity()
	{
		if (!base.enabled)
		{
			return Vector3.zero;
		}
		if (hasPath)
		{
			float num = 3.2f;
			if (IsSprinting())
			{
				num = 5.5f;
			}
			else if (HasTarget())
			{
				num = 2f;
			}
			fatigue = Mathf.Clamp01(fatigue + num * 0.04f * Time.deltaTime);
			// Keeping clear of squadmates needs a squad. A bot walking with none -- what a squad
			// roster left behind by a death that never left it produced, P29 capacity bench:
			// 4,966 NullReferenceExceptions in 43 s from LocalAvoidanceVelocity -- walks on
			// without the nudge, and the line says it happened, once.
			Vector3 avoidance = Vector3.zero;
			if (InSquad())
			{
				avoidance = LocalAvoidanceVelocity() * 0.4f;
			}
			else
			{
				ReportPathWithoutSquad();
			}
			return (GetWaypointDeltaBlockable().ToGround().normalized + avoidance).normalized * num;
		}
		// A side-step in the open (phase P28), checked clear by UpdateOpenGround before it began.
		if (!sideStepAction.TrueDone())
		{
			return sideStepDirection * CombatRules.SideStepSpeed;
		}
		return Vector3.zero;
	}

	/// <summary>
	/// The swimming stick. Guarded for the reason <see cref="Velocity"/> is, and in the same
	/// change: it reads the same path on the same claimed body.
	/// </summary>
	/// <remarks>
	/// No defect was observed through this one -- the shipping map has no water a claimed body
	/// swims in. It is guarded anyway because leaving the sibling unguarded is precisely how
	/// <see cref="Velocity"/> survived six hand-applied guards, and the companion test
	/// <c>EverySteeringOverrideCarriesTheGuardRatherThanAListOfSix</c> now refuses the seventh.
	/// </remarks>
	public override Vector3 SwimInput()
	{
		if (!base.enabled)
		{
			return Vector3.zero;
		}
		if (hasPath)
		{
			return GetWaypointDeltaBlockable().ToGround().normalized;
		}
		return Vector3.zero;
	}

	private Vector3 LocalAvoidanceVelocity()
	{
		Vector3 zero = Vector3.zero;
		int num = 0;
		foreach (AiActorController member in squad.members)
		{
			if (member == this)
			{
				continue;
			}
			Actor actor = member.actor;
			if (!actor.fallenOver && !actor.IsSeated())
			{
				Vector3 vector = (base.actor.Position() - actor.Position()).ToGround();
				float magnitude = vector.magnitude;
				if (magnitude < 1.5f)
				{
					float num2 = Mathf.Lerp(2f, 0f, magnitude / 1.5f);
					zero += vector.normalized * num2;
					num++;
				}
			}
		}
		if (num > 0)
		{
			return zero / num;
		}
		return Vector3.zero;
	}

	/// <summary>
	/// The boat's stick. Ledger <b>X-46</b>: a SUSPENDED controller returns the axes the server
	/// accepted, not the bot's opinion and not zero.
	/// </summary>
	/// <remarks>
	/// See <see cref="CarInput"/> for why the guard is on <c>enabled</c> and why it sits above the
	/// <c>hasPath</c> return rather than replacing it.
	/// </remarks>
	public override Vector2 BoatInput()
	{
		if (!base.enabled)
		{
			return NetVehicleAxisRelay.CarAxesFor(this);
		}
		if (!hasPath)
		{
			return Vector2.zero;
		}
		Vehicle vehicle = actor.seat.vehicle;
		float z = vehicle.LocalVelocity().z;
		Vector3 waypointDeltaBlockable = GetWaypointDeltaBlockable();
		waypointDeltaBlockable.y = 0f;
		float magnitude = waypointDeltaBlockable.magnitude;
		Vector3 normalized = waypointDeltaBlockable.normalized;
		Debug.DrawRay(vehicle.transform.position, waypointDeltaBlockable, Color.red);
		Vector2 vector = new Vector2(Vector3.Dot(normalized, actor.transform.right), Vector3.Dot(normalized, actor.transform.forward));
		return Vector2.ClampMagnitude(vector, 1f);
	}

	/// <summary>
	/// The car's stick. Ledger <b>X-46</b>: a SUSPENDED controller returns the axes the server
	/// accepted for this body, which is how a networked driver's vehicle moves at all.
	/// </summary>
	/// <remarks>
	/// <para>
	/// <b>Why this class is the one that reads a network relay.</b> <c>Car.FixedUpdate</c> pulls
	/// through <c>Driver().controller.CarInput()</c>, and a networked player's server-side body
	/// carries an <c>AiActorController</c> because <c>IronfrontNetBindings.CreatePlayerBody</c>
	/// instantiates the bot prefab. So this override IS the driver seam for every real networked
	/// driver, and until X-46 it answered with a bot's pathfinding — or, having no path, with
	/// nothing. Measured: 1,285 accepted <c>C_VEHICLE_INPUT</c> messages against a hull that never
	/// moved (<c>artifacts/lane-a/r5/r5-combat-05</c>).
	/// </para>
	/// <para>
	/// <b><c>enabled</c>, not <c>squad != null</c> (O-D2).</b> <c>NetServerActor.Claim</c> suspends
	/// the bot brain through <c>IAiDriver.Suspend</c>, which sets <c>enabled = false</c>, so
	/// <c>!enabled</c> names exactly "this controller is not steering this body" — the same
	/// condition X-45 and X-47 established. A real bot's controller is enabled and never reaches
	/// the relay, so an AI convoy still drives itself.
	/// </para>
	/// <para>
	/// <b>Above the <c>hasPath</c> return, not folded into it.</b> A suspended controller has no
	/// path either, so ordering them the other way would return zero before the relay was ever
	/// consulted — and the symptom would be indistinguishable from the defect being fixed.
	/// </para>
	/// </remarks>
	public override Vector2 CarInput()
	{
		if (!base.enabled)
		{
			return NetVehicleAxisRelay.CarAxesFor(this);
		}
		if (!hasPath)
		{
			return Vector2.zero;
		}
		Vehicle vehicle = actor.seat.vehicle;
		if (waitForPlayer)
		{
			return new Vector2(0f, 0f - vehicle.LocalVelocity().z);
		}
		if (vehicle.GetType() == typeof(Tank))
		{
			return GetTankInput();
		}
		return GetCarInput();
	}

	private Vector2 GetTankInput()
	{
		Vehicle vehicle = actor.seat.vehicle;
		float z = vehicle.LocalVelocity().z;
		// A tank with an enemy in its sights inside the standoff stops and fires from there (phase
		// P28, part 3); the original drove its path into point-blank range of every defender.
		if (VehicleRules.HoldStandoff(VehicleKind.Armour, HasTarget(), targetDistance))
		{
			return new Vector2(0f, Mathf.Clamp(0f - z, -1f, 1f));
		}
		if (blockerAhead)
		{
			float num = Mathf.Sign(vehicle.transform.worldToLocalMatrix.MultiplyPoint(blockerPosition).x) * 0.3f;
			if (z > 0.1f)
			{
				return new Vector2(0f - num, -1f);
			}
			return new Vector2(num, 1f);
		}
		Vector3 waypointDeltaBlockable = GetWaypointDeltaBlockable();
		if (waypointDeltaBlockable == Vector3.zero)
		{
			return Vector2.zero;
		}
		float magnitude = waypointDeltaBlockable.magnitude;
		Vector3 projectedDrivingTarget = GetProjectedDrivingTarget(3f, 1f, vehicle);
		waypointDeltaBlockable = (projectedDrivingTarget - vehicle.transform.position).ToGround();
		Vector3 vector = base.transform.worldToLocalMatrix.MultiplyVector(waypointDeltaBlockable);
		vector.y = 0f;
		bool flag = Mathf.Abs(vector.z) > Mathf.Abs(vector.x);
		if (forceAntiStuckReverse && flag && magnitude > 2.5f)
		{
			return new Vector2(0f, Mathf.Sign(0f - vector.z) * 0.5f);
		}
		return new Vector2(Mathf.Clamp(vector.x, -1f, 1f), (!flag) ? 0f : Mathf.Sign(vector.z));
	}

	private Vector2 GetCarInput()
	{
		Vehicle vehicle = actor.seat.vehicle;
		float z = vehicle.LocalVelocity().z;
		if (blockerAhead)
		{
			float num = Mathf.Sign(vehicle.transform.worldToLocalMatrix.MultiplyPoint(blockerPosition).x) * 0.3f;
			if (z > 0.1f)
			{
				return new Vector2(0f - num, -1f);
			}
			return new Vector2(num, 1f);
		}
		Vector3 waypointDeltaBlockable = GetWaypointDeltaBlockable();
		waypointDeltaBlockable.y = 0f;
		float magnitude = waypointDeltaBlockable.magnitude;
		Vector3 projectedDrivingTarget = GetProjectedDrivingTarget(4f, 0.5f, vehicle);
		waypointDeltaBlockable = (projectedDrivingTarget - vehicle.transform.position).ToGround();
		Vector3 futureProjectedDrivingTarget = GetFutureProjectedDrivingTarget(4f, 3f, vehicle);
		float value = Vector3.Dot((futureProjectedDrivingTarget - vehicle.transform.position).ToGround().normalized, waypointDeltaBlockable.normalized);
		float magnitude2 = (vehicle.Velocity() * 1f).magnitude;
		float magnitude3 = vehicle.rigidbody.linearVelocity.magnitude;
		float num2 = ((!forceAntiStuckReverse) ? (15f * (0.15f + 0.85f * Mathf.Pow(Mathf.Clamp01(value), 3f))) : 7f);
		float num3 = Mathf.Clamp01(num2 - magnitude3);
		if (magnitude3 > 1.1f * num2)
		{
			num3 = -1f;
		}
		Debug.DrawRay(vehicle.transform.position + Vector3.up, Vector3.up, Color.black);
		Debug.DrawRay(vehicle.transform.position + Vector3.up, Vector3.up * num3, Color.red);
		Vector2 result = new Vector2(Vector3.Dot(waypointDeltaBlockable * 5f, actor.transform.right), Vector3.Dot(waypointDeltaBlockable, actor.transform.forward));
		bool flag = !canTurnCarTowardsWaypoint ^ forceAntiStuckReverse;
		Color color = Color.blue;
		result.y = Mathf.Clamp(Mathf.Sign(result.y) * num3, -1f, 0.8f);
		result.x = Mathf.Clamp(result.x / (1f + Mathf.Abs(z)), -1f, 1f);
		if (flag)
		{
			result.y = Mathf.Abs(result.y);
			color = Color.red;
		}
		if (forceAntiStuckReverse)
		{
			result.y = -0.7f;
		}
		if (z < 0f)
		{
			result.x *= -1f;
		}
		Vector3 rhs = vehicle.transform.forward.ToGround();
		rhs.Normalize();
		float t = Mathf.Abs(Vector3.Dot(waypointDeltaBlockable.normalized, rhs));
		float num4 = Mathf.Lerp(1f, 0.5f, t);
		float num5 = Mathf.Lerp(0.5f, 1f, t);
		result.x *= num4;
		result.y *= num5;
		Debug.DrawRay(actor.seat.vehicle.transform.position, GetWaypointDelta(), color);
		return result;
	}

	private Vector3 GetProjectedDrivingTarget(float minDistance, float speedGain, Vehicle vehicle)
	{
		if (path.vectorPath[waypoint] == lastWaypoint)
		{
			NextWaypoint();
			return lastWaypoint;
		}
		Vector3 vector = path.vectorPath[waypoint] - lastWaypoint;
		float num = Mathf.Max(minDistance, speedGain * vehicle.rigidbody.linearVelocity.magnitude);
		Vector3 vector2 = SMath.LineSegmentVsPointClosest(lastWaypoint, path.vectorPath[waypoint], vehicle.transform.position + vector.normalized * num);
		Debug.DrawLine(vehicle.transform.position, vector2, Color.red);
		Debug.DrawRay(vector2, Vector3.up * 5f, new Color(255f, 0f, 255f));
		if (Vector3.Distance(vector2, path.vectorPath[waypoint]) < 0.2f)
		{
			NextWaypoint();
		}
		return vector2;
	}

	private Vector3 GetFutureProjectedDrivingTarget(float minDistance, float speedGain, Vehicle vehicle)
	{
		Vector3 vector = path.vectorPath[waypoint];
		if (waypoint + 1 < path.vectorPath.Count)
		{
			float magnitude = (vector - vehicle.transform.position).magnitude;
			float num = Mathf.Max(minDistance, speedGain * vehicle.rigidbody.linearVelocity.magnitude);
			Vector3 vector2 = path.vectorPath[waypoint + 1] - path.vectorPath[waypoint];
			Vector3 vector3 = vector + Mathf.Max(0f, num - magnitude) * vector2.normalized;
			Debug.DrawLine(vehicle.transform.position, vector3, Color.red);
			return vector3;
		}
		return vector;
	}

	/// <summary>
	/// The bot brain's helicopter stick. Ledger <b>X-47</b>.
	/// </summary>
	/// <remarks>
	/// <para>
	/// <b>A SUSPENDED controller flies nothing, and without this guard it throws once per physics
	/// step.</b> <c>Helicopter.FixedUpdate</c> asks its driver's controller for a stick position
	/// every step — <c>Driver().controller.HelicopterInput()</c> — and a networked player's
	/// server-side body carries an <c>AiActorController</c> with no <see cref="squad"/>, because
	/// <c>IronfrontNetBindings.CreatePlayerBody</c> instantiates the bot character. The first line
	/// below dereferences it.
	/// </para>
	/// <para>
	/// <b>Measured:</b> 309 <c>NullReferenceException</c>s in a 150 s lane-A Combat run and 1,204
	/// in a 300 s one, all of them this line
	/// (<c>artifacts/lane-a/r5/r5-combat-05-server.log</c>). Check 11 asks whether a headless
	/// server SURVIVES a networked driver; a throw per physics step for as long as a player sits
	/// in a pilot seat is the answer it was written to find.
	/// </para>
	/// <para>
	/// <b>Why <see cref="BoatInput"/> and <see cref="CarInput"/> did not throw.</b> Both opened
	/// with <c>if (!hasPath) return zero</c>, and a suspended AI has no path — so they were
	/// already returning a neutral stick where this one reached its squad first. Since X-46 all
	/// three carry the same explicit <c>enabled</c> guard, and the incidental <c>hasPath</c> cover
	/// is no longer what is holding the other two up.
	/// </para>
	/// <para>
	/// <b>The relay, not the takeoff ramp below, and not zero either since X-46.</b> The real
	/// driver input for a networked pilot arrives through <c>NetDriverInputSink</c>, which hands
	/// a body with no <c>FpsActorController</c> a <c>NetVehicleAxisRelay</c> instead — so this
	/// returns the stick the server accepted. It falls back to zero when nothing is driving, which
	/// is what a suspended controller owes a vehicle. What this method must not do, and still does
	/// not, is supply the BOT's opinion to a vehicle a player is sitting in.
	/// </para>
	/// </remarks>
	public override Vector4 HelicopterInput()
	{
		if (!base.enabled)
		{
			return NetVehicleAxisRelay.HelicopterAxesFor(this);
		}
		// An enabled pilot with no squad. A player's body left in a pilot seat by a disconnect
		// was one: the line below threw once per physics step for as long as it sat there, 867
		// NullReferenceExceptions in 14 s (2026-09-30, Island, run p29new-i16), each aborting
		// Helicopter.FixedUpdate. Such a body now leaves the match with its connection, so this is
		// the net under whatever leaves a pilot squadless next: the neutral stick the
		// relay gives a seat with no driver, without reading the relay (an enabled controller is
		// never steered by the network).
		if (!InSquad())
		{
			return Vector4.zero;
		}
		if (!squad.AllSeated() || !helicopterTakeoffAction.TrueDone())
		{
			return new Vector4(0f, -1f + helicopterTakeoffAction.Ratio() * 1.5f, 0f, 0f);
		}
		Rigidbody rigidbody = actor.seat.vehicle.rigidbody;
		Transform transform = actor.seat.vehicle.transform;
		Vector3 position = transform.position;
		Vector3 localEulerAngles = transform.localEulerAngles;
		float y = transform.eulerAngles.y;
		float num = position.y;
		Vector3 vector = position + rigidbody.linearVelocity * 3f;
		RaycastHit hitInfo;
		if (Physics.SphereCast(new Ray(vector + Vector3.up * 10f, Vector3.down), 1f, out hitInfo, 999f, 1))
		{
			num = hitInfo.distance;
		}
		float num2 = 0f;
		Vector3 zero = Vector3.zero;
		Vector3 forward = transform.forward;
		forward.y = 0f;
		forward.Normalize();
		Vector3 rhs = new Vector3(forward.z, 0f, 0f - forward.x);
		bool flag = HasTarget() && !helicopterAttackAction.TrueDone();
		Vector3 vector2 = position + forward;
		if (flag)
		{
			vector2 = target.CenterPosition() + WeaponLead();
			Debug.DrawLine(base.transform.position, vector2, Color.red);
		}
		else if (hasFlightTarget)
		{
			vector2 = flightTargetPosition;
		}
		num2 = Heading(position, vector2);
		zero = vector2 - position;
		float y2 = zero.y;
		zero.y = 0f;
		float magnitude = zero.magnitude;
		float num3 = Mathf.DeltaAngle(y, num2);
		float num4 = helicopterTargetFlightHeight - num;
		float num5 = 25f * Mathf.Clamp(Vector3.Dot(zero * 0.02f, forward), -1f, 1f);
		float current = -25f * Mathf.Clamp(Vector3.Dot(zero * 0.02f, rhs), -1f, 1f);
		if (num4 > 5f)
		{
			num5 = 0f;
			current = 0f;
		}
		float num6 = 1f;
		if (flag)
		{
			num5 = (0f - Mathf.Atan2(y2, magnitude)) * 57.29578f;
			num6 = 2.5f;
		}
		Vector3 vector3 = transform.InverseTransformDirection(rigidbody.angularVelocity);
		float x = 0.01f * num6 * num3 - vector3.y;
		float w = 0.1f * num6 * Mathf.DeltaAngle(localEulerAngles.x, num5) - 2f * vector3.x;
		float z = 0.1f * num6 * Mathf.DeltaAngle(current, localEulerAngles.z) + 2f * vector3.z;
		float y3 = ((!(num4 > 0f)) ? (0.01f * num4) : (1f * num4));
		return new Vector4(x, y3, z, w);
	}

	private float Heading(Vector3 root, Vector3 target)
	{
		Vector3 vector = target - root;
		return (0f - Mathf.Atan2(vector.z, vector.x)) * 57.29578f + 90f;
	}

	public override Vector3 FacingDirection()
	{
		float num = Time.time + randomTimeOffset;
		Vector3 vector = new Vector3(Mathf.Sin(num * 3.1f), Mathf.Cos(num * 5.3f), Mathf.Cos(num * 3.7f));
		return facingDirection * Vector3.forward + vector * (PARAMETERS.AIM_BASE_SWAY + PARAMETERS.AIM_MAX_SWAY * fatigue);
	}

	public override bool UseMuzzleDirection()
	{
		return false;
	}

	public override void ReceivedDamage(float damage, float balanceDamage, Vector3 point, Vector3 direction, Vector3 force)
	{
		// Damage with no direction has no source to turn towards. The offline ragdoll timeout
		// deals it at the bot's own position, and looking there turned the bot to world north.
		if (!HasTarget() && direction != Vector3.zero)
		{
			LookAt(point - direction * 10f);
		}
	}

	/// <summary>
	/// A hit by an enemy is incoming fire from them (phase P28), called by
	/// <c>Actor.DamageAttributed</c>, which knows who fired. The original only turned the bot to
	/// look, so one hit by a grenade, a blast or a round the near-miss warning had not flagged kept
	/// standing where it was. A teammate's stray round is ignored: friendly fire is part of the
	/// game, not a reason to dig in facing your own side.
	/// </summary>
	public void NoteHit(Actor attacker, Vector3 direction)
	{
		if (!base.enabled || attacker == null || attacker == actor || attacker.team == actor.team)
		{
			return;
		}
		Vector3 from = new Vector3(0f - direction.x, 0f, 0f - direction.z);
		if (from.sqrMagnitude > 0.01f)
		{
			MarkTakingFireFrom(from.normalized);
		}
		NoteAttacker(attacker);
	}

	public override void DisableInput()
	{
	}

	public override void EnableInput()
	{
	}

	/// <summary>
	/// The bot brain's seat bookkeeping. Ledger <b>X-45</b>.
	/// </summary>
	/// <remarks>
	/// <para>
	/// <b>A SUSPENDED controller does none of it, and that guard is load-bearing.</b> Everything
	/// below is AI STEERING state -- squad leadership, the seeker's tag penalties, the two
	/// pathfinding modifiers -- and a body that has been claimed by a connection is steered by
	/// <c>ServerPlayer</c> through <c>NetMovementAgent</c> instead. <c>NetServerActor.Claim</c>
	/// says so and disables this component to make it true (<c>IAiDriver.Suspend</c>).
	/// </para>
	/// <para>
	/// <b>Disabling the component was not enough on its own</b>, and it buys even less than this
	/// remark used to claim. It was written as "stops <c>Update</c> and the eight coroutines";
	/// <b>it does not stop the coroutines</b> — Unity only stops those when the GameObject is
	/// deactivated — which is <b>X-57</b>, found by a run on 2026-08-28 and closed by gating
	/// <c>AiWorkAllowed()</c> on <c>enabled</c>. What it stops is <c>Update</c>. Separately,
	/// <c>Actor.EnterSeat</c> calls <c>controller.StartSeated</c> DIRECTLY, and a direct call
	/// runs on a disabled MonoBehaviour. So the one AI path a networked player could reach was
	/// this one, and it dereferenced <see cref="squad"/>, which a player-slot body has never had:
	/// <c>IronfrontNetBindings.CreatePlayerBody</c> instantiates <c>ActorManager.actorPrefab</c>
	/// -- the bot character -- and no squad ever adopts it.
	/// </para>
	/// <para>
	/// <b>What it cost, and why it was invisible until 2026-08-27.</b> A
	/// <c>NullReferenceException</c> out of <c>ServerSeatBridge.Apply</c>, thrown AFTER
	/// <c>Seat.SetOccupant</c> and the transform re-parent and BEFORE <c>Actor.EnterSeat</c>
	/// finished -- so the seat was booked, the body was welded to it, and the rest of the entry
	/// never ran. Nothing in the shipped client could reach it (X-30: <c>SeatRequestMessage</c>
	/// had no production sender until R2) and lane A could not either (X-34: every frame carried
	/// <c>InputButtons.None</c>), so check 11's <i>drive</i> verb had never once been executed
	/// against a real server. The first lane-A Combat run found it in ninety seconds.
	/// </para>
	/// <para>
	/// <b>Guarded on <c>enabled</c> rather than on <c>squad != null</c>.</b> A null squad on a
	/// genuine bot is an AI setup fault and should still throw where it is thrown today -- line
	/// 644 dereferences it unguarded, so bots always have one. <c>enabled</c> names the actual
	/// condition: this controller is not driving this body.
	/// </para>
	/// </remarks>
	public override void StartSeated(Seat seat)
	{
		if (!base.enabled)
		{
			return;
		}
		if (seat.type == Seat.Type.Driver || seat.type == Seat.Type.Pilot)
		{
			squad.MakeLeader(this);
		}
		flying = seat.vehicle.GetType() == typeof(Helicopter);
		if (seat.vehicle.GetType() == typeof(Tank))
		{
			seeker.tagPenalties[0] = 100000;
			Tank tank = (Tank)seat.vehicle;
			radiusModifier.radius = tank.pathingRadius;
			radiusModifier.enabled = true;
			alternatePathModifier.enabled = true;
		}
		else if (seat.vehicle.GetType() == typeof(Car))
		{
			Car car = (Car)seat.vehicle;
			radiusModifier.enabled = car.pathingRadius > 0f;
			radiusModifier.radius = car.pathingRadius;
			alternatePathModifier.enabled = true;
			seeker.tagPenalties[0] = 100000;
		}
		else if (seat.vehicle.GetType() == typeof(Boat))
		{
			aquatic = true;
			seeker.startEndModifier.exactEndPoint = StartEndModifier.Exactness.Original;
		}
	}

	/// <summary>
	/// Unwinds what <see cref="StartSeated"/> set. Ledger <b>X-45</b>.
	/// </summary>
	/// <remarks>
	/// Guarded for <see cref="StartSeated"/>'s reason and one more: this method is the exact
	/// inverse of a call the guard above may have declined, so running it on a suspended
	/// controller would clear pathfinding state that this component never set -- and would do it
	/// on behalf of a body it is not steering. <c>Actor.ExitSeat</c> reaches it by the same
	/// direct call.
	/// </remarks>
	public override void EndSeated(Vector3 exitPosition, Quaternion flatFacing)
	{
		if (!base.enabled)
		{
			return;
		}
		flying = false;
		aquatic = false;
		radiusModifier.enabled = false;
		alternatePathModifier.enabled = false;
		seeker.tagPenalties[0] = 0;
		seeker.startEndModifier.exactEndPoint = StartEndModifier.Exactness.ClosestOnNode;
	}

	public override void StartRagdoll()
	{
	}

	public override void GettingUp()
	{
	}

	/// <summary>
	/// Re-arms the path after a ragdoll. Guarded for <see cref="Velocity"/>'s reason, and found
	/// by its companion test rather than by inspection.
	/// </summary>
	/// <remarks>
	/// Both branches START movement on this body -- <c>Goto</c> issues a new path, and
	/// <c>RecalculatePath</c> re-issues the one it had. On a claimed body that is X-71's exact
	/// mechanism arriving through a second door: the brain is suspended, and a ragdoll ending
	/// hands it the wheel back.
	/// </remarks>
	public override void EndRagdoll()
	{
		if (!base.enabled)
		{
			return;
		}
		if (inCover)
		{
			Goto(cover.transform.position);
		}
		else if (hasPath)
		{
			RecalculatePath();
		}
	}

	public override void Die()
	{
		LeaveCover();
		CancelPath();

		// A squadless body is ORDINARY here, not exceptional. Every networked player slot is one
		// of these characters, built by NetServerBindings.PlayerBodyFactory, and nothing ever puts
		// it in a squad -- InSquad() exists precisely because the field is allowed to be null.
		//
		// Without the guard the first death threw here, which ABORTED the rest of Actor.Die, so
		// the body never finished dying and died again the next frame, and the frame after that:
		// 676 NullReferenceExceptions in one 90-second lane-B run (combat-01/server.log). The
		// noise was the small half of the cost; the large half is that no player body has ever
		// completed Actor.Die on a headless server.
		if (InSquad())
		{
			squad.DropMember(this);
			if (squad.members.Count == 1)
			{
				Squad.Census.NoteLeftAlone(actor.team);
			}
		}

		squad = null;
		aiCoroutinesAwaitSquad = false;
		StopAllCoroutines();
		CancelInvoke();
	}

	public bool HasTarget()
	{
		return target != null;
	}

	/// <summary>
	/// The fog this bot looks at <paramref name="target"/> through: the map's, and at night thinner
	/// through its goggles and for a target whose muzzle flash has just lit it (NightTactics).
	/// </summary>
	private float SightFogDensity(Actor target)
	{
		float density = RenderSettings.fogDensity;
		if (!NightTactics.IsNight)
		{
			return density;
		}
		if (nightVision != null && nightVision.IsOn)
		{
			density *= NightTactics.NightVisionFogFactor;
		}
		if (target.IsHighlighted())
		{
			density *= NightTactics.MuzzleFlashFogFactor;
		}
		return density;
	}

	private bool CanSeeActor(Actor target, bool considerFov = false)
	{
		Vector3 vector = target.Position() - actor.Position();
		float magnitude = vector.magnitude;
		Vector3 normalized = vector.normalized;
		float num = 1f;
		float num2 = Vector3.Dot(normalized, FacingDirection());
		if (HasTarget() && target == this.target)
		{
			num = 1f;
		}
		else if (RenderSettings.fog)
		{
			float f = Mathf.Exp(0f - Mathf.Pow(magnitude * SightFogDensity(target), 2f));
			num = num2 * Mathf.Pow(f, 2f);
			if (target.IsHighlighted())
			{
				num *= 2f;
			}
			else if (target.StandingStill())
			{
				num = Mathf.Pow(num, 2f);
			}
			num *= PARAMETERS.VISIBILITY_MULTIPLIER;
		}
		if (target.IsSeated())
		{
			num *= target.seat.vehicle.spotChanceMultiplier;
		}
		if (UnityEngine.Random.Range(0f, 1f) < num && (!considerFov || target.IsHighlighted() || num2 > 0.1f))
		{
			for (int i = 0; i < 3; i++)
			{
				Vector3 vector2 = vector + Vector3.down * 0.5f + Vector3.Scale(UnityEngine.Random.insideUnitSphere, new Vector3(0.7f, 0.8f, 0.7f));
				Ray ray = new Ray(eyeTransform.position - eyeTransform.right * 0.2f, vector2.normalized);
				if (!Physics.Raycast(ray, vector.magnitude, 1))
				{
					return true;
				}
			}
		}
		return false;
	}

	public override void SpawnAt(Vector3 position)
	{
		target = null;
		targetVehicle = null;
		hasFlightTarget = false;
		takingFireAction.Stop();
		threatActor = null;
		targetDistance = float.PositiveInfinity;
		fallingBackUntil = 0f;
		sideStepAction.Stop();
		radiusModifier.enabled = false;
		recentAntiStuckEvents = 0;
		arrivedAtGoto = false;
		ragdollAutokillAction.Start();
		moveTimeoutAction.Start();
		StartAiCoroutines();
	}

	public override void ApplyRecoil(Vector3 impulse)
	{
		facingDirection = Quaternion.LookRotation(FacingDirection() * 20f + impulse.z * Vector3.down + Vector3.right * impulse.x, Vector3.up);
	}

	public bool FindCover()
	{
		return FindCoverAtPoint(actor.Position());
	}

	public bool FindCoverAtPoint(Vector3 point)
	{
		// A hurt bot keeps the cover it fell back to (phase P28).
		if (IsFallingBack())
		{
			return HasCover();
		}
		if (HasCover())
		{
			LeaveCover();
		}
		inCover = false;
		// Cover round the point that hides the bot from where the enemy will come (phase P28);
		// the original's nearest vacant point, whichever way it faced, when none there does.
		if (ThreatEyeAround(point, out Vector3 threatEye) && TakeLevelCover(point, threatEye, CombatRules.CoverSearchRadius, false))
		{
			CancelPath();
			Goto(cover.transform.position);
			StartSprint();
			return true;
		}
		cover = CoverManager.instance.ClosestVacant(point);
		if (HasCover())
		{
			CancelPath();
			ClaimCover(cover, cover.transform.forward, cover.type);
			Goto(cover.transform.position);
			StartSprint();
			return true;
		}
		CancelPath();
		Goto(point);
		return false;
	}

	public bool FindCoverTowards(Vector3 direction)
	{
		if (IsFallingBack())
		{
			return HasCover();
		}
		if (HasCover())
		{
			LeaveCover();
		}
		inCover = false;
		// Cover judged against the shooter itself (phase P28); the original's nearest point turned
		// toward the fire, up to 50 m off, when nothing within reach hides the bot.
		if (TakeLevelCover(actor.Position(), ThreatEye(direction), CombatRules.CoverSearchRadius, false))
		{
			Goto(cover.transform.position);
			StartSprint();
			return true;
		}
		cover = CoverManager.instance.ClosestVacantCoveringDirection(base.transform.position, direction);
		if (HasCover())
		{
			ClaimCover(cover, cover.transform.forward, cover.type);
			Goto(cover.transform.position);
			StartSprint();
			return true;
		}
		return false;
	}

	public void LeaveCover()
	{
		inCover = false;
		if (HasCover())
		{
			cover.taken = false;
			cover = null;
		}
	}

	public bool HasCover()
	{
		return cover != null;
	}

	public bool IsMovingToCover()
	{
		return HasCover() && !InCover();
	}

	/// <summary>
	/// Leaves the squad roster on the way out. Ledger <b>X-55</b>.
	/// </summary>
	/// <remarks>
	/// <para>
	/// <c>Squad.DropMember</c> had exactly one caller -- <see cref="Die"/> -- so a bot that DIED
	/// left the roster and a bot that was DESTROYED did not. <c>Squad</c> is a plain C# object
	/// with no lifecycle of its own, so nothing else was ever going to notice.
	/// </para>
	/// <para>
	/// <b>Why a stale member is a crash and not an empty slot.</b> Unity's overloaded <c>==</c>
	/// reports a destroyed object as equal to null, so the corpse passes <c>member != this</c> in
	/// <c>LocalAvoidanceVelocity</c>, passes <c>member.actor.fallenOver</c> (a managed field read,
	/// which does not throw), and is then asked for <c>Position()</c> -- which reaches
	/// <c>base.transform</c> and throws. Same defect, same mechanism and the same remedy as
	/// <c>Actor.OnDestroy</c> (X-49) one register out.
	/// </para>
	/// <para>
	/// <b>The backstop, not the fix.</b> The path that actually destroyed seated bots is
	/// <c>VehicleSpawner.OnWorldReset</c>, closed by <c>Vehicle.EjectOccupants</c>. This is here
	/// so that the NEXT path to destroy a bot -- a slot pool being cleared, a scene torn down, one
	/// not yet written -- does not reopen the same 2,044-exception cascade (O-D9).
	/// </para>
	/// </remarks>
	private void OnDestroy()
	{
		if (InSquad())
		{
			squad.DropMember(this);
		}
		BotCover.ForgetBotSpot(ownCoverSpot);
		ownCoverSpot = null;
	}

	public bool InSquad()
	{
		return squad != null;
	}

	/// <summary>The one line <see cref="Velocity"/> writes for a body walking a path with no squad.</summary>
	private void ReportPathWithoutSquad()
	{
		if (reportedPathWithoutSquad)
		{
			return;
		}
		reportedPathWithoutSquad = true;
		Debug.LogWarning("[ai] " + base.name + " (team " + actor.team + ") is walking a path with no squad: aiControlled "
			+ actor.aiControlled + ", seated " + actor.IsSeated() + ", dead " + actor.dead + ", at " + actor.Position());
	}

	/// <summary>
	/// A body a connection has just claimed leaves the bot side of the game: off its squad, its
	/// cover and its path, its AI stopped until a squad takes it again (phase P29).
	/// </summary>
	/// <remarks>
	/// Parking the brain (<c>enabled = false</c>) stops Unity's callbacks and not the running
	/// coroutines, which only idle. A body a bot had been using still sat on its squad's roster,
	/// so the commander counted and ordered a player as one of its bots.
	/// </remarks>
	public void HandOverToPlayer()
	{
		LeaveCover();
		CancelPath();
		if (InSquad())
		{
			squad.DropMember(this);
		}
		squad = null;
		StopAllCoroutines();
		CancelInvoke();
		aiCoroutinesAwaitSquad = true;
	}

	public void AssignedToSquad(Squad squad)
	{
		this.squad = squad;
		reportedPathWithoutSquad = false;
		if (IsSquadLeader())
		{
			EmoteRegroup();
		}
		else
		{
			EmoteHailLeaderSlow();
		}
		if (aiCoroutinesAwaitSquad)
		{
			StartAiCoroutines();
		}
	}

	public bool IsSquadLeader()
	{
		return squad.Leader() == this;
	}

	/// <summary>
	/// Sends a bot that has just joined <paramref name="joined"/> after the others (phase P29): to
	/// where its new leader is going, or into cover round him when the squad is holding its ground.
	/// </summary>
	public void JoinedSquad(Squad joined)
	{
		AiActorController leader = joined.Leader();
		if (leader == null || leader == this || leader.actor == null || actor.IsSeated() || IsFallingBack())
		{
			return;
		}
		if (joined.state == Squad.State.DigIn)
		{
			FindCoverAtPoint(leader.actor.Position());
			return;
		}
		if (InCover())
		{
			LeaveCover();
		}
		Vector3 goal = leader.hasPath || leader.calculatingPath ? leader.lastGotoPoint : leader.actor.Position();
		Goto(goal + Vector3.Scale(UnityEngine.Random.insideUnitSphere, new Vector3(3f, 0f, 3f)));
	}

	public bool InCover()
	{
		return inCover;
	}

	public void EmoteRegroup()
	{
		actor.EmoteRegroup();
	}

	public void EmoteMoveOrder(Vector3 target)
	{
		LookAt(target);
		actor.EmoteMove();
	}

	public void EmoteHailLeaderSlow()
	{
		Invoke("EmoteHailLeader", UnityEngine.Random.Range(0.6f, 1.5f));
	}

	public void EmoteHailPlayer()
	{
		if (!HasTarget() && FpsActorController.instance != null)
		{
			LookAt(FpsActorController.instance.actor.CenterPosition());
			actor.EmoteHail();
		}
	}

	public void EmoteHailLeader()
	{
		// This runs 0.6-1.5 s after the bot joined (EmoteHailLeaderSlow), and by then the bot can
		// lead the squad itself: a rogue split leaves it alone in a squad of one. Hailing itself
		// was a LookAt of its own position, which turned it to face world north and made Unity log
		// "Look rotation viewing vector is zero".
		if (!HasTarget() && !IsSquadLeader())
		{
			LookAt(squad.Leader().transform.position);
			actor.EmoteHail();
		}
	}

	public void EmoteHalt()
	{
		actor.EmoteHalt();
	}

	public void MarkTakingFireFrom(Vector3 direction)
	{
		takingFireDirection = direction;
		takingFireAction.Start();
	}

	/// <summary>Incoming fire from a known shooter: <see cref="ActorManager"/>'s near-miss warning.</summary>
	public void MarkTakingFireFrom(Vector3 direction, Actor shooter)
	{
		MarkTakingFireFrom(direction);
		NoteAttacker(shooter);
	}

	public bool IsTakingFire()
	{
		return !takingFireAction.TrueDone();
	}

	public override SpawnPoint SelectedSpawnPoint()
	{
		// Back to a flag where the side has an empty vehicle first (phase P32): picking a random
		// front-line flag seven times in ten left the HQ jeeps, tanks and helicopters standing
		// empty for the whole match, so nobody ever drove at an HQ.
		SpawnPoint forVehicle = ActorManager.SpawnPointForIdleVehicle(actor);
		if (forVehicle != null)
		{
			return forVehicle;
		}
		if (UnityEngine.Random.Range(0f, 1f) < 0.3f)
		{
			return ActorManager.RandomSpawnPointForTeam(actor.team);
		}
		return ActorManager.RandomFrontlineSpawnPointForTeam(actor.team);
	}

	public override Transform WeaponParent()
	{
		return weaponParent;
	}

	public bool HasTargetVehicle()
	{
		return targetVehicle != null;
	}

	public void GotoAndEnterVehicle(Vehicle vehicle)
	{
		targetVehicle = vehicle;
		Goto(vehicle.transform.position);
	}

	public void LeaveVehicle()
	{
		targetVehicle = null;
		if (actor.IsSeated())
		{
			actor.LeaveSeat();
		}
	}

	/// <summary>
	/// Whether part of a squad that does not fit <paramref name="emptySeats"/> takes the vehicle
	/// anyway: any seat in a tank or a helicopter, two or more in a car. A boat is never boarded
	/// from here (<see cref="Vehicle.AiShouldEnter"/> refuses anything in water).
	/// </summary>
	private static bool CrewSplitFits(VehicleKind kind, int emptySeats)
	{
		switch (kind)
		{
			case VehicleKind.Armour:
			case VehicleKind.Aircraft:
				return emptySeats > 0;
			case VehicleKind.Transport:
				return emptySeats >= 2;
			default:
				return false;
		}
	}

	private List<Vehicle> NearbyNonFullVehicles()
	{
		List<Vehicle> list = new List<Vehicle>(ActorManager.instance.vehicles);
		Vector3 squadPosition = actor.CenterPosition();
		list.RemoveAll((Vehicle vehicle) => !vehicle.AiShouldEnter() || (vehicle.ownerTeam >= 0 && vehicle.ownerTeam != actor.team) || Vector3.Distance(vehicle.transform.position, squadPosition) > 150f);
		list.Sort((Vehicle x, Vehicle y) => Vector3.Distance(x.transform.position, squadPosition).CompareTo(Vector3.Distance(y.transform.position, squadPosition)));
		return list;
	}

	public override void SwitchedToWeapon(Weapon weapon)
	{
	}

	public override bool Crouch()
	{
		if (!base.enabled)
		{
			return false;
		}
		if (InCover())
		{
			return (coverType == CoverPoint.Type.Crouch && (IsReloading() || CoolingDown())) || IsFallingBack();
		}
		// A far shot from the open is taken crouching (phase P28): a smaller body to hit.
		bool onFoot = !actor.IsSeated() && !actor.fallenOver && !actor.inWater;
		return CombatRules.InTheOpen(onFoot, hasPath, HasCover())
			&& sideStepAction.TrueDone()
			&& CombatRules.InTheOpenMove(HasTarget(), targetDistance, IsTakingFire()) == OpenGroundMove.Crouch;
	}

	public override void StartCrouch()
	{
	}

	public override bool EndCrouch()
	{
		return true;
	}

	public override WeaponManager.LoadoutSet GetLoadout()
	{
		WeaponManager.LoadoutSet loadoutSet = new WeaponManager.LoadoutSet();
		loadoutSet.primary = WeaponManager.EntryNamed(PinnedOr(primaryWeaponNames[UnityEngine.Random.Range(0, primaryWeaponNames.Length)], LoadoutSlot.Primary));
		loadoutSet.secondary = WeaponManager.EntryNamed(PinnedOr(secondaryWeaponNames[UnityEngine.Random.Range(0, secondaryWeaponNames.Length)], LoadoutSlot.Secondary));
		loadoutSet.gear1 = WeaponManager.EntryNamed(PinnedOr(gearNames[UnityEngine.Random.Range(0, gearNames.Length)], LoadoutSlot.Gear1));
		return loadoutSet;
	}

	// Ledger X-27. A networked player's server-side body comes through here, so which weapon
	// a lane-B shooter holds was a random draw and two runs of one programme were not
	// comparable shot-for-shot (weapon 1, 1, 15 across three runs; 30 shots against 14).
	//
	// THE DRAW IS ALWAYS CONSUMED, and that is structural rather than a discipline: `drawn` is
	// an ARGUMENT, so C# evaluates the Random.Range call before this method is entered and no
	// edit inside it can skip one. Pinning a loadout therefore cannot shift the RNG sequence
	// for anything else the seed governs — the same argument PinnedSpawnPointDirectory makes
	// for spawn selection, where the reservoir draw is likewise still taken.
	//
	// With no directory installed — every configuration that ships — this returns `drawn`
	// and the behaviour is what it was before the seam existed.
	private static string PinnedOr(string drawn, LoadoutSlot slot)
	{
		ILoadoutDirectory directory = NetServerBindings.Loadouts;
		if (directory == null)
		{
			return drawn;
		}

		string forced = directory.OverrideFor(slot);
		return string.IsNullOrEmpty(forced) ? drawn : forced;
	}

	private void SwitchToPrimaryWeapon()
	{
		for (int i = 0; i < actor.weapons.Length; i++)
		{
			if (actor.weapons[i] != null && actor.weapons[i].HasAnyAmmo())
			{
				actor.SwitchWeapon(i);
				break;
			}
		}
	}

	private void SwitchToEffectiveWeapon(Actor target)
	{
		Actor.TargetType targetType = target.GetTargetType();
		float range = Vector3.Distance(base.transform.position, target.transform.position);
		int num = -1;
		for (int i = 0; i < actor.weapons.Length; i++)
		{
			Weapon weapon = actor.weapons[i];
			if (!(weapon != null) || !weapon.HasAnyAmmo() || !weapon.EffectiveAtRange(range))
			{
				continue;
			}
			switch (weapon.EffectivenessAgainst(targetType))
			{
				case Weapon.Effectiveness.Preferred:
					if (!weapon.IsEmpty())
					{
						actor.SwitchWeapon(i);
						return;
					}
					num = i;
					break;
				case Weapon.Effectiveness.Yes:
					if (weapon.HasAnyAmmo() && weapon.EffectiveAtRange(range))
					{
						num = i;
					}
					break;
			}
		}
		if (num != -1)
		{
			actor.SwitchWeapon(num);
		}
	}

	// Not compiled into the dedicated server: IMGUI is stripped there, and Unity logs
	// 'OnGUI function detected ... not called' for every instance -- once per bot, 402
	// lines in one 100-bot match (B4, 2026-09-30).
	//
	// Nor into a release client: it draws only with ActorManager.debug on, yet Unity calls
	// an OnGUI twice a frame (layout and repaint) for every instance that has one -- every
	// bot of an offline match. Vehicle.OnGUI is the same overlay and cost a networked client
	// 0.6 ms a frame on Forest Lake (development build profile, 2026-10-02). The Editor and
	// development builds keep it.
#if !UNITY_SERVER && (UNITY_EDITOR || DEVELOPMENT_BUILD)
	private void OnGUI()
	{
		if (!ActorManager.instance.debug || actor.dead || !(Camera.main != null))
		{
			return;
		}
		float num = Vector3.Dot(actor.CenterPosition() - Camera.main.transform.position, Camera.main.transform.forward);
		if (num > 1f && num < 100f)
		{
			Vector3 vector = Camera.main.WorldToScreenPoint(actor.CenterPosition() + Vector3.up);
			GUI.skin.label.alignment = TextAnchor.UpperCenter;
			GUI.Label(new Rect(vector.x - 100f, (float)Screen.height - vector.y, 200f, 50f), string.Concat("Squad #", squad.number, ": ", squad.state, (!squad.IsGroupedUp()) ? string.Empty : " grouped"));
			if (!stayInCoverAction.TrueDone())
			{
				GUI.Label(new Rect(vector.x - 100f, (float)Screen.height - vector.y + 20f, 200f, 50f), "Staying in cover");
			}
			if (blockerAhead)
			{
				GUI.Label(new Rect(vector.x - 100f, (float)Screen.height - vector.y + 40f, 200f, 50f), "Blocker ahead!");
			}
		}
	}
#endif

	public override bool IsGroupedUp()
	{
		return squad != null && squad.IsGroupedUp();
	}

	public override bool IsSprinting()
	{
		return !sprintAction.TrueDone();
	}
}
