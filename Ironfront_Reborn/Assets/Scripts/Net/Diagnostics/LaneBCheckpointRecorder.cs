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
        private readonly StringBuilder _json = new StringBuilder(2048);

        public LaneBCheckpointRecorder(string directory, string label, string programme,
                                       LaneBRunSeeds seeds)
        {
            _directory = directory;
            _label = label;
            _programme = programme;
            _seeds = seeds;

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
            File.AppendAllText(RecordPath, _json.ToString() + "\n", Encoding.UTF8);
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
