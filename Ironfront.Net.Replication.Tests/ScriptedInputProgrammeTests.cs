using System;
using System.IO;
using System.Linq;
using Ironfront.Net.Unity.Diagnostics;
using Xunit;

namespace Ironfront.Net.Replication.Tests
{
    /// <summary>
    /// debt-closure phase 3D lane B — the pins for the scripted-input programme.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Two halves, graded two ways.</b> <see cref="ScriptedInputCursor"/> and
    /// <see cref="ScriptedInputProgramme"/> are pure and execute here. <c>LaneBHarness</c> and
    /// <c>ScriptedInputSource</c> touch <c>UnityEngine</c> and cannot, so they are graded by
    /// Roslyn over the real files — the same arrangement 3C used, and for the same reason: no
    /// gate in this repository compiles Unity code, and the two facts that would silently break
    /// lane B both live in that half.
    /// </para>
    /// <para>
    /// <b>The pin that matters most is the carried remainder.</b> A cursor that discarded the
    /// leftover of a step boundary would slide every later checkpoint by an amount that depends
    /// on the client's frame rate — so three clients on one programme would checkpoint at three
    /// different moments and their captures would disagree for a reason that is the harness's
    /// fault. That reads as a replication defect and is not one, which is exactly the failure
    /// <c>phase-3d-lane-b.md</c> § 8 row 2 exists to keep out of the results.
    /// </para>
    /// </remarks>
    public sealed class ScriptedInputProgrammeTests
    {
        // ------------------------------------------------------------------ cursor arithmetic

        /// <summary>A step's declared yaw replaces whatever the previous one integrated to.</summary>
        [Fact]
        public void EnteringAStepAdoptsItsAbsoluteYaw()
        {
            var cursor = new ScriptedInputCursor(Programme(
                Step(seconds: 1f, yaw: 0f, yawRate: 90f),
                Step(seconds: 1f, yaw: 200f)));

            cursor.Advance(1f);

            Assert.Equal(1, cursor.StepIndex);
            Assert.Equal(200f, cursor.Yaw, 3);
        }

        /// <summary>The rate is integrated, not applied as a one-off.</summary>
        [Fact]
        public void YawIntegratesAtTheStepsRate()
        {
            var cursor = new ScriptedInputCursor(Programme(Step(seconds: 4f, yaw: 0f, yawRate: 45f)));

            cursor.Advance(2f);

            Assert.Equal(90f, cursor.Yaw, 3);
        }

        /// <summary>
        /// The leftover of a frame that crosses a boundary belongs to the NEXT step. Advancing
        /// in one 2 s frame and in twenty 0.1 s frames must land in the same place.
        /// </summary>
        [Fact]
        public void ACrossedBoundaryCarriesTheRemainderIntoTheNextStep()
        {
            ScriptedInputProgramme MakeProgramme() => Programme(
                Step(seconds: 1f, yaw: 0f),
                Step(seconds: 3f, yaw: 0f, yawRate: 30f));

            var coarse = new ScriptedInputCursor(MakeProgramme());
            coarse.Advance(2f);

            var fine = new ScriptedInputCursor(MakeProgramme());
            for (int i = 0; i < 20; i++) fine.Advance(0.1f);

            // One second into a 30 deg/s step, whichever way the frames fell.
            Assert.Equal(1, coarse.StepIndex);
            Assert.Equal(1, fine.StepIndex);
            Assert.Equal(30f, coarse.Yaw, 2);
            Assert.Equal(30f, fine.Yaw, 2);
        }

        /// <summary>A checkpoint is owed once, on entry, and consumed by the taker.</summary>
        [Fact]
        public void ACheckpointFiresExactlyOnceOnEntry()
        {
            var cursor = new ScriptedInputCursor(Programme(
                Step(seconds: 1f, checkpoint: "alpha")));

            cursor.Advance(0.1f);

            Assert.True(cursor.TryTakeCheckpoint(out ScriptedCheckpoint first));
            Assert.Equal("alpha", first.Name);
            Assert.Equal(0f, first.DueAtSeconds, 3);
            Assert.False(cursor.TryTakeCheckpoint(out _));

            cursor.Advance(0.1f);
            Assert.False(cursor.TryTakeCheckpoint(out _));
        }

