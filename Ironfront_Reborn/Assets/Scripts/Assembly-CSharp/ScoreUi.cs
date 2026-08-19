using UnityEngine;
using UnityEngine.UI;

public class ScoreUi : MonoBehaviour
{
	public static ScoreUi instance;

	public Text blueScoreText;

	public Text redScoreText;

	public Text blueFlagsText;

	public Text redFlagsText;

	public Text victoryText;

	// V10 task 7, checklist row E5. Optional and unset on the shipped prefab: when the client
	// track adds real elements they are assigned here and SetAuthoritativeState uses them. Until
	// then it falls back to the flag texts, which are idle on a networked client only while
	// capture points are unimplemented -- so the fallback has to retire when task 8 lands, and
	// preferring these fields is what makes that a one-line prefab change rather than an edit here.
	public Text phaseText;

	public Text phaseTimerText;

	public Image blueBar;

	public Image redBar;

	public Image intercept;

	public Image victoryScreen;

	private Canvas canvas;

	private int blueScore;

	private int redScore;

	private int blueFlags;

	private int redFlags;

	private Color blue;

	private Color red;

	private Action bluePulse = new Action(0.5f);

	private Action redPulse = new Action(0.5f);

	private bool gameEnded;

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
	/// Awards a kill. Does nothing where there is no scoreboard.
	/// </summary>
	/// <remarks>
	/// <b>This class holds match state, not just its rendering</b> — the score, the flag count
	/// and the win condition all live here, in a UI component. So on a headless server this
	/// guard does not merely skip a redraw: the original game's match neither scores nor ends.
	/// That is a deliberate, surfaced limitation and not a silent fallback. The networked match
	/// is scored by <c>Ironfront.Net.Replication.Match.MatchStateMachine</c>, which is where
	/// authoritative match state belongs. <b>V10 D12 closes only the rendering half of that
	/// divergence</b> — <see cref="SetAuthoritativeState"/> below draws the server's numbers on
	/// a networked client. This class still holds match state that does not run headless, and
	/// that remains V8 D9's recorded divergence; moving the state itself out of this UI
	/// component is a separate redesign, not done here.
	/// </remarks>
	public static void AddScore(int blue, int red)
	{
		if (instance == null)
		{
			return;
		}
		instance.blueScore += blue * ScoreMultiplier(instance.blueFlags);
		instance.redScore += red * ScoreMultiplier(instance.redFlags);
		if (blue > 0)
		{
			instance.bluePulse.Start();
		}
		if (red > 0)
		{
			instance.redPulse.Start();
		}
		instance.UpdateUi();
		if (!instance.gameEnded)
		{
			if (instance.blueScore >= instance.redScore + GameManager.instance.victoryPoints)
			{
				Win(true);
			}
			else if (instance.redScore >= instance.blueScore + GameManager.instance.victoryPoints)
			{
				Win(false);
			}
		}
	}

	/// <summary>Records a capture. See <see cref="AddScore"/> for the headless caveat.</summary>
	public static void AddFlag(int blue, int red)
	{
		if (instance == null)
		{
			return;
		}
		instance.blueFlags += blue;
		instance.redFlags += red;
		instance.UpdateUi();
		if (!instance.gameEnded && GameManager.instance.ElapsedGameTime() > 1f)
		{
			if (!ActorManager.HasSpawnPoint(0))
			{
				Win(false);
			}
			else if (!ActorManager.HasSpawnPoint(1))
			{
				Win(true);
			}
		}
	}

	/// <summary>Ends the match. See <see cref="AddScore"/> for the headless caveat.</summary>
	public static void Win(bool blue)
	{
		if (instance == null)
		{
			return;
		}
		if (!instance.gameEnded)
		{
			instance.gameEnded = true;
			instance.victoryScreen.gameObject.SetActive(true);
			Color color = ((!blue) ? instance.red : instance.blue);
			color.a = 0.8f;
			instance.victoryScreen.color = color;
			if (blue)
			{
				instance.victoryText.text = "BLUE TEAM IS";
			}
			else
			{
				instance.victoryText.text = "RED TEAM IS";
			}
			instance.Invoke("HideVictoryScreen", 5f);
		}
	}

	private void HideVictoryScreen()
	{
		victoryScreen.gameObject.SetActive(false);
	}

	public static int ScoreMultiplier(int flags)
	{
		return flags;
	}

	/// <summary>
	/// Renders the server's authoritative match state and returns. V10 D11: never re-enters
	/// <see cref="AddScore"/> or <see cref="AddFlag"/> (both are delta-only with no getters,
	/// while this method's inputs are already totals — feeding them through those mutators
	/// would re-enter <see cref="ScoreMultiplier"/> and double-drive the win check below), and
	/// never touches <c>victoryPoints</c> itself.
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
	/// <b>Checklist E5 — stopgap field mapping, not the finished layout.</b> <see cref="ScoreUi"/>
	/// has exactly four <see cref="Text"/> fields and none of them is a dedicated phase, timer or
	/// human-count element. <see cref="blueScoreText"/> / <see cref="redScoreText"/> take the
	/// tickets (the networked equivalent of the score they already show). <see cref="blueFlagsText"/>
	/// / <see cref="redFlagsText"/> are repurposed for the phase label and the phase timer — they
	/// are otherwise idle on a networked client, because <see cref="AddFlag"/> never runs there
	/// (capture points are V10 task 8, blocked on V8 task 1). <b>The client track still has to
	/// add real phase/timer/human-count elements to this prefab</b> and this mapping should be
	/// deleted once that lands.
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
			phaseTarget.text = PhaseLabel(phase) + (humanPlayerCount > 0 ? " (" + humanPlayerCount + ")" : string.Empty);
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
		blueScore = 0;
		redScore = 0;
		blueFlags = 0;
		redFlags = 0;
		blue = blueBar.color;
		red = redBar.color;
		canvas = GetComponent<Canvas>();
		victoryScreen.gameObject.SetActive(false);
		UpdateUi();
	}

	private void UpdateUi()
	{
		blueScoreText.text = blueScore.ToString();
		redScoreText.text = redScore.ToString();
		blueFlagsText.text = blueFlags.ToString();
		redFlagsText.text = redFlags.ToString();
		bool flag = blueScore + redScore >= GameManager.instance.victoryPoints;
		intercept.enabled = flag;
		if (!flag)
		{
			float x = (float)blueScore / (float)GameManager.instance.victoryPoints;
			float x2 = 1f - (float)redScore / (float)GameManager.instance.victoryPoints;
			blueBar.rectTransform.anchorMax = new Vector2(x, 1f);
			redBar.rectTransform.anchorMin = new Vector2(x2, 0f);
		}
		else
		{
			float x3 = Mathf.Clamp01((float)(blueScore - redScore + GameManager.instance.victoryPoints) / (float)(2 * GameManager.instance.victoryPoints));
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
