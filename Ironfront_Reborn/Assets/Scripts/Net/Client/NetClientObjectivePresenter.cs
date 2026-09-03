using Ironfront.Net.Protocol;
using Ironfront.Net.Replication.Client;
using Ironfront.Net.Replication.Match;
using UnityEngine;

namespace Ironfront.Net.Unity.Client
{
    /// <summary>
    /// Renders the server's authoritative match state -- phase, tickets, phase timer and human
    /// count -- onto the match scoreboard, through <see cref="IObjectiveHud"/>. phase-V10 task 7.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>One of D3's three presenters.</b> <c>NetClientCombatPresenter</c> owns death, weapon
    /// fire and hit confirm; <c>NetClientExplosionPresenter</c> owns explosions; this one owns
    /// <c>OnMatchState</c> (task 7) and <c>OnCapturePoint</c> (task 8).
    /// </para>
    /// <para>
    /// <b>Task 8 was severed from V10 and landed afterwards.</b> D15 blocked it on V8 task 1 for
    /// two reasons: <c>ApplyAuthoritativeOwner</c> did not exist, and <c>CapturePoint.UpdateOwner</c>
    /// still ran its own 1 Hz arithmetic on clients, so replicating ownership beside it would have
    /// made two client-side writers. V8 shipped both halves -- the method, and the
    /// <c>NetContext.IsOffline</c> gate on the arithmetic -- and the blocker cleared without
    /// anything picking the task back up. This is that pick-up.
    /// </para>
    /// <para>
    /// <b>The <see cref="MatchPhase.Playing"/> timer rule.</b>
    /// <c>MatchStateMessage.PhaseSecondsRemaining</c> is 0 during <c>Playing</c> by design --
    /// that phase ends on tickets, not a clock. <see cref="MatchStateModel.HasTimer"/> is false
    /// there, and this presenter passes <c>-1</c> to <see cref="IObjectiveHud.SetAuthoritativeState"/>
    /// in that case, which is documented there as "hide the timer", never "render 0:00".
    /// </para>
    /// <para>
    /// <b>Staleness dims rather than freezes-and-lies.</b>
    /// <see cref="MatchStateModel.IsStale"/> stops new numbers from being pushed (the fixed
    /// <see cref="IObjectiveHud.SetAuthoritativeState"/> signature has no confidence flag to carry,
    /// so a stale value cannot be told apart from a live one once it is inside that call) and
    /// instead dims the whole scoreboard through <see cref="IObjectiveHud.SetAlpha"/> — which
    /// label elements that covers is the HUD's own business (phase C4b; before it, this file
    /// listed six of them by name). "Errors Over Silent Fallbacks", applied to a
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
        private readonly CapturePointView _view = new CapturePointView();
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
            _client.Router.OnCapturePoint += OnCapturePoint;
        }

        private void OnDisable()
        {
            if (_client == null) return;
            _client.Router.OnMatchState -= OnMatchState;
            _client.Router.OnCapturePoint -= OnCapturePoint;
        }

        // Never throws (D22) -- ClientMessageRouter.Route counts malformed input rather than
        // throwing, and a handler that threw here would propagate into the transport pump.
        private void OnMatchState(MatchStateMessage message)
        {
            _model.Apply(in message, Time.time);
        }

        /// <summary>
        /// Task 8. Latches the point into the engine-free view, then writes the change through
        /// <c>CapturePoint.ApplyAuthoritativeOwner</c> -- V8 D3's single write path, and the
        /// second of the two callers V8 anticipated.
        /// </summary>
        /// <remarks>
        /// <para>
        /// <b>Only dirty points are written.</b> The view returns false for a repeat, and
        /// <c>ApplyAuthoritativeOwner</c> is not free: it calls <c>SetOwner</c> on a change of
        /// hands, which drives the scoreboard's flag count and <c>MinimapUi.UpdateSpawnPointButtons</c>.
        /// Writing unconditionally at message rate would add a scoreboard flag repeatedly for a
        /// point nobody touched -- the failure ApplyAuthoritativeOwner's own remarks describe.
        /// </para>
        /// <para>
        /// <b>The second writer this used to race is gone.</b> D15 severed this task because
        /// <c>CapturePoint.UpdateOwner</c> still ran its own 1 Hz arithmetic on clients. V8 D2
        /// gated that behind <c>NetContext.IsOffline</c>, so replicated ownership is now the only
        /// writer while a match is networked, and this is safe to land.
        /// </para>
        /// <para>
        /// <b>Neutral is passed as -1 explicitly</b>, via the same
        /// <c>CapturePointOwnership</c> helpers <c>CapturePointSlave.Apply</c> uses on the server,
        /// so the two sides cannot drift on the mapping. Casting an ownership value to a team and
        /// letting neutral fall out as team 0 is the mistake the wire's 255 exists to prevent.
        /// </para>
        /// <para>
        /// <b>Contested is the wire's sense</b> -- both teams inside the radius -- which is the
        /// only one a client can know; the spawning sense is computed from presence the client
        /// does not have. A point may be owned AND contested (task 4 trap 5).
        /// </para>
        /// </remarks>
        private void OnCapturePoint(CapturePointMessage message)
        {
            if (!_view.Apply(in message)) return;

            RecomputeCapturePointCounts();

            ICapturePointDirectory points = NetSceneBindings.CapturePoints;
            if (points == null) return;

            int spawnOwner = CapturePointOwnership.ToSpawnPointOwner(message.OwningTeam);
            float control = CapturePointOwnership.ToControl(message.Owner);

            // P3 task 3.1 -- THE measurement, taken where the wire is still a wire. Everything
            // downstream of this line is derived, so a report built from CapturePoint's fields
            // cannot tell "the server sent 0" from "the client threw the magnitude away". The
            // raw quantized byte is printed beside the float because they are the two halves
            // of the question: OwnerQ is what crossed the socket, Owner is what it decodes to.
            //
            // Rate is not a concern. A point sends only when it crosses
            // MatchRules.CaptureSendThreshold or changes hands, plus once per point on join --
            // and _view.Apply above has already dropped every repeat.
            Debug.Log($"[net] capture point {message.PointId}: OwnerQ {message.OwnerQ} "
                      + $"-> Owner {message.Owner:F2}, OwningTeam {message.OwningTeam} "
                      + $"-> spawn owner {spawnOwner}, control {control:F2}, "
                      + $"contested {message.IsContested} -- flag "
                      + (control > 0f ? "VISIBLE" : "HIDDEN")
                      + $" at pole height {(1.2f + 4.8f * control):F2}");

            points.ApplyAuthoritativeOwner(
                message.PointId, spawnOwner, control, message.IsContested);
        }

        /// <summary>
        /// Tallies <see cref="_view"/>'s known points by owner and pushes the two counts to the
        /// HUD. The client-side twin of <c>MatchStateMachine.OwnedPointCount</c> -- ownership is
        /// already fully replicated per point (this method runs off the same message
        /// <see cref="OnCapturePoint"/> just latched), so summing it here needs no new wire
        /// field and cannot disagree with the server's own tally.
        /// </summary>
        /// <remarks>
        /// Neutral (<see cref="TeamId.None"/>) and never-reported points count toward neither
        /// team, the same as the server's <c>OwnedPointCount</c>.
        /// </remarks>
        private void RecomputeCapturePointCounts()
        {
            int blue = 0;
            int red = 0;

            for (int i = 0; i < _view.Capacity; i++)
            {
                if (!_view.IsKnown(i)) continue;

                byte owner = _view.OwningTeam(i);
                if (owner == TeamId.Team0) blue++;
                else if (owner == TeamId.Team1) red++;
            }

            NetClientBindings.Objectives?.SetCapturePointCounts(blue, red);
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

            // -1 sentinel: "no timer this phase" (Playing). The HUD hides
            // the timer element on that value rather than rendering a zero.
            int secondsRemaining = _model.HasTimer
                ? Mathf.CeilToInt(_model.SecondsRemaining(now))
                : -1;

            NetClientBindings.Objectives?.SetAuthoritativeState(
                (int)state.Phase, state.Score0, state.Score1, secondsRemaining,
                state.HumanPlayerCount, state.VictoryPoints);
        }

        private void SetDimmed(bool dimmed)
        {
            if (_isDimmed == dimmed) return;
            _isDimmed = dimmed;

            // One call, not six labels. Which elements the scoreboard owns -- and the standing
            // rule that dimming only SOME of them is worse than dimming none, because a
            // live-looking timer beside numbers flagged stale is the worst of the three states --
            // is the HUD's business. Since C4b that rule is enforced on the HUD's side of the
            // seam rather than remembered at this call site, which previously had to list all six
            // by name and would have failed silently on a seventh.
            NetClientBindings.Objectives?.SetAlpha(dimmed ? DimmedAlpha : LiveAlpha);
        }
    }
}
