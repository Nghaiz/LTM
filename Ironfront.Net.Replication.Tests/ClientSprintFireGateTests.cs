using System;
using System.IO;
using Ironfront.Net.Protocol;
using Ironfront.Net.Replication.Client;
using Ironfront.Net.Replication.Combat;
using Xunit;

namespace Ironfront.Net.Replication.Tests
{
    /// <summary>
    /// The CLIENT half of the protocol-10 sprint rule. Ledger <b>X-85</b>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The defect these were written against was measured, not imagined.</b> Protocol 10
    /// taught the server to refuse a shot while the actor is sprinting, and a lane-B run on the
    /// freshly built protocol-10 client proved the server half works: across a six-second
    /// window with Fire and Sprint both held the clip stayed at 30 and not one round was spent.
    /// In the same window the client recorded <c>predictedShots +51</c> and drove
    /// <c>ammoCorrections</c> from 1 to 19 — it predicted a shot on every frame the trigger was
    /// down and the server refused every one. A human holding Shift and the left mouse button
    /// takes the same path and watches the magazine drain and snap back.
    /// </para>
    /// <para>
    /// <b>None of this is about protecting the server, and the shot log of that window is
    /// unambiguous on the point.</b> Of 303 attempts: 181 refused <c>Holstered</c> by the
    /// server's sprint rule, 75 <c>OnCooldown</c>, 17 <c>NoAmmo</c>, 30 accepted — and every
    /// one of the 30 carried <c>buttons=0x0801</c>, Fire set with the Sprint bit CLEAR. Not one
    /// shot was ever accepted while sprinting. So what the tests below grade is the client no
    /// longer predicting a shot whose only possible outcome is a correction.
    /// </para>
    /// <para>
    /// <b>Every window here is stated as
    /// <see cref="ProtocolConstants.SPRINT_FIRE_BLOCK_SECONDS"/> rather than as 0.2.</b> A test
    /// carrying its own literal would keep passing while the constant moved underneath it,
    /// which is the same two-numbers-for-one-rule failure the constant's own remark forbids.
    /// </para>
    /// </remarks>
    public sealed class ClientSprintFireGateTests
    {
        /// <summary>The weapon the client tests equip: a 0.1 s cooldown and a 30-round clip.</summary>
        private static WeaponConfig Rifle => WeaponCatalog.For(WeaponIds.RK44);

        /// <summary>The shared window, named once so no test below writes a duration.</summary>
        private const float Window = ProtocolConstants.SPRINT_FIRE_BLOCK_SECONDS;

        private const float Now = 10f;

        private static ClientCombatState Armed()
        {
            var state = new ClientCombatState { LocalActorId = 1 };
            state.EquipWeapon(WeaponIds.RK44);
            return state;
        }

        // --------------------------------------------------- the measured defect

        [Fact]
        public void PredictingAShotWhileSprintingIsRefusedAndSpendsNothing()
        {
            // The lane-B window, in miniature: Fire and Sprint held together for six seconds at
            // the sim rate. Before the gate this predicted a shot on every frame the cooldown
            // allowed; the server spent nothing for the whole window, and still does.
            ClientCombatState state = Armed();

            for (int tick = 0; tick < 6 * ProtocolConstants.SIM_TICK_RATE; tick++)
            {
                float now = Now + tick / (float)ProtocolConstants.SIM_TICK_RATE;

                state.ApplySprint(sprinting: true, now);
                Assert.Equal(FireRejection.Holstered, state.PredictFire(now));
            }

            Assert.Equal(Rifle.ClipSize, state.AmmoInClip);
            Assert.Equal(0L, state.PredictedShots);
        }

        [Fact]
        public void TheTriggerStaysDeadForTheSharedWindowAfterTheLastSprintingFrame()
        {
            // The half a naive "is Sprint down right now?" check would miss. Releasing Shift
            // does not re-arm the trigger: the weapon is still coming back up, and the server
            // blocks for the full window measured from the LAST sprinting frame.
            ClientCombatState state = Armed();

            state.ApplySprint(sprinting: true, Now);
            Assert.Equal(FireRejection.Holstered, state.PredictFire(Now));

            state.ApplySprint(sprinting: false, Now + Window * 0.5f);
            Assert.Equal(FireRejection.Holstered, state.PredictFire(Now + Window * 0.5f));

            state.ApplySprint(sprinting: false, Now + Window * 0.99f);
            Assert.Equal(FireRejection.Holstered, state.PredictFire(Now + Window * 0.99f));

            Assert.Equal(Rifle.ClipSize, state.AmmoInClip);
        }

