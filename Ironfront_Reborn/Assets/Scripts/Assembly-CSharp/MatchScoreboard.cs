/// <summary>
/// The offline match's score, flag count and win condition — the state that used to live inside
/// <see cref="ScoreUi"/>. debt-closure phase 2 task 2c, ledger C-4, closing V8 D9.
/// </summary>
/// <remarks>
/// <para>
/// <b>The divergence this closes.</b> Score, flags and the victory check were fields and static
/// methods on a <c>MonoBehaviour</c> that lives on the HUD canvas. Every one of those methods
/// opened with <c>if (instance == null) return;</c> — so on a headless server, where no HUD is
/// instantiated, the guard did not merely skip a redraw: the match neither scored nor ended.
/// V8 recorded that as D9; V10 task 7 closed only the RENDERING half by adding
/// <see cref="ScoreUi.SetAuthoritativeState"/>. This is the state half.
/// </para>
/// <para>
/// <b>Not a <c>MonoBehaviour</c>, and that is the entire point.</b> It needs no scene object, no
/// prefab and no authoring step, so it exists identically on a headless server and on a client.
/// <see cref="ScoreUi"/> now renders what this holds and owns no match state of its own.
/// </para>
/// <para>
/// <b>This is the OFFLINE match's scoreboard.</b> A networked match is scored by
/// <c>Ironfront.Net.Replication.Match.MatchStateMachine</c> on the server and rendered on a
/// client through <see cref="ScoreUi.SetAuthoritativeState"/>, which deliberately never re-enters
/// the mutators here — feeding server TOTALS through delta-only mutators would re-run
/// <see cref="ScoreMultiplier"/> and double-drive the victory check (V10 D11). A CI gate (G5)
/// fails any reference to <see cref="AddScore"/> or <see cref="AddFlag"/> from
/// <c>Net/Client/</c> for exactly that reason.
/// </para>
/// <para>
/// <b><c>GameManager</c> and <c>ActorManager</c> are reached directly and that is not the same
/// mistake.</b> Both are ordinary scene components a dedicated server loads and runs; it is
/// specifically the UI canvas that a headless build does not have. Injecting them as delegates
/// would buy nothing here and would hide where the victory rule actually reads from.
/// </para>
/// </remarks>
public sealed class MatchScoreboard
{
	private static MatchScoreboard current;

	/// <summary>
	/// The live scoreboard, created on first use.
	/// </summary>
	/// <remarks>
	/// Created on demand rather than by a scene object, so the first caller — which on a
	/// dedicated server is <c>Actor.Die</c> awarding a kill — always finds one. That is the
	/// difference from the old <c>ScoreUi.instance</c>, whose null meant "silently score nothing".
	/// </remarks>
	public static MatchScoreboard Current => current ?? (current = new MatchScoreboard());

	/// <summary>Blue's score.</summary>
	public int BlueScore { get; private set; }

	/// <summary>Red's score.</summary>
	public int RedScore { get; private set; }

	/// <summary>Capture points blue holds.</summary>
	public int BlueFlags { get; private set; }

	/// <summary>Capture points red holds.</summary>
	public int RedFlags { get; private set; }

	/// <summary>True once a team has won. Latched; a second win does nothing.</summary>
	public bool GameEnded { get; private set; }

	/// <summary>
	/// The lead one team needs over the other to win, read from <c>GameManager</c>.
	/// </summary>
	/// <remarks>
	/// Read through rather than copied at construction: <c>GameManager.victoryPoints</c> is one
	/// of the five loose booleans-and-numbers ledger C-5 records as unowned (excluded by P-D10),
	/// and caching it here would add a second copy of a value that phase has not yet decided
	/// where to put. Falls back to a sane number when no <c>GameManager</c> exists, so a test or
	/// a bare scene does not divide by zero.
	/// </remarks>
	public int VictoryPoints =>
		GameManager.instance != null ? GameManager.instance.victoryPoints : DefaultVictoryPoints;

	/// <summary>Used only when no <c>GameManager</c> is present. Never a live-match value.</summary>
	private const int DefaultVictoryPoints = 100;

	/// <summary>Any number changed. The HUD redraws on this.</summary>
	// System.Action, fully qualified: Assembly-CSharp declares its own Action -- a countdown
	// timer (see ScoreUi's bluePulse) -- which shadows the BCL delegate and makes a bare
	// "event Action" a compile error rather than a subtle mis-binding.
	public event System.Action Changed;

	/// <summary>A team won. True for blue. Fires once per match.</summary>
	public event System.Action<bool> Ended;

	/// <summary>Blue scored a kill, or red did, or both.</summary>
	public event System.Action<bool, bool> Scored;

	/// <summary>
	/// Awards a kill to each team by the count given, and checks the victory condition.
	/// </summary>
	/// <remarks>
	/// Delta-only with no setter, exactly as it was on <see cref="ScoreUi"/> — which is why
	/// authoritative TOTALS must never be routed through it (V10 D11).
	/// </remarks>
	public void AddScore(int blue, int red)
	{
		BlueScore += blue * ScoreMultiplier(BlueFlags);
		RedScore += red * ScoreMultiplier(RedFlags);

		Scored?.Invoke(blue > 0, red > 0);
		Changed?.Invoke();

		if (GameEnded)
		{
			return;
		}
		if (BlueScore >= RedScore + VictoryPoints)
		{
			Win(true);
		}
		else if (RedScore >= BlueScore + VictoryPoints)
		{
			Win(false);
		}
	}

	/// <summary>Records a capture, and checks the spawn-point-loss win condition.</summary>
	public void AddFlag(int blue, int red)
	{
		BlueFlags += blue;
		RedFlags += red;
		Changed?.Invoke();

		if (GameEnded)
		{
			return;
		}

		// The elapsed-time gate keeps the opening moments of a match, before spawn points have
		// registered themselves, from reading as one team having already lost every one.
		if (GameManager.instance == null || GameManager.instance.ElapsedGameTime() <= 1f)
		{
			return;
		}
		if (!ActorManager.HasSpawnPoint(0))
		{
			Win(false);
		}
		else if (!ActorManager.HasSpawnPoint(1))
		{
			Win(true);
		}
	}

	/// <summary>Ends the match. Latched, so the second caller does nothing.</summary>
	public void Win(bool blue)
	{
		if (GameEnded)
		{
			return;
		}
		GameEnded = true;
		Ended?.Invoke(blue);
	}

	/// <summary>A team's score multiplier at this flag count.</summary>
	public static int ScoreMultiplier(int flags)
	{
		return flags;
	}

	/// <summary>Zeroes everything. Called when a match starts.</summary>
	public void Reset()
	{
		BlueScore = 0;
		RedScore = 0;
		BlueFlags = 0;
		RedFlags = 0;
		GameEnded = false;
		Changed?.Invoke();
	}
}
