using System;
using System.IO;
using System.Text.RegularExpressions;
using Ironfront.Net.Protocol;
using Ironfront.Net.Replication.Match;
using Ironfront.Net.Replication.Movement;
using Ironfront.Net.Replication.World;
using Xunit;

namespace Ironfront.Net.Replication.Tests
{
    /// <summary>
    /// Phase-V8: one capture authority, elimination on the server, and a vehicle spawner that
    /// survives a headless process.
    /// </summary>
    /// <remarks>
    /// Split in three, by what each half can actually prove. The ownership mapping, the
    /// elimination rule and the spawn scheduler are engine-free and are tested as behaviour. The
    /// Unity components are tested as SHAPE, by reading their source — the same technique and
    /// the same caveat as <see cref="VehicleSourceInvariantTests"/>: <c>Assembly-CSharp</c> does
    /// not compile under <c>dotnet test</c>, so a behavioural assertion on
    /// <c>CapturePoint.Start</c> needs an Editor session, while a text assertion catches the
    /// regression that actually happens — someone reinstating the unguarded form months later
    /// during an unrelated edit.
    /// </remarks>
    public sealed class ObjectiveAuthorityTests
    {
        private const float Tick = 1f / ProtocolConstants.SIM_TICK_RATE;

        // ------------------------------------------------------------------ task 3: the mapping

        /// <summary>
        /// The assertion the whole phase exists for, at the level this project can assert it
        /// without Unity: whatever the authority says the owning team is, that is what
        /// <c>SpawnPoint.owner</c> is told — on every tick, including the tick it flips.
        /// </summary>
        [Fact]
        public void SpawnPointOwnerTracksTheAuthoritativeTeamEveryTick()
        {
            var point = new CapturePointState(0, Vec3.Zero, radius: 10f, captureSpeed: 1f);
            MatchRules rules = MatchRules.Default;

            int flips = 0;
            byte previous = point.OwningTeam;

            // Team 1 takes it, then team 0 takes it back, so the trace crosses BOTH thresholds
            // and neutral in between -- a mapping that is only ever exercised while one team
            // holds the point would pass on a straight cast.
            for (int i = 0; i < 300; i++)
            {
                point.Tick(team0Count: 0, team1Count: 2, Tick, rules);
                AssertOwnerMappingHolds(point);
                if (point.OwningTeam != previous) { flips++; previous = point.OwningTeam; }
            }

            for (int i = 0; i < 600; i++)
            {
                point.Tick(team0Count: 2, team1Count: 0, Tick, rules);
                AssertOwnerMappingHolds(point);
                if (point.OwningTeam != previous) { flips++; previous = point.OwningTeam; }
            }

            Assert.True(flips >= 2, $"the trace never changed hands twice ({flips} flips) — it proves nothing");
        }

        [Fact]
        public void ANeutralPointLeavesSpawnPointOwnerAtMinusOne()
        {
            var point = new CapturePointState(0, Vec3.Zero, radius: 10f);

            Assert.Equal(TeamId.None, point.OwningTeam);
            Assert.Equal(-1, CapturePointOwnership.ToSpawnPointOwner(point.OwningTeam));
        }

        /// <summary>
        /// <see cref="TeamId.None"/> is 255 and neutral is -1: the two conventions disagree, and
        /// both plausible casts are wrong in a way that only shows up as "nobody can spawn" or
        /// "everybody can spawn here".
        /// </summary>
        [Fact]
        public void TheNeutralMappingIsNotACast()
        {
            Assert.Equal(0, CapturePointOwnership.ToSpawnPointOwner(TeamId.Team0));
            Assert.Equal(1, CapturePointOwnership.ToSpawnPointOwner(TeamId.Team1));
            Assert.Equal(-1, CapturePointOwnership.ToSpawnPointOwner(TeamId.None));

            // The two wrong answers, named. Widening the byte gives 255, which no team matches,
            // so nobody spawns; narrowing through sbyte gives -1 by accident, which every
            // `owner < 0` eligibility test reads as "any team may spawn here" — right value,
            // for a reason that stops being true the moment TeamId.None changes.
            Assert.NotEqual(TeamId.None, CapturePointOwnership.ToSpawnPointOwner(TeamId.None));
            Assert.NotEqual(0, CapturePointOwnership.ToSpawnPointOwner(TeamId.None));
        }

        [Fact]
        public void ControlIsTheMagnitudeSoTheFlagPoleRisesForBothTeams()
        {
            Assert.Equal(1f, CapturePointOwnership.ToControl(-1f));
            Assert.Equal(1f, CapturePointOwnership.ToControl(1f));
            Assert.Equal(0f, CapturePointOwnership.ToControl(0f));
            Assert.Equal(0.4f, CapturePointOwnership.ToControl(-0.4f), 5);
        }

