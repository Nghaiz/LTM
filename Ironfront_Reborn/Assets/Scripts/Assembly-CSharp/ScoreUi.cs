using Ironfront.Net.Unity;
using UnityEngine;
using UnityEngine.UI;

/// <summary>
/// Draws the scoreboard. Holds no match state.
/// </summary>
/// <remarks>
/// <para>
/// <b>V8 D9 is closed.</b> This class used to own the score, the flag count, the multiplier and
/// the victory check, all behind <c>if (instance == null) return;</c> — so a headless server,
/// which instantiates no HUD, ran a match that neither scored nor ended. V10 task 7 closed the
/// rendering half by adding <see cref="SetAuthoritativeState"/>; debt-closure phase 2 task 2c
/// moved the state itself to <see cref="MatchScoreboard"/>, which is a plain class and therefore
/// exists on every build. What is left here is drawing.
/// </para>
/// <para>
/// Two sources feed it and they never mix. Offline, <see cref="MatchScoreboard"/> raises
/// <c>Changed</c> and <see cref="UpdateUi"/> redraws. On a networked client,
/// <see cref="SetAuthoritativeState"/> writes the server's totals straight to the text fields
/// and the bars, and never touches the offline scoreboard — routing them through it would
/// re-enter the multiplier and double-drive the win check (V10 D11). Both paths lay out the
/// bars through the same <see cref="ApplyScoreBars"/>, so the two pictures cannot disagree.
/// </para>
/// </remarks>
public class ScoreUi : MonoBehaviour
{
	public static ScoreUi instance;

	public Text blueScoreText;

	public Text redScoreText;

	public Text blueFlagsText;

	public Text redFlagsText;

	public Text victoryText;

	// V10 task 7, checklist row E5. Authored on the shipped prefab since 2026-08-19 (debt
	// closure phase 1 task 1.6, ledger A-9): Score UI Canvas/Phase Row/Phase Label and
	// /Phase Timer. The flag-text fallback below is now dead on the shipped prefab and is kept
	// only for a prefab that predates the authoring; it has to retire when task 8 lands,
	// because capture points start writing to those same labels. Pinned by
	// AssetWiringDetectors.ScoreUiTextRefsAreAssigned, which fails if either field is unset,
	// names no object, or points at a label something else already drives.
	public Text phaseText;

	public Text phaseTimerText;

	// V10 task 7, checklist row E5, ledger A-6. E5 names THREE elements -- phase, timer and
	// human count -- and this is the third. Until it was authored the count was concatenated
	// into the phase label, which made the label's width change every time somebody joined and
	// left the count with no independent position, style or visibility. Pinned by
	// AssetWiringDetectors.ScoreUiTextRefsAreAssigned alongside the other two.
	public Text humanCountText;

	public Image blueBar;

	public Image redBar;

	public Image intercept;

	public Image victoryScreen;

	private Canvas canvas;

	private Color blue;

	private Color red;

	private Action bluePulse = new Action(0.5f);

	private Action redPulse = new Action(0.5f);

	// V10 task 7 (D11): last values rendered by SetAuthoritativeState, so a networked client
	// rebuilds its Text strings only when the server's numbers actually change rather than once
	// a frame. secondsRemaining starts at -2, distinct from the -1 "no timer" sentinel, so the
	// very first call always renders.
	private bool hasAuthoritativeState;

	private int lastPhase;

	private int lastTickets0 = -1;

	private int lastTickets1 = -1;

	private int lastSecondsRemaining = -2;

	private int lastHumanPlayerCount = -1;

	// P11. The bars are geometry over (blueScore, redScore, victoryPoints), so victoryPoints has
	// to join the early-return comparison below: a host that changes the victory margin between
	// rounds moves every bar without moving either score, and a comparison that does not see it
	// would leave the bar drawn to the previous round's scale.
	private int lastVictoryPoints = -1;

