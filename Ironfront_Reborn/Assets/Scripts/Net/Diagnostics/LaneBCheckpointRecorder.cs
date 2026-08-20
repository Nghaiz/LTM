using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using Ironfront.Net.Replication.Client;
using Ironfront.Net.Unity.Client;
using UnityEngine;

namespace Ironfront.Net.Unity.Diagnostics
{
    /// <summary>
    /// Writes the artifact for one lane-B checkpoint: a screenshot, and a JSONL record of every
    /// number a reader would otherwise have to take on trust. Phase-3D lane B § 4.3.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The artifact is the deliverable.</b> <c>phase-3d-lane-b.md</c> § 5 grades a verdict
    /// with no artifact as a failed row, so this class exists to make the artifact a
    /// by-product of running rather than something a person remembers to capture.
    /// </para>
    /// <para>
    /// <b>Both seeds travel on every record</b> (§ 4.4). <c>UnityEngine.Random</c> and
    /// <c>NetworkSimulator</c> are two generators, and a record naming one claims a
    /// reproducibility it does not have — the argument <c>HeadlessLoadBootstrap</c> already
    /// makes for lane A.
    /// </para>
    /// <para>
    /// <b>It reads the shipped overlays' own sources rather than new instrumentation</b> (§ 3):
    /// <c>ClientVehicleStage.DrivenStats</c> is what <c>VehicleReplicationOverlay</c> draws,
    /// <c>NetClientBootstrap.SmoothedRttMs</c> is what <c>TransportDebugOverlay</c> draws, and
    /// the remote registries are internal to this assembly. Nothing here measures anything the
    /// running game does not already measure for itself.
    /// </para>
    /// </remarks>
    public sealed class LaneBCheckpointRecorder
    {
        private readonly string _directory;
        private readonly string _label;
        private readonly string _programme;
        private readonly LaneBRunSeeds _seeds;
        private readonly ScriptedTargetSolver _solver;
        private readonly StringBuilder _json = new StringBuilder(4096);

        private static readonly UTF8Encoding NoBom = new UTF8Encoding(false);

        /// <param name="solver">
        /// Optional. Present, every record carries what the live step was aiming at and whether
        /// the name resolved — which is the difference between "check 1 failed" and "check 1
        /// never had a target", and no screenshot can tell those apart.
        /// </param>
        public LaneBCheckpointRecorder(string directory, string label, string programme,
                                       LaneBRunSeeds seeds, ScriptedTargetSolver solver = null)
        {
            _directory = directory;
            _label = label;
            _programme = programme;
            _seeds = seeds;
            _solver = solver;

            Directory.CreateDirectory(_directory);
            RecordPath = Path.Combine(_directory, _label + "-checkpoints.jsonl");
        }

        /// <summary>Where the JSONL records land. Named in the phase report per check.</summary>
        public string RecordPath { get; }

        /// <summary>How many checkpoints have been written.</summary>
        public int Count { get; private set; }

        /// <summary>
        /// Captures one checkpoint. The screenshot lands beside the record and is named by it.
        /// </summary>
        /// <remarks>
        /// <c>ScreenCapture.CaptureScreenshot</c> writes at the end of the current frame rather
        /// than now, so the PNG appears a frame after the record that names it. That is a
        /// filesystem race only for a reader who checks between the two; the runner waits for
        /// the process to exit before collecting, so it never sees the gap.
        /// </remarks>
        public void Capture(string checkpoint, float dueAtSeconds, float elapsedSeconds)
        {
            Count++;

            string safe = Sanitize(checkpoint);
            string shot = $"{_label}-{Count:D2}-{safe}.png";

            // Skipped under -batchmode: there is no framebuffer, and CaptureScreenshot writes a
            // zero-byte file rather than failing, which is worse than an honest absence.
            bool captured = !Application.isBatchMode;
            if (captured) ScreenCapture.CaptureScreenshot(Path.Combine(_directory, shot));

            Compose(checkpoint, dueAtSeconds, elapsedSeconds, captured ? shot : null);
            // UTF8Encoding(false), not Encoding.UTF8: the latter writes a BOM on the first
            // append, and a JSONL file whose first line starts with U+FEFF fails json.loads in
            // every reader that does not know to ask for utf-8-sig. The artifact IS the
            // deliverable here, so it has to be readable by the obvious command.
            File.AppendAllText(RecordPath, _json.ToString() + "\n", NoBom);
        }