        // ------------------------------------------------------------------ task 2: uncapturable

        /// <summary>
        /// An HQ is expressed as a capture speed of zero rather than as a branch inside
        /// <see cref="CapturePointState"/>: it never moves, and it still owns its team's spawns
        /// and still drains the other side's tickets.
        /// </summary>
        [Fact]
        public void AnUncapturablePointDoesNotMoveButStillCounts()
        {
            var hq = new CapturePointState(0, Vec3.Zero, radius: 10f, captureSpeed: 0f);
            var rules = MatchRules.Default;

            for (int i = 0; i < 600; i++) hq.Tick(0, 4, Tick, rules);

            Assert.Equal(0f, hq.Owner);
            Assert.Equal(TeamId.None, hq.OwningTeam);

            // And a machine holding one still bleeds the side that owns nothing.
            var owned = new CapturePointState(0, Vec3.Zero, radius: 10f, captureSpeed: 0f);
            var machine = new MatchStateMachine(rules, owned);
            Drive(machine, toPhase: MatchPhase.Playing);

            // Team 0 takes it the only way an uncapturable point can be owned: by starting there.
            // (The server adopts CapturePoint.Start's opening ownership; nothing in Tick can.)
            Assert.False(owned.Tick(4, 0, Tick, rules) && owned.OwningTeam == TeamId.Team0);
        }

        // ------------------------------------------------------------------ task 4: elimination

        [Fact]
        public void LosingEverySpawnPointEndsTheMatchOnce()
        {
            var rules = new MatchRules { MinPlayersToStart = 1, WarmupSeconds = 0f };
            var machine = new MatchStateMachine(rules);

            int endings = 0;
            machine.MatchEnded += _ => endings++;

            Drive(machine, toPhase: MatchPhase.Playing);

            // Past the grace window with both teams still holding ground: nothing happens.
            machine.SetSpawnPointCounts(3, 3);
            for (int i = 0; i < 120; i++) machine.Tick(Tick, 1, ReadOnlySpan<ActorPresence>.Empty);
            Assert.Equal(MatchPhase.Playing, machine.Phase);

            machine.SetSpawnPointCounts(0, 3);
            machine.Tick(Tick, 1, ReadOnlySpan<ActorPresence>.Empty);

            Assert.Equal(MatchPhase.Ended, machine.Phase);
            Assert.Equal(1, endings);
        }

        /// <summary>
        /// The eliminated side must be reported as the LOSER. <c>MatchStateMessage.WinningTeam</c>
        /// is derived from the two scores against the victory margin, so a team wiped off the map
        /// while merely level on points would otherwise be broadcast as an undecided round it had
        /// in fact just lost.
        /// </summary>
        [Fact]
        public void TheEliminatedTeamIsTheOneThatLoses()
        {
            var rules = new MatchRules { MinPlayersToStart = 1, WarmupSeconds = 0f };
            var machine = new MatchStateMachine(rules);

            byte winner = TeamId.Team0;
            machine.MatchEnded += team => winner = team;

            Drive(machine, toPhase: MatchPhase.Playing);
            RunPastGrace(machine, rules);

            machine.SetSpawnPointCounts(0, 5);
            machine.Tick(Tick, 1, ReadOnlySpan<ActorPresence>.Empty);

            Assert.Equal(TeamId.Team1, winner);

            // Elimination is expressed by MOVING THE SCORE, so the survivor is exactly the
            // victory margin clear -- which is what makes the broadcast scoreboard legible and
            // what keeps WinningTeam honest without a second end path. The loser's own score is
            // never touched: it did not spend anything by being wiped out.
            Assert.Equal(0, machine.Score0);
            Assert.Equal(machine.VictoryPoints, machine.Score1);
        }

        [Fact]
        public void BothTeamsAtZeroSpawnPointsIsADraw()
        {
            var rules = new MatchRules { MinPlayersToStart = 1, WarmupSeconds = 0f };
            var machine = new MatchStateMachine(rules);

            byte winner = TeamId.Team0;
            machine.MatchEnded += team => winner = team;

            Drive(machine, toPhase: MatchPhase.Playing);
            RunPastGrace(machine, rules);

            machine.SetSpawnPointCounts(0, 0);
            machine.Tick(Tick, 1, ReadOnlySpan<ActorPresence>.Empty);

            Assert.Equal(MatchPhase.Ended, machine.Phase);
            Assert.Equal(TeamId.None, winner);
        }

