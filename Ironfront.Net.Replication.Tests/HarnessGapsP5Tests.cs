using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;
using Xunit;

namespace Ironfront.Net.Replication.Tests
{
    /// <summary>
    /// P5 — the three gaps in the harness itself. Ledger <b>X-28</b>, <b>X-29</b>, <b>X-37</b>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>What is testable here and what is not</b>, on the split
    /// <see cref="ApproachVehicleTests"/> already draws. The recorder, the harness and the new
    /// death-input sampler all name <c>UnityEngine</c> and never reach <c>dotnet test</c>, so
    /// they are pinned as source text. The lane-B programmes are plain data on disk and ARE
    /// gradeable behaviourally, so the invariants that live in them are parsed and asserted
    /// rather than grepped.
    /// </para>
    /// <para>
    /// <b>The programme assertions are the load-bearing half.</b> Two of this phase's three
    /// tasks come down to an ordering in recorded data — a capture that must come after a
    /// respawn edge, and a seat toggle that must come after another client's — and both are
    /// invisible in every artifact when they are wrong: the run completes, every client exits
    /// 0, the checkpoint count is full, and the case simply never happened. A source grep
    /// cannot see either. Parsing can.
    /// </para>
    /// </remarks>
    public sealed class HarnessGapsP5Tests
    {
        // ---------------------------------------------------------------- X-29, check 13

        /// <summary>
        /// The driver exposes its death-suppression flag read-only, and the recorder writes it.
        /// </summary>
        /// <remarks>
        /// Both halves, because either alone is inert: an accessor nothing reads changes no
        /// verdict, and a recorder field no component writes would render as a constant. This is
        /// the same two-sided pin <c>ALostLinkIsRecordedAndFailsTheRun</c> uses, and for the
        /// same reason — the one-sided version of it was walked straight past by a mutation.
        /// </remarks>
        [Fact]
        public void DeathInputSuppressionIsExposedAndRecorded()
        {
            string driver = UnitySource("Net/Client/NetClientLocalCombatDriver.cs");
            Assert.Contains(
                "public bool IsInputSuppressedByDeath => _inputSuppressedByDeath;", driver);

            string recorder = UnitySource("Net/Diagnostics/LaneBCheckpointRecorder.cs");
            Assert.Contains("\\\"inputSuppressedByDeath\\\":", recorder);
            Assert.Contains("driver.IsInputSuppressedByDeath", recorder);
        }

        /// <summary>
        /// The window carries <c>deadFrames</c> beside <c>suppressedFrames</c>.
        /// </summary>
        /// <remarks>
        /// <b>This is the anti-vacuity pin, and it is the whole point of the field.</b> Zero
        /// suppressed frames means one of two opposite things: nobody died in this window, or
        /// somebody died and kept their input. Without the dead count those render identically
        /// and the failure reads exactly like the healthy case — which is how check 13 came to
        /// be graded on two of its three terms in the first place.
        /// </remarks>
        [Fact]
        public void TheDeathInputWindowCannotPassVacuously()
        {
            string sampler = UnitySource("Net/Diagnostics/LaneBDeathInputSampler.cs");
            Assert.Contains("public readonly long DeadFrames;", sampler);
            Assert.Contains("public readonly long SuppressedFrames;", sampler);

            // IsAlive, not !suppressed: deriving deadness from the suppression flag would make
            // "dead with input" — the failure this grades — inexpressible.
            Assert.Contains("if (!_driver.State.IsAlive)", sampler);

            string recorder = UnitySource("Net/Diagnostics/LaneBCheckpointRecorder.cs");
            Assert.Contains("Num(\"deadFrames\", window.DeadFrames)", recorder);
            Assert.Contains("Num(\"suppressedFrames\", window.SuppressedFrames)", recorder);
        }

