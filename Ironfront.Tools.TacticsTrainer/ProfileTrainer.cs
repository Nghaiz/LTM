using System;
using System.Collections.Generic;
using System.Globalization;
using Ironfront.Net.Replication.Ai;
using Ironfront.Tools.TacticsTrainer.Baselines;

namespace Ironfront.Tools.TacticsTrainer
{
    /// <summary>One generation of a run, for the report.</summary>
    public readonly struct GenerationRecord
    {
        public GenerationRecord(int generation, float sigma, float bestOfGeneration, float bestSoFar)
        {
            Generation = generation;
            Sigma = sigma;
            BestOfGeneration = bestOfGeneration;
            BestSoFar = bestSoFar;
        }

        public int Generation { get; }
        public float Sigma { get; }
        public float BestOfGeneration { get; }
        public float BestSoFar { get; }
    }

    /// <summary>Everything a run produced: the profiles, their scores, and how it got there.</summary>
    public sealed class TrainingRun
    {
        public ulong Seed { get; init; }
        public int Generations { get; init; }
        public int Population { get; init; }
        public int TrainRounds { get; init; }
        public int LeagueRounds { get; init; }
        public int TestRounds { get; init; }
        public int ValidationRounds { get; init; }
        public int Finalists { get; init; }
        public float TrainedValidation { get; init; }
        public TacticsProfile Start { get; init; } = new TacticsProfile();
        public TacticsProfile Trained { get; init; } = new TacticsProfile();
        public float StartTrainFitness { get; init; }
        public float TrainedTrainFitness { get; init; }
        public EvaluationScore OriginalMirror { get; init; }
        public EvaluationScore V1Test { get; init; }
        public EvaluationScore StartTest { get; init; }
        public EvaluationScore TrainedTest { get; init; }
        public EvaluationScore TrainedVsV1 { get; init; }
        public EvaluationScore TrainedMirror { get; init; }
        public List<(string Label, EvaluationScore V1, EvaluationScore Trained, EvaluationScore TrainedVsV1)> BySlice { get; init; } = new();
        public List<GenerationRecord> History { get; init; } = new();
    }

    /// <summary>
    /// Trains <see cref="TacticsProfile"/> with <see cref="Cmaes"/> against a league of opponents:
    /// the original game's squads and the commander that shipped before (phase P29). Every random
    /// draw comes from the run's seed, and every round's from its own, so a run replays exactly.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Why a league and not one opponent.</b> A profile tuned against one opponent learns that
    /// opponent's weaknesses; AlphaStar's league (Vinyals et al., Nature 2019) and PSRO (Lanctot et
    /// al., NeurIPS 2017) both answer that by scoring against a mixture. Here the mixture is small
    /// and fixed: the original squads, which is the baseline the owner compares with, and the P28
    /// commander, which is what the game ran before -- so the result must beat what it replaces,
    /// not only what it was compared with before.
    /// </para>
    /// <para>
    /// <b>Common random numbers.</b> Every candidate of a run plays the same rounds, so two
    /// candidates differ by their weights and not by their luck -- the variance reduction OpenAI's
    /// evolution strategies rely on (Salimans et al., arXiv:1703.03864).
    /// </para>
    /// </remarks>
    public static class ProfileTrainer
    {
        /// <summary>The first step, in the unit cube the weights are searched in.</summary>
        public const double StartSigma = 0.2;

        /// <summary>How many of the last generations put their best forward for validation.</summary>
        public const int Finalists = 10;

        /// <summary>How much of the fitness is the margin against the original squads; the rest is against the P28 commander.</summary>
        public const float OriginalWeight = 0.6f;

        /// <summary>
        /// What a step outside the searched ranges costs, per squared unit: the strategy samples
        /// outside [0, 1], the candidate is clamped back to play, and this keeps the mean inside.
        /// </summary>
        public const float BoundaryPenalty = 1f;

        /// <summary>
        /// Where a run starts: the hand-set weights, the profile's own initialisers. Not
        /// <see cref="TacticsProfile.Default"/>, which is this run's own output: a re-run starts where
        /// the first one did.
        /// </summary>
        public static TacticsProfile StartProfile() => new TacticsProfile();

        public static TrainingRun Train(TacticsProfile start, ulong seed, int generations, int population, Action<string>? log = null)
            => Train(start, seed, generations, population, TrainingSets.For(seed), log);