        /// <summary>
        /// A map whose points all start neutral has both counts at zero on tick one. Without the
        /// grace window the round ends before anybody has moved — every round, forever.
        /// </summary>
        [Fact]
        public void EliminationDoesNotFireInsideTheGraceWindow()
        {
            var rules = new MatchRules { MinPlayersToStart = 1, WarmupSeconds = 0f, EliminationGraceSeconds = 1f };
            var machine = new MatchStateMachine(rules);

            Drive(machine, toPhase: MatchPhase.Playing);

            machine.SetSpawnPointCounts(0, 0);

            // Just inside the window.
            for (int i = 0; i < (int)(1f / Tick) - 2; i++)
                machine.Tick(Tick, 1, ReadOnlySpan<ActorPresence>.Empty);

            Assert.Equal(MatchPhase.Playing, machine.Phase);
        }

        /// <summary>
        /// A host that never reports counts must not have every round end on it. "Unknown" is
        /// not "both teams are wiped out", and reading it as such would break exactly the
        /// deployments that forgot to wire the call.
        /// </summary>
        [Fact]
        public void UnreportedSpawnPointCountsLeaveEliminationInert()
        {
            var rules = new MatchRules { MinPlayersToStart = 1, WarmupSeconds = 0f };
            var machine = new MatchStateMachine(rules);

            Drive(machine, toPhase: MatchPhase.Playing);
            for (int i = 0; i < 600; i++) machine.Tick(Tick, 1, ReadOnlySpan<ActorPresence>.Empty);

            Assert.Equal(MatchPhase.Playing, machine.Phase);
        }

        /// <summary>
        /// The grace window is measured from the start of each round, not from construction —
        /// otherwise round two ends on its own first tick if its points open neutral.
        /// </summary>
        [Fact]
        public void TheGraceWindowRestartsWithEveryRound()
        {
            var rules = new MatchRules
            {
                MinPlayersToStart = 1,
                WarmupSeconds     = 0f,
                PostMatchSeconds  = 0f,
            };
            var machine = new MatchStateMachine(rules);

            Drive(machine, toPhase: MatchPhase.Playing);
            RunPastGrace(machine, rules);

            machine.SetSpawnPointCounts(0, 4);
            machine.Tick(Tick, 1, ReadOnlySpan<ActorPresence>.Empty);
            Assert.Equal(MatchPhase.Ended, machine.Phase);

            // Ended -> Resetting -> WaitingForPlayers -> Warmup -> Playing, all with the counts
            // still saying team 0 is wiped out.
            for (int i = 0; i < 8 && machine.Phase != MatchPhase.Playing; i++)
                machine.Tick(Tick, 1, ReadOnlySpan<ActorPresence>.Empty);

            Assert.Equal(MatchPhase.Playing, machine.Phase);

            // The very first tick of round two must NOT end it: the window is running again.
            machine.Tick(Tick, 1, ReadOnlySpan<ActorPresence>.Empty);
            Assert.Equal(MatchPhase.Playing, machine.Phase);
        }

        // ------------------------------------------------------------------ task 5: the scheduler

        [Fact]
        public void AClearPadSpawnsImmediatelyOnTheOpeningRequest()
        {
            var scheduler = new VehicleSpawnScheduler(VehicleRespawnType.AfterDestroyed, spawnSeconds: 16f);
            scheduler.RequestSpawnNow();

            Assert.True(scheduler.Tick(Tick, Clear).ShouldSpawn);
        }

        [Fact]
        public void ABlockedSpawnerGivesUpAfterItsBudget()
        {
            var scheduler = new VehicleSpawnScheduler(
                VehicleRespawnType.AfterDestroyed, spawnSeconds: 0f, maxBlockedRetries: 4, blockedRetrySeconds: 1f);

            scheduler.RequestSpawnNow();

            int gaveUp = 0;
            // Four retries at one second each, plus slack. Blocked the whole time.
            for (int i = 0; i < 600; i++)
                if (scheduler.Tick(Tick, Blocked).GaveUp) gaveUp++;

            Assert.Equal(1, gaveUp);
            Assert.Equal(VehicleSpawnPhase.GaveUp, scheduler.Phase);
            Assert.Equal(4, scheduler.BlockedRetries);

            // And it never spawns afterwards, even once the pad clears -- it is disarmed, not
            // merely waiting.
            for (int i = 0; i < 600; i++)
                Assert.False(scheduler.Tick(Tick, Clear).ShouldSpawn);
        }

        [Fact]
        public void AGaveUpSpawnerReArmsOnTheNextDeathEvent()
        {
            var scheduler = new VehicleSpawnScheduler(
                VehicleRespawnType.AfterDestroyed, spawnSeconds: 0f, maxBlockedRetries: 2, blockedRetrySeconds: 1f);

            scheduler.RequestSpawnNow();
            for (int i = 0; i < 300; i++) scheduler.Tick(Tick, Blocked);
            Assert.Equal(VehicleSpawnPhase.GaveUp, scheduler.Phase);

            scheduler.ReportVehicleDied(wasLastSpawned: true, hasBeenUsed: false);
            Assert.Equal(VehicleSpawnPhase.CountingDown, scheduler.Phase);
            Assert.True(scheduler.Tick(Tick, Clear).ShouldSpawn);
        }

