using System;
using System.IO;
using Ironfront.Net.Protocol;
using Ironfront.Net.Replication.Combat;
using Ironfront.Net.Replication.Movement;
using Ironfront.Net.Replication.Server;
using Xunit;

namespace Ironfront.Net.Replication.Tests
{
    /// <summary>
    /// Handoff section 5.2: one press is one shot on a semi-automatic, and an automatic's rate
    /// follows the cooldown rather than the number of packets a client chose to send.
    /// </summary>
    public sealed class SemiAutoTriggerEdgeTests
    {
        private const ushort Shooter = TriggerFixture.Shooter;

        [Fact]
        public void ASemiAutomaticHeldForThirtyTicksFiresOnce()
        {
            var fixture = new TriggerFixture(SprintFireGateTests.SemiAuto);

            int fired = fixture.StepFor(30, InputButtons.Fire);

            Assert.Equal(1, fired);
            Assert.Equal(1, fixture.Weapon.ClipSpent(fixture.Config));
        }

        [Fact]
        public void ReleasingAndPressingAgainArmsTheEdgeForASecondRound()
        {
            var fixture = new TriggerFixture(SprintFireGateTests.SemiAuto);

            Assert.Equal(1, fixture.StepFor(30, InputButtons.Fire));
            Assert.Equal(0, fixture.StepFor(1, InputButtons.None, startAt: 1f));
            Assert.Equal(1, fixture.StepFor(30, InputButtons.Fire, startAt: 2f));

            Assert.Equal(2, fixture.Weapon.ClipSpent(fixture.Config));
        }

        [Fact]
        public void EnteringSprintReArmsTheSemiAutoEdge()
        {
            // Holding Fire through a sprint and coming out of it is a trigger EDGE, matching the
            // original controller -- not a continuation that the player has to release and press
            // again to recover from.
            var fixture = new TriggerFixture(SprintFireGateTests.SemiAuto);

            Assert.Equal(1, fixture.StepFor(3, InputButtons.Fire));
            Assert.Equal(0, fixture.StepFor(3, InputButtons.Fire | InputButtons.Sprint, startAt: 1f));
            Assert.Equal(1, fixture.StepFor(3, InputButtons.Fire, startAt: 2f));
        }

        [Fact]
        public void AnAutomaticFiresOnTheCooldownWhileTheTriggerIsHeld()
        {
            // Asserted as the SPACING between shots rather than as a count, because the count
            // is 9 or 10 depending on how 1/30 accumulates in a float and pinning either number
            // would pin that arithmetic instead of the rule. The rule is that consecutive shots
            // are at least one cooldown apart and that the trigger being held does not add any.
            var fixture = new TriggerFixture(Automatic);
            var firedAt = new System.Collections.Generic.List<float>();

            for (int tick = 0; tick < 30; tick++)
            {
                float now = tick / (float)ProtocolConstants.SIM_TICK_RATE;
                if (fixture.Step(now, InputButtons.Fire).Fired) firedAt.Add(now);
            }

            Assert.InRange(firedAt.Count, 9, 10);

            for (int i = 1; i < firedAt.Count; i++)
                Assert.True(
                    firedAt[i] - firedAt[i - 1] >= Automatic.Cooldown - 1e-4f,
                    $"shot {i} came {firedAt[i] - firedAt[i - 1]} s after the last, "
                    + $"inside the {Automatic.Cooldown} s cooldown");
        }

        [Fact]
        public void AnAutomaticsRateDoesNotChangeWhenInputRedundancyIsRaised()
        {
            // The half of section 10.2 that catches counting PACKETS instead of ticks. A client
            // sends INPUT_REDUNDANCY copies of each frame; if the trigger advanced per arrival
            // rather than per processed frame, three copies would be three shots.
            int once = FireThroughTheSession(redundancy: 1, ticks: 30);
            int thrice = FireThroughTheSession(
                redundancy: ProtocolConstants.INPUT_REDUNDANCY, ticks: 30);

            Assert.Equal(once, thrice);
            Assert.True(once > 1, "the run fired nothing, so the comparison proves nothing");
        }