	/// <summary>
	/// Draws the victory banner. Driven by <see cref="MatchScoreboard.Ended"/>.
	/// </summary>
	/// <remarks>
	/// The banner is UI and stays here; the DECISION that a team won is not, and moved to
	/// <see cref="MatchScoreboard"/> in debt-closure phase 2 (ledger C-4, closing V8 D9).
	/// </remarks>
	private void OnMatchEnded(bool blue)
	{
		if (victoryScreen == null)
		{
			return;
		}
		victoryScreen.gameObject.SetActive(true);
		Color color = ((!blue) ? red : this.blue);
		color.a = 0.8f;
		victoryScreen.color = color;
		if (victoryText != null)
		{
			victoryText.text = blue ? "BLUE TEAM IS" : "RED TEAM IS";
		}
		Invoke("HideVictoryScreen", 5f);
	}

	/// <summary>Pulses a team's bar on a kill. Purely cosmetic.</summary>
	private void OnScored(bool blueScored, bool redScored)
	{
		if (blueScored)
		{
			bluePulse.Start();
		}
		if (redScored)
		{
			redPulse.Start();
		}
	}

	private void HideVictoryScreen()
	{
		victoryScreen.gameObject.SetActive(false);
	}

	/// <summary>Kept as a forwarder so existing callers do not have to move.</summary>
	/// <remarks>
	/// The rule itself lives on <see cref="MatchScoreboard"/> now. One implementation, two names
	/// — never two implementations.
	/// </remarks>
	public static int ScoreMultiplier(int flags)
	{
		return MatchScoreboard.ScoreMultiplier(flags);
	}