        /// <summary>
        /// A checkpoint on the step a long frame lands in is still owed after that frame.
        /// </summary>
        /// <remarks>
        /// Without this the third client — the one whose window opened last and whose first
        /// frame is the longest — is the one that silently loses its checkpoint, which is the
        /// hardest version of this bug to see.
        /// </remarks>
        [Fact]
        public void ALongFrameStillOwesTheCheckpointItLandedOn()
        {
            var cursor = new ScriptedInputCursor(Programme(
                Step(seconds: 0.05f, checkpoint: "first"),
                Step(seconds: 5f, checkpoint: "second")));

            cursor.Advance(1f);

            Assert.True(cursor.TryTakeCheckpoint(out ScriptedCheckpoint first));
            Assert.Equal("first", first.Name);

            Assert.True(cursor.TryTakeCheckpoint(out ScriptedCheckpoint second));
            Assert.Equal("second", second.Name);

            // The due times say how late each capture is: 'second' came due 0.05 s into a frame
            // that ran for a whole second, so its screenshot is 0.95 s stale and the artifact
            // says so rather than leaving a reader to assume it is not.
            Assert.Equal(0f, first.DueAtSeconds, 3);
            Assert.Equal(0.05f, second.DueAtSeconds, 3);

            Assert.False(cursor.TryTakeCheckpoint(out _));
        }

        /// <summary>Advance reports the end of the programme rather than making the caller ask.</summary>
        [Fact]
        public void AdvanceReportsFalseOnceTheProgrammeIsSpent()
        {
            var cursor = new ScriptedInputCursor(Programme(Step(seconds: 1f)));

            Assert.True(cursor.Advance(0.5f));
            Assert.False(cursor.Advance(0.6f));
            Assert.True(cursor.Finished);
            Assert.Null(cursor.Current);
        }

        /// <summary>Yaw stays in 0..360, because the protocol quantizes an unsigned angle.</summary>
        [Fact]
        public void YawWrapsIntoTheUnsignedRange()
        {
            var cursor = new ScriptedInputCursor(Programme(Step(seconds: 10f, yaw: 350f, yawRate: 90f)));

            cursor.Advance(1f);

            Assert.InRange(cursor.Yaw, 0f, 360f);
            Assert.Equal(80f, cursor.Yaw, 2);
        }

        [Fact]
        public void TotalSecondsSumsEveryStep()
        {
            Assert.Equal(6f, Programme(Step(seconds: 1f), Step(seconds: 2f), Step(seconds: 3f))
                                 .TotalSeconds, 3);
        }

        // --------------------------------------------------------------- the Unity half, by Roslyn

        /// <summary>
        /// The harness installs BOTH halves of the input seam and enables the clock.
        /// </summary>
        /// <remarks>
        /// <para>
        /// Three separate facts, each of which is silent when it breaks. Without
        /// <c>SetInputSource</c> the client cannot fire, aim, reload or drive — the whole of
        /// row X-3 restated. Without <c>NetPredictionClock.InputSource</c> movement falls back
        /// to <c>MovementSimulation.FromUnityInput</c>, which samples a keyboard nobody is at,
        /// so every scripted client stands still while its programme runs to completion and the
        /// run reports success. And <c>NetPredictionClock</c> ships DISABLED (checklist A4), so
        /// without the enable no <c>C_INPUT</c> is sent at all and every client is a spectator.
        /// </para>
        /// <para>
        /// Graded as text because nothing here compiles Unity code. That is weaker than
        /// executing it and is stated rather than hidden.
        /// </para>
        /// </remarks>
        [Fact]
        public void TheHarnessInstallsBothInputSeamsAndEnablesTheClock()
        {
            string harness = UnitySource("Net/Diagnostics/LaneBHarness.cs");

            Assert.Contains("SetInputSource(_source)", harness);
            Assert.Contains("clock.InputSource = BuildMoveInput", harness);
            Assert.Contains("clock.enabled = true", harness);
        }