        /// <summary>
        /// Defect 2. <c>spawningQueued</c> was declared and never used while
        /// <c>StartSpawnCountdown</c> was a bare <c>Invoke</c>, so two deaths produced two
        /// vehicles from one spawner.
        /// </summary>
        [Fact]
        public void TwoDeathEventsScheduleOneRespawn()
        {
            var scheduler = new VehicleSpawnScheduler(VehicleRespawnType.AfterDestroyed, spawnSeconds: 1f);

            scheduler.ReportVehicleDied(true, false);
            scheduler.ReportVehicleDied(true, false);

            int spawns = 0;
            for (int i = 0; i < 300; i++)
            {
                if (!scheduler.Tick(Tick, Clear).ShouldSpawn) continue;
                spawns++;
                scheduler.ReportSpawned();
            }

            Assert.Equal(1, spawns);
        }

        [Fact]
        public void NeverNeverSchedules()
        {
            var scheduler = new VehicleSpawnScheduler(VehicleRespawnType.Never, spawnSeconds: 1f);

            scheduler.ReportVehicleDied(true, false);
            scheduler.ReportFirstDriverEntered(true);

            Assert.Equal(VehicleSpawnPhase.Idle, scheduler.Phase);
            for (int i = 0; i < 300; i++)
                Assert.False(scheduler.Tick(Tick, Clear).ShouldSpawn);
        }

        /// <summary>
        /// <c>AfterMoved</c> schedules when its vehicle is driven off, and NOT again when that
        /// same vehicle later dies — the replacement was already on its way, and scheduling
        /// twice is defect 2 wearing a different hat.
        /// </summary>
        [Fact]
        public void AfterMovedSchedulesOnFirstDriverAndNotAgainOnThatVehiclesDeath()
        {
            var scheduler = new VehicleSpawnScheduler(VehicleRespawnType.AfterMoved, spawnSeconds: 1f);
            scheduler.RequestSpawnNow();
            scheduler.Tick(Tick, Clear);
            scheduler.ReportSpawned();

            scheduler.ReportFirstDriverEntered(wasLastSpawned: true);
            Assert.Equal(VehicleSpawnPhase.CountingDown, scheduler.Phase);

            scheduler.ReportVehicleDied(wasLastSpawned: true, hasBeenUsed: true);

            int spawns = 0;
            for (int i = 0; i < 300; i++)
            {
                if (!scheduler.Tick(Tick, Clear).ShouldSpawn) continue;
                spawns++;
                scheduler.ReportSpawned();
            }

            Assert.Equal(1, spawns);
        }

        [Fact]
        public void AWorldResetCancelsWhateverWasPending()
        {
            var scheduler = new VehicleSpawnScheduler(VehicleRespawnType.AfterDestroyed, spawnSeconds: 1f);
            scheduler.ReportVehicleDied(true, false);
            Assert.True(scheduler.HasSpawnPending);

            scheduler.ReportWorldReset();

            Assert.Equal(VehicleSpawnPhase.Idle, scheduler.Phase);
            Assert.False(scheduler.HasSpawnPending);
            for (int i = 0; i < 300; i++)
                Assert.False(scheduler.Tick(Tick, Clear).ShouldSpawn);
        }

        /// <summary>
        /// The blocked test is only paid for while a spawn is actually waiting on space. A map
        /// with thirty idle spawners must not run thirty overlap-sphere queries a frame.
        /// </summary>
        [Fact]
        public void AnIdleSpawnerNeverAsksWhetherThePadIsBlocked()
        {
            var scheduler = new VehicleSpawnScheduler(VehicleRespawnType.AfterDestroyed, spawnSeconds: 10f);
            int probes = 0;
            Func<bool> counting = () => { probes++; return false; };

            for (int i = 0; i < 300; i++) scheduler.Tick(Tick, counting);
            Assert.Equal(0, probes);

            scheduler.ReportVehicleDied(true, false);
            for (int i = 0; i < 30; i++) scheduler.Tick(Tick, counting);
            Assert.Equal(0, probes);
        }

        [Fact]
        public void TheSchedulerAllocatesNothingOverAThousandTicks()
        {
            var scheduler = new VehicleSpawnScheduler(VehicleRespawnType.AfterDestroyed, spawnSeconds: 1f);
            scheduler.RequestSpawnNow();

            // Warm the delegate and the first transitions before measuring.
            for (int i = 0; i < 10; i++) scheduler.Tick(Tick, Blocked);

            long before = GC.GetAllocatedBytesForCurrentThread();
            for (int i = 0; i < 1000; i++) scheduler.Tick(Tick, Blocked);

            Assert.Equal(0, GC.GetAllocatedBytesForCurrentThread() - before);
        }