        private void Compose(string checkpoint, float dueAt, float elapsed, string screenshot)
        {
            NetClientBootstrap client = NetClientBootstrap.Current;
            ClientVehicleStage stage = Object.FindFirstObjectByType<ClientVehicleStage>(
                FindObjectsInactive.Include);

            _json.Clear();
            _json.Append('{');
            Str("label", _label); Comma();
            Str("programme", _programme); Comma();
            Str("checkpoint", checkpoint); Comma();
            Num("dueAtSeconds", dueAt); Comma();
            Num("elapsedSeconds", elapsed); Comma();
            Num("unitySeed", _seeds.UnitySeed); Comma();
            Str("simPreset", _seeds.SimulatorPreset); Comma();
            Num("simSeed", _seeds.SimulatorSeed); Comma();
            Num("playerId", _seeds.PlayerId); Comma();
            Str("displayName", _seeds.DisplayName); Comma();

            if (client != null)
            {
                Num("connectionId", client.ConnectionId); Comma();
                Num("localActorId", client.LocalActorId); Comma();
                Num("rttMs", client.SmoothedRttMs); Comma();
                Num("snapshotsApplied", client.Router.VehicleSnapshotsApplied); Comma();
                Num("vehicleBaselineMiss", client.Router.UnknownVehicleBaselines); Comma();
                Num("interpBuffered", client.Router.VehicleInterpolator.Count); Comma();
                Num("interpNewestTick", client.Router.VehicleInterpolator.NewestTick); Comma();
                Num("interpStalled", client.Router.VehicleInterpolator.StalledCount); Comma();
                Num("interpReordered", client.Router.VehicleInterpolator.OutOfOrderCount); Comma();
            }
            else
            {
                Str("client", "absent"); Comma();
            }

            AppendLocalActor(client);
            Comma();

            if (stage != null)
            {
                VehicleCorrectionStats s = stage.DrivenStats;
                Num("drivenVehicleId", stage.DrivenVehicleId); Comma();
                Num("occupiedVehicleId", stage.OccupiedVehicleId); Comma();
                Num("inputsSent", stage.InputsSent); Comma();
                Num("starvedFrames", stage.StarvedFrames); Comma();
                Num("correctionBlends", s.BlendCount); Comma();
                Num("correctionSnaps", s.SnapCount); Comma();
                Num("lastPositionErrorM", s.LastPositionError); Comma();
                Num("lastAngleErrorDeg", s.LastAngleError); Comma();
                Str("predictionMode",
                    stage.Config.PredictLocalVehicle ? "predicted" : "no-prediction");
                Comma();
            }

            AppendVehicles();
            Comma();
            AppendRemoteActors();
            Comma();
            AppendAim();
            Comma();
            AppendCombat();
            Comma();
            AppendHud();
            Comma();
            AppendObjectives();
            Comma();
            AppendCameras();

            if (screenshot != null)
            {
                Comma();
                Str("screenshot", screenshot);
            }

            _json.Append('}');
        }

        private void AppendLocalActor(NetClientBootstrap client)
        {
            FpsActorController local = FpsActorController.instance;
            if (local == null)
            {
                _json.Append("\"localActor\":null");
                return;
            }

            Vector3 p = local.transform.position;
            _json.Append("\"localActor\":{");
            Num("x", p.x); Comma(); Num("y", p.y); Comma(); Num("z", p.z); Comma();
            Num("yaw", local.transform.eulerAngles.y); Comma();
            Num("aimPitch", local.InputSource.Pitch); Comma();
            Num("buttons", local.InputSource.Buttons);
            _json.Append('}');
        }