        /// <summary>
        /// The scripted source asks <c>InputButtonPacker</c> for the mask and never builds one.
        /// </summary>
        /// <remarks>
        /// A second transcription of the <c>C_INPUT</c> bit numbers in a harness would drift
        /// from the shipped one with nothing watching, and lane B would then grade a wire format
        /// only the harness believes in. Same pin 3C wrote against <c>ClientPredictionStage</c>.
        /// </remarks>
        [Fact]
        public void TheScriptedSourceAsksThePackerForTheMask()
        {
            string source = UnitySource("Net/Diagnostics/ScriptedInputSource.cs");

            Assert.Contains("InputButtonPacker.Pack(", source);
            Assert.DoesNotContain("InputButtons.Fire", source);
            Assert.DoesNotContain("1 <<", source);
        }

        /// <summary>
        /// The harness strips one bootstrap BEFORE Start, which is the only window that works.
        /// </summary>
        /// <remarks>
        /// <c>NetServerBootstrap</c> fills sixteen player bodies in <c>Start</c> and
        /// <c>NetClientBootstrap</c> dials in <c>Start</c>. Moving this to any later callback
        /// leaves three client processes each holding sixteen claimable bodies and a self-dialed
        /// connection — a topology no check in <c>phase-3-harness.md</c> § 2 describes.
        /// </remarks>
        [Fact]
        public void TheHarnessStripsTheOtherBootstrapInSceneLoaded()
        {
            string harness = UnitySource("Net/Diagnostics/LaneBHarness.cs");

            Assert.Contains("SceneManager.sceneLoaded += OnSceneLoaded", harness);
            Assert.Contains("NetContext.SetRole(NetRole.Server)", harness);
            Assert.Contains("NetContext.SetRole(NetRole.Client)", harness);
        }

        /// <summary>
        /// The run summary says whether the client still had a link, and the runner fails on it.
        /// </summary>
        /// <remarks>
        /// <para>
        /// <b>A disconnected client runs its script perfectly.</b> It advances the cursor,
        /// captures every checkpoint, exits 0 with "programme complete", and draws both seeds
        /// from the right place — while its body falls through an empty world. Every other row
        /// the runner grades (exit code, checkpoint count, both seeds, the player id) is
        /// structurally incapable of noticing, so the run reports success and the artifact is
        /// about nothing. <c>artifacts/lane-b/combat-02/run.json</c> is that report:
        /// <c>"passed": true</c>, <c>"failures": []</c>, and all three clients dropped with
        /// <c>TransportError</c> seconds after joining.
        /// </para>
        /// <para>
        /// Both halves are pinned because either one alone is inert: a field nothing reads
        /// changes no verdict, and a runner check against a field no player writes would pass
        /// vacuously on every old build — which is why the runner treats a MISSING field as a
        /// failure rather than as a pass.
        /// </para>
        /// </remarks>
        [Fact]
        public void ALostLinkIsRecordedAndFailsTheRun()
        {
            string harness = UnitySource("Net/Diagnostics/LaneBHarness.cs");
            Assert.Contains("if (live == null || !live.IsConnected) _lostConnection = true;", harness);
            Assert.Contains("\\\"lostConnection\\\":", harness);

            // The whole guard expression, not the field name: Contains("$summary.lostConnection")
            // still matches "$summary.lostConnectionXX", so a rename of the field would leave
            // this pin green while the gate read a property that does not exist -- and in
            // PowerShell a missing property is $null, which is falsy, so the run would report
            // PASS on every disconnected client. The pin was written that way first and the
            // mutation walked straight past it.
            string runner = RepoFile("tools/run-lane-b.ps1");
            Assert.Contains("elseif ($summary.lostConnection) {", runner);
            Assert.Contains("-notcontains 'lostConnection'", runner);
            Assert.Contains("elseif (-not $summary.connectedAtFinish) {", runner);
        }

