using System;
using System.IO;
using Ironfront.Net.Protocol;
using Ironfront.Net.Replication.Client;
using Ironfront.Net.Replication.Combat;
using Xunit;

namespace Ironfront.Net.Replication.Tests
{
    /// <summary>
    /// The CLIENT half of the protocol-10 semi-automatic edge. Ledger <b>X-86</b>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The defect was measured, and the server was not part of it.</b> A shot-logged lane-B
    /// run on 2026-09-14 held a SIGNAL DMR's trigger through two five-second presses. The
    /// server's log is unambiguous: 298 <c>[shot]</c> lines, exactly 2 with <c>fired=True</c> —
    /// one round per press, the rising-edge rule in
    /// <see cref="EffectiveTriggerPolicy.Advance"/> doing precisely its job, and 298
    /// <c>rejection=None</c> because no second attempt was ever made. Over those same two
    /// presses the CLIENT recorded <c>predictedShots +29</c> and <c>ammoCorrections +9</c>:
    /// <see cref="ClientCombatState.PredictFire"/> was gated on the cooldown alone, which is
    /// the right rule for an automatic and the wrong one for every other weapon. A player
    /// watches that as a magazine draining and snapping back on a rifle that fired once.
    /// </para>
    /// <para>
    /// <b>These tests grade the client, never the server.</b> The server's own edge has its
    /// tests in <see cref="SemiAutoTriggerEdgeTests"/> and is not touched here — it was correct
    /// before this work and is correct after it.
    /// </para>
    /// <para>
    /// <b>Every duration is derived from <see cref="WeaponConfig.Cooldown"/> or
    /// <see cref="ProtocolConstants.SIM_TICK_RATE"/>, never written as a literal.</b> A press
    /// spelled "5 seconds" would keep passing while a weapon's cooldown moved underneath it,
    /// and the whole claim these make is "many cooldowns' worth of held trigger spends one
    /// round" — which is a statement about the cooldown, not about five.
    /// </para>
    /// </remarks>
    public sealed class ClientSemiAutoEdgeTests
    {
        private const ushort LocalActor = 1;
        private const float Now = 10f;

        /// <summary>SIGNAL DMR: the weapon the measured run used. 0.14 s cooldown, clip of 20.</summary>
        private static WeaponConfig SemiAuto => WeaponCatalog.For(WeaponIds.SIGNAL_DMR);

        /// <summary>RK-44: the control. Same trigger, opposite <see cref="WeaponConfig.Automatic"/>.</summary>
        private static WeaponConfig Automatic => WeaponCatalog.For(WeaponIds.RK44);

        /// <summary>
        /// How many cooldowns a "held" press covers. Large enough that an ungated client
        /// empties the SIGNAL DMR's whole 20-round clip, which is what the run recorded.
        /// </summary>
        private const int CooldownsPerPress = 20;

        private static ClientCombatState Armed(byte weaponId)
        {
            var state = new ClientCombatState { LocalActorId = LocalActor };
            state.EquipWeapon(weaponId);
            return state;
        }

        /// <summary>Sim ticks spanning <paramref name="seconds"/>, at the shared tick rate.</summary>
        private static int Ticks(float seconds)
            => (int)MathF.Ceiling(seconds * ProtocolConstants.SIM_TICK_RATE);

        /// <summary>
        /// One frame of the production loop, in the order <c>NetClientLocalCombatDriver.Update</c>
        /// runs it: sprint block first, then the edge, and <see cref="ClientCombatState.PredictFire"/>
        /// ONLY when the edge says so.
        /// </summary>
        /// <remarks>
        /// The whole point of routing every test through one helper is that no test can
        /// accidentally grade a call order the driver does not use. The source-invariant test
        /// at the bottom is what keeps this helper honest about the driver.
        /// </remarks>
        private static bool Frame(
            ClientCombatState state, bool fire, float nowSeconds, bool sprint = false)
        {
            state.ApplySprint(sprint, nowSeconds);
            if (!state.ApplyTrigger(fire && state.IsAlive, nowSeconds)) return false;

            return state.PredictFire(nowSeconds) == FireRejection.None;
        }