        [Fact]
        public void ADuplicatedInputTickFiresNothingAndSpendsNothing()
        {
            var fixture = new TriggerFixture(Automatic);
            var session = new ClientSession(connectionId: 3, actorId: Shooter);
            var observer = new CombatObserver(fixture);

            session.EnqueueInput(1, TriggerFixture.Frame(InputButtons.Fire));
            InputAuthority.ApplyPendingInput(session, Dt, Still, observer);

            int afterFirst = fixture.Weapon.ClipSpent(fixture.Config);
            Assert.Equal(1, afterFirst);

            // The same tick again, exactly as the redundancy in the next packet repeats it.
            session.EnqueueInput(1, TriggerFixture.Frame(InputButtons.Fire));
            InputAuthority.ApplyPendingInput(session, Dt, Still, observer);

            Assert.Equal(afterFirst, fixture.Weapon.ClipSpent(fixture.Config));
            Assert.Equal(1, observer.FramesSeen);
        }

        /// <summary>
        /// Every weapon its own prefab marks <c>auto: 0</c> spends exactly one round per press,
        /// however long the press is -- the sidearm's report, graded against the asset rather
        /// than against a config written out here.
        /// </summary>
        /// <remarks>
        /// <para>
        /// <b>The defect this is the detector for.</b> A row's <see cref="WeaponConfig.Automatic"/>
        /// was taken from that row's own prose rather than from the prefab, and a row whose comment
        /// discussed cadence without ever saying "semi" kept the constructor default of
        /// <see langword="true"/>. The two SIDEARMS are where that cost a round:
        /// <c>mk25.prefab</c> authors <c>auto: 0</c>, the catalogue said automatic, and
        /// <c>Automatic</c> is the only thing standing between a held trigger and
        /// <see cref="WeaponConfig.Cooldown"/> -- 0.05 s, 1.5 ticks at 30 Hz. A press held for
        /// three ticks, which is an ordinary ~100 ms click, spent TWO rounds; the same defect sat
        /// latent on <c>RECON_LRR</c> (0.1 s, two rounds from four ticks) and on <c>EAGLE_76</c>,
        /// where the 1.1 s cooldown hid it.
        /// </para>
        /// <para>
        /// <b>The prefab is read off disk rather than transcribed into the test.</b> A copy of the
        /// flag here could agree with the copy in the catalogue while both drifted from the asset,
        /// which is the whole shape of the bug being closed. Assets/ is invisible to
        /// <c>dotnet build</c>, so reading the file is the only way to grade it from this side --
        /// the same reason <c>ClientSemiAutoEdgeTests</c> reads the Unity driver off disk.
        /// </para>
        /// <para>
        /// <b>Three press lengths, and the short one is the point.</b> Three ticks is the click a
        /// player actually makes and the one that spent two; thirty is the held trigger that used
        /// to empty the clip. Both are asserted as EXACTLY one rather than as "at most one", so a
        /// trigger that stops re-arming after the first press cannot pass this by being quiet.
        /// </para>
        /// <para>
        /// <b>RECON_LRR and EAGLE_76 are here because they are the same defect, not neighbours of
        /// it.</b> Same field, same table, same error, and the asset answers all four the same way.
        /// <c>SIGNAL_DMR</c> is deliberately NOT here: <c>dmr.prefab</c> authors <c>auto: 1</c>
        /// while the catalogue says semi, and that opposite-signed disagreement is a cadence
        /// decision about a shipped weapon rather than this defect -- it is booked in
        /// <c>WeaponCatalog</c> beside the entry.
        /// </para>
        /// </remarks>
        [Theory]
        [InlineData(WeaponIds.SIND7, "mk25.prefab")]
        [InlineData(WeaponIds.SIND7_SUPPRESSED, "mk25 suppressed.prefab")]
        [InlineData(WeaponIds.EAGLE_76, "shotgun.prefab")]
        [InlineData(WeaponIds.RECON_LRR, "RFB.prefab")]
        [InlineData(WeaponIds.SL_DEFENDER, "sniper.prefab")]
        public void AWeaponItsPrefabCallsSemiAutomaticSpendsOneRoundPerPress(
            byte weaponId, string prefabFile)
        {
            Assert.True(
                PrefabAuthorsSemiAuto(prefabFile),
                prefabFile + " no longer authors `auto: 0`, so it is the wrong fixture for this "
                + "test -- move the id to a prefab that does, or delete the row");

            WeaponConfig config = WeaponCatalog.For(weaponId);

            Assert.False(
                config.Automatic,
                WeaponIds.NameOf(weaponId) + " is catalogued automatic while " + prefabFile
                + " authors auto: 0, which is the defect: `Automatic` bypasses the rising edge and "
                + "leaves Cooldown as the only limit on a held trigger");

            foreach (int ticks in new[] { 1, 3, 30 })
            {
                var fixture = new TriggerFixture(config);
                int fired = fixture.StepFor(ticks, InputButtons.Fire);

                Assert.True(
                    fired == 1,
                    $"{WeaponIds.NameOf(weaponId)} fired {fired} time(s) over a {ticks}-tick press, "
                    + $"at a {config.Cooldown} s cooldown");
                Assert.Equal(1, fixture.Weapon.ClipSpent(fixture.Config));
            }
        }