        /// <summary>
        /// Every replicated vehicle this client is drawing, with the turret pose it last
        /// applied. Checks 7 and 12 are a diff of this array across two clients.
        /// </summary>
        private void AppendVehicles()
        {
            var registry = Object.FindFirstObjectByType<RemoteVehicleRegistry>(
                FindObjectsInactive.Include);

            _json.Append("\"vehicles\":[");
            if (registry != null)
            {
                List<ushort> ids = registry.LiveIds;
                for (int i = 0; i < ids.Count; i++)
                {
                    if (i > 0) _json.Append(',');
                    ushort id = ids[i];
                    _json.Append('{');
                    Num("id", id); Comma();

                    // NetClientVehicle is a plain class, not a MonoBehaviour -- it holds the
                    // Vehicle it drives rather than being one, so the transform comes from that.
                    if (registry.TryFind(id, out NetClientVehicle vehicle)
                        && vehicle != null && vehicle.Exists)
                    {
                        Transform t = vehicle.Vehicle.transform;
                        Vector3 p = t.position;
                        Num("x", p.x); Comma(); Num("y", p.y); Comma(); Num("z", p.z); Comma();
                        Num("yaw", t.eulerAngles.y); Comma();
                        Str("mode", vehicle.Mode.ToString()); Comma();
                    }

                    bool posed = registry.TryGetTurretPose(id, out float ty, out float tp);
                    Num("turretYaw", posed ? ty : float.NaN); Comma();
                    Num("turretPitch", posed ? tp : float.NaN);
                    _json.Append('}');
                }
            }

            _json.Append(']');
        }

        /// <summary>
        /// What the live step was pointing at, and whether the name resolved.
        /// </summary>
        /// <remarks>
        /// <b>An unresolved target is the most important thing this file can say.</b> A check-1
        /// run where the shooter never found "OBS-A" produces exactly the same artifact as one
        /// where it found the target and missed: no killfeed line, full health on both sides.
        /// The screenshot cannot tell them apart either. This is the field that can.
        /// </remarks>
        private void AppendAim()
        {
            _json.Append("\"aim\":");

            if (_solver == null || string.IsNullOrEmpty(_solver.LastRequestedName))
            {
                _json.Append("null");
                return;
            }

            ScriptedTargetSolver.Solution s = _solver.Last;
            _json.Append('{');
            Str("requested", _solver.LastRequestedName); Comma();
            _json.Append("\"resolved\":").Append(s.Resolved ? "true" : "false"); Comma();
            Num("targetActorId", s.ActorId); Comma();
            Num("yaw", s.Resolved ? s.Yaw : float.NaN); Comma();
            Num("pitch", s.Resolved ? s.Pitch : float.NaN); Comma();
            Num("distanceM", s.Resolved ? s.Distance : float.NaN);
            _json.Append('}');
        }