        /// <summary>
        /// The SHIPPED player gives the transport's warning sink somewhere to go — not just the
        /// harness.
        /// </summary>
        /// <remarks>
        /// <para>
        /// <c>NetLog.Warning</c> is an <c>Action&lt;string&gt;</c> the transport writes to; with
        /// no subscriber the only two lines that ever explain a <c>TransportError</c> —
        /// "reliable sequence N abandoned after M resends" and "reliable sequence slot collision
        /// at N" — are formatted and handed to a null delegate. <c>Connection.Update</c>'s own
        /// comment says it ends the connection "loudly instead of continuing quietly"; without a
        /// sink the loud half reaches nobody and a dropped client presents as a bare reason code.
        /// </para>
        /// <para>
        /// <b>This test used to assert only that <c>LaneBHarness</c> installed one</b>, which was
        /// true and was defect 2 of the phase-3D report at the same time: the harness was the
        /// ONLY subscriber in the repository, so every shipped client and dedicated server ran
        /// deaf. It now asserts the sink lives in Shared and that both bootstraps install it.
        /// Asserting the harness alone would additionally be wrong now that the file compiles out
        /// under <c>IRONFRONT_NO_DIAGNOSTICS</c>.
        /// </para>
        /// </remarks>
        [Fact]
        public void TheShippedPlayerGivesTheTransportWarningsSomewhereToGo()
        {
            string sink = UnitySource("Net/Shared/NetLogUnitySink.cs");
            Assert.Contains("NetLog.Warning =", sink);
            Assert.Contains("NetLog.Error =", sink);

            // The wired half. A sink nothing calls is the same silence one indirection further
            // out, which is the failure this test exists to catch.
            //
            // Uncommented, deliberately. The first version of this used Assert.Contains and a
            // mutation that commented the call OUT still passed -- the same trap the phase-3D
            // report records for `Assert.Contains("$summary.lostConnection")`. A commented call
            // is exactly the mutation this must catch, so matching one is not a near miss.
            AssertCallsUncommented("Net/Client/NetClientBootstrap.cs", "NetLogUnitySink.Install();");
            AssertCallsUncommented("Net/Server/NetServerBootstrap.cs", "NetLogUnitySink.Install();");
            AssertCallsUncommented("Net/Diagnostics/LaneBHarness.cs", "NetLogUnitySink.Install();");

            // And nobody keeps a private copy: a second assignment is a second thing to fix when
            // the severity policy changes, and the batchmode-severity reasoning lives in one file.
            Assert.DoesNotContain("NetLog.Warning =", UnitySource("Net/Diagnostics/LaneBHarness.cs"));
        }

        /// <summary>
        /// A vehicle-scoped counter never reaches the artifact under a generic name.
        /// </summary>
        /// <remarks>
        /// Every reading in <c>LaneBCheckpointRecorder</c>'s vehicle block is legitimately zero
        /// on an on-foot programme, so a generic name turns "no vehicle in this run" into what
        /// reads as "replication is dead". That is not hypothetical: on 2026-08-21 the combat
        /// set's record showed <c>snapshotsApplied: 0</c>, <c>inputsSent: 0</c> and four zeroed
        /// <c>interp*</c> fields, and it took <c>remoteActorCount: 55</c> plus the client's own
        /// log line to establish that the client was replicating perfectly well.
        ///
        /// <c>snapshotsApplied</c> is pinned by IDENTITY rather than by absence, because the
        /// name was already taken: <c>NetVerificationHarness</c> publishes it from
        /// <c>Router.SnapshotsApplied</c>, the actor-stream counter. Two harnesses emitting one
        /// key with two meanings is worse than either name alone, since the artifact carries no
        /// way to tell which one you are holding.
        /// </remarks>
        [Fact]
        public void TheCheckpointRecorderNamesVehicleCountersAsVehicleCounters()
        {
            string recorder = UnitySource("Net/Diagnostics/LaneBCheckpointRecorder.cs");

            // The generic key means the actor stream, in BOTH harnesses.
            Assert.Contains("Num(\"snapshotsApplied\", client.Router.SnapshotsApplied)", recorder);

            foreach (string key in new[]
                     {
                         "vehicleSnapshotsApplied", "vehicleInterpBuffered", "vehicleInterpNewestTick",
                         "vehicleInterpStalled", "vehicleInterpReordered", "vehicleInputsSent",
                         "vehicleStarvedFrames",
                     })
            {
                Assert.Contains($"Num(\"{key}\"", recorder);
            }

            // And the generic forms are gone, so the fix cannot silently drift back.
            foreach (string stale in new[]
                     {
                         "Num(\"interpBuffered\"", "Num(\"interpNewestTick\"", "Num(\"interpStalled\"",
                         "Num(\"interpReordered\"", "Num(\"inputsSent\"", "Num(\"starvedFrames\"",
                     })
            {
                Assert.DoesNotContain(stale, recorder);
            }
        }