        // ------------------------------------------------------------------ shape: CapturePoint

        /// <summary>
        /// D2. The 1 Hz arithmetic is scheduled at <c>NetRole.Offline</c> and nowhere else — on
        /// the CLIENT as well as the server, because a client running its own would fight the
        /// <c>S_CAPTURE_POINT</c> messages it is already receiving.
        /// </summary>
        [Fact]
        public void TheSceneComponentDoesNotRunItsOwnArithmeticOnServerOrClient()
        {
            string start = MethodBody(ReadScript("CapturePoint.cs"), "CapturePoint.cs", "private void Start()");

            Assert.Contains("InvokeRepeating(\"UpdateOwner\"", start);
            Assert.Contains("NetContext.IsOffline", start);

            // The guard must ENCLOSE the schedule, not merely sit in the same method.
            int guardAt = start.IndexOf("NetContext.IsOffline", StringComparison.Ordinal);
            int scheduleAt = start.IndexOf("InvokeRepeating(\"UpdateOwner\"", StringComparison.Ordinal);
            Assert.True(guardAt < scheduleAt,
                "CapturePoint.Start schedules UpdateOwner before testing the role — every role would run it.");
        }

        /// <summary>
        /// D2's other half: the opening-ownership setup above the guard runs in EVERY role. It
        /// decides where the flags START, which the server then adopts, so gating it would open
        /// a networked round on a different map layout than the single-player one.
        /// </summary>
        [Fact]
        public void ReverseAndAssaultModeStillDecideOpeningOwnershipInEveryRole()
        {
            string start = MethodBody(ReadScript("CapturePoint.cs"), "CapturePoint.cs", "private void Start()");

            int reverseAt = start.IndexOf("reverseMode", StringComparison.Ordinal);
            int assaultAt = start.IndexOf("assaultMode", StringComparison.Ordinal);
            int guardAt   = start.IndexOf("NetContext.IsOffline", StringComparison.Ordinal);

            Assert.True(reverseAt >= 0 && assaultAt >= 0, "CapturePoint.Start no longer applies the opening modes.");
            Assert.True(reverseAt < guardAt && assaultAt < guardAt,
                "the opening-ownership setup moved inside the offline guard — a networked round would start on a different layout.");
        }

        /// <summary>
        /// D3. Exactly one method may assign the ownership fields. This is the grep the phase's
        /// acceptance criterion 3 asks a reviewer to run, run automatically instead.
        /// </summary>
        [Fact]
        public void OwnershipHasOneWritePathOutsideTheOfflineArithmetic()
        {
            string source = StripComments(ReadScript("CapturePoint.cs"));

            (int start, int end) apply = MethodSpan(source, "CapturePoint.cs",
                "public void ApplyAuthoritativeOwner(int team, float authoritativeControl, bool contested)");
            (int start, int end) update = MethodSpan(source, "CapturePoint.cs", "private void UpdateOwner()");
            (int start, int end) startMethod = MethodSpan(source, "CapturePoint.cs", "private void Start()");

            // Every assignment to isContested, anywhere in the file, must be inside one of
            // those three: the authoritative door, the offline arithmetic, or the opening setup.
            MatchCollection writes = Regex.Matches(source, @"\bisContested\s*=(?!=)");
            Assert.NotEmpty(writes);

            foreach (System.Text.RegularExpressions.Match write in writes)
            {
                bool inside = Within(write.Index, apply) || Within(write.Index, update)
                           || Within(write.Index, startMethod) || Within(write.Index, RefreshSpan(source));

                Assert.True(inside,
                    $"CapturePoint.cs assigns isContested at offset {write.Index}, outside every sanctioned "
                    + "write path. Two writers into these fields is how the duplicate-authority bug happened.");
            }
        }

        /// <summary>
        /// D4. The presence scan survives the split, allocation-free and without the constructs
        /// conventions.md § 3.2 bans — because it now runs on the server's tick path, not on a
        /// 1 Hz Invoke.
        /// </summary>
        [Fact]
        public void ThePresenceRefreshDoesNotAllocateOrUseBannedConstructs()
        {
            string body = MethodBody(ReadScript("CapturePoint.cs"), "CapturePoint.cs",
                "public bool RefreshPresence(ReadOnlySpan<ActorPresence> actors)");

            AssertAbsent(body, "foreach", "CapturePoint.RefreshPresence", "conventions.md § 3.2");
            AssertAbsent(body, "List<", "CapturePoint.RefreshPresence", "no allocation on the hot path");
            AssertAbsent(body, "Dictionary<", "CapturePoint.RefreshPresence", "no allocation on the hot path");
            AssertAbsent(body, "AliveActorsInRange", "CapturePoint.RefreshPresence",
                "ActorManager.AliveActorsInRange allocates a List and a Dictionary per call");

            // A square root per actor per point per tick, for a comparison that never needed one.
            AssertAbsent(body, ".magnitude >", "CapturePoint.RefreshPresence", "compare squared distances");
        }

