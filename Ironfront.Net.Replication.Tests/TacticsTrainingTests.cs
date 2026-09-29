using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Ironfront.Net.Replication.Ai;
using Ironfront.Tools.TacticsTrainer;
using Xunit;

namespace Ironfront.Net.Replication.Tests
{
    /// <summary>
    /// The claims phase P28 part 4's report makes, held on every run: a training run replays from
    /// its seed, the simulator is fair to both sides, the tuner moves every weight the commander
    /// has, and the profile the game ships beats the original squads on rounds the tuner never saw.
    /// </summary>
    public sealed class TacticsTrainingTests
    {
        private const ulong Seed = 2026093001UL;

        /// <summary>One round at a time: see <see cref="Evaluation.MaxParallelism"/>. Order-independent, so the results are the same.</summary>
        public TacticsTrainingTests() => Evaluation.MaxParallelism = 1;

        /// <summary>
        /// The shipped profile's lead over the hand-set one on the held-out rounds: +0.54 when it
        /// was trained (plans/reports/2026-09-30-p28-tactics-training.md). A fall below this is a regression.
        /// </summary>
        private const float LeadOverHandSet = 0.4f;

        /// <summary>
        /// How far behind the original squads the shipped profile may be, on the same rounds: it was
        /// -0.08 when trained -- roughly level, where the hand-set profile was -0.56.
        /// </summary>
        private const float BehindOriginalAtMost = 0.15f;

        [Fact]
        public void ARoundReplaysExactly()
        {
            var spec = new MatchSpec(SimMap.Island(), 16, 0, 42UL);
            SimResult a = Evaluation.Play(spec, () => new CommanderPolicy(TacticsProfile.Default()), () => new OriginalPolicy());
            SimResult b = Evaluation.Play(spec, () => new CommanderPolicy(TacticsProfile.Default()), () => new OriginalPolicy());
            Assert.Equal((a.Winner, a.Score0, a.Score1, a.Seconds), (b.Winner, b.Score0, b.Score1, b.Seconds));
        }

        /// <summary>A handful of rounds, for the claims that hold on any set: CI plays these, not thousands.</summary>
        private static List<MatchSpec> Few(ulong seed)
            => Evaluation.Rounds(TrainingPlan.RealMaps(), new[] { 8, 16 }, 1, seed);

        [Fact]
        public void TheOriginalAgainstItself_IsExactlyEven()
        {
            // Every round is played once from each side on the same seed, so a map's lopsided start
            // cancels; with the same policy on both sides the two halves must mirror exactly.
            EvaluationScore mirror = Evaluation.Score(Few(Seed), () => new OriginalPolicy(), () => new OriginalPolicy());
            Assert.Equal(0f, mirror.MeanMargin, 6);
            Assert.Equal(mirror.Wins, mirror.Losses);
        }

        [Fact]
        public void ATrainingRunReplaysFromItsSeed()
        {
            var sets = new TrainingSets(Few(Seed), Few(Seed + 1), Few(Seed + 2));
            TrainingRun first = ProfileTrainer.Train(new TacticsProfile(), Seed, 2, 4, sets);
            TrainingRun second = ProfileTrainer.Train(new TacticsProfile(), Seed, 2, 4, sets);

            Assert.Equal(ProfileVector.From(first.Trained), ProfileVector.From(second.Trained));
            Assert.Equal(first.TrainedTrainFitness, second.TrainedTrainFitness);
            Assert.Equal(first.TrainedTest.MeanMargin, second.TrainedTest.MeanMargin);
        }

        [Fact]
        public void TheShippedProfile_BeatsTheHandSetOne_OnRoundsTheTunerNeverSaw()
        {
            EvaluationScore shipped = Evaluation.Score(TrainingPlan.TestRounds(Seed), TacticsProfile.Default(), new TacticsProfile());
            Assert.True(shipped.MeanMargin >= LeadOverHandSet && shipped.Wins > shipped.Losses,
                $"the shipped profile no longer clearly beats the hand-set one: {shipped.Wins} won, {shipped.Losses} lost, "
                + $"{shipped.Draws} drawn, mean margin {shipped.MeanMargin:+0.000;-0.000} (at least +{LeadOverHandSet:0.00}). "
                + "Re-run the trainer and read plans/reports/2026-09-30-p28-tactics-training.md before changing this number.");
        }

        [Fact]
        public void TheShippedProfile_KeepsLevelWithTheOriginalSquads()
        {
            EvaluationScore shipped = Evaluation.Score(TrainingPlan.TestRounds(Seed), TacticsProfile.Default());
            Assert.True(shipped.MeanMargin >= -BehindOriginalAtMost,
                $"the shipped commander has fallen behind the original squads: {shipped.Wins} won, {shipped.Losses} lost, "
                + $"{shipped.Draws} drawn, mean margin {shipped.MeanMargin:+0.000;-0.000} (no worse than -{BehindOriginalAtMost:0.00}). "
                + "A planner change that loses the ground part 4 won back shows here first.");
        }

        [Fact]
        public void TheTunerMovesEveryWeightButTheScale_AndNothingElse()
        {
            string[] tuned = ProfileVector.Parameters.Select(p => p.Name).ToArray();
            string[] weights = typeof(TacticsProfile)
                .GetProperties(BindingFlags.Public | BindingFlags.Instance)
                .Where(p => p.CanWrite && p.Name != nameof(TacticsProfile.TargetBase))
                .Select(p => p.Name)
                .ToArray();

            Assert.Empty(weights.Except(tuned));   // a weight the tuner never moves
            Assert.Empty(tuned.Except(weights));   // a tuned name the profile no longer has
        }

        [Fact]
        public void AProfileSurvivesTheTripThroughTheVector()
        {
            TacticsProfile profile = TacticsProfile.Default();
            TacticsProfile back = ProfileVector.To(ProfileVector.From(profile));
            foreach (ProfileParameter p in ProfileVector.Parameters)
                Assert.Equal(p.Get(profile), p.Get(back), 3);
        }

        [Fact]
        public void EveryShippedWeightLiesInsideTheRangeTheTunerSearches()
        {
            TacticsProfile profile = TacticsProfile.Default();
            foreach (ProfileParameter p in ProfileVector.Parameters)
            {
                float value = p.Get(profile);
                Assert.True(value >= p.Min && value <= p.Max, $"{p.Name} = {value} is outside [{p.Min}, {p.Max}]");
            }
        }

        [Fact]
        public void TheRealMaps_AreConnectedBothWays()
        {
            foreach (SimMap map in TrainingPlan.RealMaps())
            {
                var seen = new HashSet<int> { 0 };
                var queue = new Queue<int>(seen);
                while (queue.Count > 0)
                    foreach (int n in map.Neighbours[queue.Dequeue()])
                        if (seen.Add(n)) queue.Enqueue(n);
                Assert.Equal(map.Flags.Count, seen.Count);
            }
        }
    }
}