	/// <summary>
	/// Renders the server's authoritative match state and returns. V10 D11: never re-enters
	/// <c>MatchScoreboard.AddScore</c> or <c>AddFlag</c> (both are delta-only with no getters,
	/// while this method's inputs are already totals — feeding them through those mutators
	/// would re-enter <see cref="ScoreMultiplier"/> and double-drive the win check), and never
	/// touches <c>victoryPoints</c> itself.
	/// </summary>
	/// <param name="phase">
	/// <c>Ironfront.Net.Protocol.MatchPhase</c> as a plain <c>int</c> — this file takes no
	/// dependency on <c>Ironfront.Net.Replication</c> for a cosmetic label, the same reason
	/// <c>IngameUi.Hit(int severity)</c> is an <c>int</c> and not an enum.
	/// </param>
	/// <param name="secondsRemaining">
	/// Whole seconds left in the phase, or a negative value meaning "this phase has no timer" —
	/// <c>MatchPhase.Playing</c> ends on the score margin, not a clock, and rendering it as
	/// "0:00" would tell every player the round is over. A negative value hides the timer.
	/// </param>
	/// <param name="victoryPoints">
	/// The lead a side needs to win, from <c>S_MATCH_STATE</c>. The bars are meaningless without
	/// it — both branches of <see cref="ApplyScoreBars"/> divide by it — and it is a
	/// host-editable match setting, so it is sent rather than assumed.
	/// </param>
	/// <remarks>
	/// <para>
	/// <b>Checklist E5 — the phase and timer elements are authored; the human count is not.</b>
	/// <see cref="phaseText"/> and <see cref="phaseTimerText"/> are dedicated elements on the
	/// shipped prefab as of 2026-08-19 (ledger A-9), so the flag-text fallback below no longer
	/// runs there. <see cref="blueScoreText"/> / <see cref="redScoreText"/> take the server's
	/// scores, which since P11 are the same ascending quantity they already showed offline. The fallback to
	/// <see cref="blueFlagsText"/> / <see cref="redFlagsText"/> survives only for a prefab that
	/// predates the authoring, and must be deleted when capture points land (V10 task 8, blocked
	/// on V8 task 1) — from then on those labels are live and borrowing them collides.
	/// <see cref="humanCountText"/> is E5's third element, authored by phase 6 task 6.1's sibling
	/// 6.6 (ledger A-6). The count used to be concatenated into the phase label, which made that
	/// label's width change every time somebody joined; it now renders on its own and the
	/// concatenation survives only as a fallback for a prefab that predates the element.
	/// </para>
	/// <para>
	/// Staleness is not a parameter here — that decision belongs to the presenter, which has the
	/// clock this method does not, and dims the same four fields directly through their already-
	/// public references rather than through a sixth parameter on this signature.
	/// </para>
	/// </remarks>
	public static void SetAuthoritativeState(
		int phase, int score0, int score1, int secondsRemaining, int humanPlayerCount,
		int victoryPoints)
	{
		if (instance == null)
		{
			return;
		}
		if (instance.hasAuthoritativeState
			&& instance.lastPhase == phase
			&& instance.lastTickets0 == score0
			&& instance.lastTickets1 == score1
			&& instance.lastSecondsRemaining == secondsRemaining
			&& instance.lastHumanPlayerCount == humanPlayerCount
			&& instance.lastVictoryPoints == victoryPoints)
		{
			return;
		}
		instance.hasAuthoritativeState = true;
		instance.lastPhase = phase;
		instance.lastTickets0 = score0;
		instance.lastTickets1 = score1;
		instance.lastSecondsRemaining = secondsRemaining;
		instance.lastHumanPlayerCount = humanPlayerCount;
		instance.lastVictoryPoints = victoryPoints;
		if (instance.blueScoreText != null)
		{
			instance.blueScoreText.text = score0.ToString();
		}
		if (instance.redScoreText != null)
		{
			instance.redScoreText.text = score1.ToString();
		}
		// P11, audit F3. The bars are the most prominent element on the scoreboard and until now
		// nothing networked ever touched them: blueBar, redBar and intercept were written only by
		// UpdateUi, the OFFLINE renderer, so a networked client watched a bar driven by an
		// offline scoreboard that never scored. Same geometry as the offline path, by
		// construction -- ApplyScoreBars is the one copy.
		ApplyScoreBars(instance, score0, score1, victoryPoints);
		Text phaseTarget = instance.phaseText != null ? instance.phaseText : instance.blueFlagsText;
		if (phaseTarget != null)
		{
			// The count moves OUT of the phase label the moment a dedicated element exists.
			// Keeping both would render it twice; keeping only the concatenation is the E5 gap
			// (ledger A-6). The fallback survives for a prefab that predates the authoring, and
			// retires with the flag-text fallback below it.
			phaseTarget.text = instance.humanCountText != null
				? PhaseLabel(phase)
				: PhaseLabel(phase) + (humanPlayerCount > 0 ? " (" + humanPlayerCount + ")" : string.Empty);
		}
		if (instance.humanCountText != null)
		{
			// Self-describing, because a bare number in a HUD corner says nothing and a
			// companion static label would be a second thing to author and keep in sync.
			// Zero renders blank rather than "0 players" -- before the first broadcast there is
			// no answer, and stating one would be a fabricated zero.
			instance.humanCountText.text = humanPlayerCount > 0
				? humanPlayerCount + (humanPlayerCount == 1 ? " player" : " players")
				: string.Empty;
		}
		// A negative secondsRemaining means this phase has no clock. Blank, never "0:00".
		Text timerTarget = instance.phaseTimerText != null ? instance.phaseTimerText : instance.redFlagsText;
		if (timerTarget != null)
		{
			timerTarget.text = secondsRemaining >= 0 ? FormatTimer(secondsRemaining) : string.Empty;
		}
		if (instance.phaseText == null || instance.phaseTimerText == null)
		{
			Ironfront.Net.Unity.NetPresenterGate.WarnOnce(
				"scoreui-no-phase-elements",
				"[net] ScoreUi has no dedicated phase/timer Text, so the networked HUD is "
				+ "borrowing the flag labels. That collides with capture points the moment V10 "
				+ "task 8 lands. Client-track item E5 -- assign phaseText and phaseTimerText.");
		}
	}

