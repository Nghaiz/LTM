using System;
using System.Collections.Generic;
using Ironfront.Net.Replication.Match;
using Ironfront.Net.Replication.Movement;
using Ironfront.Net.Unity;
using UnityEngine;

/// <summary>
/// A capture point: its geometry, its flag, its contested-spawn safety, and — offline only —
/// its ownership arithmetic.
/// </summary>
/// <remarks>
/// <para>
/// <b>Phase-V8 split this class in two.</b> Before it, <see cref="UpdateOwner"/> ran on every
/// role at 1 Hz and decided ownership, while the netcode's <c>CapturePointState</c> decided it
/// again at 30 Hz with different rules — and the one every respawn read was the one nothing
/// replicated. The arithmetic is now authoritative elsewhere (D1); this component keeps the
/// geometry, the flag rendering, and <see cref="GetSpawnPosition"/>'s contested-spawn logic,
/// which is real gameplay and is computed nowhere else.
/// </para>
/// <para>
/// <b>Offline is unchanged, deliberately</b> (D2). The single-player game is a shipping
/// product, so <see cref="UpdateOwner"/> is disabled by role rather than deleted, and at
/// <see cref="NetRole.Offline"/> it runs exactly the code it always did. It is disabled on the
/// CLIENT as well as the server: a client running its own 1 Hz arithmetic would fight the
/// <c>S_CAPTURE_POINT</c> messages it is already being sent and reproduce the same disagreement
/// one process further out.
/// </para>
/// <para>
/// <b>Ownership has exactly one write path</b> (D3): <see cref="ApplyAuthoritativeOwner"/>.
/// Two writers into a private field is how the original bug happened.
/// </para>
/// </remarks>
public class CapturePoint : SpawnPoint
{
	private const float UPDATE_RATE = 1f;

	private const float CAPTURE_RATE_PER_PERSON = 0.05f;

	private const int HQ_QUALITY_LEVEL = 5;

	private const float CONTESTED_SPAWNPOINT_SAFE_DOT = 0.8f;

	public Transform contestedSpawnpointContainer;

	private Vector3[] contestedSpawnpointFlatDirection;

	private bool[] contestedSpawnpointIsSafe;

	public float captureRange = 10f;

	/// <summary>
	/// Ownership gained per second by a single attacker standing on this point.
	/// </summary>
	/// <remarks>
	/// Phase-V8 D6. Authored per point, so one flag can be slower to take than another; the
	/// default matches <c>MatchController._captureSpeed</c>, so an unedited prefab behaves
	/// exactly as it did before this field existed. Read by the server at startup and never
	/// by this component — the offline arithmetic below keeps its own long-standing 0.05
	/// per-person-per-second rate so that D2's promise is literal.
	/// </remarks>
	public float captureSpeed = 0.2f;

	public bool canBeCaptured = true;

	public Transform flagParent;

	public GameObject lqFlag;

	public GameObject hqFlag;

	private float control = 1f;

	private int pendingOwner;

	private Renderer flagRenderer;

	private bool isContested;

	private Action unsafeAction = new Action(10f);

	private bool playerWasInRadius;

	protected override void Awake()
	{
		base.Awake();
		if (contestedSpawnpointContainer != null)
		{
			int childCount = contestedSpawnpointContainer.childCount;
			contestedSpawnpointIsSafe = new bool[childCount];
			contestedSpawnpointFlatDirection = new Vector3[childCount];
			for (int i = 0; i < childCount; i++)
			{
				contestedSpawnpointFlatDirection[i] = (contestedSpawnpointContainer.GetChild(i).transform.position - base.transform.position).ToGround().normalized;
			}
			Renderer[] componentsInChildren = contestedSpawnpointContainer.GetComponentsInChildren<Renderer>();
			Renderer[] array = componentsInChildren;
			foreach (Renderer renderer in array)
			{
				renderer.enabled = false;
			}
			ClearContestedSpawnpointSafeFlags();
		}
		if (QualitySettings.GetQualityLevel() >= 5)
		{
			lqFlag.SetActive(false);
			hqFlag.SetActive(true);
			flagRenderer = hqFlag.GetComponent<Renderer>();
		}
		else
		{
			lqFlag.SetActive(true);
			hqFlag.SetActive(false);
			flagRenderer = lqFlag.GetComponent<Renderer>();
		}
	}