        /// <summary>
        /// The two lines that dereference a renderer are on the server's path now, and a
        /// dedicated server has no renderer to dereference.
        /// </summary>
        [Fact]
        public void TheServerPathNeverDereferencesTheFlagRendererUnguarded()
        {
            string source = StripComments(ReadScript("CapturePoint.cs"));

            // Every flagRenderer USE (not the field declaration, and not a null test) must be
            // inside SetFlagVisible or behind an explicit null check.
            AssertAbsent(
                MethodBody(ReadScript("CapturePoint.cs"), "CapturePoint.cs",
                    "public void ApplyAuthoritativeOwner(int team, float authoritativeControl, bool contested)"),
                "flagRenderer.enabled",
                "CapturePoint.ApplyAuthoritativeOwner",
                "the flag toggle must go through the null-guarded helper");

            string setOwner = MethodBody(ReadScript("CapturePoint.cs"), "CapturePoint.cs", "private void SetOwner(int team)");
            int guardAt = setOwner.IndexOf("flagRenderer != null", StringComparison.Ordinal);
            int useAt = setOwner.IndexOf("flagRenderer.material", StringComparison.Ordinal);

            Assert.True(guardAt >= 0, "CapturePoint.SetOwner touches the renderer with no null guard, and it now runs on the server.");
            Assert.True(guardAt < useAt, "CapturePoint.SetOwner's null guard does not enclose its renderer write.");

            // IngameUi must never be dereferenced unguarded on the server's path. Containment in
            // the offline-only UpdateOwner USED to be the whole rule, and it was a proxy for the
            // real property rather than the property itself -- which is why it also forbade the
            // correct thing. The capture indicator has to run in every role (it is driven by the
            // LOCAL player's distance, and a networked client has no other path to it), so the
            // rule is now the property: guarded, or inside the offline-only arithmetic.
            (int start, int end) update = MethodSpan(source, "CapturePoint.cs", "private void UpdateOwner()");
            (int start, int end) indicator =
                MethodSpan(source, "CapturePoint.cs", "private void UpdateFlagIndicator()");

            MatchCollection uses = Regex.Matches(source, @"IngameUi\.instance");
            foreach (System.Text.RegularExpressions.Match use in uses)
                Assert.True(Within(use.Index, update) || Within(use.Index, indicator),
                    $"CapturePoint.cs dereferences IngameUi.instance at offset {use.Index}, outside both the "
                    + "offline-only UpdateOwner and the null-guarded UpdateFlagIndicator. A headless server has "
                    + "no IngameUi.");

            // ...and inside UpdateFlagIndicator the guard must ENCLOSE the uses, the same shape
            // SetOwner's renderer guard is held to above. Without this half the method could
            // deref first and null-check afterwards and still satisfy the containment test.
            string indicatorBody = source.Substring(indicator.start, indicator.end - indicator.start);
            int uiGuardAt = indicatorBody.IndexOf("IngameUi.instance == null", StringComparison.Ordinal);
            int uiUseAt = indicatorBody.IndexOf("IngameUi.instance.", StringComparison.Ordinal);

            Assert.True(uiGuardAt >= 0,
                "CapturePoint.UpdateFlagIndicator touches IngameUi with no null guard, and it runs in every role.");
            Assert.True(uiUseAt < 0 || uiGuardAt < uiUseAt,
                "CapturePoint.UpdateFlagIndicator's null guard does not enclose its IngameUi uses.");

            // The other singleton on that path. A dedicated server has no local player either.
            Assert.Contains("FpsActorController.instance", indicatorBody);
            Assert.True(
                indicatorBody.Contains("local != null") || indicatorBody.Contains("local == null"),
                "CapturePoint.UpdateFlagIndicator does not null-check FpsActorController.instance.");
        }

        // ------------------------------------------------------------------ shape: VehicleSpawner

        /// <summary>
        /// Defect 1. The unbounded <c>while (SpawnIsBlocked()) yield return WaitForSeconds(1f)</c>
        /// is gone, and so is the coroutine that hosted it.
        /// </summary>
        [Fact]
        public void TheSpawnerHasNoUnboundedBlockedWait()
        {
            string source = StripComments(ReadScript("VehicleSpawner.cs"));

            AssertAbsent(source, "WaitForSeconds", "VehicleSpawner.cs", "the blocked wait is bounded by the scheduler now");
            AssertAbsent(source, "StartCoroutine", "VehicleSpawner.cs", "the lifecycle is a state machine, not a coroutine");
            AssertAbsent(source, "Invoke(", "VehicleSpawner.cs", "a string-named Invoke cannot be guarded against re-entry");
            Assert.Contains("VehicleSpawnScheduler", source);
        }