        /// <summary>
        /// The sampler is constructed, ticked every frame, and handed to the recorder.
        /// </summary>
        /// <remarks>
        /// Present is not wired. A sampler that is built and never ticked reports a window of
        /// zero frames on every checkpoint, which <c>DriverPresent</c> would report as
        /// <c>false</c> — legible, but only if somebody reads it. The three sites are pinned
        /// separately because dropping any one of them produces a different silent zero.
        /// </remarks>
        [Fact]
        public void TheDeathInputSamplerIsWiredIntoTheHarness()
        {
            string harness = UnitySource("Net/Diagnostics/LaneBHarness.cs");

            Assert.Contains("_deathInput = new LaneBDeathInputSampler();", harness);
            Assert.Contains("_deathInput?.Sample();", harness);
            Assert.Contains("_deathInput);", harness);
        }

        /// <summary>
        /// Every programme that raises a respawn edge captures at least once afterwards.
        /// </summary>
        /// <remarks>
        /// <para>
        /// X-29's second half: a checkpoint fires at its step's ENTRY
        /// (<c>ScriptedInputCursor.EnterStepIfNeeded</c>), so a capture named on the step that
        /// SETS <c>respawn</c> is taken before the request is even sent. Three victim
        /// programmes ended on exactly that step, which is why no artifact in the project has
        /// ever shown a respawn landing.
        /// </para>
        /// <para>
        /// Asserted over every programme on disk rather than the three that were fixed, so the
        /// next victim programme written cannot reintroduce it.
        /// </para>
        /// </remarks>
        [Fact]
        public void ARespawnStepIsFollowedByACapture()
        {
            var offenders = new List<string>();

            foreach (string path in ProgrammeFiles())
            {
                List<JsonElement> steps = Steps(path);

                for (int i = 0; i < steps.Count; i++)
                {
                    if (!Flag(steps[i], "respawn")) continue;

                    bool captureAfter = steps
                        .Skip(i + 1)
                        .Any(s => !string.IsNullOrEmpty(Text(s, "checkpoint")));

                    if (!captureAfter) offenders.Add($"{Path.GetFileName(path)} step {i}");
                }
            }

            Assert.True(offenders.Count == 0,
                "a respawn edge with no capture after it — the artifact cannot show the "
                + "respawn landing (X-29): " + string.Join(", ", offenders));
        }

        // ---------------------------------------------------------------- X-37, check 5

        /// <summary>
        /// The E11 set exists and its seat-toggle ordering is the one that provokes the case.
        /// </summary>
        /// <remarks>
        /// <para>
        /// <b>The ordering IS the programme.</b> The only <c>MountedTurret</c> in the project is
        /// on <c>tank.prefab</c> and it is the GUNNER seat's weapon — index 1 of the Vehicle's
        /// two-entry seats array — while <c>ClientSeatRequester</c> always asks for seat 0 and
        /// reaches index 1 only by being refused. So the A16 hijack in
        /// <c>MountedTurret.Unholster</c> can only fire for a client whose request arrives
        /// AFTER another client has booked seat 0.
        /// </para>
        /// <para>
        /// Reverse the two toggles and OBS-B takes the driver's seat: the run completes, every
        /// checkpoint is written, <c>activeCameras</c> reads baseline, and E11 is unprovoked
        /// again with nothing anywhere saying so. That is the failure this asserts against.
        /// </para>
        /// </remarks>
        [Fact]
        public void TheE11SetTogglesSeatZeroBeforeTheTurret()
        {
            int holder = FirstStepWith(ProgrammePath("e11-driver.json"), "seatToggle");
            int gunner = FirstStepWith(ProgrammePath("e11-observer-b.json"), "seatToggle");

            Assert.True(holder >= 0, "e11-driver.json never toggles a seat");
            Assert.True(gunner >= 0, "e11-observer-b.json never toggles a seat");

            Assert.True(holder < gunner,
                $"e11-driver must book seat 0 before e11-observer-b asks (holder step {holder}, "
                + $"gunner step {gunner}) — otherwise the gunner takes seat 0 and no turret is "
                + "ever mounted, so check 5 grades nothing and says nothing");

            // The witness never toggles: a third occupant would take the seat under test.
            Assert.Equal(-1, FirstStepWith(ProgrammePath("e11-observer-a.json"), "seatToggle"));
        }