	private void Start()
	{
		if (GameManager.instance.reverseMode)
		{
			if (owner == 0)
			{
				owner = 1;
			}
			else if (owner == 1)
			{
				owner = 0;
			}
		}
		SetOwner(owner);
		if (owner == -1)
		{
			if (GameManager.instance.assaultMode)
			{
				SetOwner(1);
			}
			else
			{
				control = 0f;
			}
		}
		// D2. The reverseMode / assaultMode initialisation above runs in EVERY role -- it
		// decides the OPENING ownership, which the server then adopts as its own initial value,
		// so skipping it would start a networked match on a different map layout than the
		// single-player one. Only the repeating arithmetic below is role-gated.
		if (NetContext.IsOffline)
		{
			InvokeRepeating("UpdateOwner", 1f, 1f);
		}
	}

	private void Update()
	{
		Vector3 localPosition = flagParent.localPosition;
		localPosition.y = 1.2f + 4.8f * control;
		flagParent.localPosition = Vector3.Lerp(flagParent.localPosition, localPosition, 3f * Time.deltaTime);
		UpdateFlagIndicator();
	}

	private void UpdateOwner()
	{
		if (!canBeCaptured)
		{
			return;
		}
		int num = owner;
		List<Actor> list = ActorManager.AliveActorsInRange(base.transform.position, captureRange);
		Dictionary<int, int> dictionary = new Dictionary<int, int>();
		isContested = false;
		if (contestedSpawnpointContainer != null)
		{
			ClearContestedSpawnpointSafeFlags();
		}
		foreach (Actor item in list)
		{
			if (dictionary.ContainsKey(item.team))
			{
				Dictionary<int, int> dictionary2;
				Dictionary<int, int> dictionary3 = (dictionary2 = dictionary);
				int team;
				int key = (team = item.team);
				team = dictionary2[team];
				dictionary3[key] = team + 1;
			}
			else
			{
				dictionary.Add(item.team, 1);
			}
			if (item.team != owner)
			{
				isContested = true;
				if (contestedSpawnpointContainer != null)
				{
					UpdateContestedSpawnpointSafeFlags(item);
				}
			}
		}
		int num2 = -1;
		int num3 = 0;
		int num4 = 0;
		for (int i = 0; i < 2; i++)
		{
			if (dictionary.ContainsKey(i) && dictionary[i] > num4)
			{
				num2 = i;
				num3 = num4;
				num4 = dictionary[i];
			}
		}
		int num5 = num4 - num3;
		if (num2 != -1)
		{
			if (num2 != pendingOwner)
			{
				control -= (float)num5 * 0.05f;
				if (control <= 0f)
				{
					SetOwner(num2);
					control = 0.01f;
				}
			}
			else
			{
				control = Mathf.Clamp01(control + (float)num5 * 0.05f);
				if (control == 1f && owner != pendingOwner)
				{
					SetOwner(pendingOwner);
				}
			}
		}
		if (isContested)
		{
			unsafeAction.Start();
		}
		SetFlagVisible(control > 0f);
	}

	/// <summary>
	/// Drives the top-left capture indicator from the LOCAL player's distance to this point.
	/// </summary>
	/// <remarks>
	/// <para>
	/// <b>This lived in <see cref="UpdateOwner"/> and therefore never ran in a networked match.</b>
	/// That method is started by <c>InvokeRepeating</c> only when <c>NetContext.IsOffline</c>, so
	/// the V8 split that role-gated the capture arithmetic took the three HUD calls with it. The
	/// indicator is authored, wired and anchored top-left in <c>Ingame UI Container.prefab</c>,
	/// and it was simply never turned on. It belongs in <see cref="Update"/>, which runs in every
	/// role, every frame, and already reads <see cref="control"/> and <c>owner</c>.
	/// </para>
	/// <para>
	/// <b>The subject is the local player, not "any non-AI actor".</b> The old test set its flag
	/// for any actor with <c>!aiControlled</c> in range, which in single-player is the local
	/// player by coincidence and in a networked match is any human on the map — so a stranger
	/// capturing a point would have flashed the indicator on this client's HUD. Asking about
	/// <c>FpsActorController.instance</c> is the same question the widget is actually answering.
	/// </para>
	/// <para>
	/// Both singletons are null-guarded for <see cref="SetFlagVisible"/>'s reason: a dedicated
	/// server runs this component with no local player and no HUD.
	/// </para>
	/// </remarks>
	private void UpdateFlagIndicator()
	{
		if (IngameUi.instance == null)
		{
			return;
		}
		FpsActorController local = FpsActorController.instance;
		bool inRadius = local != null && local.actor != null && !local.actor.dead
			&& (local.actor.transform.position - base.transform.position).sqrMagnitude
			   <= captureRange * captureRange;

		if (inRadius && !playerWasInRadius)
		{
			IngameUi.instance.ShowFlagIndicator();
		}
		else if (!inRadius && playerWasInRadius)
		{
			IngameUi.instance.HideFlagIndicator();
		}
		if (inRadius)
		{
			IngameUi.instance.SetFlagIndicator(control, owner);
		}
		playerWasInRadius = inRadius;
	}

