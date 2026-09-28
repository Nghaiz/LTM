using System;
using System.Diagnostics;
using System.IO;
using Ironfront.Net.Protocol;
using Ironfront.Net.Replication.Client;
using Ironfront.Net.Replication.Combat;
using Xunit;

namespace Ironfront.Net.Replication.Tests
{
    /// <summary>
    /// <see cref="ClientCombatState.ServerAmmoInClip"/>, and the lane-B grade that rests on it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Why a second clip exists at all.</b> <see cref="ClientCombatState.AmmoInClip"/> is a
    /// PREDICTION, and <see cref="ClientCombatState.ReconcileAmmo"/> deliberately KEEPS that
    /// prediction whenever it is within <see cref="ClientCombatState.AmmoResyncThreshold"/> of
    /// the snapshot — so the gap does not shrink on its own and the predicted clip can sit two
    /// rounds off the server's indefinitely. That is correct for a HUD, which must not flicker,
    /// and useless for anything MEASURING the server.
    /// </para>
    /// <para>
    /// <b>The measurement that made it necessary, in both directions on one day.</b> On
    /// 2026-09-14 the lane-B grader counted rounds off the predicted clip. On a p10-semi run it
    /// printed "first press: 1 round PASS" and "second press: 2 rounds FAIL" off identical,
    /// correct server behaviour. On a p10-sprint run on Island where the server fired NOTHING
    /// for the whole programme — 97 attempts, 97 refused <c>Holstered</c> — it printed "PASS
    /// post-sprint window: ammoInClip 30 -> 29, predictedShots +34". The false PASS is the worse
    /// of the two: a red gets investigated, a green ends the question.
    /// </para>
    /// </remarks>
    public sealed class ClientAuthoritativeClipTests
    {
        private const ushort LocalActor = 1;
        private const float Now = 10f;

        private static WeaponConfig Rifle => WeaponCatalog.For(WeaponIds.RK44);

        private static ClientCombatState Equipped()
        {
            var state = new ClientCombatState { LocalActorId = LocalActor };
            state.EquipWeapon(WeaponIds.RK44);
            return state;
        }

        private static ActorSnapshotEntry Clip(byte ammo)
            => new ActorSnapshotEntry
            {
                ActorId    = LocalActor,
                ChangeMask = SnapshotField.Weapon,
                WeaponId   = WeaponIds.RK44,
                AmmoInClip = ammo,
            };

        [Fact]
        public void BeforeAnySnapshotTheServerClipReadsAsNotMeasuredRatherThanAsEmpty()
        {
            var state = Equipped();

            Assert.False(state.HasServerAmmo);

            // The point of the flag, spelled out: a bare 0 is indistinguishable from a dry
            // magazine, and a grader that believed the byte would open every match by reporting
            // the player out of ammo.
            Assert.Equal(0, state.ServerAmmoInClip);
        }

        [Fact]
        public void TheServerClipIsTakenVerbatimWhileThePredictionKeepsItsDrift()
        {
            // The whole contract in one test. The prediction is one round ahead -- the normal
            // operating condition -- so ReconcileAmmo KEEPS it and AmmoInClip does NOT converge
            // on the snapshot. ServerAmmoInClip must show the snapshot anyway.
            var state = Equipped();

            state.ApplySnapshot(Clip(Rifle.ClipSize), Now);
            Assert.Equal(FireRejection.None, state.PredictFire(Now));

            state.ApplySnapshot(Clip(Rifle.ClipSize), Now + Rifle.Cooldown);

            Assert.True(state.HasServerAmmo);
            Assert.Equal(Rifle.ClipSize, state.ServerAmmoInClip);
            Assert.Equal(Rifle.ClipSize - 1, state.AmmoInClip);

            // And the reconcile did NOT fire, which is what makes the drift sticky rather than
            // self-correcting. If this ever becomes non-zero the premise above has changed.
            Assert.Equal(0, state.SnapshotAmmoCorrections);
        }

        [Fact]
        public void TheServerClipMovesEvenWhenThePredictedOneIsFrozenInsideTheThreshold()
        {
            // The measured artifact: `released` and `press-2` both recorded ammoInClip 18 across
            // two seconds in which the server spent a round. Reading the predicted clip there
            // says "0 rounds spent"; reading this one says "1", which is the truth.
            var state = Equipped();
            byte full = Rifle.ClipSize;

            state.ApplySnapshot(Clip(full), Now);
            Assert.Equal(FireRejection.None, state.PredictFire(Now));
            Assert.Equal(FireRejection.None, state.PredictFire(Now + Rifle.Cooldown));

            byte predictedBefore = state.AmmoInClip;
            state.ApplySnapshot(Clip((byte)(full - 1)), Now + Rifle.Cooldown * 2);
            byte serverBefore = state.ServerAmmoInClip;

            state.ApplySnapshot(Clip((byte)(full - 2)), Now + Rifle.Cooldown * 3);

            Assert.Equal(predictedBefore, state.AmmoInClip);
            Assert.Equal(1, serverBefore - state.ServerAmmoInClip);
        }