        [Fact]
        public void TheTriggerComesBackTheInstantTheWindowExpires()
        {
            // The boundary itself, so the gate cannot be "fixed" into refusing forever. The
            // server's own test asserts the same edge from the other side.
            ClientCombatState state = Armed();

            state.ApplySprint(sprinting: true, Now);
            state.PredictFire(Now);

            state.ApplySprint(sprinting: false, Now + Window);

            Assert.Equal(FireRejection.None, state.PredictFire(Now + Window));
            Assert.Equal(Rifle.ClipSize - 1, state.AmmoInClip);
            Assert.Equal(1L, state.PredictedShots);
        }

        [Fact]
        public void APlayerWhoIsNotSprintingPredictsExactlyAsBefore()
        {
            // The regression direction. A gate that refused a shot the server would have taken
            // is the same disagreement pointed the other way, and it would be far less visible:
            // the player's HUD would simply be one round behind for the rest of the life.
            ClientCombatState state = Armed();

            for (int shot = 0; shot < 5; shot++)
            {
                float now = Now + shot * (Rifle.Cooldown + 0.01f);

                state.ApplySprint(sprinting: false, now);
                Assert.Equal(FireRejection.None, state.PredictFire(now));
            }

            Assert.Equal(Rifle.ClipSize - 5, state.AmmoInClip);
            Assert.Equal(5L, state.PredictedShots);
        }

        [Fact]
        public void TheWindowIsMeasuredFromTheEndOfTheSprintAndNotItsStart()
        {
            // Stamped once on the leading edge the block would expire mid-sprint and let a shot
            // out of a lowered weapon. Sprint for a full second, then check the window is still
            // running from the moment Shift came up rather than from the moment it went down.
            ClientCombatState state = Armed();

            for (int tick = 0; tick <= ProtocolConstants.SIM_TICK_RATE; tick++)
                state.ApplySprint(sprinting: true, Now + tick / (float)ProtocolConstants.SIM_TICK_RATE);

            float released = Now + 1f;

            state.ApplySprint(sprinting: false, released + Window * 0.5f);
            Assert.Equal(FireRejection.Holstered, state.PredictFire(released + Window * 0.5f));

            state.ApplySprint(sprinting: false, released + Window);
            Assert.Equal(FireRejection.None, state.PredictFire(released + Window));
        }

        // --------------------------------------------------- one rule, not two copies

        [Theory]
        [InlineData(true)]
        [InlineData(false)]
        public void TheClientAndTheServerRefuseTheSameFrames(bool releaseEarly)
        {
            // The assertion the whole shape exists to make: drive BOTH sides over the same
            // button programme and compare their verdicts frame by frame. A second predicate on
            // the client would pass every test above and fail this one the moment the two
            // disagreed by an edge case — which is exactly how the original defect would have
            // come back in a narrower band.
            //
            // Fire is held for the whole programme, which is what makes the comparison sound:
            // with an alive, deployed, on-foot actor the server's Effective bit is then a
            // function of the sprint rule alone. On the client the mirror of that bit is
            // "not refused as Holstered" -- nothing on that side ever lowers its weapon
            // (WeaponRuntimeState.Loaded is the only writer of Unholstered there), so Holstered
            // can only have come from the gate. OnCooldown and NoAmmo are the cooldown and the
            // clip answering, which the server applies later in ServerFireResolver, so they are
            // correctly not part of this verdict.
            ClientCombatState client = Armed();

            EffectiveTrigger trigger = EffectiveTrigger.Idle;
            WeaponRuntimeState weapon = WeaponRuntimeState.Loaded(Rifle);
            ActorFireEligibility actor = ActorFireEligibility.OnFoot(isAlive: true);

            for (int tick = 0; tick < 120; tick++)
            {
                float now = Now + tick / (float)ProtocolConstants.SIM_TICK_RATE;

                // Sprint in bursts, so the programme crosses the window's expiry in both
                // directions several times rather than testing one transition twice.
                bool sprinting = releaseEarly ? tick % 20 < 3 : tick % 20 < 14;

                InputButtons buttons =
                    InputButtons.Fire | (sprinting ? InputButtons.Sprint : InputButtons.None);

                TriggerOutcome server = EffectiveTriggerPolicy.Advance(
                    ref trigger, ref weapon, new InputFrame(0, 0, 0, 0, buttons), in actor,
                    Rifle.Automatic, now);

                client.ApplySprint(sprinting, now);
                FireRejection rejection = client.PredictFire(now);

                Assert.Equal(server.Effective, rejection != FireRejection.Holstered);
            }
        }

        // --------------------------------------------------- the life boundary