        /// <summary>
        /// A run on the given rounds: the report's run uses <see cref="TrainingSets.For"/>; a test
        /// that only asks whether a run replays passes a handful, so CI does not play thousands.
        /// </summary>
        public static TrainingRun Train(TacticsProfile start, ulong seed, int generations, int population, TrainingSets sets, Action<string>? log = null)
        {
            if (population < 4) throw new ArgumentException("population must be at least 4", nameof(population));

            List<MatchSpec> train = sets.Train;
            List<MatchSpec> league = TrainingPlan.LeagueOf(train);
            var rng = SimRandom.For(seed, 0xE5);
            var cma = new Cmaes(ProfileVector.From(start), StartSigma, population);

            float[] startVector = ProfileVector.From(start);
            float startFitness = Fitness(train, league, startVector);
            float[] best = (float[])startVector.Clone();
            float bestFitness = startFitness;
            var history = new List<GenerationRecord>(generations);

            int lambda = cma.Lambda;
            var fitness = new float[lambda];
            var order = new int[lambda];
            var finalists = new List<float[]>();
            for (int g = 0; g < generations; g++)
            {
                double sigma = cma.Sigma;
                double[][] points = cma.Ask(rng);
                var played = new float[lambda][];
                for (int k = 0; k < lambda; k++)
                {
                    played[k] = Clamp(points[k], out float outside);
                    fitness[k] = Fitness(train, league, played[k]) - BoundaryPenalty * outside;
                    order[k] = k;
                }
                // Best first; ties by index, so the order never depends on a sort's stability.
                Array.Sort(order, (a, b) => fitness[b] != fitness[a] ? fitness[b].CompareTo(fitness[a]) : a.CompareTo(b));
                cma.Tell(order);

                float top = fitness[order[0]];
                if (top > bestFitness)
                {
                    bestFitness = top;
                    best = (float[])played[order[0]].Clone();
                }
                if (g >= generations - Finalists) finalists.Add((float[])played[order[0]].Clone());
                history.Add(new GenerationRecord(g + 1, (float)sigma, top, bestFitness));
                log?.Invoke(string.Create(CultureInfo.InvariantCulture,
                    $"generation {g + 1,3}: sigma {sigma:0.000}, best {top:+0.000;-0.000;+0.000}, best so far {bestFitness:+0.000;-0.000;+0.000}"));
            }

            // Chosen on rounds the search never scored. The best training score of thousands of
            // candidates is biased upward by the noise it was picked on, so the last generations'
            // bests, the best ever seen and the distribution's mean are judged afresh.
            finalists.Add(best);
            finalists.Add(Clamp(ToDouble(cma.Mean), out _));
            List<MatchSpec> validation = sets.Validation;
            List<MatchSpec> validationLeague = TrainingPlan.LeagueOf(validation);
            float[] chosen = best;
            float chosenValidation = float.NegativeInfinity;
            foreach (float[] finalist in finalists)
            {
                float score = Fitness(validation, validationLeague, finalist);
                if (score > chosenValidation)
                {
                    chosenValidation = score;
                    chosen = finalist;
                }
            }
            log?.Invoke(string.Create(CultureInfo.InvariantCulture,
                $"validation: {finalists.Count} finalists, chosen scores {chosenValidation:+0.000;-0.000;+0.000}"));

            TacticsProfile trained = ProfileVector.To(chosen);
            TacticsProfileV1 v1 = TacticsProfileV1.Default();
            List<MatchSpec> test = sets.Test;
            var run = new TrainingRun
            {
                Seed = seed,
                Generations = generations,
                Population = lambda,
                TrainRounds = train.Count,
                LeagueRounds = league.Count,
                TestRounds = test.Count,
                Start = start.Clone(),
                Trained = trained,
                StartTrainFitness = startFitness,
                TrainedTrainFitness = Fitness(train, league, chosen),
                ValidationRounds = validation.Count,
                TrainedValidation = chosenValidation,
                Finalists = finalists.Count,
                OriginalMirror = Evaluation.Score(test, () => new OriginalPolicy(), () => new OriginalPolicy()),
                V1Test = Evaluation.ScoreV1(test, v1),
                StartTest = Evaluation.Score(test, start),
                TrainedTest = Evaluation.Score(test, trained),
                TrainedVsV1 = Evaluation.ScoreAgainstV1(test, trained, v1),
                TrainedMirror = Evaluation.Score(test, trained, trained),
                History = history,
            };
            foreach (int size in TrainingPlan.TestSizes)
            {
                List<MatchSpec> slice = test.FindAll(r => r.BotsPerTeam == size);
                run.BySlice.Add((size + " a side", Evaluation.ScoreV1(slice, v1), Evaluation.Score(slice, trained), Evaluation.ScoreAgainstV1(slice, trained, v1)));
            }
            foreach (SimMap map in TrainingPlan.RealMaps())
            {
                List<MatchSpec> slice = test.FindAll(r => r.Map.Name == map.Name);
                run.BySlice.Add((map.Name, Evaluation.ScoreV1(slice, v1), Evaluation.Score(slice, trained), Evaluation.ScoreAgainstV1(slice, trained, v1)));
            }
            return run;
        }

        /// <summary>
        /// A profile's fitness: its mean margin against the original squads over
        /// <paramref name="rounds"/>, and against the P28 commander over <paramref name="league"/>,
        /// weighted by <see cref="OriginalWeight"/>.
        /// </summary>
        public static float Fitness(List<MatchSpec> rounds, List<MatchSpec> league, float[] u)
        {
            TacticsProfile profile = ProfileVector.To(u);
            float original = Evaluation.Score(rounds, profile).MeanMargin;
            float previous = Evaluation.ScoreAgainstV1(league, profile, TacticsProfileV1.Default()).MeanMargin;
            return OriginalWeight * original + (1f - OriginalWeight) * previous;
        }

        private static float[] Clamp(double[] x, out float outside)
        {
            var u = new float[x.Length];
            double sq = 0;
            for (int i = 0; i < x.Length; i++)
            {
                double c = Math.Clamp(x[i], 0.0, 1.0);
                sq += (x[i] - c) * (x[i] - c);
                u[i] = (float)c;
            }
            outside = (float)sq;
            return u;
        }

        private static double[] ToDouble(float[] f)
        {
            var d = new double[f.Length];
            for (int i = 0; i < f.Length; i++) d[i] = f[i];
            return d;
        }
    }
}