        /// <summary>
        /// The SHIPPED build gives them somewhere to go too — not only the test harness.
        /// </summary>
        /// <remarks>
        /// The companion to the test above, and the half that actually matters in production.
        /// A sink only the lane-B harness installs is a sink the dedicated server does not
        /// have, and the dedicated server is the process that will be running when a reliable
        /// channel dies in front of real players. Pinned on
        /// <see cref="IronfrontLog"/>'s <c>RuntimeInitializeOnLoadMethod</c> boot path
        /// specifically, because that is the only wiring in this project that cannot be
        /// omitted from a scene or a prefab.
        /// </remarks>
        [Fact]
        public void TheShippedBuildGivesTheTransportWarningsSomewhereToGo()
        {
            string log = UnitySource("Net/Diagnostics/IronfrontLog.cs");

            Assert.Contains("RuntimeInitializeOnLoadMethod", log);
            Assert.Contains("AttachTransportLog();", log);
            Assert.Contains("NetLog.Warning ??=", log);
            Assert.Contains("NetLog.Error ??=", log);
        }

        /// <summary>
        /// A client process declares its role BEFORE any scene <c>Awake</c> can read it, and
        /// the server bootstrap defers to that declaration.
        /// </summary>
        /// <remarks>
        /// <para>
        /// <b>Both halves, because either one alone is inert.</b> The declaration without the
        /// deference is overwritten by <c>NetServerBootstrap.Awake</c> a moment later; the
        /// deference without the declaration has nothing to defer to. Only together do they
        /// close the window in which <c>NetClientPresenterGuard.IsPresentable</c> answers
        /// "server" inside a client process — the window that gave <c>combat-fix01</c> a
        /// permanently disabled combat driver and killfeed, and therefore
        /// <c>weaponId: 0</c> and <c>namedPlayers: 0</c>.
        /// </para>
        /// <para>
        /// <b><c>BeforeSceneLoad</c> is asserted by name, not by "some initializer".</b>
        /// <c>AfterSceneLoad</c> compiles, installs the harness, and is exactly the timing that
        /// was already wrong — a pin that both spellings satisfy would have passed on the bug.
        /// </para>
        /// </remarks>
        [Fact]
        public void AClientProcessDeclaresItsRoleBeforeAnySceneAwakeCanReadIt()
        {
            string harness = UnitySource("Net/Diagnostics/LaneBHarness.cs");

            Assert.Contains(
                "[RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]",
                harness);
            Assert.Contains("private static void DeclareRole()", harness);
            Assert.Contains("NetContext.SetRole(isServer ? NetRole.Server : NetRole.Client);",
                            harness);

            // The other half. An unconditional claim here re-opens the window whatever the
            // harness declared.
            string server = UnitySource("Net/Server/NetServerBootstrap.cs");
            Assert.Contains("if (!NetContext.IsClient) NetContext.SetRole(NetRole.Server);",
                            server);
        }

        /// <summary>
        /// A programme's <c>aimAtPlayer</c> survives the fact that the server names players
        /// <c>#&lt;playerId&gt;</c> and has never heard of "OBS-A".
        /// </summary>
        /// <remarks>
        /// <para>
        /// Both ends, because either alone resolves nothing: the runner has to EXPORT the
        /// mapping it already owns, and the solver has to FALL BACK to it. Asserted as a pair
        /// for the same reason the role pin above is — a half-wired translation looks exactly
        /// like a wired one from either file on its own.
        /// </para>
        /// <para>
        /// The literal name is still tried first, so a deployment whose players carry real
        /// usernames is unaffected by the harness's aliasing.
        /// </para>
        /// </remarks>
        [Fact]
        public void AScriptedAimResolvesTheNameTheServerActuallyPublishes()
        {
            string solver = UnitySource("Net/Diagnostics/ScriptedTargetSolver.cs");

            Assert.Contains("ushort found = Scan(names, playerName);", solver);
            Assert.Contains("if (found == 0) found = Scan(names, RosterAlias(playerName));", solver);
            Assert.Contains("IRONFRONT_LANEB_ROSTER", solver);
            // The server's own rendering, from ServerTickLoop.DisplayNameFor.
            Assert.Contains("\"#\" + id", solver);

            Assert.Contains("IRONFRONT_LANEB_ROSTER", RepoFile("tools/run-lane-b.ps1"));
        }