        /// <summary>
        /// The recorder carries the seat-request outcome, counters and answer both.
        /// </summary>
        /// <remarks>
        /// Ledger <b>X-65</b>: in <c>p4-turret-02</c> a client stood 1.7 m from a vehicle against
        /// a 6 m reach limit, toggled, and nothing appeared anywhere — no grant, no rejection,
        /// no log line — so the run could not distinguish a request that was never sent from one
        /// that was refused. Both fields are required because neither is sufficient:
        /// <c>LastResult</c> initialises to <c>Entered</c> before any answer arrives, so an
        /// untouched requester and a granted one read alike and only <c>requestsSent</c>
        /// separates them.
        /// </remarks>
        [Fact]
        public void TheSeatRequestOutcomeIsRecorded()
        {
            string recorder = UnitySource("Net/Diagnostics/LaneBCheckpointRecorder.cs");

            Assert.Contains("AppendSeatRequests();", recorder);
            Assert.Contains("\\\"seat\\\":", recorder);
            Assert.Contains("requester.RequestsSent", recorder);
            Assert.Contains("requester.LastResult", recorder);
        }

        // ---------------------------------------------------------------- X-28, spawn geometry

        /// <summary>
        /// The separation shooter withdraws before it approaches, so the approach has work to do.
        /// </summary>
        /// <remarks>
        /// <para>
        /// Acceptance criterion 5 asks that the shooter start OUTSIDE
        /// <c>holdDistanceMeters</c>, evidenced by a non-zero <c>ApproachMoveZ</c> on the
        /// approach step's first frame. <c>ApproachMoveZ</c> returns 1 only while distance
        /// exceeds the hold, and same-team clients spawn about 3 m apart against a hold of 6 —
        /// so without a withdraw the approach is inert from frame one and the metre or two of
        /// spread a run reports is spawn jitter rather than movement.
        /// </para>
        /// <para>
        /// This pins the programme's shape, not the run's outcome. The outcome is
        /// <c>aim.distanceM</c> at the <c>approaching</c> capture, and it belongs in the report.
        /// </para>
        /// </remarks>
        [Fact]
        public void TheSeparationShooterWithdrawsBeforeApproaching()
        {
            List<JsonElement> steps = Steps(ProgrammePath("separation-driver.json"));

            int withdraw = steps.FindIndex(
                s => s.TryGetProperty("moveZ", out JsonElement z) && z.GetDouble() < 0.0);
            int approach = steps.FindIndex(s => Flag(s, "approach"));

            Assert.True(withdraw >= 0, "separation-driver.json never walks away from its target");
            Assert.True(approach >= 0, "separation-driver.json never approaches");
            Assert.True(withdraw < approach,
                $"the withdraw (step {withdraw}) must precede the approach (step {approach}), or "
                + "the shooter starts inside holdDistanceMeters and ApproachMoveZ is 0 from the "
                + "first frame — X-28's measured consequence");
        }

        /// <summary>
        /// The runner can turn the server's shot log on, so X-28's criterion 4 is a command.
        /// </summary>
        /// <remarks>
        /// <c>ServerCombatBridge.LogShot</c> prints the nearest OTHER target beside every
        /// trigger frame, and it is the only artifact that answers "did the resolver pick the
        /// intended target or the witness". The 2026-08-25 measurement set
        /// <c>IRONFRONT_LOG_SHOTS</c> by hand, so the run that produced it was reproducible only
        /// by somebody who already knew to.
        /// </remarks>
        [Fact]
        public void TheRunnerCanEnableTheShotLog()
        {
            string runner = RepoFile("tools/run-lane-b.ps1");

            // A word boundary, not Contains. `Contains("[switch] $LogShots")` matches
            // `[switch] $LogShotsDisabled` too, and this gate was observed surviving exactly
            // that mutation — the same trap ALostLinkIsRecordedAndFailsTheRun documents. A
            // renamed parameter leaves `if ($LogShots)` reading an undeclared variable, which
            // in PowerShell is $null and therefore falsy: the switch would silently stop
            // working and every run would grade X-28 with no shot log and no error.
            Assert.Matches(@"\[switch\] \$LogShots\b", runner);

            // The whole guard, so the declaration and the assignment cannot drift apart.
            Assert.Contains("if ($LogShots) { $env:IRONFRONT_LOG_SHOTS = \"1\" }", runner);

            // Emitted by the server only; a client never reaches ServerCombatBridge.
            string bridge = UnitySource("Net/Server/ServerCombatBridge.cs");
            Assert.Contains("IRONFRONT_LOG_SHOTS", bridge);
        }

