using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Ironfront.Net.Replication.Ai;

namespace Ironfront.Tools.TacticsTrainer
{
    /// <summary>One round to play: a map, a side size, which side the profile under test commands, a seed.</summary>
    public readonly struct MatchSpec
    {
        public MatchSpec(SimMap map, int botsPerTeam, int commanderTeam, ulong seed)
        {
            Map = map;
            BotsPerTeam = botsPerTeam;
            CommanderTeam = commanderTeam;
            Seed = seed;
        }

        public SimMap Map { get; }
        public int BotsPerTeam { get; }
        public int CommanderTeam { get; }
        public ulong Seed { get; }
    }

    /// <summary>How a profile did over a set of rounds.</summary>
    public readonly struct EvaluationScore
    {
        public EvaluationScore(int wins, int losses, int draws, float meanMargin)
        {
            Wins = wins;
            Losses = losses;
            Draws = draws;
            MeanMargin = meanMargin;
        }

        public int Wins { get; }
        public int Losses { get; }
        public int Draws { get; }

        /// <summary>The mean of <see cref="SimResult.MarginFor"/> for the tested side: the fitness.</summary>
        public float MeanMargin { get; }

        public int Rounds => Wins + Losses + Draws;

        public float WinRate => Rounds == 0 ? 0f : Wins / (float)Rounds;
    }

    /// <summary>Plays rounds and scores a profile over them, in parallel and deterministically.</summary>
    public static class Evaluation
    {
        /// <summary>
        /// The rounds a run is judged on: every map, every side size, the tested profile on each
        /// side in turn (so a map's lopsided start cancels), under seeds drawn from <paramref name="seed"/>.
        /// </summary>
        public static List<MatchSpec> Rounds(IReadOnlyList<SimMap> maps, IReadOnlyList<int> sizes, int repeats, ulong seed)
        {
            var rounds = new List<MatchSpec>();
            ulong stream = 0;
            foreach (SimMap map in maps)
                foreach (int size in sizes)
                    for (int r = 0; r < repeats; r++)
                    {
                        ulong roundSeed = SimRandom.For(seed, stream++).NextULong();
                        rounds.Add(new MatchSpec(map, size, 0, roundSeed));
                        rounds.Add(new MatchSpec(map, size, 1, roundSeed));
                    }
            return rounds;
        }

        /// <summary>Maps for a run: the two real ones and <paramref name="generated"/> drawn from <paramref name="seed"/>.</summary>
        public static List<SimMap> Maps(int generated, ulong seed)
        {
            var maps = new List<SimMap> { SimMap.Dustbowl(), SimMap.Island() };
            var rng = new SimRandom(seed);
            for (int i = 0; i < generated; i++) maps.Add(SimMap.Generate(rng, "Generated-" + seed + "-" + i));
            return maps;
        }

        /// <summary>One round: <paramref name="tested"/> commands one side, <paramref name="opponent"/> the other.</summary>
        public static SimResult Play(MatchSpec spec, Func<ISidePolicy> tested, Func<ISidePolicy> opponent)
        {
            ISidePolicy blue = spec.CommanderTeam == 0 ? tested() : opponent();
            ISidePolicy red = spec.CommanderTeam == 1 ? tested() : opponent();
            var sim = new ConquestSim(spec.Map, spec.BotsPerTeam, blue, red, new SimRandom(spec.Seed));
            return sim.Run();
        }

        /// <summary>The commander on <paramref name="profile"/> against <paramref name="opponent"/> (the original by default).</summary>
        public static EvaluationScore Score(IReadOnlyList<MatchSpec> rounds, TacticsProfile profile, TacticsProfile? opponent = null)
        {
            Func<ISidePolicy> tested = () => new CommanderPolicy(profile.Clone());
            Func<ISidePolicy> other = opponent == null
                ? () => new OriginalPolicy()
                : () => new CommanderPolicy(opponent.Clone());
            return Score(rounds, tested, other);
        }

        public static EvaluationScore Score(IReadOnlyList<MatchSpec> rounds, Func<ISidePolicy> tested, Func<ISidePolicy> opponent)
        {
            var results = new SimResult[rounds.Count];
            Parallel.For(0, rounds.Count, i => results[i] = Play(rounds[i], tested, opponent));

            int wins = 0, losses = 0, draws = 0;
            double margin = 0;
            for (int i = 0; i < results.Length; i++)
            {
                int side = rounds[i].CommanderTeam;
                SimResult result = results[i];
                if (result.Winner == side) wins++;
                else if (result.Winner >= 0) losses++;
                else draws++;
                margin += result.MarginFor(side);
            }
            return new EvaluationScore(wins, losses, draws, results.Length == 0 ? 0f : (float)(margin / results.Length));
        }
    }
}
