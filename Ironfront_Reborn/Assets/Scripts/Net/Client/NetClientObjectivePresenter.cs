using Ironfront.Net.Protocol;
using Ironfront.Net.Replication.Client;
using UnityEngine;
using UnityEngine.UI;

namespace Ironfront.Net.Unity.Client
{
    /// <summary>
    /// Renders the server's authoritative match state -- phase, tickets, phase timer and human
    /// count -- onto <see cref="ScoreUi"/>. phase-V10 task 7.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>One of D3's three presenters.</b> <c>NetClientCombatPresenter</c> owns death, weapon
    /// fire and hit confirm; <c>NetClientExplosionPresenter</c> owns explosions; this one owns
    /// <c>OnMatchState</c> only. <b>It does not subscribe <c>OnCapturePoint</c></b> -- that half
    /// of the objective presenter is task 8, hard-blocked on V8 task 1 (D15), and is added later
    /// in the same file rather than here.
    /// </para>
    /// <para>
    /// <b>The <see cref="MatchPhase.Playing"/> timer rule.</b>
    /// <c>MatchStateMessage.PhaseSecondsRemaining</c> is 0 during <c>Playing</c> by design --
    /// that phase ends on tickets, not a clock. <see cref="MatchStateModel.HasTimer"/> is false
    /// there, and this presenter passes <c>-1</c> to <see cref="ScoreUi.SetAuthoritativeState"/>
    /// in that case, which is documented there as "hide the timer", never "render 0:00".
    /// </para>
    /// <para>
    /// <b>Staleness dims rather than freezes-and-lies.</b>
    /// <see cref="MatchStateModel.IsStale"/> stops new numbers from being pushed (the fixed
    /// <see cref="ScoreUi.SetAuthoritativeState"/> signature has no confidence flag to carry,
    /// so a stale value cannot be told apart from a live one once it is inside that call) and
    /// instead dims the same Text elements the last good render used, via their already-public
    /// fields on <see cref="ScoreUi.instance"/>. "Errors Over Silent Fallbacks", applied to a
    /// clock: unknown is shown as unknown, not as the last known-good number pretending to be
    /// current.
    /// </para>
    /// <para>
    /// <b>D22.</b> Inert unless <see cref="NetClientPresenterGuard.IsPresentable"/>. The
    /// <c>OnMatchState</c> handler only latches a struct into <see cref="MatchStateModel"/> --
    /// it cannot throw, which matters because it runs on the transport pump.
    /// </para>
    /// </remarks>
    [DefaultExecutionOrder(-50)]
    [DisallowMultipleComponent]
    public sealed class NetClientObjectivePresenter : MonoBehaviour
    {
        // Dimmed rather than hidden, so the last known-good numbers stay legible while flagged
        // as no longer trustworthy.
        private const float DimmedAlpha = 0.35f;
        private const float LiveAlpha = 1f;

        private NetClientBootstrap _client;
        private readonly MatchStateModel _model = new MatchStateModel();
        private bool _isDimmed;

        private void Awake()
        {
            if (!NetClientPresenterGuard.IsPresentable)
            {
                enabled = false;
                return;
            }

            // Guard.TryResolveClient rather than a bare NetClientBootstrap.Current: a null
            // bootstrap here logs once instead of this presenter silently never subscribing for
            // its whole life (task 1 trap 3).
            NetClientPresenterGuard.TryResolveClient(nameof(NetClientObjectivePresenter), out _client);
        }

        private void OnEnable()
        {
            if (_client == null) return;
            _client.Router.OnMatchState += OnMatchState;
        }

        private void OnDisable()
        {
            if (_client == null) return;
            _client.Router.OnMatchState -= OnMatchState;
        }

        // Never throws (D22) -- ClientMessageRouter.Route counts malformed input rather than
        // throwing, and a handler that threw here would propagate into the transport pump.
        private void OnMatchState(MatchStateMessage message)
        {
            _model.Apply(in message, Time.time);
        }

        private void Update()
        {
            if (_client == null || !_model.HasState) return;

            float now = Time.time;

            if (_model.IsStale(now))
            {
                SetDimmed(true);
                return;
            }

            SetDimmed(false);

            MatchStateMessage state = _model.Current;

            // -1 sentinel: "no timer this phase" (Playing). ScoreUi.SetAuthoritativeState hides
            // the timer element on that value rather than rendering a zero.
            int secondsRemaining = _model.HasTimer
                ? Mathf.CeilToInt(_model.SecondsRemaining(now))
                : -1;

            ScoreUi.SetAuthoritativeState(
                (int)state.Phase, state.Tickets0, state.Tickets1, secondsRemaining, state.HumanPlayerCount);
        }

        private void SetDimmed(bool dimmed)
        {
            if (_isDimmed == dimmed) return;
            _isDimmed = dimmed;

            if (ScoreUi.instance == null) return;

            float alpha = dimmed ? DimmedAlpha : LiveAlpha;
            SetAlpha(ScoreUi.instance.blueScoreText, alpha);
            SetAlpha(ScoreUi.instance.redScoreText, alpha);
            SetAlpha(ScoreUi.instance.blueFlagsText, alpha);
            SetAlpha(ScoreUi.instance.redFlagsText, alpha);

            // The dedicated phase/timer elements too, when the prefab has them. Dimming only
            // the four legacy fields would leave the timer reading as live while the numbers
            // beside it are flagged stale -- worse than not dimming at all.
            SetAlpha(ScoreUi.instance.phaseText, alpha);
            SetAlpha(ScoreUi.instance.phaseTimerText, alpha);
        }

        private static void SetAlpha(Text text, float alpha)
        {
            if (text == null) return;

            Color color = text.color;
            color.a = alpha;
            text.color = color;
        }
    }
}
