// Diagnostics are compiled OUT of a shipping client build.
//
// The sense is INVERTED on purpose. Unity's BuildPlayerOptions.extraScriptingDefines can only
// ADD symbols, never subtract one, so a positive IRONFRONT_DIAGNOSTICS would have to be off in
// ProjectSettings and switched on for every build that needs it -- which is the Editor, the
// EditMode tests and the lane-B harness, i.e. everything except the one build that does not
// exist yet. Defaulting ON and letting a shipping build ADD IRONFRONT_NO_DIAGNOSTICS is the
// only arrangement the mechanism actually supports.
//
// Nothing outside Assets/Scripts/Net/Diagnostics/ names a type from this folder: the ten
// mentions elsewhere are doc-comments, checked 2026-08-21. So this guard needs no companion
// guard at any call site, and a strip cannot leave a dangling reference behind it.
#if !IRONFRONT_NO_DIAGNOSTICS
using System.Text;
using Ironfront.Net.Replication.Client;
using Ironfront.Net.Unity.Client;
using UnityEngine;

namespace Ironfront.Net.Unity.Diagnostics
{
    /// <summary>
    /// Shows, during play, whether vehicle replication is actually working: how far behind the
    /// interpolator is running, how often it starved, and whether driver prediction is
    /// converging. V5 task 4.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>A rising <c>Snap</c> count under a healthy network is the trigger for the
    /// <c>NoPrediction</c> fallback</b> (V5-D6, design section 9). That number is the whole
    /// reason this exists: without it "prediction is not converging" is a playtester's feeling
    /// rather than a reading, and the decision to flip the flag has nothing behind it. CI grades
    /// the arithmetic; this grades the running game.
    /// </para>
    /// <para>
    /// <b><c>Stalled</c> rising is the network, not a bug.</b> The interpolator never
    /// extrapolates (V5-D2), so a starved buffer holds the last pose and moves that counter —
    /// which is exactly the signal a freeze is meant to give.
    /// </para>
    /// <para>
    /// Separate from <see cref="TransportDebugOverlay"/>, which reports the socket. These are
    /// different questions with different answers: a perfectly healthy transport can carry a
    /// stream a client cannot converge against.
    /// </para>
    /// </remarks>
    [DisallowMultipleComponent]
    public sealed class VehicleReplicationOverlay : MonoBehaviour
    {
        [SerializeField] private bool _visible;
        [SerializeField] private bool _requireShift = true;
        [SerializeField] private KeyCode _toggleKey = KeyCode.F4;
        [SerializeField] private float _refreshSeconds = 0.25f;

        private ClientVehicleStage _stage;
        private NetClientBootstrap _client;
        private readonly StringBuilder _builder = new StringBuilder(256);
        private string _text = "Vehicle replication: unbound";
        private float _nextRefresh;
        private GUIStyle _style;

        /// <summary>Whether the overlay is currently drawn.</summary>
        public bool Visible => _visible;

        private void Awake()
        {
            _client = NetClientBootstrap.Current;
            _stage = FindFirstObjectByType<ClientVehicleStage>(FindObjectsInactive.Include);
        }

        private void Update()
        {
            if (Input.GetKeyDown(_toggleKey)
                && (!_requireShift || Input.GetKey(KeyCode.LeftShift) || Input.GetKey(KeyCode.RightShift)))
                _visible = !_visible;

            if (!_visible || Time.unscaledTime < _nextRefresh) return;
            _nextRefresh = Time.unscaledTime + Mathf.Max(0.05f, _refreshSeconds);

            // Re-resolved while missing rather than once: the stage lives on the netcode object,
            // which may be created after this overlay.
            if (_stage == null)
                _stage = FindFirstObjectByType<ClientVehicleStage>(FindObjectsInactive.Include);
            if (_client == null) _client = NetClientBootstrap.Current;

            _text = Compose();
        }

        private string Compose()
        {
            if (_client == null || _stage == null) return "Vehicle replication: unbound";

            VehicleSnapshotInterpolator buffer = _client.Router.VehicleInterpolator;
            VehicleCorrectionStats stats = _stage.DrivenStats;

            _builder.Clear();
            _builder.Append("VEHICLE REPLICATION\n");
            _builder.Append("applied ").Append(_client.Router.VehicleSnapshotsApplied)
                    .Append("  baseline-miss ").Append(_client.Router.UnknownVehicleBaselines).Append('\n');
            _builder.Append("buffered ").Append(buffer.Count)
                    .Append("/").Append(VehicleSnapshotInterpolator.Capacity)
                    .Append("  newest ").Append(buffer.NewestTick).Append('\n');
            _builder.Append("stalled ").Append(buffer.StalledCount)
                    .Append("  reordered ").Append(buffer.OutOfOrderCount)
                    .Append("  starved-frames ").Append(_stage.StarvedFrames).Append('\n');
            _builder.Append("mode ")
                    .Append(_stage.Config.PredictLocalVehicle ? "predicted" : "NO-PREDICTION")
                    .Append("  driving ").Append(_stage.DrivenVehicleId)
                    .Append("  sent ").Append(_stage.InputsSent).Append('\n');
            _builder.Append("blend ").Append(stats.BlendCount)
                    .Append("  snap ").Append(stats.SnapCount)
                    .Append("  err ").Append(stats.LastPositionError.ToString("F2")).Append(" m / ")
                    .Append(stats.LastAngleError.ToString("F1")).Append(" deg");

            return _builder.ToString();
        }

        private void OnGUI()
        {
            if (!_visible) return;

            if (_style == null)
            {
                _style = new GUIStyle(GUI.skin.box);
                _style.alignment = TextAnchor.UpperLeft;
                _style.fontSize = 13;
                _style.normal.textColor = Color.white;
            }

            GUI.Box(new Rect(12f, 166f, 400f, 118f), _text, _style);
        }
    }
}
#endif