	private static string PhaseLabel(int phase)
	{
		switch (phase)
		{
		case 0:
			return "Waiting";
		case 1:
			return "Warmup";
		case 2:
			return "Playing";
		case 3:
			return "Ended";
		case 4:
			return "Resetting";
		default:
			return string.Empty;
		}
	}

	private static string FormatTimer(int totalSeconds)
	{
		if (totalSeconds < 0)
		{
			totalSeconds = 0;
		}
		int minutes = totalSeconds / 60;
		int seconds = totalSeconds % 60;
		return minutes.ToString() + ":" + (seconds < 10 ? "0" + seconds : seconds.ToString());
	}

	private void Awake()
	{
		instance = this;
		blue = blueBar.color;
		red = redBar.color;
		canvas = GetComponent<Canvas>();
		victoryScreen.gameObject.SetActive(false);

		// The scoreboard outlives any one HUD: a match that started before this canvas woke has
		// already scored, and Reset() here would throw those points away. Resetting belongs to
		// whatever starts a match, not to whatever draws it.
		MatchScoreboard board = MatchScoreboard.Current;

		// P12 D-2. `Changed` is the OFFLINE renderer and it is subscribed only offline.
		//
		// UpdateUi paints both score labels from the local MatchScoreboard. On a networked
		// client that board is fed by nothing authoritative, so every repaint overwrote the
		// server's numbers -- and SetAuthoritativeState early-returns on unchanged inputs, so
		// they were not restored until the server's own totals next moved. A capture flip was
		// enough to leave the wrong score on screen indefinitely.
		//
		// NOT SUBSCRIBING is the shape, rather than subscribing and early-returning inside
		// UpdateUi -- and the next reader will assume the latter, which is why this says so.
		// The early-return shape buys exactly one thing: surviving a mid-session flip between
		// offline and networked. This project has no such flip; NetContext.Role is set once at
		// startup. Paying for it would mean a handler on a per-flip event that must stay inert,
		// which is a thing to get wrong for a case that cannot happen.
		//
		// OnDestroy mirrors this. An unsubscribe of a handler that was never added is harmless
		// in C#, but a pair that does not match is a pair that lies to the next reader.
		if (NetContext.IsOffline)
		{
			board.Changed += UpdateUi;
		}

		board.Ended += OnMatchEnded;
		board.Scored += OnScored;

		// The first paint is offline's too. Networked, the labels stay at their authored value
		// until the first S_MATCH_STATE arrives, which is the honest reading: this client has
		// not been told the score yet.
		if (NetContext.IsOffline)
		{
			UpdateUi();
		}
	}

	private void OnDestroy()
	{
		if (instance == this)
		{
			instance = null;
		}
		MatchScoreboard board = MatchScoreboard.Current;

		// Mirrors Awake's gate. See it for why the subscription is conditional at all.
		if (NetContext.IsOffline)
		{
			board.Changed -= UpdateUi;
		}

		board.Ended -= OnMatchEnded;
		board.Scored -= OnScored;
	}

	private void UpdateUi()
	{
		MatchScoreboard board = MatchScoreboard.Current;
		int blueScore = board.BlueScore;
		int redScore = board.RedScore;
		int victoryPoints = board.VictoryPoints;
		blueScoreText.text = blueScore.ToString();
		redScoreText.text = redScore.ToString();
		blueFlagsText.text = board.BlueFlags.ToString();
		redFlagsText.text = board.RedFlags.ToString();
		ApplyScoreBars(this, blueScore, redScore, victoryPoints);
	}