	/// <summary>
	/// Applies the server's ownership to this component. The ONLY write path to
	/// <c>owner</c>, <see cref="control"/>, <see cref="pendingOwner"/> and
	/// <see cref="isContested"/> once the netcode is running. Phase-V8 D3.
	/// </summary>
	/// <param name="team">The owning team, or -1 for neutral.</param>
	/// <param name="authoritativeControl">0..1 capture progress, for the flag-pole height.</param>
	/// <param name="contested">Somebody hostile to the owner is inside the radius.</param>
	/// <remarks>
	/// <para>
	/// <see cref="SetOwner"/> is called only on an actual change of hands, so
	/// <c>MatchScoreboard.AddFlag</c> and <c>MinimapUi.UpdateSpawnPointButtons</c> still fire exactly
	/// once per flip and no more — at 30 Hz an unconditional call would add a flag to the
	/// scoreboard thirty times a second for a point nobody touched.
	/// </para>
	/// <para>
	/// Called by the server's <c>CapturePointSlave</c> every tick, and by the client's
	/// <c>S_CAPTURE_POINT</c> handler on every message. Both feed the same fields through the
	/// same door.
	/// </para>
	/// </remarks>
	public void ApplyAuthoritativeOwner(int team, float authoritativeControl, bool contested)
	{
		isContested = contested;
		control = Mathf.Clamp01(authoritativeControl);

		if (team != owner)
		{
			SetOwner(team);
		}

		if (isContested)
		{
			unsafeAction.Start();
		}

		// The one line of rendering that was trapped inside the arithmetic. Null-guarded
		// because a dedicated server has no flag renderer to speak of.
		SetFlagVisible(control > 0f);
	}

	/// <summary>
	/// Recomputes the contested-spawn safe directions from authoritative presence, and reports
	/// whether anybody hostile to the current owner is inside the capture radius.
	/// </summary>
	/// <remarks>
	/// <para>
	/// Phase-V8 D4 and D5. Disabling <see cref="UpdateOwner"/> wholesale would leave
	/// <see cref="contestedSpawnpointIsSafe"/> stuck at all-true and quietly turn safe spawning
	/// into random spawning, with nothing logged anywhere — so the split is presence-versus-
	/// arithmetic, not on-versus-off.
	/// </para>
	/// <para>
	/// Fed from the caller's reusable presence span rather than
	/// <c>ActorManager.AliveActorsInRange</c>, which allocates a <c>List&lt;Actor&gt;</c> and a
	/// <c>Dictionary&lt;int,int&gt;</c> on every call. Nothing here allocates.
	/// </para>
	/// <para>
	/// <b>"Contested" here is the original's sense — a non-owner is present — not the wire's.</b>
	/// <c>CaptureFlags.Contested</c> means both teams are present, and the two differ exactly
	/// when an owned point is being attacked by one team and defended by nobody: not contested
	/// on the wire, and precisely when a defender most needs to spawn away from the attackers.
	/// </para>
	/// </remarks>
	public bool RefreshPresence(ReadOnlySpan<ActorPresence> actors)
	{
		bool hasSafeFlags = contestedSpawnpointContainer != null && contestedSpawnpointIsSafe != null;
		if (hasSafeFlags)
		{
			ClearContestedSpawnpointSafeFlags();
		}

		Vector3 centre = base.transform.position;
		float rangeSquared = captureRange * captureRange;
		bool hostilePresent = false;

		for (int i = 0; i < actors.Length; i++)
		{
			if (!actors[i].IsAlive)
			{
				continue;
			}
			Vec3 p = actors[i].Position;
			Vector3 position = new Vector3(p.X, p.Y, p.Z);
			if ((position - centre).sqrMagnitude > rangeSquared)
			{
				continue;
			}
			if (actors[i].Team == owner)
			{
				continue;
			}
			hostilePresent = true;
			if (hasSafeFlags)
			{
				UpdateContestedSpawnpointSafeFlags(position);
			}
		}

		return hostilePresent;
	}

	/// <summary>Shows or hides the flag, tolerating a build that has no renderer.</summary>
	private void SetFlagVisible(bool visible)
	{
		if (flagRenderer != null)
		{
			flagRenderer.enabled = visible;
		}
	}

	private void ClearContestedSpawnpointSafeFlags()
	{
		for (int i = 0; i < contestedSpawnpointIsSafe.Length; i++)
		{
			contestedSpawnpointIsSafe[i] = true;
		}
	}