        [Fact]
        public void ANewLifeStartsUnderNoSprintBlock()
        {
            // ClientSession.ResetWeapon clears the server's trigger on respawn for this reason:
            // carrying a block across a death refuses the first shot of a life from a sprint the
            // PREVIOUS body was doing. On this side it would refuse it on the client only, which
            // is the disagreement rather than a shared rule.
            ClientCombatState client = Armed();

            client.ApplySprint(sprinting: true, Now);
            Assert.Equal(FireRejection.Holstered, client.PredictFire(Now));

            client.ApplySnapshot(Entry(alive: false), Now);
            client.ApplySnapshot(Entry(alive: true), Now + 0.01f);

            Assert.Equal(FireRejection.None, client.PredictFire(Now + 0.01f));
        }

        [Fact]
        public void ResetDropsTheSprintBlockWithEverythingElse()
        {
            ClientCombatState client = Armed();

            client.ApplySprint(sprinting: true, Now);
            client.Reset();
            client.EquipWeapon(WeaponIds.RK44);

            Assert.Equal(FireRejection.None, client.PredictFire(Now));
        }

        [Fact]
        public void AWeaponSwitchMidSprintDoesNotHandThePlayerAFreeShot()
        {
            // Sprinting is a fact about the body, not about the gun in its hands -- the sentence
            // is EffectiveTrigger's own, and this is the client-side half of it. Clearing the
            // block in EquipWeapon would make a switch a way to fire out of a sprint.
            ClientCombatState client = Armed();

            client.ApplySprint(sprinting: true, Now);
            client.EquipWeapon(WeaponIds.RK44);

            Assert.Equal(FireRejection.Holstered, client.PredictFire(Now));
        }

        // --------------------------------------------------- the gate is actually wired

        [Fact]
        public void TheUnityDriverAdvancesTheSprintGateOutsideTheFireGuard()
        {
            // A gate nothing calls is a gate that reports green on every broken build. Everything
            // under Assets/ is invisible to dotnet build, so this reads the driver off disk --
            // the same thing the source-invariant tests beside it do -- and asserts the two facts
            // that make the library gate reachable at all.
            //
            // What it can and cannot prove: it proves the call is present, unconditional, and
            // fed by the real Sprint bit. It does not prove the file compiles. Unity is the only
            // thing that can say that, and this test does not pretend otherwise.
            string source = File.ReadAllText(Path.Combine(
                RepoRoot(), "Ironfront_Reborn", "Assets", "Scripts", "Net", "Client",
                "NetClientLocalCombatDriver.cs"));

            Assert.Contains("SprintPressed()", source);
            Assert.Contains("(ushort)InputButtons.Sprint", source);

            int advance = IndexOfStatement(source, "_state.ApplySprint(");

            Assert.True(
                advance >= 0,
                "NetClientLocalCombatDriver must call _state.ApplySprint as an UNCONDITIONAL "
                + "statement. Folding it under the FirePressed() guard leaves nothing stamped "
                + "for a player who sprints without firing, so the shot they take on the frame "
                + "they release Shift is predicted here and refused by the server.");

            int predict = source.IndexOf("_state.PredictFire(", StringComparison.Ordinal);

            Assert.True(
                predict > advance,
                "the sprint gate must be advanced BEFORE PredictFire reads it, or the block the "
                + "prediction is checked against is one frame stale.");
        }

        /// <summary>
        /// Where <paramref name="call"/> appears at the START of a line, comments and
        /// <c>if</c>-guarded uses excluded. The distinction this whole test rests on.
        /// </summary>
        private static int IndexOfStatement(string source, string call)
        {
            foreach (string raw in source.Split('\n'))
            {
                if (raw.TrimStart().StartsWith(call, StringComparison.Ordinal))
                    return source.IndexOf(raw.Trim(), StringComparison.Ordinal);
            }

            return -1;
        }

        private static ActorSnapshotEntry Entry(bool alive)
            => new ActorSnapshotEntry
            {
                ActorId = 1,
                ChangeMask = SnapshotField.StateFlags,
                StateFlags = alive ? ActorStateFlags.IsAlive : ActorStateFlags.None,
            };

        private static string RepoRoot()
        {
            for (DirectoryInfo? d = new DirectoryInfo(Directory.GetCurrentDirectory());
                 d != null; d = d.Parent)
            {
                if (File.Exists(Path.Combine(d.FullName, "Ironfront.sln"))) return d.FullName;
            }

            throw new InvalidOperationException(
                "Ironfront.sln not found walking up from " + Directory.GetCurrentDirectory());
        }
    }
}