	/// <summary>
	/// Positions <see cref="blueBar"/>, <see cref="redBar"/> and <see cref="intercept"/> for one
	/// pair of scores. The ONE copy of the bar geometry.
	/// </summary>
	/// <remarks>
	/// <para>
	/// <b>Why it is extracted (P11 task 3.5).</b> Two renderers now write these three elements —
	/// <see cref="UpdateUi"/> for the offline scoreboard and
	/// <see cref="SetAuthoritativeState"/> for the server's numbers. A second copy of this
	/// arithmetic would let the offline and networked bars disagree about where a given score
	/// sits, which is the same "one copy" discipline the score rule itself now follows one layer
	/// down, in <c>ConquestScoreRule</c>.
	/// </para>
	/// <para>
	/// <b>Two branches, and each is doing something.</b> Early in a round the two scores are far
	/// apart from the margin, so the bars are drawn INDEPENDENTLY — each side's own progress
	/// toward <paramref name="victoryPoints"/>, growing from its own end, with a gap in the
	/// middle and no intercept marker. Once the combined score reaches the margin the gap has
	/// closed and the display becomes a single MARGIN bar: one boundary, centred at parity,
	/// reaching an end when a side is <paramref name="victoryPoints"/> clear. The
	/// <c>1f -</c> on the red anchor is what makes red grow leftward from the right edge, and
	/// the <c>Clamp01</c> holds the boundary on screen when a lead overshoots the margin between
	/// the last award and the end of the round.
	/// </para>
	/// <para>
	/// Ported verbatim from the offline renderer rather than rebuilt from the formula, because
	/// the working copy is the specification here.
	/// </para>
	/// </remarks>
	private static void ApplyScoreBars(ScoreUi ui, int blueScore, int redScore, int victoryPoints)
	{
		if (ui.blueBar == null || ui.redBar == null || ui.intercept == null)
		{
			return;
		}
		bool flag = blueScore + redScore >= victoryPoints;
		ui.intercept.enabled = flag;
		if (!flag)
		{
			float x = (float)blueScore / (float)victoryPoints;
			float x2 = 1f - (float)redScore / (float)victoryPoints;
			ui.blueBar.rectTransform.anchorMax = new Vector2(x, 1f);
			ui.redBar.rectTransform.anchorMin = new Vector2(x2, 0f);
		}
		else
		{
			float x3 = Mathf.Clamp01((float)(blueScore - redScore + victoryPoints) / (float)(2 * victoryPoints));
			ui.blueBar.rectTransform.anchorMax = new Vector2(x3, 1f);
			ui.redBar.rectTransform.anchorMin = new Vector2(x3, 0f);
			ui.intercept.rectTransform.anchorMin = new Vector2(x3, 0f);
			ui.intercept.rectTransform.anchorMax = new Vector2(x3, 1f);
		}
	}

	private void Update()
	{
		if (!bluePulse.Done())
		{
			blueBar.color = Color.Lerp(Color.white, blue, bluePulse.Ratio());
		}
		if (!redPulse.Done())
		{
			redBar.color = Color.Lerp(Color.white, red, redPulse.Ratio());
		}
		// TAB BELONGS TO THE SCOREBOARD NOW (P18 3.3). It was bound here to an early dismissal
		// of the victory banner, which is a five-second overlay that also hides itself -- and
		// leaving two behaviours on one key would have meant a player opening the scoreboard at
		// the end of a round dismissed the result instead.
		//
		// The dismissal is kept rather than deleted, on V: the banner must not become
		// undismissable, and criterion 6 grades both halves. V is free in every code poll AND in
		// ProjectSettings/InputManager.asset -- checked, not assumed, because the last key chosen
		// without checking that file was Return, which the Loadout axis already owned and which
		// therefore opened chat and toggled the deploy screen in one press (see ClientChatSender).
		if (Input.GetKeyDown(KeyCode.V))
		{
			HideVictoryScreen();
		}
		if (Input.GetKeyDown(KeyCode.Home))
		{
			canvas.enabled = !canvas.enabled;
		}
	}
}
