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
/// and never touches the offline scoreboard — routing them through it would re-enter the
/// multiplier and double-drive the win check (V10 D11).
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
	/// <c>MatchPhase.Playing</c> ends on tickets, not a clock, and rendering it as "0:00" would
	/// tell every player the round is over. A negative value hides the timer instead.
	/// </param>
	/// <remarks>
	/// <para>
	/// <b>Checklist E5 — the phase and timer elements are authored; the human count is not.</b>
	/// <see cref="phaseText"/> and <see cref="phaseTimerText"/> are dedicated elements on the
	/// shipped prefab as of 2026-08-19 (ledger A-9), so the flag-text fallback below no longer
	/// runs there. <see cref="blueScoreText"/> / <see cref="redScoreText"/> take the tickets (the
	/// networked equivalent of the score they already show). The fallback to
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
		int phase, int tickets0, int tickets1, int secondsRemaining, int humanPlayerCount)
	{
		if (instance == null)
		{
			return;
		}
		if (instance.hasAuthoritativeState
			&& instance.lastPhase == phase
			&& instance.lastTickets0 == tickets0
			&& instance.lastTickets1 == tickets1
			&& instance.lastSecondsRemaining == secondsRemaining
			&& instance.lastHumanPlayerCount == humanPlayerCount)
		{
			return;
		}
		instance.hasAuthoritativeState = true;
		instance.lastPhase = phase;
		instance.lastTickets0 = tickets0;
		instance.lastTickets1 = tickets1;
		instance.lastSecondsRemaining = secondsRemaining;
		instance.lastHumanPlayerCount = humanPlayerCount;
		if (instance.blueScoreText != null)
		{
			instance.blueScoreText.text = tickets0.ToString();
		}
		if (instance.redScoreText != null)
		{
			instance.redScoreText.text = tickets1.ToString();
		}
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
			Ironfront.Net.Unity.Client.NetClientPresenterGuard.WarnOnce(
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
		board.Changed += UpdateUi;
		board.Ended += OnMatchEnded;
		board.Scored += OnScored;
		UpdateUi();
	}

	private void OnDestroy()
	{
		if (instance == this)
		{
			instance = null;
		}
		MatchScoreboard board = MatchScoreboard.Current;
		board.Changed -= UpdateUi;
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
		bool flag = blueScore + redScore >= victoryPoints;
		intercept.enabled = flag;
		if (!flag)
		{
			float x = (float)blueScore / (float)victoryPoints;
			float x2 = 1f - (float)redScore / (float)victoryPoints;
			blueBar.rectTransform.anchorMax = new Vector2(x, 1f);
			redBar.rectTransform.anchorMin = new Vector2(x2, 0f);
		}
		else
		{
			float x3 = Mathf.Clamp01((float)(blueScore - redScore + victoryPoints) / (float)(2 * victoryPoints));
			blueBar.rectTransform.anchorMax = new Vector2(x3, 1f);
			redBar.rectTransform.anchorMin = new Vector2(x3, 0f);
			intercept.rectTransform.anchorMin = new Vector2(x3, 0f);
			intercept.rectTransform.anchorMax = new Vector2(x3, 1f);
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
		if (Input.GetKeyDown(KeyCode.Tab))
		{
			HideVictoryScreen();
		}
		if (Input.GetKeyDown(KeyCode.Home))
		{
			canvas.enabled = !canvas.enabled;
		}
	}
}