        /// <summary>
        /// Holds (or releases) the trigger across <paramref name="ticks"/> frames and returns
        /// how many shots were predicted.
        /// </summary>
        /// <param name="hz">
        /// The sampling rate of the loop. Defaults to the sim tick rate; a test below runs the
        /// same press at four times that to show the RATE does not change the count, which is
        /// the one thing the client and the server genuinely do differently.
        /// </param>
        private static int Hold(
            ClientCombatState state, int ticks, bool fire, float startAt,
            bool sprint = false, float hz = ProtocolConstants.SIM_TICK_RATE)
        {
            int predicted = 0;
            for (int i = 0; i < ticks; i++)
                if (Frame(state, fire, startAt + i / hz, sprint)) predicted++;

            return predicted;
        }

        // ------------------------------------------------- the measured defect

        [Fact]
        public void HoldingTheTriggerOnASemiAutomaticSpendsExactlyOneRound()
        {
            var state = Armed(WeaponIds.SIGNAL_DMR);
            int ticks = Ticks(SemiAuto.Cooldown * CooldownsPerPress);

            int predicted = Hold(state, ticks, fire: true, startAt: Now);

            Assert.Equal(1, predicted);
            Assert.Equal(SemiAuto.ClipSize - 1, state.AmmoInClip);
            Assert.Equal(1, state.PredictedShots);

            // Stated as a second assertion rather than left implicit: without the edge rule the
            // cooldown alone admits one shot per cooldown, so this press would have taken the
            // whole magazine. That is the number the lane-B run recorded.
            Assert.True(
                ticks > CooldownsPerPress,
                $"the press must span more frames than cooldowns for this to mean anything; "
                + $"{ticks} frames over {CooldownsPerPress} cooldowns");
        }

        [Fact]
        public void HoldingTheTriggerOnAnAutomaticKeepsSpending()
        {
            // The control, and the reason the fix is a shared rule rather than a blanket edge:
            // an automatic must still fire on every effective frame the cooldown allows.
            var state = Armed(WeaponIds.RK44);
            int ticks = Ticks(Automatic.Cooldown * CooldownsPerPress);

            int predicted = Hold(state, ticks, fire: true, startAt: Now);

            // Asserted as a RANGE around the cooldowns the window contains, never as an exact
            // count: n frames at 1/30 s land on either side of a 0.095 s cooldown depending on
            // how the float accumulates, and pinning the count would pin that arithmetic.
            Assert.InRange(predicted, CooldownsPerPress - 1, CooldownsPerPress + 1);
            Assert.Equal(Automatic.ClipSize - predicted, state.AmmoInClip);
        }

        [Fact]
        public void ReleasingAndPressingAgainSpendsASecondRound()
        {
            var state = Armed(WeaponIds.SIGNAL_DMR);
            int ticks = Ticks(SemiAuto.Cooldown * CooldownsPerPress);
            float press2 = Now + SemiAuto.Cooldown * (CooldownsPerPress + 2);

            Assert.Equal(1, Hold(state, ticks, fire: true, startAt: Now));

            // The release. One frame is enough — this is an edge, not a duration.
            Assert.Equal(0, Hold(state, 1, fire: false, startAt: press2 - SemiAuto.Cooldown));
            Assert.Equal(1, Hold(state, ticks, fire: true, startAt: press2));

            Assert.Equal(SemiAuto.ClipSize - 2, state.AmmoInClip);
        }

        [Fact]
        public void TheCountIsTheSameWhenTheLoopSamplesFourTimesFaster()
        {
            // The one thing the two sides genuinely do differently: the server advances the edge
            // once per PROCESSED input frame and this side advances it once per RENDER frame.
            // EffectiveTriggerPolicy's remark says that difference is bounded and harmless, and
            // this is the assertion behind the claim -- a faster loop samples the same held
            // button more often, and the edge collapses every extra sample.
            var slow = Armed(WeaponIds.SIGNAL_DMR);
            var fast = Armed(WeaponIds.SIGNAL_DMR);
            float seconds = SemiAuto.Cooldown * CooldownsPerPress;

            int slowShots = Hold(slow, Ticks(seconds), fire: true, startAt: Now);
            int fastShots = Hold(
                fast, Ticks(seconds) * 4, fire: true, startAt: Now,
                hz: ProtocolConstants.SIM_TICK_RATE * 4);

            Assert.Equal(1, slowShots);
            Assert.Equal(slowShots, fastShots);
        }