        /// <summary>
        /// The local player's combat state and the killfeed, with names resolved — checks 1
        /// (E7) and 13.
        /// </summary>
        /// <remarks>
        /// <para>
        /// <b>The names are resolved HERE rather than left as ids</b>, because check 1's pass
        /// condition is a killfeed line <i>with a name</i>: an artifact holding
        /// <c>killer: 41</c> proves the feed fired and proves nothing about the half the check
        /// is actually about. <c>PlayerNameTable.NameOf</c> returns null for an actor no
        /// broadcast has named, and that null is written as null — a manufactured "Player 41"
        /// would make a missing name indistinguishable from a real one, which is the failure
        /// that table's own remark warns about.
        /// </para>
        /// <para>
        /// <b>Both models are read after the presenter's own <c>Update</c> has pruned them</b>
        /// — the recorder runs from the harness's <c>Update</c>, and the presenter sits at
        /// <c>DefaultExecutionOrder(-50)</c>, so what lands here is what the HUD drew this
        /// frame rather than a feed one frame staler than the screenshot beside it.
        /// </para>
        /// </remarks>
        private void AppendCombat()
        {
            var driver = Object.FindFirstObjectByType<NetClientLocalCombatDriver>(
                FindObjectsInactive.Include);
            var presenter = Object.FindFirstObjectByType<NetClientCombatPresenter>(
                FindObjectsInactive.Include);

            _json.Append("\"combat\":");

            if (driver == null && presenter == null) { _json.Append("null"); return; }

            _json.Append('{');

            if (driver != null)
            {
                ClientCombatState state = driver.State;
                float now = Time.time;

                Num("health", state.Health); Comma();
                _json.Append("\"alive\":").Append(state.IsAlive ? "true" : "false"); Comma();
                Num("ammoInClip", state.AmmoInClip); Comma();
                Num("clipSize", state.ClipSize); Comma();
                _json.Append("\"reloading\":").Append(state.IsReloading ? "true" : "false"); Comma();
                Num("weaponId", state.WeaponId); Comma();
                Num("predictedShots", state.PredictedShots); Comma();
                Num("ammoCorrections", state.SnapshotAmmoCorrections); Comma();
                _json.Append("\"canRespawn\":")
                     .Append(state.CanRequestRespawn(now) ? "true" : "false"); Comma();
                Num("secondsUntilRespawn", state.SecondsUntilRespawn(now)); Comma();
            }
            else
            {
                Str("combatDriver", "absent"); Comma();
            }

            if (presenter != null)
            {
                KillfeedModel feed = presenter.Killfeed;
                PlayerNameTable names = presenter.Names;

                Num("hitmarkerHits", presenter.Hitmarker.HitCount); Comma();
                Num("killfeedTotalKills", feed.TotalKills); Comma();
                Num("namedPlayers", names.Count); Comma();

                _json.Append("\"killfeed\":[");
                for (int i = 0; i < feed.Count; i++)
                {
                    if (i > 0) _json.Append(',');
                    KillfeedEntry e = feed[i];

                    _json.Append('{');
                    Num("killerActorId", e.KillerActorId); Comma();
                    NullableStr("killerName", names.NameOf(e.KillerActorId)); Comma();
                    Num("victimActorId", e.VictimActorId); Comma();
                    NullableStr("victimName", names.NameOf(e.VictimActorId)); Comma();
                    Str("cause", e.Cause.ToString()); Comma();
                    _json.Append("\"headshot\":").Append(e.Headshot ? "true" : "false"); Comma();
                    _json.Append("\"environment\":")
                         .Append(e.KilledByEnvironment ? "true" : "false"); Comma();
                    Num("postedAtSeconds", e.PostedAtSeconds);
                    _json.Append('}');
                }

                _json.Append(']');
            }
            else
            {
                Str("combatPresenter", "absent");
            }

            _json.Append('}');
        }