        /// <summary>
        /// The checkpoint says whether the two combat components were RUNNING, so a zero can
        /// never again be read as "nothing happened yet" when it meant "this was disabled".
        /// </summary>
        [Fact]
        public void TheCheckpointRecorderReportsWhetherTheCombatComponentsWereEnabled()
        {
            string recorder = UnitySource("Net/Diagnostics/LaneBCheckpointRecorder.cs");

            // The SOURCE text, so the escapes are literal backslashes — this is Roslyn-free
            // text matching over a Unity file, not a parse.
            Assert.Contains("_json.Append(\"\\\"driverEnabled\\\":\")", recorder);
            Assert.Contains("driver.isActiveAndEnabled", recorder);
            Assert.Contains("_json.Append(\"\\\"presenterEnabled\\\":\")", recorder);
            Assert.Contains("presenter.isActiveAndEnabled", recorder);
        }

        /// <summary>
        /// The two linked files stay engine-free, or they silently leave this suite.
        /// </summary>
        /// <remarks>
        /// The build would fail rather than degrade, but the failure names a missing type
        /// somewhere in <c>Linked\</c> and not the rule that was broken. This says the rule.
        /// </remarks>
        [Fact]
        public void TheLinkedFilesNameNoUnityEngine()
        {
            foreach (string file in new[] { "ScriptedInputProgramme.cs", "ScriptedInputCursor.cs" })
            {
                // A DIRECTIVE, not the substring: both files discuss the rule in their own
                // remarks, and a naive Contains would fail on the prose that explains why the
                // rule exists — a pin that goes red for being documented is not a pin.
                string text = UnitySource("Net/Diagnostics/" + file);
                bool imports = text
                    .Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
                    .Any(line => line.TrimStart().StartsWith("using UnityEngine",
                                                             StringComparison.Ordinal));

                Assert.False(imports,
                    $"{file} names UnityEngine, so it can no longer be linked into this suite. "
                    + "Move whatever needed it into LaneBHarness or the recorder.");
            }
        }

        // ------------------------------------------------------------------------------ helpers

        private static ScriptedInputProgramme Programme(params ScriptedInputStep[] steps)
            => new ScriptedInputProgramme { name = "test", steps = steps };

        private static ScriptedInputStep Step(
            float seconds = 1f, float yaw = 0f, float yawRate = 0f, string? checkpoint = null)
            => new ScriptedInputStep
            {
                seconds = seconds,
                yawDegrees = yaw,
                yawRateDegreesPerSecond = yawRate,
                checkpoint = checkpoint,
            };

        /// <summary>
        /// Asserts <paramref name="call"/> appears on a line that is not commented out.
        /// </summary>
        /// <remarks>
        /// <c>Assert.Contains</c> is satisfied by the text inside <c>// call();</c>, which makes
        /// it blind to the one edit most likely to break the wiring it is guarding.
        /// </remarks>
        private static void AssertCallsUncommented(string relativePath, string call)
        {
            foreach (string line in UnitySource(relativePath).Split('\n'))
            {
                string trimmed = line.TrimStart();
                if (trimmed.StartsWith("//", StringComparison.Ordinal)) continue;
                if (trimmed.StartsWith("*", StringComparison.Ordinal)) continue;
                if (trimmed.Contains(call, StringComparison.Ordinal)) return;
            }

            Assert.Fail($"{relativePath} does not call {call} on any uncommented line.");
        }

        private static string UnitySource(string relativePath)
        {
            string path = Path.Combine(
                RepoRoot(), "Ironfront_Reborn", "Assets", "Scripts",
                relativePath.Replace('/', Path.DirectorySeparatorChar));

            Assert.True(File.Exists(path), $"missing Unity source: {path}");
            return File.ReadAllText(path);
        }

        /// <summary>Any repo-relative file, for pinning the runner as well as the harness.</summary>
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