        // ------------------------------------------------- the traps that break it silently

        [Fact]
        public void AnEdgeAdvancedOnlyWhileTheTriggerIsHeldLatchesForTheRestOfTheLife()
        {
            // NOT a blessing of this call shape -- a demonstration that it is broken, so the
            // every-frame contract on ApplyTrigger has a failing case attached to it rather
            // than only a paragraph. A caller that folds the advance under its own
            // `if (FirePressed())` never sees the release, so WasEffective stays true and the
            // weapon fires once and then never again. That is worse than the bug being fixed
            // AND it reads to a grader as a flat clip, which is what success looks like.
            var state = Armed(WeaponIds.SIGNAL_DMR);
            int ticks = Ticks(SemiAuto.Cooldown * CooldownsPerPress);
            float press2 = Now + SemiAuto.Cooldown * (CooldownsPerPress + 2);

            Assert.Equal(1, Hold(state, ticks, fire: true, startAt: Now));

            // The released frames simply are not delivered -- the skipped call, spelled out.
            Assert.Equal(0, Hold(state, ticks, fire: true, startAt: press2));

            Assert.Equal(SemiAuto.ClipSize - 1, state.AmmoInClip);
        }

        [Fact]
        public void AWeaponSwitchWhileHoldingFireArmsTheNewWeaponsFirstShot()
        {
            // ClientSession.SwitchWeaponTo re-arms the server's edge on a switch. Without the
            // matching ReArm here, a player who swaps with the trigger down is holding a weapon
            // whose edge is already spent and has to release and press again to fire it.
            var state = Armed(WeaponIds.SIGNAL_DMR);

            Assert.Equal(1, Hold(state, Ticks(SemiAuto.Cooldown * 4), fire: true, startAt: Now));

            state.EquipWeapon(WeaponIds.SL_DEFENDER);

            float after = Now + SemiAuto.Cooldown * 8;
            Assert.Equal(1, Hold(state, Ticks(SemiAuto.Cooldown * 4), fire: true, startAt: after));
        }

        [Fact]
        public void AServerSideWeaponSwapAlsoArmsTheEdge()
        {
            // The OTHER way a weapon changes: a respawn with a different loadout, or a pickup.
            // The client never called EquipWeapon for it, so re-arming in that method alone
            // would leave exactly this path with a dead trigger.
            var state = Armed(WeaponIds.SIGNAL_DMR);

            Assert.Equal(1, Hold(state, Ticks(SemiAuto.Cooldown * 4), fire: true, startAt: Now));

            state.ApplySnapshot(
                new ActorSnapshotEntry
                {
                    ActorId    = LocalActor,
                    ChangeMask = SnapshotField.Weapon,
                    WeaponId   = WeaponIds.SL_DEFENDER,
                    AmmoInClip = WeaponCatalog.For(WeaponIds.SL_DEFENDER).ClipSize,
                },
                Now);

            float after = Now + SemiAuto.Cooldown * 8;
            Assert.Equal(1, Hold(state, Ticks(SemiAuto.Cooldown * 4), fire: true, startAt: after));
        }

