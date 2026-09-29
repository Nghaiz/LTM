using System.Collections.Generic;

namespace Ironfront.Tools.TacticsTrainer
{
    /// <summary>
    /// Which rounds a run trains on and which it is judged on. The two share no generated map and
    /// no seed, so the test numbers are on rounds the tuner never saw.
    /// </summary>
    /// <remarks>
    /// <b>Weighted to the maps the game has.</b> Only Dustbowl and Island ship, so they are played
    /// several times each; twenty-four generated maps sit beside them so the weights learn the mode,
    /// not two layouts. A first plan with four generated maps and one repeat scored the same profile
    /// -0.20 on its training rounds and +0.09 on its test rounds: too few rounds, and the tuner
    /// learned the maps it was shown.
    /// </remarks>
    /// <summary>The three sets of rounds a run uses: to search on, to choose on, to report on.</summary>
    public sealed class TrainingSets
    {
        public TrainingSets(List<MatchSpec> train, List<MatchSpec> validation, List<MatchSpec> test)
        {
            Train = train;
            Validation = validation;
            Test = test;
        }

        public List<MatchSpec> Train { get; }
        public List<MatchSpec> Validation { get; }
        public List<MatchSpec> Test { get; }

        /// <summary>The report's sets: <see cref="TrainingPlan"/>'s 320 rounds each, under <paramref name="seed"/>.</summary>
        public static TrainingSets For(ulong seed)
            => new TrainingSets(TrainingPlan.TrainRounds(seed), TrainingPlan.ValidationRounds(seed), TrainingPlan.TestRounds(seed));
    }

    public static class TrainingPlan
    {
        /// <summary>
        /// Side sizes trained and judged on: from a handful up to the 100-bot match the owner plans.
        /// Training on 8 to 32 alone left 50 a side at -0.28 on the test rounds.
        /// </summary>
        public static readonly int[] Sizes = { 4, 8, 16, 32, 50 };

        public static int[] TrainSizes => Sizes;

        public static int[] TestSizes => Sizes;

        private const int RealRepeats = 4;
        private const int Generated = 24;
        private const ulong TestStream = 0x7E57_0000_0000_0001UL;
        private const ulong ValidationStream = 0x7A11_0000_0000_0001UL;

        public static IReadOnlyList<SimMap> RealMaps() => new[] { SimMap.Dustbowl(), SimMap.Island() };

        /// <summary>
        /// 320 rounds: both real maps four times and twenty-four generated maps once, five sizes,
        /// each side.
        /// </summary>
        public static List<MatchSpec> TrainRounds(ulong seed) => RoundsFor(seed, TrainSizes);

        /// <summary>320 rounds of the same shape on other seeds and other generated maps.</summary>
        public static List<MatchSpec> TestRounds(ulong seed) => RoundsFor(seed ^ TestStream, TestSizes);

        /// <summary>
        /// A third 320, which picks the run's result from its finalists, so the test rounds are
        /// used for nothing but the numbers the report states.
        /// </summary>
        public static List<MatchSpec> ValidationRounds(ulong seed) => RoundsFor(seed ^ ValidationStream, Sizes);

        private static List<MatchSpec> RoundsFor(ulong seed, int[] sizes)
        {
            List<MatchSpec> rounds = Evaluation.Rounds(RealMaps(), sizes, RealRepeats, seed);
            List<SimMap> generated = Evaluation.Maps(Generated, seed);
            generated.RemoveRange(0, 2);
            rounds.AddRange(Evaluation.Rounds(generated, sizes, 1, seed + 1));
            return rounds;
        }
    }
}