        // ---------------------------------------------------------------- the stale sentence

        /// <summary>
        /// The presenter no longer claims nothing owns a <c>ClientCombatState</c>.
        /// </summary>
        /// <remarks>
        /// <c>NetClientLocalCombatDriver</c> declares itself the one production owner and holds
        /// one. The sentence that stood in <c>KnockOverLocalActor</c> was the last surviving
        /// copy of a fact that had stopped being true — the same decay this consolidation
        /// exists to end — so it is pinned as an absence rather than left to be re-read.
        /// </remarks>
        [Fact]
        public void ThePresenterDoesNotClaimAnUnownedCombatState()
        {
            string presenter = UnitySource("Net/Client/NetClientCombatPresenter.cs");

            Assert.DoesNotContain("no Unity component holds one yet", presenter);
            Assert.Contains("NetClientLocalCombatDriver declares itself", presenter);
        }

        // ---------------------------------------------------------------- helpers

        private static IEnumerable<string> ProgrammeFiles() =>
            Directory
                .EnumerateFiles(Path.Combine(RepoRoot(), "tools", "lane-b"), "*.json")
                .OrderBy(p => p, StringComparer.Ordinal);

        private static string ProgrammePath(string name)
        {
            string path = Path.Combine(RepoRoot(), "tools", "lane-b", name);
            Assert.True(File.Exists(path), $"missing lane-B programme: {path}");
            return path;
        }

        private static List<JsonElement> Steps(string path)
        {
            using var document = JsonDocument.Parse(File.ReadAllText(path));

            Assert.True(document.RootElement.TryGetProperty("steps", out JsonElement steps),
                $"{Path.GetFileName(path)} has no steps array");

            // Cloned: the JsonDocument owns the buffer these elements point into and is disposed
            // on the way out of this method.
            return steps.EnumerateArray().Select(s => s.Clone()).ToList();
        }

        private static int FirstStepWith(string path, string flag) =>
            Steps(path).FindIndex(s => Flag(s, flag));

        private static bool Flag(JsonElement step, string name) =>
            step.TryGetProperty(name, out JsonElement value)
            && value.ValueKind == JsonValueKind.True;

        private static string Text(JsonElement step, string name) =>
            step.TryGetProperty(name, out JsonElement value) && value.ValueKind == JsonValueKind.String
                ? value.GetString() ?? string.Empty
                : string.Empty;

        private static string UnitySource(string relativePath)
        {
            string path = Path.Combine(
                RepoRoot(), "Ironfront_Reborn", "Assets", "Scripts",
                relativePath.Replace('/', Path.DirectorySeparatorChar));

            Assert.True(File.Exists(path), $"missing Unity source: {path}");
            return File.ReadAllText(path);
        }

        private static string RepoFile(string relativePath)
        {
            string path = Path.Combine(
                RepoRoot(), relativePath.Replace('/', Path.DirectorySeparatorChar));

            Assert.True(File.Exists(path), $"missing repo file: {path}");
            return File.ReadAllText(path);
        }

        private static string RepoRoot()
        {
            for (DirectoryInfo? d = new DirectoryInfo(Directory.GetCurrentDirectory());
                 d != null;
                 d = d.Parent)
            {
                if (File.Exists(Path.Combine(d.FullName, "Ironfront.sln"))) return d.FullName;
            }

            throw new InvalidOperationException(
                "Ironfront.sln not found walking up from " + Directory.GetCurrentDirectory());
        }
    }
}