        [Fact]
        public void ComingOutOfASprintWithTheTriggerHeldIsAnEdgeOnThisSideToo()
        {
            // The server's rule, from SemiAutoTriggerEdgeTests.EnteringSprintReArmsTheSemiAutoEdge:
            // a sprint lowers the effective trigger, so coming out of one is an EDGE and fires.
            //
            // This is why ApplyTrigger takes a clock and folds SprintAllowsFire into the
            // effective trigger instead of advancing the raw Fire bit. With the raw bit,
            // WasEffective would stay true straight through the sprint, the server would fire
            // the round it owes at the end of the window and this side would predict nothing --
            // and a one-round gap is INSIDE AmmoResyncThreshold, so ReconcileAmmo would KEEP
            // the wrong prediction. A silent permanent bias, not a visible correction.
            var state = Armed(WeaponIds.SIGNAL_DMR);
            float window = ProtocolConstants.SPRINT_FIRE_BLOCK_SECONDS;

            Assert.Equal(1, Hold(state, Ticks(SemiAuto.Cooldown * 4), fire: true, startAt: Now));

            float sprintAt = Now + SemiAuto.Cooldown * 6;
            Assert.Equal(
                0, Hold(state, Ticks(window), fire: true, startAt: sprintAt, sprint: true));

            float freed = sprintAt + window * 2;
            Assert.Equal(1, Hold(state, Ticks(SemiAuto.Cooldown * 4), fire: true, startAt: freed));

            Assert.Equal(SemiAuto.ClipSize - 2, state.AmmoInClip);
        }

        // ------------------------------------------------- the gate is actually wired

        [Fact]
        public void TheUnityDriverGatesPredictFireOnTheEdgeItAdvancesEveryFrame()
        {
            // A rule nothing calls is a rule that reports green on every broken build. Assets/
            // is invisible to dotnet build, so this reads the driver off disk -- the same thing
            // the source-invariant tests beside it do -- and asserts the shape that makes the
            // library rule reachable.
            //
            // What it can and cannot prove: that the advance and the prediction are ONE
            // statement, so no frame can predict without advancing and no frame can skip the
            // advance. It does not prove the file compiles; only Unity can say that.
            string source = File.ReadAllText(Path.Combine(
                RepoRoot(), "Ironfront_Reborn", "Assets", "Scripts", "Net", "Client",
                "NetClientLocalCombatDriver.cs"));

            string guard = GuardLine(source, "_state.ApplyTrigger(");

            Assert.True(
                guard != null,
                "NetClientLocalCombatDriver must gate its prediction on _state.ApplyTrigger. "
                + "Without it the client predicts a round on every frame the trigger is down, "
                + "whatever the weapon is -- +29 predicted shots against 2 the server fired.");

            Assert.Contains("_state.PredictFire(", guard);

            Assert.True(
                guard.TrimStart().StartsWith("if (", StringComparison.Ordinal),
                "the advance must BE the if-condition, not a statement above a separate guard. "
                + "Any other shape allows a frame that predicts without advancing, or -- far "
                + "worse -- a frame that skips the advance, which latches the edge and leaves "
                + "the weapon firing once per life.");

            int sprint = source.IndexOf("_state.ApplySprint(", StringComparison.Ordinal);
            Assert.True(
                sprint >= 0 && sprint < source.IndexOf(guard, StringComparison.Ordinal),
                "ApplySprint must run BEFORE the trigger advance: ApplyTrigger reads the block "
                + "ApplySprint stamps, so the other order tests a window one frame stale.");
        }

        /// <summary>
        /// The whole statement containing <paramref name="call"/>, joined across the line break
        /// an if-condition is allowed to have. Null when the call is only ever in a comment.
        /// </summary>
        private static string GuardLine(string source, string call)
        {
            string[] lines = source.Replace("\r\n", "\n").Split('\n');
            for (int i = 0; i < lines.Length; i++)
            {
                string trimmed = lines[i].TrimStart();
                if (trimmed.StartsWith("//", StringComparison.Ordinal)) continue;
                if (!trimmed.Contains(call)) continue;

                return i + 1 < lines.Length && !trimmed.Contains(";")
                    ? lines[i] + " " + lines[i + 1].Trim()
                    : lines[i];
            }

            return null;
        }

        private static string RepoRoot()
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir != null && !File.Exists(Path.Combine(dir.FullName, "Ironfront.sln")))
                dir = dir.Parent;

            Assert.True(dir != null, "could not find Ironfront.sln above the test binary");
            return dir.FullName;
        }
    }
}