        [Fact]
        public void ASnapshotWithoutTheWeaponFieldLeavesTheServerClipAlone()
        {
            // The encoder masks on change, so most snapshots carry no weapon field at all.
            // Treating an absent field as a zero would report the clip emptying every tick.
            var state = Equipped();
            state.ApplySnapshot(Clip(Rifle.ClipSize), Now);

            state.ApplySnapshot(
                new ActorSnapshotEntry
                {
                    ActorId    = LocalActor,
                    ChangeMask = SnapshotField.Health | SnapshotField.StateFlags,
                    Health     = 90,
                    StateFlags = ActorStateFlags.IsAlive,
                },
                Now + 1f);

            Assert.True(state.HasServerAmmo);
            Assert.Equal(Rifle.ClipSize, state.ServerAmmoInClip);
        }

        [Fact]
        public void AResetReturnsTheServerClipToNotMeasuredAndNotToZero()
        {
            var state = Equipped();
            state.ApplySnapshot(Clip(Rifle.ClipSize), Now);

            state.Reset();

            Assert.False(state.HasServerAmmo);
        }

        // ------------------------------------------------- the grader that consumes it

        [Fact]
        public void TheLaneBGraderPassesItsOwnMutationSuite()
        {
            // WHY THIS SHELLS OUT. `tools/analyse_lane_b.py --p10-gate` is the only part of the
            // lane-B toolchain that DECIDES rather than prints, and it decided wrongly in both
            // directions on 2026-09-14. Its cases live in the tool because this repo has no
            // Python test project; running them from here is what stops that suite being a file
            // nobody executes. CI already runs python3 (ci.yml runs recount_debt_ledger.py), so
            // an interpreter is not an optimistic assumption.
            //
            // A missing interpreter FAILS rather than skips. A skip would render identically to
            // a pass in the summary line, which is the exact shape of green this whole change
            // exists to remove.
            string script = Path.Combine(RepoRoot(), "tools", "analyse_lane_b.py");
            Assert.True(File.Exists(script), $"the grader is missing: {script}");

            (int code, string output) = RunPython(script);

            Assert.True(
                code == 0,
                $"tools/analyse_lane_b.py --self-test exited {code}. The p10 grade disagrees "
                + $"with a case whose answer is known:\n{output}");

            // The suite reports its own case count, so a run that silently graded nothing is
            // distinguishable from a run that graded everything and agreed.
            Assert.Contains("self-test: 0 failed", output);
        }

        /// <summary>
        /// Runs the self-test on the first interpreter that starts. <c>python3</c> first because
        /// that is what CI invokes; <c>python</c> second because Windows installs commonly ship
        /// only that name.
        /// </summary>
        private static (int, string) RunPython(string script)
        {
            string failures = "";

            foreach (string exe in new[] { "python3", "python" })
            {
                try
                {
                    using var process = Process.Start(new ProcessStartInfo(exe)
                    {
                        ArgumentList        = { script, "--self-test" },
                        RedirectStandardOutput = true,
                        RedirectStandardError  = true,
                        UseShellExecute        = false,
                    });

                    Assert.True(process != null, $"{exe} started no process");

                    string output = process!.StandardOutput.ReadToEnd()
                                    + process.StandardError.ReadToEnd();
                    process.WaitForExit();

                    // An interpreter that is present but cannot run the file at all (a Windows
                    // App Execution Alias answers and exits 9009) is a missing interpreter, not
                    // a red grade. Keep looking rather than reporting the grader broken.
                    if (process.ExitCode == 9009 || output.Contains("Microsoft Store"))
                    {
                        failures += $"{exe}: not a real interpreter; ";
                        continue;
                    }

                    return (process.ExitCode, output);
                }
                catch (Exception error)
                {
                    failures += $"{exe}: {error.Message}; ";
                }
            }

            Assert.Fail(
                "no Python interpreter could run the lane-B grader's self-test, so the only "
                + "tests covering the p10 PASS/FAIL decision did not run. This is a FAIL and "
                + "not a skip on purpose: a skipped gate reads exactly like a passing one. "
                + $"Tried: {failures}");
            return (1, failures);
        }

        private static string RepoRoot()
        {
            DirectoryInfo? dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir != null && !File.Exists(Path.Combine(dir.FullName, "Ironfront.sln")))
                dir = dir.Parent;

            Assert.True(dir != null, "could not find Ironfront.sln above the test binary");
            return dir!.FullName;
        }
    }
}