        /// <summary>
        /// What the scoreboard is DRAWING, beside what the offline scoreboard holds — check 2
        /// (E8).
        /// </summary>
        /// <remarks>
        /// <para>
        /// <b>The rendered strings, not the numbers behind them.</b> E8 grades that the HUD
        /// reflects authoritative state, and reading the authoritative value back out of the
        /// same object that was told it would pass whether or not a single label was ever
        /// written. The <c>Text</c> components are the last hop before a person's eyes, so
        /// those are what gets recorded.
        /// </para>
        /// <para>
        /// <b><c>MatchScoreboard.Current</c> is recorded to prove it stayed at zero.</b> V10 D11
        /// is explicit that a networked client must NOT route the server's totals through the
        /// offline scoreboard, because that re-enters the multiplier and double-drives the win
        /// check. A non-zero pair here on a client is that defect, and nothing else in the run
        /// would show it.
        /// </para>
        /// </remarks>
        private void AppendHud()
        {
            ScoreUi ui = ScoreUi.instance;
            MatchScoreboard board = MatchScoreboard.Current;

            _json.Append("\"hud\":");
            if (ui == null && board == null) { _json.Append("null"); return; }

            _json.Append('{');

            if (ui != null)
            {
                NullableStr("blueScoreText", TextOf(ui.blueScoreText)); Comma();
                NullableStr("redScoreText", TextOf(ui.redScoreText)); Comma();
                NullableStr("blueFlagsText", TextOf(ui.blueFlagsText)); Comma();
                NullableStr("redFlagsText", TextOf(ui.redFlagsText)); Comma();
                NullableStr("phaseText", TextOf(ui.phaseText)); Comma();
                NullableStr("phaseTimerText", TextOf(ui.phaseTimerText)); Comma();
                _json.Append("\"phaseTimerVisible\":")
                     .Append(IsVisible(ui.phaseTimerText) ? "true" : "false"); Comma();
                _json.Append("\"victoryVisible\":")
                     .Append(ui.victoryScreen != null
                             && ui.victoryScreen.gameObject.activeInHierarchy ? "true" : "false");
                Comma();
            }
            else
            {
                Str("scoreUi", "absent"); Comma();
            }

            if (board != null)
            {
                Num("offlineBlueScore", board.BlueScore); Comma();
                Num("offlineRedScore", board.RedScore); Comma();
                Num("offlineBlueFlags", board.BlueFlags); Comma();
                Num("offlineRedFlags", board.RedFlags); Comma();
                _json.Append("\"offlineGameEnded\":")
                     .Append(board.GameEnded ? "true" : "false");
            }
            else
            {
                Str("scoreboard", "absent");
            }

            _json.Append('}');
        }

        /// <summary>
        /// Every capture point's authoritative owner and capture progress — check 3 (E9).
        /// </summary>
        /// <remarks>
        /// <para>
        /// <b>Two clients' arrays are diffed against each other, not against the server.</b> E9
        /// reads "flag colour and capture bar change on both clients at the same authoritative
        /// value", and neither client can see the server's copy. What it CAN see is the other
        /// client's, and a disagreement between two clients fed the same
        /// <c>ApplyAuthoritativeOwner</c> is either a lost message or a client running its own
        /// arithmetic — which is the other half of the same check.
        /// </para>
        /// <para>
        /// <b>Owner only; the capture bar is not machine-readable from here.</b>
        /// <c>CapturePoint.control</c> is private and the bar it drives lives behind
        /// <c>IngameUi.SetFlagIndicator</c>, so the progress half of E9 stays a human judgment
        /// against the screenshot pair, recorded as one. Making the field public to grade it
        /// would be a change to shipped client code for a harness's convenience, which § 6
        /// forbids — the honest cost is that half of one check is graded by eye.
        /// </para>
        /// </remarks>
        private void AppendObjectives()
        {
            CapturePoint[] points = Object.FindObjectsByType<CapturePoint>(
                FindObjectsInactive.Include, FindObjectsSortMode.None);

            _json.Append("\"capturePoints\":[");

            if (points != null)
            {
                // Sorted by name so two clients' arrays line up index for index. Unity's scene
                // order is not a contract, and a diff that compared point 0 on one client with
                // a different point 0 on another would report a flip that never happened.
                System.Array.Sort(points, (a, b) => string.CompareOrdinal(
                    a != null ? a.name : string.Empty, b != null ? b.name : string.Empty));

                for (int i = 0; i < points.Length; i++)
                {
                    CapturePoint p = points[i];
                    if (p == null) continue;

                    if (i > 0) _json.Append(',');
                    _json.Append('{');
                    Str("name", p.name); Comma();
                    Num("owner", p.owner);
                    _json.Append('}');
                }
            }

            _json.Append(']');
        }