	private void UpdateContestedSpawnpointSafeFlags(Actor attacker)
	{
		UpdateContestedSpawnpointSafeFlags(attacker.Position());
	}

	/// <summary>
	/// Marks every contested spawn point facing <paramref name="attackerPosition"/> unsafe.
	/// </summary>
	/// <remarks>
	/// Position-based so the offline scan and <see cref="RefreshPresence"/> share ONE copy of
	/// the dot-product rule. Two copies of a rule this quiet — a threshold on a flattened
	/// direction, whose only symptom when wrong is that players occasionally spawn facing the
	/// enemy — would diverge without anybody noticing.
	/// </remarks>
	private void UpdateContestedSpawnpointSafeFlags(Vector3 attackerPosition)
	{
		Vector3 normalized = (attackerPosition - base.transform.position).ToGround().normalized;
		for (int i = 0; i < contestedSpawnpointIsSafe.Length; i++)
		{
			if (contestedSpawnpointIsSafe[i])
			{
				float num = Vector3.Dot(normalized, contestedSpawnpointFlatDirection[i]);
				contestedSpawnpointIsSafe[i] = num < 0.8f;
			}
		}
	}

	public override bool IsSafe()
	{
		return unsafeAction.TrueDone();
	}

	private void SetOwner(int team)
	{
		int num = 0;
		int num2 = 0;
		switch (team)
		{
		case 0:
			num2++;
			break;
		case 1:
			num++;
			break;
		}
		if (team != owner)
		{
			if (owner == 0)
			{
				num2--;
			}
			else if (owner == 1)
			{
				num--;
			}
		}
		owner = team;
		pendingOwner = team;
		// Null-guarded: SetOwner is now on the SERVER's path (it is how a flip reaches
		// SpawnPoint.owner), and a dedicated server has no flag renderer. Touching .material
		// would also instantiate a material clone per point on a process with no graphics.
		if (flagRenderer != null)
		{
			flagRenderer.material.color = Color.Lerp(ColorScheme.TeamColor(team), Color.black, 0.2f);
		}
		// debt-closure phase 2 task 2c. See Actor.Die for why this is no longer the HUD's call.
		//
		// P12 D-2: offline only, the same gate line 147 puts on this file's own arithmetic.
		// SetOwner is reached from ApplyAuthoritativeOwner -- the SERVER-DRIVEN capture path --
		// so on a networked client every flip fed the local scoreboard, and ScoreUi.UpdateUi
		// then painted those locally-counted numbers over the server's. The server's own flag
		// count for the kill multiplier lives in MatchStateMachine.OwnedPointCount, not here,
		// so nothing authoritative reads what this gate stops writing.
		//
		// The two MinimapUi calls below are deliberately NOT gated: they are how a client
		// RENDERS a flip it was told about, which is exactly what it should be doing.
		if (NetContext.IsOffline)
		{
			MatchScoreboard.Current.AddFlag(num2, num);
		}
		MinimapUi.UpdateSpawnPointButtons();
		// debt-closure phase 2 task 2d, ledger C-6: the point now carries a minimap marker that
		// recolours as it flips. SetMarker is idempotent by subject, so calling it on every flip
		// recolours rather than stacking a second icon per capture.
		MinimapUi.SetMarker(base.transform, ColorScheme.TeamColor(team));
	}

	public override float GotoRadius()
	{
		return captureRange * 0.9f;
	}

	public override Vector3 GetSpawnPosition()
	{
		if (isContested && contestedSpawnpointContainer != null)
		{
			return GetSafeSpawnPosition();
		}
		return base.GetSpawnPosition();
	}

	/// <summary>
	/// Picks a contested-safe child, ground-snapped. Same defect as X-81's container-branch
	/// fix, one field over: this used to return <c>contestedSpawnpointContainer.GetChild(...)
	/// .position</c> verbatim. Shares <see cref="SpawnPoint.SnappedContainerChildPosition"/>
	/// rather than a second snap/warn implementation.
	/// </summary>
	private Vector3 GetSafeSpawnPosition()
	{
		int childCount = contestedSpawnpointContainer.childCount;
		if (childCount == 0)
		{
			return base.GetSpawnPosition();
		}
		int num = UnityEngine.Random.Range(0, childCount);
		for (int i = 0; i < childCount; i++)
		{
			int num2 = (num + i) % childCount;
			if (contestedSpawnpointIsSafe[num2])
			{
				return SnappedContainerChildPosition(contestedSpawnpointContainer.GetChild(num2));
			}
		}
		return SnappedContainerChildPosition(contestedSpawnpointContainer.GetChild(num));
	}
}