        /// <summary>
        /// The dead <c>spawningQueued</c> field is gone rather than left as a decoy, and the
        /// spawner subscribes to the world reset that had no subscribers at all.
        /// </summary>
        [Fact]
        public void TheSpawnerSubscribesToTheWorldResetAndKeepsNoDeadGuardField()
        {
            string source = StripComments(ReadScript("VehicleSpawner.cs"));

            AssertAbsent(source, "spawningQueued", "VehicleSpawner.cs",
                "the guard lives on the scheduler; a second dead copy is what the original had");

            Assert.Contains("NetWorldLifecycle.ResetRequested +=", source);
            Assert.Contains("NetWorldLifecycle.ResetRequested -=", source);
        }

        /// <summary>
        /// Defect 5's shape, in the file that owns it: the two <c>spawner</c> calls in
        /// <c>Vehicle.cs</c> are both null-guarded. An asymmetric null check is a bug, not a
        /// style difference — a vehicle placed straight into a scene NREs the first time
        /// anybody drives it.
        /// </summary>
        [Fact]
        public void BothSpawnerCallbacksInVehicleAreNullGuarded()
        {
            string source = StripComments(ReadScript("Vehicle.cs"));

            MatchCollection calls = Regex.Matches(source, @"spawner\.(FirstDriverEntered|VehicleDied)");
            Assert.Equal(2, calls.Count);

            foreach (System.Text.RegularExpressions.Match call in calls)
            {
                // The nearest preceding null test must be within a few lines of the call.
                int guardAt = source.LastIndexOf("spawner != null", call.Index, StringComparison.Ordinal);
                Assert.True(guardAt >= 0, $"Vehicle.cs calls {call.Value} with no preceding null guard.");

                int newlines = 0;
                for (int i = guardAt; i < call.Index; i++) if (source[i] == '\n') newlines++;
                Assert.True(newlines <= 4,
                    $"Vehicle.cs's {call.Value} is {newlines} lines from the nearest `spawner != null` — too far to be its guard.");
            }
        }

        // ------------------------------------------------------------------ shape: engine-free

        /// <summary>
        /// The new engine-free half stays engine-free and stays off the allocating constructs,
        /// the same standing check <c>Vehicles/</c> already carries.
        /// </summary>
        [Fact]
        public void TheNewSeamStaysEngineFree()
        {
            string folder = Path.Combine(RepoRoot(), "Ironfront.Net.Replication", "World");
            string[] files = Directory.GetFiles(folder, "*.cs");

            Assert.True(files.Length >= 2, $"Expected the World seam types under {folder}, found {files.Length} files.");

            for (int i = 0; i < files.Length; i++)
            {
                string body = StripComments(File.ReadAllText(files[i]));
                string name = Path.GetFileName(files[i]);

                AssertAbsent(body, "UnityEngine", name, "the seam must build without Unity");
                AssertAbsent(body, "System.Linq", name, "conventions.md § 3.2");
                AssertAbsent(body, "foreach", name, "conventions.md § 3.2");
                AssertAbsent(body, "new[]", name, "no allocation on the hot path");
                AssertAbsent(body, "List<", name, "no allocation on the hot path");
                AssertAbsent(body, "Dictionary<", name, "no allocation on the hot path");
                AssertAbsent(body, ".ToArray()", name, "no allocation on the hot path");
            }
        }

        /// <summary>
        /// <c>CapturePointSlave.Apply</c> runs every tick on the server; nothing in it may
        /// allocate. Asserted as shape because the type lives in a Unity assembly.
        /// </summary>
        [Fact]
        public void TheSlaveAllocatesNothingOnItsTickPath()
        {
            string path = Path.Combine(
                RepoRoot(), "Ironfront_Reborn", "Assets", "Scripts", "Net", "Server", "CapturePointSlave.cs");
            Assert.True(File.Exists(path), $"Expected CapturePointSlave.cs at {path}.");

            string source = File.ReadAllText(path);
            string apply = MethodBody(source, "CapturePointSlave.cs",
                "public void Apply(IReadOnlyList<CapturePointState> states, ReadOnlySpan<ActorPresence> actors)");

            AssertAbsent(apply, "foreach", "CapturePointSlave.Apply", "conventions.md § 3.2");
            AssertAbsent(apply, "new ", "CapturePointSlave.Apply", "no allocation on the hot path");
            AssertAbsent(apply, "Dictionary<", "CapturePointSlave.Apply", "no allocation on the hot path");
            AssertAbsent(apply, ".ToArray()", "CapturePointSlave.Apply", "no allocation on the hot path");
        }

        // ------------------------------------------------------------------ helpers