        /// <summary>
        /// Every enabled camera, by name — check 5 (E11).
        /// </summary>
        /// <remarks>
        /// <b>E11 is a negative, and a negative needs a baseline.</b> The check reads "A's
        /// cameras do not change" while B climbs into a mounted turret, so the artifact has to
        /// carry what A's cameras WERE before B did anything. Recording the enabled set at every
        /// checkpoint makes the before-and-after a diff rather than a memory.
        /// </remarks>
        private void AppendCameras()
        {
            Camera[] cameras = Object.FindObjectsByType<Camera>(
                FindObjectsInactive.Exclude, FindObjectsSortMode.None);

            _json.Append("\"activeCameras\":[");

            if (cameras != null)
            {
                System.Array.Sort(cameras, (a, b) => string.CompareOrdinal(
                    a != null ? a.name : string.Empty, b != null ? b.name : string.Empty));

                bool first = true;
                for (int i = 0; i < cameras.Length; i++)
                {
                    Camera c = cameras[i];
                    if (c == null || !c.enabled) continue;

                    if (!first) _json.Append(',');
                    first = false;

                    _json.Append('{');
                    Str("name", c.name); Comma();
                    Num("depth", c.depth); Comma();
                    Num("fieldOfView", c.fieldOfView);
                    _json.Append('}');
                }
            }

            _json.Append(']');
        }

        private static string TextOf(UnityEngine.UI.Text text)
            => text != null ? text.text : null;

        private static bool IsVisible(UnityEngine.UI.Text text)
            => text != null && text.gameObject.activeInHierarchy && text.enabled;

        private void AppendRemoteActors()
        {
            var registry = Object.FindFirstObjectByType<RemoteActorRegistry>(
                FindObjectsInactive.Include);

            _json.Append("\"remoteActorCount\":");
            _json.Append(registry != null
                ? registry.LiveCount.ToString(CultureInfo.InvariantCulture)
                : "-1");
        }

        private void Comma() => _json.Append(',');

        private void Str(string key, string value)
        {
            _json.Append('"').Append(key).Append("\":\"").Append(Escape(value)).Append('"');
        }

        /// <summary>
        /// A string that may genuinely be absent, written as JSON <c>null</c> rather than as an
        /// empty string.
        /// </summary>
        /// <remarks>
        /// The distinction is the whole of check 1's second half: an actor no <c>S_PLAYER_LIST</c>
        /// has named reads <c>null</c>, while an actor named with an empty string reads
        /// <c>""</c>. Collapsing the two would let a killfeed with no names in it grade as a
        /// killfeed with names.
        /// </remarks>
        private void NullableStr(string key, string value)
        {
            if (value == null) _json.Append('"').Append(key).Append("\":null");
            else Str(key, value);
        }

        private void Num(string key, float value)
        {
            _json.Append('"').Append(key).Append("\":");

            // NaN and Infinity are not JSON. A missing turret pose is genuinely unknown, and
            // null says so where 0 would read as "aimed due north".
            if (float.IsNaN(value) || float.IsInfinity(value)) _json.Append("null");
            else _json.Append(value.ToString("R", CultureInfo.InvariantCulture));
        }

        private void Num(string key, long value)
            => _json.Append('"').Append(key).Append("\":")
                    .Append(value.ToString(CultureInfo.InvariantCulture));

        private static string Escape(string value)
            => string.IsNullOrEmpty(value) ? string.Empty : value.Replace("\\", "\\\\").Replace("\"", "\\\"");

        private static string Sanitize(string value)
        {
            if (string.IsNullOrEmpty(value)) return "checkpoint";

            var sb = new StringBuilder(value.Length);
            foreach (char c in value)
                sb.Append(char.IsLetterOrDigit(c) || c == '-' || c == '_' ? c : '-');

            return sb.ToString();
        }
    }

    /// <summary>The two seeds and the identity a lane-B run is replayed from.</summary>
    public struct LaneBRunSeeds
    {
        public int UnitySeed;
        public string SimulatorPreset;
        public int SimulatorSeed;
        public long PlayerId;
        public string DisplayName;
    }
}