        /// <summary>
        /// Whether a weapon prefab's <c>Weapon.Configuration</c> authors <c>auto: 0</c>.
        /// </summary>
        /// <remarks>
        /// Exactly one <c>auto:</c> line per weapon prefab, asserted rather than assumed: a prefab
        /// that grew a second one would otherwise be graded on whichever came first, and the field
        /// is a <c>bool</c> written as <c>0</c>/<c>1</c> so anything else is reported rather than
        /// read as false.
        /// </remarks>
        private static bool PrefabAuthorsSemiAuto(string prefabFile)
        {
            string path = Path.Combine(
                RepoRoot(), "Ironfront_Reborn", "Assets", "Prefab", prefabFile);

            Assert.True(File.Exists(path), "no such prefab: " + path);

            var flags = new System.Collections.Generic.List<string>();

            foreach (string line in File.ReadAllLines(path))
            {
                string trimmed = line.TrimStart();
                if (trimmed.StartsWith("auto:", StringComparison.Ordinal)) flags.Add(trimmed);
            }

            Assert.Single(flags);
            Assert.True(
                flags[0] == "auto: 0" || flags[0] == "auto: 1",
                prefabFile + " authors `" + flags[0] + "`, which is not a flag this can read");

            return flags[0] == "auto: 0";
        }

        /// <summary>The repository root, found by walking up to the solution file.</summary>
        private static string RepoRoot()
        {
            DirectoryInfo? dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir != null && !File.Exists(Path.Combine(dir.FullName, "Ironfront.sln")))
                dir = dir.Parent;

            Assert.True(dir != null, "could not find Ironfront.sln above the test binary");
            return dir!.FullName;
        }

        /// <summary>
        /// Drives <paramref name="ticks"/> ticks of held Fire through the real accepted-input
        /// path, sending each frame <paramref name="redundancy"/> times.
        /// </summary>
        private static int FireThroughTheSession(int redundancy, int ticks)
        {
            var fixture = new TriggerFixture(Automatic);
            var session = new ClientSession(connectionId: 3, actorId: Shooter);
            var observer = new CombatObserver(fixture);

            for (uint tick = 1; tick <= ticks; tick++)
            {
                for (int copy = 0; copy < redundancy; copy++)
                    session.EnqueueInput(tick, TriggerFixture.Frame(InputButtons.Fire));

                InputAuthority.ApplyPendingInput(session, Dt, Still, observer);
            }

            return observer.Shots;
        }

        private const float Dt = 1f / ProtocolConstants.SIM_TICK_RATE;

        /// <summary>Collision stands still: these tests grade the trigger, not the move.</summary>
        private static Vec3 Still(Vec3 motion) => motion;

        /// <summary>
        /// Steps combat for every frame the input path ACCEPTS, which is the seam the server
        /// itself uses. Driving the authority directly would skip the tick dedup, which is the
        /// mechanism under test.
        /// </summary>
        private sealed class CombatObserver : IAcceptedFrameObserver
        {
            private readonly TriggerFixture _fixture;

            public CombatObserver(TriggerFixture fixture) => _fixture = fixture;

            public int Shots { get; private set; }

            public int FramesSeen { get; private set; }

            public void OnAcceptedFrame(
                ClientSession session, uint tick, in InputFrame frame, in MoveInput input)
            {
                FramesSeen++;

                float now = tick / (float)ProtocolConstants.SIM_TICK_RATE;

                if (_fixture.Authority.Step(
                        ref _fixture.Weapon, ref _fixture.Trigger, in FixtureConfig, Shooter,
                        in frame, in _fixture.State, ReadOnlySpan<HitscanTarget>.Empty,
                        in _fixture.Actor, in _fixture.Ammo, now, smoothedRttMs: 0f,
                        currentTick: tick, _fixture.Hits).Fired)
                    Shots++;
            }
        }

        /// <summary>A plain automatic: 0.1 s between shots, thirty rounds.</summary>
        private static readonly WeaponConfig Automatic = WeaponConfig.Rifle;

        private static readonly WeaponConfig FixtureConfig = Automatic;
    }
}