        private static readonly Func<bool> Blocked = () => true;
        private static readonly Func<bool> Clear = () => false;

        private static void AssertOwnerMappingHolds(CapturePointState point)
        {
            int mapped = CapturePointOwnership.ToSpawnPointOwner(point.OwningTeam);

            if (point.OwningTeam == TeamId.Team0) Assert.Equal(0, mapped);
            else if (point.OwningTeam == TeamId.Team1) Assert.Equal(1, mapped);
            else Assert.Equal(-1, mapped);
        }

        private static void Drive(MatchStateMachine machine, MatchPhase toPhase)
        {
            for (int i = 0; i < 4000 && machine.Phase != toPhase; i++)
                machine.Tick(Tick, machine.Rules.MinPlayersToStart, ReadOnlySpan<ActorPresence>.Empty);

            Assert.Equal(toPhase, machine.Phase);
        }

        private static void RunPastGrace(MatchStateMachine machine, MatchRules rules)
        {
            machine.SetSpawnPointCounts(3, 3);
            int ticks = (int)(rules.EliminationGraceSeconds / Tick) + 4;
            for (int i = 0; i < ticks; i++)
                machine.Tick(Tick, 1, ReadOnlySpan<ActorPresence>.Empty);
        }

        private static bool Within(int offset, (int start, int end) span)
            => offset >= span.start && offset < span.end;

        private static (int start, int end) RefreshSpan(string strippedSource)
            => MethodSpan(strippedSource, "CapturePoint.cs", "public bool RefreshPresence(ReadOnlySpan<ActorPresence> actors)");

        private static void AssertAbsent(string haystack, string needle, string where, string why)
        {
            Assert.True(
                !haystack.Contains(needle, StringComparison.Ordinal),
                $"{where} must not contain \"{needle}\" — {why}.");
        }

        private static string ReadScript(string fileName)
        {
            string path = Path.Combine(
                RepoRoot(), "Ironfront_Reborn", "Assets", "Scripts", "Assembly-CSharp", fileName);

            Assert.True(File.Exists(path), $"Expected to find {fileName} at {path}.");
            return File.ReadAllText(path);
        }

        private static string RepoRoot()
        {
            DirectoryInfo? directory = new DirectoryInfo(AppContext.BaseDirectory);
            while (directory != null)
            {
                if (File.Exists(Path.Combine(directory.FullName, "Ironfront.sln")))
                    return directory.FullName;

                directory = directory.Parent;
            }

            throw new InvalidOperationException(
                $"No Ironfront.sln found walking up from {AppContext.BaseDirectory}.");
        }

        private static string MethodBody(string source, string file, string signature)
        {
            string stripped = StripComments(source);
            (int start, int end) span = MethodSpan(stripped, file, signature);
            return stripped.Substring(span.start, span.end - span.start);
        }

        private static (int start, int end) MethodSpan(string source, string file, string signature)
        {
            string stripped = StripComments(source);
            int at = stripped.IndexOf(signature, StringComparison.Ordinal);
            Assert.True(at >= 0, $"{file}: no method with signature \"{signature}\". Was it renamed?");

            int open = stripped.IndexOf('{', at + signature.Length);
            Assert.True(open >= 0, $"{file}: \"{signature}\" has no body.");

            int depth = 0;
            for (int i = open; i < stripped.Length; i++)
            {
                if (stripped[i] == '{') depth++;
                else if (stripped[i] == '}')
                {
                    depth--;
                    if (depth == 0) return (open + 1, i);
                }
            }

            throw new InvalidOperationException($"{file}: unbalanced braces after \"{signature}\".");
        }

        /// <summary>
        /// Blanks comment text with spaces, preserving every offset. Same helper and same
        /// caveats as <see cref="VehicleSourceInvariantTests"/> — string literals are left
        /// intact because several invariants here are about them
        /// (<c>InvokeRepeating("UpdateOwner", …)</c>).
        /// </summary>
        private static string StripComments(string source)
        {
            char[] output = source.ToCharArray();
            int i = 0;

            while (i < output.Length)
            {
                char c = output[i];

                if (c == '/' && i + 1 < output.Length && output[i + 1] == '/')
                {
                    while (i < output.Length && output[i] != '\n') { output[i] = ' '; i++; }
                }
                else if (c == '/' && i + 1 < output.Length && output[i + 1] == '*')
                {
                    while (i + 1 < output.Length && !(output[i] == '*' && output[i + 1] == '/'))
                    {
                        if (output[i] != '\n') output[i] = ' ';
                        i++;
                    }
                    if (i < output.Length) { output[i] = ' '; i++; }
                    if (i < output.Length) { output[i] = ' '; i++; }
                }
                else
                {
                    i++;
                }
            }

            return new string(output);
        }
    }
}
