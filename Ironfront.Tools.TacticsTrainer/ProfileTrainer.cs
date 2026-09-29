using System;
using System.Collections.Generic;
using System.Globalization;
using Ironfront.Net.Replication.Ai;

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
        public int TestRounds { get; init; }
        public int ValidationRounds { get; init; }
        public int Finalists { get; init; }
        public float TrainedValidation { get; init; }
        public TacticsProfile Start { get; init; } = TacticsProfile.Default();
        public TacticsProfile Trained { get; init; } = TacticsProfile.Default();
        public float StartTrainFitness { get; init; }
        public float TrainedTrainFitness { get; init; }
        public EvaluationScore OriginalMirror { get; init; }
        public EvaluationScore StartTest { get; init; }
        public EvaluationScore TrainedTest { get; init; }
        public EvaluationScore TrainedVsStart { get; init; }
        public List<(string Label, EvaluationScore Start, EvaluationScore Trained)> BySlice { get; init; } = new();
        public List<GenerationRecord> History { get; init; } = new();
    }

    /// <summary>
    /// A (mu/mu_w, lambda) evolution strategy with mirrored sampling over the profile's weights,
    /// scored by <see cref="Evaluation.Score"/> against the original squads. Every random draw
    /// comes from the run's seed, and every round's from its own, so a run replays exactly.
    /// </summary>
    public static class ProfileTrainer
    {
        public const float StartSigma = 0.25f;

        /// <summary>How many of the last generations put their best forward for validation.</summary>
        public const int Finalists = 10;
        public const float SigmaDecay = 0.97f;

        public static TrainingRun Train(TacticsProfile start, ulong seed, int generations, int population, Action<string>? log = null)
        {
            if (population < 2 || population % 2 != 0) throw new ArgumentException("population must be even and at least 2", nameof(population));

            List<MatchSpec> train = TrainingPlan.TrainRounds(seed);
            var rng = SimRandom.For(seed, 0xE5);
            int n = ProfileVector.Length;
            int parents = population / 2;
            float[] weights = RecombinationWeights(parents);

            float[] mean = ProfileVector.From(start);
            float startFitness = Fitness(train, mean);
            float[] best = (float[])mean.Clone();
            float bestFitness = startFitness;
            float sigma = StartSigma;
            var history = new List<GenerationRecord>(generations);

            var candidates = new float[population][];
            var fitness = new float[population];
            var order = new int[population];
            // The finalists the validation rounds choose between: each late generation's best.
            var finalists = new List<float[]>();
            for (int g = 0; g < generations; g++)
            {
                for (int k = 0; k < population; k += 2)
                {
                    var plus = new float[n];
                    var minus = new float[n];
                    for (int i = 0; i < n; i++)
                    {
                        float step = sigma * (float)rng.NextGaussian();
                        plus[i] = Math.Clamp(mean[i] + step, 0f, 1f);
                        minus[i] = Math.Clamp(mean[i] - step, 0f, 1f);
                    }
                    candidates[k] = plus;
                    candidates[k + 1] = minus;
                }
                for (int k = 0; k < population; k++)
                {
                    fitness[k] = Fitness(train, candidates[k]);
                    order[k] = k;
                }
                // Best first; ties by index, so the order never depends on a sort's stability.
                Array.Sort(order, (a, b) => fitness[b] != fitness[a] ? fitness[b].CompareTo(fitness[a]) : a.CompareTo(b));

                var next = new float[n];
                for (int r = 0; r < parents; r++)
                {
                    float[] c = candidates[order[r]];
                    for (int i = 0; i < n; i++) next[i] += weights[r] * c[i];
                }
                mean = next;

                if (fitness[order[0]] > bestFitness)
                {
                    bestFitness = fitness[order[0]];
                    best = (float[])candidates[order[0]].Clone();
                }
                if (g >= generations - Finalists) finalists.Add((float[])candidates[order[0]].Clone());
                history.Add(new GenerationRecord(g + 1, sigma, fitness[order[0]], bestFitness));
                log?.Invoke(string.Create(CultureInfo.InvariantCulture,
                    $"generation {g + 1,3}: sigma {sigma:0.000}, best {fitness[order[0]]:+0.000;-0.000}, best so far {bestFitness:+0.000;-0.000}"));
                sigma *= SigmaDecay;
            }

            // Chosen on rounds the search never scored. The best training score of thousands of
            // candidates is biased upward by the noise it was picked on -- a first run scored its
            // pick +0.08 in training and -0.06 on the test rounds -- so the last generations' bests,
            // the best ever seen and the recombined mean are judged afresh on the validation rounds.
            finalists.Add(best);
            finalists.Add(mean);
            List<MatchSpec> validation = TrainingPlan.ValidationRounds(seed);
            float[] chosen = best;
            float chosenValidation = float.NegativeInfinity;
            foreach (float[] finalist in finalists)
            {
                float score = Fitness(validation, finalist);
                if (score > chosenValidation)
                {
                    chosenValidation = score;
                    chosen = finalist;
                }
            }
            log?.Invoke(string.Create(CultureInfo.InvariantCulture,
                $"validation: {finalists.Count} finalists, chosen scores {chosenValidation:+0.000;-0.000}"));

            TacticsProfile trained = ProfileVector.To(chosen);
            List<MatchSpec> test = TrainingPlan.TestRounds(seed);
            var run = new TrainingRun
            {
                Seed = seed,
                Generations = generations,
                Population = population,
                TrainRounds = train.Count,
                TestRounds = test.Count,
                Start = start.Clone(),
                Trained = trained,
                StartTrainFitness = startFitness,
                TrainedTrainFitness = Fitness(train, chosen),
                ValidationRounds = validation.Count,
                TrainedValidation = chosenValidation,
                Finalists = finalists.Count,
                OriginalMirror = Evaluation.Score(test, () => new OriginalPolicy(), () => new OriginalPolicy()),
                StartTest = Evaluation.Score(test, start),
                TrainedTest = Evaluation.Score(test, trained),
                TrainedVsStart = Evaluation.Score(test, trained, start),
                History = history,
            };
            foreach (int size in TrainingPlan.TestSizes)
            {
                List<MatchSpec> slice = test.FindAll(r => r.BotsPerTeam == size);
                run.BySlice.Add((size + " a side", Evaluation.Score(slice, start), Evaluation.Score(slice, trained)));
            }
            foreach (SimMap map in TrainingPlan.RealMaps())
            {
                List<MatchSpec> slice = test.FindAll(r => r.Map.Name == map.Name);
                run.BySlice.Add((map.Name, Evaluation.Score(slice, start), Evaluation.Score(slice, trained)));
            }
            return run;
        }

        private static float Fitness(List<MatchSpec> rounds, float[] u)
            => Evaluation.Score(rounds, ProfileVector.To(u)).MeanMargin;

        private static float[] RecombinationWeights(int parents)
        {
            var weights = new float[parents];
            double sum = 0;
            for (int r = 0; r < parents; r++)
            {
                weights[r] = (float)(Math.Log(parents + 0.5) - Math.Log(r + 1));
                sum += weights[r];
            }
            for (int r = 0; r < parents; r++) weights[r] = (float)(weights[r] / sum);
            return weights;
        }
    }
}
