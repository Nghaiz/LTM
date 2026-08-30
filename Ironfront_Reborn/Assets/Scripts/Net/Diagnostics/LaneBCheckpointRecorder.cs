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
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using Ironfront.Net.Protocol;
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
        private readonly LaneBExplosionLog _explosions;
        private readonly LaneBAllocationSampler _allocation;
        private readonly LaneBDeathInputSampler _deathInput;
        private readonly StringBuilder _json = new StringBuilder(4096);

        private static readonly UTF8Encoding NoBom = new UTF8Encoding(false);

        /// <param name="solver">
        /// Optional. Present, every record carries what the live step was aiming at and whether
        /// the name resolved — which is the difference between "check 1 failed" and "check 1
        /// never had a target", and no screenshot can tell those apart.
        /// </param>
        /// <param name="allocation">
        /// Optional. Present, every record carries the per-frame managed allocation measured
        /// since the previous checkpoint, which is the only instrument check 10 has ever had
        /// (ledger X-33). Absent, the record says <c>"allocation":null</c> rather than zero —
        /// see <see cref="AllocationWindow.Valid"/> for why those must not look alike.
        /// </param>
        /// <param name="deathInput">
        /// Optional. Present, every record carries whether a death took the local player's input
        /// away at any point since the previous checkpoint — check 13's middle term, and the one
        /// X-29 filed as having no measurement at all. Absent, the record says
        /// <c>"deathInput":null</c> rather than a set of zeroes, for <paramref name="allocation"/>'s
        /// reason: a window that was never sampled and a window in which nobody died are
        /// different facts and must not render alike.
        /// </param>
        public LaneBCheckpointRecorder(string directory, string label, string programme,
                                       LaneBRunSeeds seeds, ScriptedTargetSolver solver = null,
                                       LaneBExplosionLog explosions = null,
                                       LaneBAllocationSampler allocation = null,
                                       LaneBDeathInputSampler deathInput = null)
        {
            _directory = directory;
            _label = label;
            _programme = programme;
            _seeds = seeds;
            _solver = solver;
            _explosions = explosions;
            _allocation = allocation;
            _deathInput = deathInput;

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
                // Both counters, each under a name that says which one it is.
                //
                // This block used to publish VehicleSnapshotsApplied as "snapshotsApplied",
                // and the four VehicleInterpolator readings as bare "interp*". On an ON-FOOT
                // programme every one of them is legitimately zero — nobody is in a vehicle —
                // and the record then reads, to anyone who did not open this file, as a client
                // that applied no snapshots and buffered nothing: replication dead. It is the
                // mirror of a green that proves nothing, and it cost a real investigation on
                // 2026-08-21 before `remoteActorCount: 55` and the client's own
                // "first snapshot applied at server tick 143" contradicted it.
                //
                // The name was not merely vague, it was already TAKEN: NetVerificationHarness
                // publishes "snapshotsApplied" from Router.SnapshotsApplied — the actor-stream
                // counter. Two harnesses, one key, two meanings, and no way to tell from the
                // artifact which one you were holding.
                Num("snapshotsApplied", client.Router.SnapshotsApplied); Comma();
                Num("vehicleSnapshotsApplied", client.Router.VehicleSnapshotsApplied); Comma();
                Num("vehicleBaselineMiss", client.Router.UnknownVehicleBaselines); Comma();
                Num("vehicleInterpBuffered", client.Router.VehicleInterpolator.Count); Comma();
                Num("vehicleInterpNewestTick", client.Router.VehicleInterpolator.NewestTick); Comma();
                Num("vehicleInterpStalled", client.Router.VehicleInterpolator.StalledCount); Comma();
                Num("vehicleInterpReordered", client.Router.VehicleInterpolator.OutOfOrderCount); Comma();
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
                // Vehicle-scoped like everything else in this block — see the note above.
                Num("vehicleInputsSent", stage.InputsSent); Comma();
                Num("vehicleStarvedFrames", stage.StarvedFrames); Comma();
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
            AppendAim(client);
            Comma();
            AppendMinimap();
            Comma();
            AppendCombat();
            Comma();
            AppendSeatRequests();
            Comma();
            AppendHud();
            Comma();
            AppendObjectives();
            Comma();
            AppendCameras();
            Comma();
            AppendPresenterOrdering();
            Comma();
            AppendExplosions();
            Comma();
            AppendAllocation();

            if (screenshot != null)
            {
                Comma();
                Str("screenshot", screenshot);
            }

            _json.Append('}');
        }

        private void AppendLocalActor(NetClientBootstrap client)
        {
            ILocalPlayerRig local = NetClientBindings.LocalPlayer;
            if (!local.Exists)
            {
                _json.Append("\"localActor\":null");
                return;
            }

            Vector3 p = local.Position;
            _json.Append("\"localActor\":{");
            Num("x", p.x); Comma(); Num("y", p.y); Comma(); Num("z", p.z); Comma();
            Num("yaw", local.YawDegrees); Comma();
            Num("aimPitch", local.InputSource.Pitch); Comma();
            Num("buttons", local.InputSource.Buttons); Comma();

            // X-13. The row cited correctionSnaps / lastPositionErrorM as evidence that the
            // local body is never corrected. Those are VEHICLE-scoped -- they come from
            // ClientVehicleStage.DrivenStats -- so on an on-foot programme they read zero
            // whatever the local actor is doing, and they say nothing about it. That is the
            // same misattribution 525f68b fixed five fields over.
            //
            // The local body has its own correction path and it was unreported: the server
            // snapshot for the local actor goes through ClientPredictionStage.OnSnapshotApplied
            // into PredictionReconciler, and a 2 km disagreement should come out as a Resync.
            // Three ways that can silently not happen, and the record could not tell them
            // apart -- so it reports all three now rather than one summary:
            //
            //   predictionStage : the component is on Player Fps Actor.prefab, so absent here
            //                     means this body is not that prefab.
            //   inSnapshot      : the decoded snapshot carries no entry for the local actor id,
            //                     so OnSnapshotApplied returns before it reconciles anything.
            //   corrections     : the reconciler ran and agreed, which with a 2 km gap would
            //                     mean the authoritative position it was handed is not the one
            //                     the server logged.
            //
            // authoritativeX/Y/Z is what the snapshot actually carries, so the artifact can be
            // compared against the server log line without a second run.
            AppendLocalPrediction(client, local);
            _json.Append('}');
        }

        private void AppendLocalPrediction(NetClientBootstrap client, ILocalPlayerRig local)
        {
            // Through the rig's own GameObject: ClientPredictionStage is a Net/Client type, so
            // this assembly may name it once it references that assembly. What it may NOT name is
            // FpsActorController, which is why the rig arrives as a seam rather than as itself.
            GameObject rig = local.GameObject;
            ClientPredictionStage stage =
                rig != null ? rig.GetComponent<ClientPredictionStage>() : null;
            Bool("predictionStage", stage != null); Comma();

            if (client == null)
            {
                _json.Append("\"inSnapshot\":null");
                return;
            }

            Num("corrections", client.Reconciler.CorrectionCount); Comma();
            Num("resyncs", client.Reconciler.ResyncCount); Comma();
            Num("pendingInputs", client.Reconciler.Pending); Comma();

            ushort localActorId = client.LocalActorId;

            // Declared and defaulted OUTSIDE the condition: an `out` inside a short-circuiting
            // && is only definitely assigned on the branch that ran, and CS0170 is the compiler
            // saying so.
            ActorSnapshotEntry authoritative = default;
            bool found = localActorId != 0
                         && client.Router.Decoder.Current.TryFind(localActorId, out authoritative);

            Bool("inSnapshot", found);

            if (!found) return;

            Comma();
            Num("authoritativeX", Quantize.UnpackPos(authoritative.PosX)); Comma();
            Num("authoritativeY", Quantize.UnpackPos(authoritative.PosY)); Comma();
            Num("authoritativeZ", Quantize.UnpackPos(authoritative.PosZ));
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
                IReadOnlyList<ushort> ids = registry.LiveIds;
                for (int i = 0; i < ids.Count; i++)
                {
                    if (i > 0) _json.Append(',');
                    ushort id = ids[i];
                    _json.Append('{');
                    Num("id", id); Comma();

                    // Through the registry's pose read rather than the record itself: phase C4c
                    // sealed Net/Client into an assembly, and NetClientVehicle is internal to it
                    // -- correctly, since it is a collaborator of the vehicle stage rather than
                    // API. TryGetPose is the narrow public read this reach actually wanted.
                    if (registry.TryGetPose(id, out Vector3 p, out float yaw, out string mode))
                    {
                        Num("x", p.x); Comma(); Num("y", p.y); Comma(); Num("z", p.z); Comma();
                        Num("yaw", yaw); Comma();
                        Str("mode", mode); Comma();
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
        /// <summary>
        /// Whether the minimap is open, and whether the live step asked for it.
        /// </summary>
        /// <remarks>
        /// <b>Ledger X-61.</b> The map opens only while a key is held and a scripted client has
        /// no keyboard, so until <c>MinimapUi.HoldSource</c> existed no run could open it and no
        /// artifact could say so. Both halves are written: <c>held</c> is what the programme
        /// ASKED for and <c>openness</c> is what the game DID, because a request that had no
        /// effect and a request never made render identically otherwise — and P3's minimap icons
        /// are still owed a screenshot precisely because nothing distinguished them.
        ///
        /// A null <c>MinimapUi</c> writes <c>openness: -1</c> rather than 0. Zero is a real
        /// value meaning "closed", and a HUD that does not exist is not a closed map.
        /// </remarks>
        private void AppendMinimap()
        {
            MinimapUi map = UnityEngine.Object.FindFirstObjectByType<MinimapUi>(
                FindObjectsInactive.Exclude);

            _json.Append("\"minimap\":{");
            _json.Append("\"present\":").Append(map != null ? "true" : "false"); Comma();
            _json.Append("\"held\":")
                 .Append(MinimapUi.HoldSource != null && MinimapUi.HoldSource() ? "true" : "false");
            Comma();
            Num("openness", map != null ? map.Openness : -1f);
            _json.Append('}');
        }

        private void AppendAim(NetClientBootstrap client)
        {
            _json.Append("\"aim\":");

            // Ledger X-44: a VEHICLE solve requests no name, so name-emptiness alone would
            // write `aim: null` for an approach that resolved a vehicle -- indistinguishable
            // from a step that never ran, which is the exact confusion this block exists to
            // prevent. LastRequestWasVehicle is the second half of the question.
            if (_solver == null
                || (string.IsNullOrEmpty(_solver.LastRequestedName)
                    && !_solver.LastRequestWasVehicle))
            {
                _json.Append("null");
                return;
            }

            ScriptedTargetSolver.Solution s = _solver.Last;

            // How old the cached solve is. X-72: Solver.Last is a cache, and a step that stops
            // aiming stops refilling it -- so every later checkpoint re-wrote the same
            // resolved-true reading with the same frozen distance, and a target that had walked
            // 500 m away was indistinguishable from one standing still. A one-frame tolerance,
            // not zero: the capture and the input source both run in Update and Unity does not
            // order them, so a genuinely live solve can be one frame behind this line.
            int ageFrames = _solver.LastSolvedFrame < 0
                ? int.MaxValue
                : Time.frameCount - _solver.LastSolvedFrame;
            bool live = ageFrames <= 1;

            _json.Append('{');
            Str("requested", _solver.LastRequestWasVehicle
                ? "<nearest vehicle>"
                : _solver.LastRequestedName); Comma();
            _json.Append("\"resolved\":").Append(s.Resolved ? "true" : "false"); Comma();

            // Reported, so a reader can tell a stale reading from a live one without having to
            // know that Last is a cache at all.
            _json.Append("\"live\":").Append(live ? "true" : "false"); Comma();
            Num("ageFrames", ageFrames == int.MaxValue ? -1 : ageFrames); Comma();

            Num("targetActorId", s.ActorId); Comma();
            Num("targetVehicleId", s.VehicleId); Comma();

            // NaN unless the reading is BOTH resolved and current. A stale measurement rendered
            // as a live one is worse than no measurement: the first ends an investigation, the
            // second prompts one.
            Num("yaw", s.Resolved && live ? s.Yaw : float.NaN); Comma();
            Num("pitch", s.Resolved && live ? s.Pitch : float.NaN); Comma();
            Num("distanceM", s.Resolved && live ? s.Distance : float.NaN); Comma();
            AppendAimTarget(client, s.ActorId);
            _json.Append('}');
        }

        /// <summary>
        /// Where the aimed actor's PROXY stands on this client, and where the snapshot says it
        /// should stand. X-17.
        /// </summary>
        /// <remarks>
        /// <para>
        /// <b>`resolved: true` says the name found a transform, not that the transform is
        /// anywhere real.</b> On 2026-08-22 the driver aimed at (0, 2000, 0) -- the pool's
        /// parking spot, two kilometres straight up -- while the victim's own record put it at
        /// (1088.1, 103.1, 954.3). The aim block said `resolved` and the distance said 2570 m,
        /// and neither field could say that the thing being aimed at was a proxy nobody had
        /// ever positioned. Reading a parked proxy off `pitch` alone is a trap: pitch is
        /// POSITIVE-IS-DOWN (<c>ScriptedAim</c>), so the -42.6 deg that block carried reads as
        /// a target far BELOW the map if you assume the ordinary sign, and as one far above it
        /// if you do not. That is why these are coordinates and not an angle.
        /// </para>
        /// <para>
        /// <b>The two fields separate the only two causes there are.</b>
        /// <c>RemoteActorRegistry.Update</c> writes a proxy's position only for an actor the
        /// interpolated snapshot pair carries, so either the entry is absent
        /// (<c>inSnapshot: false</c> -- the server is not replicating player actors) or it is
        /// present and the write still did not land (<c>inSnapshot: true</c> with proxy and
        /// authoritative disagreeing -- the lerp or the baseline). Those are fixed in different
        /// files, and without this block the choice is a guess.
        /// </para>
        /// </remarks>
        private void AppendAimTarget(NetClientBootstrap client, ushort actorId)
        {
            _json.Append("\"target\":");

            if (actorId == 0) { _json.Append("null"); return; }

            _json.Append('{');

            var registry = Object.FindFirstObjectByType<RemoteActorRegistry>(
                FindObjectsInactive.Include);

            Transform proxy = null;
            bool hasProxy = registry != null
                            && registry.TryFind(actorId, out proxy)
                            && proxy != null;

            Bool("hasProxy", hasProxy);

            if (hasProxy)
            {
                Vector3 p = proxy.position;
                Comma();
                Num("proxyX", p.x); Comma();
                Num("proxyY", p.y); Comma();
                Num("proxyZ", p.z);
            }

            // Same CS0170 shape as AppendLocalPrediction: `out` inside a short-circuiting &&.
            ActorSnapshotEntry entry = default;
            bool inSnapshot = client != null
                              && client.Router.Decoder.Current.TryFind(actorId, out entry);

            Comma();
            Bool("inSnapshot", inSnapshot);

            if (inSnapshot)
            {
                Comma();
                Num("snapshotX", Quantize.UnpackPos(entry.PosX)); Comma();
                Num("snapshotY", Quantize.UnpackPos(entry.PosY)); Comma();
                Num("snapshotZ", Quantize.UnpackPos(entry.PosZ));
            }

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

                // Whether the component is RUNNING, not merely present. Both of these are found
                // with FindObjectsInactive.Include, and both disable themselves in Awake when
                // NetClientPresenterGuard.IsPresentable is false — silently, with no log. A
                // disabled driver reports weaponId 0 / clipSize 0 / predictedShots 0 and a
                // disabled presenter reports namedPlayers 0, which reads identically to "the
                // player simply has not fired yet" and to "nobody has joined". combat-fix01 was
                // exactly that and cost a full investigation before the log ordering explained
                // it. One boolean each, and the artifact can never be ambiguous about it again.
                _json.Append("\"driverEnabled\":")
                     .Append(driver.isActiveAndEnabled ? "true" : "false"); Comma();

                Num("health", state.Health); Comma();
                _json.Append("\"alive\":").Append(state.IsAlive ? "true" : "false"); Comma();
                Num("ammoInClip", state.AmmoInClip); Comma();
                Num("clipSize", state.ClipSize); Comma();
                _json.Append("\"reloading\":").Append(state.IsReloading ? "true" : "false"); Comma();
                Num("weaponId", state.WeaponId); Comma();
                Num("predictedShots", state.PredictedShots); Comma();
                Num("ammoCorrections", state.SnapshotAmmoCorrections); Comma();
                // Check 13's middle term, and the reason X-29 named it missing: driverEnabled
                // above says whether the COMPONENT runs (it must, to accept a respawn request),
                // which is a different fact from whether the dead player's input is suppressed.
                // FpsActorController.IsInputEnabled is that fact, and it is read-only.
                ILocalPlayerRig localForInput = NetClientBindings.LocalPlayer;
                if (localForInput.Exists)
                {
                    _json.Append("\"localInputEnabled\":")
                         .Append(localForInput.IsInputEnabled ? "true" : "false"); Comma();
                }
                else
                {
                    _json.Append("\"localInputEnabled\":null"); Comma();
                }

                // The term localInputEnabled above cannot carry, and the reason it cannot:
                // FpsActorController.Start disables input and only SpawnAt re-enables it, so
                // that flag is pinned false on every lane-B client whether it is alive or dead.
                // This one is written by the death path itself.
                _json.Append("\"inputSuppressedByDeath\":")
                     .Append(driver.IsInputSuppressedByDeath ? "true" : "false"); Comma();

                AppendDeathInput(); Comma();

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

                // See driverEnabled above — same guard, same silence, same ambiguity.
                _json.Append("\"presenterEnabled\":")
                     .Append(presenter.isActiveAndEnabled ? "true" : "false"); Comma();

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
        /// Whether a death took the local player's input away since the previous checkpoint —
        /// check 13's middle term. Ledger <b>X-29</b>.
        /// </summary>
        /// <remarks>
        /// <para>
        /// <b>A WINDOW, not the instant, and both are written.</b> The instantaneous
        /// <c>inputSuppressedByDeath</c> beside this answers "is input suppressed right now",
        /// and a death can open and close between two captures: across all 21 checkpoints of
        /// <c>p4-pointblank-01</c> the record showed <c>alive: true</c> every time while the
        /// killfeed proved both players died repeatedly. This window counts every frame in
        /// between, so a death that fell between two captures is still counted.
        /// </para>
        /// <para>
        /// <b>The instant is not useless and the claim is deliberately narrow.</b> "A capture
        /// never lands inside the dead window" would be false — <c>p5-separation-02</c>'s
        /// <c>killed</c> capture landed on one and read <c>inputSuppressedByDeath: true</c>,
        /// and seven lane-B artifacts predating this field already carried a checkpoint with
        /// <c>alive: false</c>. The claim is only that the instant cannot be RELIED on: over
        /// those same runs it caught 1 of the 6 windows in which a death occurred.
        /// </para>
        /// <para>
        /// <b><c>deadFrames</c> is what stops a vacuous pass.</b> Zero suppressed frames means
        /// one of two opposite things: nobody died in this window, or somebody died and kept
        /// their input. Without the dead count those render identically and the healthy reading
        /// is the same as the failure. Read them as a pair: <c>deadFrames &gt; 0</c> with
        /// <c>suppressedFrames == 0</c> is the failure check 13 is looking for.
        /// </para>
        /// <para>
        /// <b><c>null</c> when no sampler was supplied</b>, for <c>AppendAllocation</c>'s reason:
        /// a server process has no driver, and a set of zeroes from a window nobody sampled
        /// would grade check 13 on the strength of not having measured.
        /// </para>
        /// </remarks>
        private void AppendDeathInput()
        {
            if (_deathInput == null)
            {
                _json.Append("\"deathInput\":null");
                return;
            }

            DeathInputWindow window = _deathInput.TakeWindow();

            _json.Append("\"deathInput\":{");
            Bool("driverPresent", window.DriverPresent); Comma();
            Num("frames", window.Frames); Comma();
            Num("suppressedFrames", window.SuppressedFrames); Comma();
            Num("deadFrames", window.DeadFrames); Comma();
            Bool("suppressionObserved", window.SuppressionObserved); Comma();
            Bool("deathObserved", window.DeathObserved);
            _json.Append('}');
        }

        /// <summary>
        /// What the server said about this client's seat requests — ledger <b>X-65</b>, and the
        /// precondition for grading check 5.
        /// </summary>
        /// <remarks>
        /// <para>
        /// <b>P4 named the missing field and this is it.</b> In <c>p4-turret-02</c> a second
        /// client stood 1.7 m from vehicle 15 — against <c>SeatArbiter.MaxSeatReachMetres</c> of
        /// 6 — pressed the seat toggle, and <c>occupiedVehicleId</c> stayed 0 with no
        /// <c>S_SEAT_CHANGE</c>, no rejection and no log line anywhere in the artifact. The run
        /// could not distinguish a request that was never sent from one that was refused, which
        /// are opposite defects.
        /// </para>
        /// <para>
        /// <b>Counters AND the last answer, because neither is sufficient.</b>
        /// <c>requestsSent 0</c> after a programmed toggle localises the fault to the client's
        /// own send path; <c>requestsSent 2, lastResult RejectedOccupied</c> localises it to the
        /// arbiter's answer. <c>lastResult</c> alone cannot say which, because
        /// <c>ClientSeatRequester.LastResult</c> initialises to <c>Entered</c> before any answer
        /// has arrived — so an untouched requester and a granted one read the same, and only
        /// <c>requestsSent</c> separates them.
        /// </para>
        /// <para>
        /// <b>Why this gates E11 rather than merely check 12.</b> The only
        /// <c>MountedTurret</c> in the project is on <c>tank.prefab</c>, at seat index 1 — the
        /// Gunner — and <c>ClientSeatRequester</c> reaches index 1 only by being refused index 0
        /// first. So the A16 camera hijack E11 grades cannot be provoked at all unless the
        /// occupied-walk works, and until this field existed there was no way to see whether it
        /// had run.
        /// </para>
        /// </remarks>
        private void AppendSeatRequests()
        {
            var requester = Object.FindFirstObjectByType<ClientSeatRequester>(
                FindObjectsInactive.Include);

            if (requester == null)
            {
                _json.Append("\"seat\":null");
                return;
            }

            _json.Append("\"seat\":{");
            Bool("requesterEnabled", requester.isActiveAndEnabled); Comma();
            Num("requestsSent", requester.RequestsSent); Comma();
            Num("requestsRefused", requester.RequestsRefused); Comma();
            Num("pressesWhileWaiting", requester.PressesWhileWaiting); Comma();
            Str("lastResult", requester.LastResult.ToString()); Comma();
            Str("lastRefusalText", requester.LastRefusalText ?? string.Empty);
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
            IDiagnosticsProbe probe = NetDiagnosticsBindings.Probe;

            // Declared and defaulted OUTSIDE the condition, for the reason AppendLocalPrediction
            // records: an `out` inside a short-circuiting && is only definitely assigned on the
            // branch that ran, and CS0170 is the compiler saying so.
            HudReading hud = default;
            ScoreboardReading board = default;

            bool hasHud   = probe != null && probe.TryReadHud(out hud);
            bool hasBoard = probe != null && probe.TryReadScoreboard(out board);

            _json.Append("\"hud\":");
            if (!hasHud && !hasBoard) { _json.Append("null"); return; }

            _json.Append('{');

            if (hasHud)
            {
                NullableStr("blueScoreText", hud.BlueScore); Comma();
                NullableStr("redScoreText", hud.RedScore); Comma();
                NullableStr("blueFlagsText", hud.BlueFlags); Comma();
                NullableStr("redFlagsText", hud.RedFlags); Comma();
                NullableStr("phaseText", hud.Phase); Comma();
                NullableStr("phaseTimerText", hud.PhaseTimer); Comma();
                _json.Append("\"phaseTimerVisible\":")
                     .Append(hud.PhaseTimerVisible ? "true" : "false"); Comma();
                _json.Append("\"victoryVisible\":")
                     .Append(hud.VictoryVisible ? "true" : "false");
                Comma();
            }
            else
            {
                Str("scoreUi", "absent"); Comma();
            }

            if (hasBoard)
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
        // Reused across checkpoints: the recorder runs on a timer during a match, and a fresh
        // list per read would be garbage generated by the instrument measuring the frame time.
        private readonly List<CapturePointReading> _capturePoints = new List<CapturePointReading>();

        private void AppendObjectives()
        {
            _capturePoints.Clear();
            NetDiagnosticsBindings.Probe?.ReadCapturePoints(_capturePoints);

            _json.Append("\"capturePoints\":[");

            for (int i = 0; i < _capturePoints.Count; i++)
            {
                if (i > 0) _json.Append(',');
                _json.Append('{');
                Str("name", _capturePoints[i].Name); Comma();
                Num("owner", _capturePoints[i].Owner);
                _json.Append('}');
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
        /// <summary>
        /// Every authoritative explosion this client has been sent — check 4 (E10).
        /// </summary>
        /// <remarks>
        /// <para>
        /// <b><c>explosionsAttached</c> is the load-bearing field, not the list.</b> An empty
        /// list from a log that never attached and an empty list from a run where nothing
        /// exploded are opposite verdicts for check 4, and they render identically. So the
        /// listening state is stated rather than inferred.
        /// </para>
        /// <para>
        /// <b>What comparing two clients' lists can and cannot show.</b> Both decode the same
        /// <c>S_EXPLOSION</c> through the same <c>Quantize.UnpackPos</c>, so agreeing POSITIONS
        /// prove nothing — that comparison cannot fail. What can fail is RECEIPT: a blast
        /// interest management culled for one client, a client whose presenter never
        /// instantiated, a blast the server never emitted. Read the counts first and the
        /// coordinates second.
        /// </para>
        /// <para>
        /// The thrower's DRAWN position is not here and cannot be: the presenter suppresses the
        /// server's confirmation of a blast it already predicted, and exposes neither the drawn
        /// centre nor the suppressor's verdict. Ledger X-29.
        /// </para>
        /// </remarks>
        private void AppendExplosions()
        {
            if (_explosions == null)
            {
                _json.Append("\"explosionsAttached\":false,\"explosionsTotal\":0,\"explosions\":[]");
                return;
            }

            _json.Append("\"explosionsAttached\":")
                 .Append(_explosions.Attached ? "true" : "false"); Comma();
            Num("explosionsTotal", _explosions.TotalReceived); Comma();

            _json.Append("\"explosions\":[");

            IReadOnlyList<LaneBExplosionLog.Entry> entries = _explosions.Entries;
            for (int i = 0; i < entries.Count; i++)
            {
                if (i > 0) Comma();

                LaneBExplosionLog.Entry e = entries[i];
                _json.Append('{');
                Num("atSeconds", e.Seconds); Comma();
                Num("sourceActorId", e.SourceActorId); Comma();
                Num("x", e.X); Comma();
                Num("y", e.Y); Comma();
                Num("z", e.Z); Comma();
                Num("radiusMetres", e.RadiusMetres); Comma();
                Str("kind", e.Kind.ToString());
                _json.Append('}');
            }

            _json.Append(']');
        }

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

        /// <summary>
        /// Check 6 (E12), as V10 § 7 states its pass condition: the presenters that found no
        /// <c>NetClientBootstrap</c> in <c>Awake</c>. Empty is the pass.
        /// </summary>
        /// <remarks>
        /// <para>
        /// <b>The field records an OUTCOME, unlike <c>activeCameras</c> beside it.</b> Check 5's
        /// field was recorded at every checkpoint of every run and carried baseline throughout,
        /// because nothing ever attempted a camera hijack — so it recorded the absence of the
        /// case rather than a verdict, which is what X-37 filed. This one is not in that
        /// position: E12's case is an ordinary client start, so every run has always provoked it
        /// and only the reading was missing.
        /// </para>
        /// <para>
        /// <b>A count as well as the names, and both.</b> The names are what a person needs to
        /// fix it; the count is what a grader can assert on without parsing an array that is
        /// empty in the healthy case — and "the key was absent" and "the list was empty" must
        /// not be the same reading, or a recorder that stopped writing this would look like a
        /// pass forever.
        /// </para>
        /// </remarks>
        private void AppendPresenterOrdering()
        {
            _json.Append("\"presentersWithNoBootstrap\":[");

            int count = 0;
            foreach (string presenter in NetClientPresenterGuard.PresentersThatFoundNoBootstrap)
            {
                if (count > 0) _json.Append(',');
                _json.Append('"').Append(Escape(presenter)).Append('"');
                count++;
            }

            _json.Append("],");
            Num("presentersWithNoBootstrapCount", count);
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

        /// <summary>
        /// Per-frame managed allocation since the previous checkpoint. Ledger <b>X-33</b>,
        /// check 10.
        /// </summary>
        /// <remarks>
        /// <para>
        /// <b>A WINDOW, not a total, and not one frame.</b> The window is drained at every
        /// checkpoint, so two consecutive records describe two disjoint spans — which is what
        /// makes them subtractable. Check 10 asks whether <c>ClientVehicleStage</c> ADDS
        /// allocation, and "adds" is a difference: read <c>bytesPerFrame</c> on a checkpoint
        /// where <c>drivenVehicleId</c> is 0 against one where it is not, in the same run.
        /// </para>
        /// <para>
        /// <b><c>bytesPerFrame</c> is -1 when there is no answer</b>, never 0. A non-development
        /// player has no profiler counters and a window with no frames has nothing to divide by;
        /// both would render as a flawless zero and grade check 10 PASS on the strength of not
        /// having measured. <c>valid</c> and <c>frames</c> are carried beside it so the reason is
        /// legible rather than inferred.
        /// </para>
        /// <para>
        /// <b><c>probeBytesPerFrame</c> is how this record says it is not evidence.</b> A run
        /// with <c>IRONFRONT_LANEB_ALLOC_PROBE</c> armed is deliberately allocating and exists
        /// to prove the instrument can rise (acceptance criterion 5). Non-zero here means every
        /// allocation figure in this file grades the recorder, not the game.
        /// </para>
        /// </remarks>
        private void AppendAllocation()
        {
            if (_allocation == null)
            {
                _json.Append("\"allocation\":null");
                return;
            }

            AllocationWindow window = _allocation.TakeWindow();

            _json.Append("\"allocation\":{");
            Bool("valid", window.Valid); Comma();
            Str("counter", LaneBAllocationSampler.CounterName); Comma();
            Num("frames", window.Frames); Comma();
            Num("totalBytes", window.TotalBytes); Comma();
            Num("maxBytesInAFrame", window.MaxBytesInAFrame); Comma();
            Num("bytesPerFrame", (float)window.BytesPerFrame); Comma();
            Num("probeBytesPerFrame", _allocation.ProbeBytesPerFrame);
            _json.Append('}');
        }

        private void Comma() => _json.Append(',');

        private void Bool(string key, bool value)
        {
            _json.Append('"').Append(key).Append("\":").Append(value ? "true" : "false");
        }

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
#endif
